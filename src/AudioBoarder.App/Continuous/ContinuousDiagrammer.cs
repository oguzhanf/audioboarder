using AudioBoarder.App.Configuration;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;
using AudioBoarder.Services.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AudioBoarder.App.Continuous;

/// <summary>
/// Subscribes to the audio pipeline's transcript stream and automatically
/// triggers DiagramOrchestrator.GenerateAsync against a FAST chat deployment
/// as the meeting progresses. No button-press required.
/// </summary>
public sealed class ContinuousDiagrammer : IAsyncDisposable
{
    private readonly AudioPipeline _pipeline;
    private readonly DiagramOrchestrator _orchestrator;
    private readonly RealtimeSettings _settings;
    private readonly ILogger<ContinuousDiagrammer> _logger;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private DateTimeOffset _lastGenerationAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDeepPassAt = DateTimeOffset.MinValue;
    private int _newSegmentsSinceLast;
    private bool _generationInFlight;
    private bool _pendingFollowup;
    private bool _wakeScheduled;
    private Task? _generationTask;
    private Task? _wakeTask;
    private long _totalGenerations;
    private bool _started;

    public event EventHandler<ContinuousGenerationEvent>? GenerationTriggered;
    public event EventHandler<ContinuousGenerationEvent>? GenerationCompleted;
    public event EventHandler<ContinuousGenerationEvent>? GenerationFailed;

    public ContinuousDiagrammer(
        AudioPipeline pipeline,
        DiagramOrchestrator orchestrator,
        IOptions<AudioBoarderSettings> settings,
        ILogger<ContinuousDiagrammer>? logger = null)
    {
        _pipeline = pipeline;
        _orchestrator = orchestrator;
        _settings = settings.Value.Realtime;
        _logger = logger ?? NullLogger<ContinuousDiagrammer>.Instance;
    }

    public bool IsRunning => _started;
    public long TotalGenerations => Interlocked.Read(ref _totalGenerations);
    public int PendingNewSegments => Volatile.Read(ref _newSegmentsSinceLast);
    public DateTimeOffset LastGenerationAt => _lastGenerationAt;

    public TimeSpan? TimeUntilNextEligible
    {
        get
        {
            if (_lastGenerationAt == DateTimeOffset.MinValue) return TimeSpan.Zero;
            var elapsed = DateTimeOffset.UtcNow - _lastGenerationAt;
            var interval = TimeSpan.FromSeconds(_settings.MinIntervalSeconds);
            return elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
        }
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
        Interlocked.Exchange(ref _newSegmentsSinceLast, 0);
        lock (_gate)
        {
            _generationInFlight = false;
            _pendingFollowup = false;
            _wakeScheduled = false;
        }
        _lastDeepPassAt = DateTimeOffset.UtcNow; // first deep pass fires after the interval
        _pipeline.SegmentEmitted += OnSegment;
        _started = true;
        _logger.LogInformation("Continuous diagrammer started (interval>={Interval}s, minSegs={MinSegs})",
            _settings.MinIntervalSeconds, _settings.MinNewSegments);
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _pipeline.SegmentEmitted -= OnSegment;
        _started = false;
        _cts?.Cancel();
        Task[] pending;
        lock (_gate)
        {
            pending = new[] { _generationTask, _wakeTask }
                .Where(t => t is not null)
                .Cast<Task>()
                .ToArray();
        }
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _logger.LogInformation("Continuous diagrammer stopped");
    }

    private void OnSegment(object? sender, TranscriptSegment segment)
    {
        Interlocked.Increment(ref _newSegmentsSinceLast);
        MaybeTrigger();
    }

    private void MaybeTrigger()
    {
        lock (_gate)
        {
            if (_generationInFlight)
            {
                _pendingFollowup = true;
                return;
            }
            if (Volatile.Read(ref _newSegmentsSinceLast) < _settings.MinNewSegments) return;
            var nextEligible = _lastGenerationAt + TimeSpan.FromSeconds(_settings.MinIntervalSeconds);
            var delay = nextEligible - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                if (!_wakeScheduled)
                {
                    _wakeScheduled = true;
                    var ct = _cts?.Token ?? CancellationToken.None;
                    _wakeTask = WakeWhenEligibleAsync(delay, ct);
                }
                return;
            }
            _generationInFlight = true;
            _pendingFollowup = false;
            var runToken = _cts?.Token ?? CancellationToken.None;
            _generationTask = Task.Run(() => RunOneAsync(runToken), CancellationToken.None);
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

        if (_started) MaybeTrigger();
    }

    private async Task RunOneAsync(CancellationToken ct)
    {
        // Snapshot the count we're about to act on — DON'T zero the counter yet.
        // If more segments arrive while this call is running we need to know to
        // schedule a follow-up; resetting prematurely would lose that signal.
        var segmentsConsumed = Volatile.Read(ref _newSegmentsSinceLast);
        var evt = new ContinuousGenerationEvent(segmentsConsumed, DateTimeOffset.UtcNow);

        // Periodically run a DEEP pass (smart model: groups + clean structure,
        // like Deep Refine) instead of a quick fast-model update — so the diagram
        // automatically reaches refined quality without the user pressing a button.
        var now = DateTimeOffset.UtcNow;
        var deep = _settings.DeepPassIntervalSeconds > 0
                   && (now - _lastDeepPassAt).TotalSeconds >= _settings.DeepPassIntervalSeconds
                   && _orchestrator.Scene.Nodes.Count > 0;

        try
        {
            GenerationTriggered?.Invoke(this, evt);
            await _orchestrator.GenerateAsync(
                userInstruction: null,
                layoutOptions: null,
                isContinuous: !deep,
                isAutomatic: true,
                ct: ct).ConfigureAwait(false);
            if (deep) _lastDeepPassAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _totalGenerations);
            GenerationCompleted?.Invoke(this, evt);
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Continuous generation failed");
            GenerationFailed?.Invoke(this, evt);
        }
        finally
        {
            // Do not retry a failed batch forever. Its transcript remains in the
            // rolling buffer and will be considered again when fresh speech arrives.
            // Subtract only the snapshotted count so segments received in-flight stay pending.
            Interlocked.Add(ref _newSegmentsSinceLast, -segmentsConsumed);
            _lastGenerationAt = DateTimeOffset.UtcNow;
            bool runAgain;
            lock (_gate)
            {
                _generationInFlight = false;
                // Re-evaluate eligibility immediately: if pending OR if new segments
                // accumulated past the threshold while we were busy, kick again.
                runAgain = _pendingFollowup
                           || Volatile.Read(ref _newSegmentsSinceLast) >= _settings.MinNewSegments;
                _pendingFollowup = false;
            }
            if (runAgain && _started) MaybeTrigger();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}

public sealed record ContinuousGenerationEvent(int SegmentsConsumed, DateTimeOffset At);
