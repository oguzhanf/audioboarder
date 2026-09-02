using System.Diagnostics;
using System.Net.Http;
using AudioBoarder.App.Configuration;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;
using AudioBoarder.Services.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AudioBoarder.App.Continuous;

/// <summary>
/// Extracts transcript deltas through the fast model and schedules coalesced deep
/// synthesis after a speech pause or a flushed meeting stop.
/// </summary>
public sealed class ContinuousDiagrammer : IAsyncDisposable
{
    private readonly AudioPipeline _pipeline;
    private readonly DiagramOrchestrator _orchestrator;
    private readonly TranscriptBuffer _buffer;
    private readonly RealtimeSettings _settings;
    private readonly ILogger<ContinuousDiagrammer> _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _wakeCts;
    private CancellationTokenSource? _pauseCts;
    private DateTimeOffset _lastGenerationAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSpeechAt = DateTimeOffset.MinValue;
    private TranscriptCursor _committedCursor;
    private TranscriptCursor _lastAttemptThrough;
    private bool _fastInFlight;
    private bool _pendingFast;
    private bool _wakeScheduled;
    private bool _deepInFlight;
    private bool _pendingDeep;
    private bool _pendingDeepCommitsCursor;
    private bool _started;
    private bool _hasCommittedCursor;
    private Task? _fastTask;
    private Task? _wakeTask;
    private Task? _pauseTask;
    private Task? _deepTask;
    private TimeSpan _observedFastLatency;
    private long _totalGenerations;
    private long _attempts;
    private long _failures;
    private int _consecutiveFailures;
    private int _consecutiveUncommittedResults;
    private DateTimeOffset _retryNotBefore = DateTimeOffset.MinValue;
    private string? _lastSafeErrorCode;
    private ContinuousRuntimeSnapshot _runtime = ContinuousRuntimeSnapshot.Idle;

    public event EventHandler<ContinuousGenerationEvent>? GenerationTriggered;
    public event EventHandler<ContinuousGenerationEvent>? GenerationCompleted;
    public event EventHandler<ContinuousGenerationEvent>? GenerationFailed;
    public event EventHandler<ContinuousRuntimeSnapshot>? RuntimeChanged;

    public ContinuousDiagrammer(
        AudioPipeline pipeline,
        DiagramOrchestrator orchestrator,
        IOptions<AudioBoarderSettings> settings,
        ILogger<ContinuousDiagrammer>? logger = null)
    {
        _pipeline = pipeline;
        _orchestrator = orchestrator;
        _buffer = orchestrator.TranscriptBuffer;
        _settings = settings.Value.Realtime;
        _logger = logger ?? NullLogger<ContinuousDiagrammer>.Instance;
    }

    public bool IsRunning => _started;
    public long TotalGenerations => Interlocked.Read(ref _totalGenerations);
    public long Attempts => Interlocked.Read(ref _attempts);
    public long Successes => TotalGenerations;
    public long Failures => Interlocked.Read(ref _failures);
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);
    public string? LastSafeErrorCode { get { lock (_gate) return _lastSafeErrorCode; } }
    public TranscriptCursor CommittedCursor { get { lock (_gate) return _committedCursor; } }
    public DateTimeOffset LastGenerationAt { get { lock (_gate) return _lastGenerationAt; } }
    public ContinuousRuntimeSnapshot RuntimeSnapshot { get { lock (_gate) return _runtime; } }
    public int PendingNewSegments
    {
        get
        {
            TranscriptCursor cursor;
            lock (_gate) cursor = _committedCursor;
            return _pipeline.IsRunning || _started
                ? _buffer.ReadAfter(cursor).Segments.Count
                : 0;
        }
    }

    public TimeSpan? TimeUntilNextEligible
    {
        get
        {
            DateTimeOffset lastGenerationAt;
            DateTimeOffset retryAt;
            TimeSpan observed;
            lock (_gate)
            {
                lastGenerationAt = _lastGenerationAt;
                retryAt = _retryNotBefore;
                observed = _observedFastLatency;
            }

            if (lastGenerationAt == DateTimeOffset.MinValue) return TimeSpan.Zero;
            var elapsed = DateTimeOffset.UtcNow - lastGenerationAt;
            var interval = EffectiveFastInterval(observed);
            var intervalRemaining = elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
            var retryRemaining = retryAt > DateTimeOffset.UtcNow
                ? retryAt - DateTimeOffset.UtcNow
                : TimeSpan.Zero;
            return retryRemaining > intervalRemaining ? retryRemaining : intervalRemaining;
        }
    }

    /// <summary>
    /// Declares every segment currently in the buffer accounted for. Used when the
    /// user clears or replaces the meeting so pre-barrier speech cannot regenerate
    /// the old board after an in-flight response is rejected by the generation epoch.
    /// </summary>
    public void ResetTranscriptProgress()
    {
        CancelWake();
        CancelPause();
        lock (_gate)
        {
            _committedCursor = _buffer.CurrentCursor;
            _lastAttemptThrough = _committedCursor;
            _hasCommittedCursor = true;
            _pendingFast = false;
            _pendingDeep = false;
            _pendingDeepCommitsCursor = false;
            _retryNotBefore = DateTimeOffset.MinValue;
            _lastSafeErrorCode = null;
        }
        Volatile.Write(ref _consecutiveFailures, 0);
        Volatile.Write(ref _consecutiveUncommittedResults, 0);
        PublishRuntime();
    }

    public void Start()
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Continuous diagramming disabled via settings");
            return;
        }
        if (_started) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (!_hasCommittedCursor)
            {
                _committedCursor = _buffer.CurrentCursor;
                _lastAttemptThrough = _committedCursor;
                _hasCommittedCursor = true;
            }
            _fastInFlight = false;
            _pendingFast = false;
            _wakeScheduled = false;
            _pendingDeep = false;
            _pendingDeepCommitsCursor = false;
            _retryNotBefore = DateTimeOffset.MinValue;
            _lastSafeErrorCode = null;
        }
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _totalGenerations, 0);
        Interlocked.Exchange(ref _failures, 0);
        Volatile.Write(ref _consecutiveFailures, 0);
        Volatile.Write(ref _consecutiveUncommittedResults, 0);
        _pipeline.SegmentEmitted += OnSegment;
        _started = true;
        PublishRuntime();
        _logger.LogInformation(
            "Continuous diagrammer started (fastInterval>={Interval}s, minSegs={MinSegs}, deepPause={Pause}s)",
            _settings.MinIntervalSeconds, _settings.MinNewSegments, _settings.DeepPauseSeconds);
        MaybeTriggerFast();
    }

    public async Task StopAsync(bool synthesizeDeep = false)
    {
        if (!_started && !synthesizeDeep) return;
        _pipeline.SegmentEmitted -= OnSegment;
        _started = false;
        CancelPause();
        CancelWake();

        Task? fast;
        lock (_gate) fast = _fastTask;
        if (synthesizeDeep)
        {
            try
            {
                if (fast is not null) await fast.ConfigureAwait(false);
                await RequestDeepAsync(DeepSynthesisTrigger.MeetingStop, commitCursor: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _cts?.Cancel();
            }
        }
        else
        {
            _cts?.Cancel();
            try
            {
                if (fast is not null) await fast.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        Task? deep;
        lock (_gate) deep = _deepTask;
        try
        {
            if (deep is not null) await deep.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        PublishRuntime();
        _logger.LogInformation("Continuous diagrammer stopped");
    }

    public Task RequestDeepAsync(
        DeepSynthesisTrigger trigger,
        bool commitCursor = false,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_deepInFlight)
            {
                _pendingDeep = true;
                _pendingDeepCommitsCursor |= commitCursor;
                PublishRuntimeLocked(GenerationRuntimeStage.Queued, GenerationMode.DeepSynthesis);
                return _deepTask ?? Task.CompletedTask;
            }

            var hasProvisional = HasProvisionalContent();
            var pending = _buffer.ReadAfter(_committedCursor);
            if (trigger == DeepSynthesisTrigger.SpeechPause && !hasProvisional)
                return Task.CompletedTask;
            if (trigger == DeepSynthesisTrigger.MeetingStop &&
                !hasProvisional && pending.Segments.Count == 0 && _orchestrator.Scene.Nodes.Count == 0)
                return Task.CompletedTask;

            _deepInFlight = true;
            var token = trigger == DeepSynthesisTrigger.MeetingStop
                ? ct
                : _cts?.Token ?? ct;
            _deepTask = Task.Run(
                () => RunDeepAsync(trigger, commitCursor, token),
                CancellationToken.None);
            return _deepTask;
        }
    }

    private void OnSegment(object? sender, TranscriptSegment segment)
    {
        lock (_gate) _lastSpeechAt = DateTimeOffset.UtcNow;
        SchedulePauseCheck();
        MaybeTriggerFast();
        PublishRuntime();
    }

    private void MaybeTriggerFast()
    {
        lock (_gate)
        {
            if (!_started) return;
            if (_fastInFlight)
            {
                _pendingFast = true;
                PublishRuntimeLocked(GenerationRuntimeStage.Queued, GenerationMode.ContinuousExtraction);
                return;
            }

            var slice = _buffer.ReadAfter(_committedCursor);
            if (slice.HasGap)
            {
                if (!string.Equals(_lastSafeErrorCode, "transcript_gap", StringComparison.Ordinal))
                {
                    _lastSafeErrorCode = "transcript_gap";
                    _logger.LogWarning(
                        "Continuous transcript cursor gap detected; requestedAfter={Requested} firstAvailable={First}",
                        slice.RequestedAfter.Sequence, slice.FirstAvailable.Sequence);
                }
            }
            if (slice.Segments.Count < _settings.MinNewSegments)
            {
                PublishRuntimeLocked(null, null);
                return;
            }

            var nextEligible = _lastGenerationAt + EffectiveFastInterval(_observedFastLatency);
            if (_retryNotBefore > nextEligible) nextEligible = _retryNotBefore;
            var delay = nextEligible - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                if (!_wakeScheduled)
                {
                    _wakeScheduled = true;
                    _wakeCts?.Dispose();
                    _wakeCts = CancellationTokenSource.CreateLinkedTokenSource(
                        _cts?.Token ?? CancellationToken.None);
                    _wakeTask = WakeWhenEligibleAsync(delay, _wakeCts.Token);
                }
                PublishRuntimeLocked(GenerationRuntimeStage.Queued, GenerationMode.ContinuousExtraction);
                return;
            }

            _fastInFlight = true;
            _pendingFast = false;
            _lastAttemptThrough = slice.Through;
            var runToken = _cts?.Token ?? CancellationToken.None;
            _fastTask = Task.Run(() => RunFastAsync(slice, runToken), CancellationToken.None);
            PublishRuntimeLocked(GenerationRuntimeStage.Extracting, GenerationMode.ContinuousExtraction);
        }
    }

    private async Task WakeWhenEligibleAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (_gate) _wakeScheduled = false;
        }
        MaybeTriggerFast();
        PublishRuntime();
    }

    private async Task RunFastAsync(TranscriptSlice slice, CancellationToken ct)
    {
        var evt = new ContinuousGenerationEvent(
            slice.Segments.Count,
            DateTimeOffset.UtcNow,
            GenerationMode.ContinuousExtraction,
            slice.Through);
        var sw = Stopwatch.StartNew();
        try
        {
            Interlocked.Increment(ref _attempts);
            lock (_gate) _lastGenerationAt = DateTimeOffset.UtcNow;
            Notify(GenerationTriggered, evt, "triggered");
            var result = await _orchestrator.GenerateAsync(
                userInstruction: null,
                layoutOptions: null,
                mode: GenerationMode.ContinuousExtraction,
                transcriptWindow: slice.Segments,
                ct: ct).ConfigureAwait(false);
            sw.Stop();
            if (result.HasSafeApplication)
            {
                Interlocked.Increment(ref _totalGenerations);
                lock (_gate)
                {
                    _committedCursor = slice.Through;
                    _observedFastLatency = _observedFastLatency == TimeSpan.Zero
                        ? sw.Elapsed
                        : TimeSpan.FromMilliseconds(
                            _observedFastLatency.TotalMilliseconds * 0.7 +
                            sw.Elapsed.TotalMilliseconds * 0.3);
                    _retryNotBefore = DateTimeOffset.MinValue;
                    _lastSafeErrorCode = null;
                }
                Volatile.Write(ref _consecutiveFailures, 0);
                Volatile.Write(ref _consecutiveUncommittedResults, 0);
            }
            else
            {
                RecordUncommittedResult(result);
            }
            Notify(GenerationCompleted, evt, "completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cursor remains unchanged, so a restart retries this exact append slice.
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            Notify(GenerationFailed, evt, "failed");
        }
        finally
        {
            bool runAgain;
            lock (_gate)
            {
                _fastInFlight = false;
                runAgain = _started &&
                           (_pendingFast ||
                            _buffer.ReadAfter(_committedCursor).Segments.Count >= _settings.MinNewSegments);
                _pendingFast = false;
            }
            PublishRuntime();
            if (runAgain) MaybeTriggerFast();
        }
    }

    private async Task RunDeepAsync(
        DeepSynthesisTrigger trigger,
        bool commitCursor,
        CancellationToken ct)
    {
        var through = _buffer.CurrentCursor;
        var transcript = _buffer.Snapshot();
        var resultRejected = false;
        var evt = new ContinuousGenerationEvent(
            transcript.Count,
            DateTimeOffset.UtcNow,
            GenerationMode.DeepSynthesis,
            through,
            trigger);
        try
        {
            Interlocked.Increment(ref _attempts);
            Notify(GenerationTriggered, evt, "triggered");
            PublishRuntime(GenerationRuntimeStage.DeepSynthesizing, GenerationMode.DeepSynthesis);
            var result = await _orchestrator.GenerateAsync(
                userInstruction: trigger == DeepSynthesisTrigger.MeetingStop
                    ? "Deeply synthesize the flushed meeting transcript."
                    : "Deeply synthesize and canonicalize the current grounded diagram.",
                mode: GenerationMode.DeepSynthesis,
                transcriptWindow: transcript,
                ct: ct).ConfigureAwait(false);
            resultRejected = !result.HasSafeApplication;
            if (result.HasSafeApplication)
                Interlocked.Increment(ref _totalGenerations);
            lock (_gate)
            {
                if (commitCursor && result.HasSafeApplication) _committedCursor = through;
                if (result.HasSafeApplication)
                    _retryNotBefore = DateTimeOffset.MinValue;
                _lastSafeErrorCode = result.SafeErrorCode;
            }
            if (result.HasSafeApplication)
            {
                Volatile.Write(ref _consecutiveFailures, 0);
                Volatile.Write(ref _consecutiveUncommittedResults, 0);
            }
            Notify(GenerationCompleted, evt, "completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RecordFailure(ex);
            Notify(GenerationFailed, evt, "failed");
        }
        finally
        {
            bool rerun;
            bool rerunCommits;
            lock (_gate)
            {
                _deepInFlight = false;
                rerun = _pendingDeep;
                rerunCommits = _pendingDeepCommitsCursor;
                _pendingDeep = false;
                _pendingDeepCommitsCursor = false;
            }
            PublishRuntime(
                resultRejected ? GenerationRuntimeStage.Degraded : null,
                GenerationMode.DeepSynthesis);
            if (rerun && (_started || rerunCommits))
                await RequestDeepAsync(
                    rerunCommits ? DeepSynthesisTrigger.MeetingStop : DeepSynthesisTrigger.SpeechPause,
                    rerunCommits,
                    rerunCommits ? CancellationToken.None : ct).ConfigureAwait(false);
        }
    }

    private void SchedulePauseCheck()
    {
        if (_settings.DeepPauseSeconds <= 0 || !_started) return;
        CancelPause();
        _pauseCts = CancellationTokenSource.CreateLinkedTokenSource(
            _cts?.Token ?? CancellationToken.None);
        _pauseTask = PauseCheckAsync(
            TimeSpan.FromSeconds(_settings.DeepPauseSeconds),
            _pauseCts.Token);
    }

    private async Task PauseCheckAsync(TimeSpan pause, CancellationToken ct)
    {
        try
        {
            await Task.Delay(pause, ct).ConfigureAwait(false);
            DateTimeOffset lastSpeech;
            lock (_gate) lastSpeech = _lastSpeechAt;
            if (!_started || DateTimeOffset.UtcNow - lastSpeech < pause) return;
            await RequestDeepAsync(DeepSynthesisTrigger.SpeechPause, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private bool HasProvisionalContent()
    {
        lock (_orchestrator.Scene.SyncRoot)
        {
            return _orchestrator.Scene.Nodes.Values.Any(
                       x => x.LifecycleState == ElementLifecycleState.Provisional) ||
                   _orchestrator.Scene.Edges.Values.Any(
                       x => x.LifecycleState == ElementLifecycleState.Provisional) ||
                   _orchestrator.Scene.Groups.Values.Any(
                       x => x.LifecycleState == ElementLifecycleState.Provisional);
        }
    }

    private TimeSpan EffectiveFastInterval(TimeSpan observed)
    {
        var configured = TimeSpan.FromSeconds(Math.Max(0, _settings.MinIntervalSeconds));
        if (observed <= TimeSpan.Zero) return configured;
        var adaptive = TimeSpan.FromMilliseconds(observed.TotalMilliseconds * 0.75);
        return adaptive > configured ? adaptive : configured;
    }

    private void RecordFailure(Exception ex)
    {
        var consecutive = Interlocked.Increment(ref _consecutiveFailures);
        Interlocked.Increment(ref _failures);
        var backoff = TimeSpan.FromMilliseconds(Math.Min(
            10_000, 250 * Math.Pow(2, Math.Min(consecutive - 1, 6))));
        var safeCode = SafeErrorCode(ex);
        lock (_gate)
        {
            _retryNotBefore = DateTimeOffset.UtcNow + backoff;
            _lastSafeErrorCode = safeCode;
            PublishRuntimeLocked(
                consecutive > 1 ? GenerationRuntimeStage.Error : GenerationRuntimeStage.Degraded,
                null);
        }
        _logger.LogWarning(
            "Continuous generation failed; category={Category} consecutiveFailures={Consecutive} retryInMs={RetryMs:F0}",
            safeCode, consecutive, backoff.TotalMilliseconds);
    }

    private void RecordUncommittedResult(DiagramGenerationResult result)
    {
        var consecutive = Interlocked.Increment(ref _consecutiveUncommittedResults);
        var backoff = TimeSpan.FromMilliseconds(Math.Min(
            10_000, 250 * Math.Pow(2, Math.Min(consecutive - 1, 6))));
        var safeCode = result.SafeErrorCode ?? "no_safe_application";
        lock (_gate)
        {
            var retryAt = DateTimeOffset.UtcNow + backoff;
            if (retryAt > _retryNotBefore) _retryNotBefore = retryAt;
            _lastSafeErrorCode = safeCode;
            _pendingFast = true;
            PublishRuntimeLocked(GenerationRuntimeStage.Degraded, GenerationMode.ContinuousExtraction);
        }
        _logger.LogWarning(
            "Continuous generation produced no committable application; category={Category} disposition={Disposition} retryInMs={RetryMs:F0}",
            safeCode, result.StaleDisposition, backoff.TotalMilliseconds);
    }

    public ContinuousDiagrammerDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            return new ContinuousDiagrammerDiagnostics(
                Interlocked.Read(ref _attempts),
                Interlocked.Read(ref _totalGenerations),
                Interlocked.Read(ref _failures),
                Volatile.Read(ref _consecutiveFailures),
                _lastSafeErrorCode,
                _committedCursor,
                _buffer.ReadAfter(_committedCursor).Segments.Count);
        }
    }

    private void PublishRuntime(
        GenerationRuntimeStage? requested = null,
        GenerationMode? mode = null)
    {
        ContinuousRuntimeSnapshot snapshot;
        lock (_gate) snapshot = PublishRuntimeLocked(requested, mode);
        Notify(RuntimeChanged, snapshot, "runtime_changed");
    }

    private ContinuousRuntimeSnapshot PublishRuntimeLocked(
        GenerationRuntimeStage? requested,
        GenerationMode? mode)
    {
        var slice = _buffer.ReadAfter(_committedCursor);
        var current = _buffer.CurrentCursor;
        var lag = slice.Segments.Count == 0
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - slice.Segments[0].End;
        if (lag < TimeSpan.Zero) lag = TimeSpan.Zero;
        var throughTimestamp = slice.Segments.LastOrDefault()?.End;
        var stage = requested ??
                    (_deepInFlight
                        ? GenerationRuntimeStage.DeepSynthesizing
                        : _fastInFlight
                            ? GenerationRuntimeStage.Extracting
                            : Volatile.Read(ref _consecutiveFailures) > 1
                                ? GenerationRuntimeStage.Error
                                : Volatile.Read(ref _consecutiveFailures) == 1
                                    ? GenerationRuntimeStage.Degraded
                                    : slice.Segments.Count >= _settings.MinNewSegments
                                        ? GenerationRuntimeStage.Behind
                                        : _started
                                            ? GenerationRuntimeStage.Current
                                            : GenerationRuntimeStage.Idle);
        _runtime = new ContinuousRuntimeSnapshot(
            stage,
            mode ?? _runtime.Mode,
            _committedCursor,
            current,
            _lastAttemptThrough,
            throughTimestamp,
            slice.Segments.Count,
            lag,
            _fastInFlight,
            _deepInFlight,
            DateTimeOffset.UtcNow,
            _lastSafeErrorCode);
        return _runtime;
    }

    private void CancelWake()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _wakeCts;
            _wakeCts = null;
            _wakeScheduled = false;
        }
        cts?.Cancel();
        cts?.Dispose();
    }

    private void CancelPause()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _pauseCts;
            _pauseCts = null;
        }
        cts?.Cancel();
        cts?.Dispose();
    }

    private static string SafeErrorCode(Exception ex) => ex switch
    {
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate_limited",
        HttpRequestException { StatusCode: { } status } when (int)status >= 500 => "service_failure",
        HttpRequestException => "network",
        _ => "generation_failure",
    };

    private void Notify<T>(EventHandler<T>? handler, T value, string eventName)
    {
        try { handler?.Invoke(this, value); }
        catch
        {
            _logger.LogWarning(
                "Continuous generation event observer failed; event={Event} category={Category}",
                eventName, "event_observer");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _wakeCts?.Dispose();
        _pauseCts?.Dispose();
        _cts?.Dispose();
    }
}

public enum DeepSynthesisTrigger
{
    SpeechPause,
    MeetingStop,
}

public sealed record ContinuousGenerationEvent(
    int SegmentsConsumed,
    DateTimeOffset At,
    GenerationMode Mode = GenerationMode.ContinuousExtraction,
    TranscriptCursor Through = default,
    DeepSynthesisTrigger? DeepTrigger = null);

public sealed record ContinuousRuntimeSnapshot(
    GenerationRuntimeStage Stage,
    GenerationMode? Mode,
    TranscriptCursor CommittedCursor,
    TranscriptCursor CurrentCursor,
    TranscriptCursor LastAttemptThrough,
    DateTimeOffset? CurrentThroughTimestamp,
    int PendingSegments,
    TimeSpan Lag,
    bool FastInFlight,
    bool DeepInFlight,
    DateTimeOffset UpdatedAt,
    string? SafeErrorCode)
{
    public static ContinuousRuntimeSnapshot Idle { get; } = new(
        GenerationRuntimeStage.Idle,
        null,
        default,
        default,
        default,
        null,
        0,
        TimeSpan.Zero,
        false,
        false,
        DateTimeOffset.MinValue,
        null);
}

public sealed record ContinuousDiagrammerDiagnostics(
    long Attempts,
    long Successes,
    long Failures,
    int ConsecutiveFailures,
    string? LastSafeErrorCode,
    TranscriptCursor CommittedCursor,
    int PendingSegments);
