using System.Collections.ObjectModel;
using System.Windows.Threading;
using AudioBoarder.App.Continuous;
using AudioBoarder.App.Health;
using AudioBoarder.App.Sessions;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;
using AudioBoarder.Services.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AudioBoarder.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DiagramOrchestrator _orchestrator;
    private readonly AudioPipeline _pipeline;
    private readonly TranscriptBuffer _buffer;
    private readonly SessionStore _sessions;
    private readonly Export.DiagramExporter _exporter;
    private readonly Export.ExcalidrawExporter _excalidrawExporter;
    private readonly StartupHealthService _health;
    private readonly ContinuousDiagrammer _continuous;
    private readonly Auth.AzureCredentialProvider _credentials;
    private readonly AudioDeviceService _devices;
    private readonly ILogger<MainViewModel> _logger;
    private DispatcherTimer? _captureTimer;
    private DateTimeOffset _listenStartedAt;
    private double _maxPeakObserved;

    public ObservableCollection<CaptionViewModel> Captions { get; } = new();
    public ObservableCollection<NoteViewModel> Notes { get; } = new();
    public ObservableCollection<HealthState> HealthStates { get; } = new();
    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();
    public SceneGraph Scene => _orchestrator.Scene;

    [ObservableProperty] private string statusMessage = "Initializing…";
    [ObservableProperty] private bool isListening;
    [ObservableProperty] private bool isGenerating;
    [ObservableProperty] private string? refinementInstruction;
    [ObservableProperty] private int sceneRevision;
    [ObservableProperty] private bool isAudioReady;
    [ObservableProperty] private bool isTranscriptionReady;
    [ObservableProperty] private bool isAzureReady;
    [ObservableProperty] private bool isHealthPanelVisible = true;
    [ObservableProperty] private long continuousUpdates;
    [ObservableProperty] private double micLevel;
    [ObservableProperty] private AudioDeviceInfo? selectedInputDevice;
    [ObservableProperty] private string liveTranscript = "";
    [ObservableProperty] private string interimText = "";

    /// <summary>When true the central canvas is the live Excalidraw whiteboard;
    /// when false it falls back to the classic SkiaSharp renderer.</summary>
    [ObservableProperty] private bool showWhiteboard = true;
    public bool ShowClassicView => !ShowWhiteboard;
    partial void OnShowWhiteboardChanged(bool value) => OnPropertyChanged(nameof(ShowClassicView));

    /// <summary>Committed transcript plus the live in-progress hypothesis, so the
    /// caption panel updates word-by-word as you speak (Teams-style).</summary>
    public string TranscriptDisplay =>
        string.IsNullOrEmpty(InterimText) ? LiveTranscript
        : (string.IsNullOrEmpty(LiveTranscript) ? InterimText : LiveTranscript + " " + InterimText);

    partial void OnLiveTranscriptChanged(string value) => OnPropertyChanged(nameof(TranscriptDisplay));
    partial void OnInterimTextChanged(string value) => OnPropertyChanged(nameof(TranscriptDisplay));

    public event EventHandler? SceneInvalidated;

    public MainViewModel(
        DiagramOrchestrator orchestrator,
        AudioPipeline pipeline,
        TranscriptBuffer buffer,
        SessionStore sessions,
        Export.DiagramExporter exporter,
        Export.ExcalidrawExporter excalidrawExporter,
        StartupHealthService health,
        ContinuousDiagrammer continuous,
        Auth.AzureCredentialProvider credentials,
        AudioDeviceService devices,
        ILogger<MainViewModel> logger)
    {
        _orchestrator = orchestrator;
        _pipeline = pipeline;
        _buffer = buffer;
        _sessions = sessions;
        _exporter = exporter;
        _excalidrawExporter = excalidrawExporter;
        _health = health;
        _continuous = continuous;
        _credentials = credentials;
        _devices = devices;
        _logger = logger;

        RefreshInputDevices();
        _pipeline.SegmentEmitted += (_, seg) => UiInvoke(() => OnSegment(seg));
        _pipeline.InterimEmitted += (_, seg) => UiInvoke(() => InterimText = seg.Text?.Trim() ?? "");
        _pipeline.CaptureFailed += (_, err) => UiInvoke(() => StatusMessage = $"Capture error ({err.Role}): {err.Message}");

        _orchestrator.GenerationStarted += (_, e) => UiInvoke(() =>
        {
            IsGenerating = true;
            StatusMessage = $"Updating diagram via {e.GeneratorName}…";
            ToggleListenCommand.NotifyCanExecuteChanged();
            RefineDiagramCommand.NotifyCanExecuteChanged();
        });
        _orchestrator.GenerationCompleted += (_, e) => UiInvoke(async () =>
        {
            IsGenerating = false;
            StatusMessage = $"Updated: {e.Result.ApplyResult.OperationsApplied} ops in {e.Result.Response.Elapsed.TotalMilliseconds:F0} ms.";
            RefreshNotes();
            SceneRevision = Scene.Revision;
            SceneInvalidated?.Invoke(this, EventArgs.Empty);
            // Snapshot on the UI thread (Clone locks SyncRoot) then save off-thread,
            // so the serializer never enumerates the live graph while a background
            // patch mutates it.
            var snapshot = Scene.Clone();
            _ = Task.Run(() => _sessions.SaveAsync(snapshot));
            RefineDiagramCommand.NotifyCanExecuteChanged();
            ExportPngCommand.NotifyCanExecuteChanged();
            ExportExcalidrawCommand.NotifyCanExecuteChanged();
            await Task.CompletedTask;
        });
        _orchestrator.GenerationFailed += (_, e) => UiInvoke(() =>
        {
            IsGenerating = false;
            StatusMessage = $"Update failed: {e.Error.Message}";
            RefineDiagramCommand.NotifyCanExecuteChanged();
        });

        _continuous.GenerationTriggered += (_, e) => UiInvoke(() =>
            StatusMessage = $"Continuous: triggering update after {e.SegmentsConsumed} new caption(s)…");
        _continuous.GenerationCompleted += (_, _) => UiInvoke(() => ContinuousUpdates = _continuous.TotalGenerations);
        _continuous.GenerationFailed += (_, _) => UiInvoke(() => { /* status set by orchestrator failed handler */ });

        _health.StateChanged += (_, state) => UiInvoke(() => UpdateHealth(state));
        foreach (var s in _health.States.Values) UpdateHealth(s);
    }

    private void UpdateHealth(HealthState state)
    {
        // Route by Key (stable subsystem id) so the pill updates in place even
        // when the title/detail changes (e.g. transcription flipping from local
        // Whisper to a cloud deployment after discovery completes).
        var key = string.IsNullOrEmpty(state.Key) ? state.Title : state.Key;
        var idx = HealthStates.ToList().FindIndex(s =>
            (string.IsNullOrEmpty(s.Key) ? s.Title : s.Key) == key);
        if (idx >= 0) HealthStates[idx] = state;
        else HealthStates.Add(state);

        switch (key)
        {
            case StartupHealthService.AudioKey:
                IsAudioReady = state.IsReady || state.Status == ComponentStatus.Degraded;
                break;
            case StartupHealthService.TranscriptionKey:
                IsTranscriptionReady = state.IsReady;
                break;
            case StartupHealthService.LlmKey:
                IsAzureReady = state.IsReady;
                break;
        }

        var anyChecking = _health.States.Values.Any(s => s.Status == ComponentStatus.Checking);
        var anyFailed = _health.States.Values.Any(s => s.Status == ComponentStatus.Failed);
        if (anyChecking) StatusMessage = "Health checks running…";
        else if (anyFailed) StatusMessage = "Some components unavailable. See health panel.";
        else if (IsAudioReady && IsTranscriptionReady && IsAzureReady) StatusMessage = "Ready — click Listen to start.";

        ToggleListenCommand.NotifyCanExecuteChanged();
        RefineDiagramCommand.NotifyCanExecuteChanged();
    }

    public bool CanListen => !IsGenerating && IsAudioReady && IsTranscriptionReady;
    public bool CanRefine => !IsGenerating && IsAzureReady && Scene.Nodes.Count > 0;
    public bool CanExport => Scene.Nodes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanListen))]
    public async Task ToggleListenAsync()
    {
        try
        {
            if (IsListening)
            {
                _captureTimer?.Stop();
                await _continuous.StopAsync();
                await _pipeline.StopAsync(CancellationToken.None);
                IsListening = false;
                InterimText = "";
                StatusMessage = $"Stopped. {Captions.Count} captions · {ContinuousUpdates} auto-updates.";
            }
            else
            {
                StatusMessage = "Starting capture…";
                await _pipeline.StartAsync(CancellationToken.None);
                IsListening = true;
                _listenStartedAt = DateTimeOffset.UtcNow;
                _maxPeakObserved = 0;
                _continuous.Start();
                StatusMessage = "Listening — diagram will auto-update as the conversation progresses.";
                _captureTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1),
                    DispatcherPriority.Background,
                    (_, _) => RefreshListenStatus(),
                    Dispatcher.CurrentDispatcher);
                _captureTimer.Start();
            }
            ToggleListenCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggle listen failed");
            StatusMessage = $"Listen failed: {ex.Message}";
            IsListening = false;
        }
    }

    private void RefreshListenStatus()
    {
        if (!IsListening) return;
        var chunks = _pipeline.ChunksReceived;
        var transcribed = _pipeline.ChunksTranscribed;
        var segments = _pipeline.SegmentsEmitted;
        var pending = _continuous.PendingNewSegments;
        var totalUpdates = _continuous.TotalGenerations;
        var nextEligible = _continuous.TimeUntilNextEligible;
        var listeningFor = DateTimeOffset.UtcNow - _listenStartedAt;
        var peak = _pipeline.RecentPeakAmplitude;
        if (peak > _maxPeakObserved) _maxPeakObserved = peak;
        // Live meter (0..100), gently amplified so normal speech fills the bar.
        MicLevel = Math.Min(100.0, peak * 400.0);

        if (IsGenerating)
            return; // GenerationStarted set the status

        // No-signal detection. Only the "no chunks at all" case is a hard error
        // (mic muted or grabbed by another app). A quiet mic that simply hasn't
        // heard speech yet gets a gentle hint — and once it has EVER registered
        // a real peak, we stop nagging entirely.
        if (listeningFor > TimeSpan.FromSeconds(3) && chunks == 0)
        {
            StatusMessage = $"⚠ No microphone signal. {DescribeSilentMic()}";
            return;
        }

        if (segments == 0)
        {
            var lvl = $"mic level {peak:P0}";
            if (_maxPeakObserved >= 0.006)
                StatusMessage = $"Listening · {lvl} · transcribing…";
            else if (listeningFor > TimeSpan.FromSeconds(6))
                StatusMessage = $"Listening · {lvl} · {DescribeSilentMic()}";
            else
                StatusMessage = $"Listening · {lvl} · warming up…";
        }
        else
        {
            var nextStr = nextEligible.HasValue && nextEligible.Value > TimeSpan.Zero
                ? $"next update in ~{nextEligible.Value.TotalSeconds:F0}s"
                : $"next update when {Math.Max(0, 3 - pending)} more caption(s) arrive";
            StatusMessage = $"Listening · mic {peak:P0} · {segments} captions · {totalUpdates} auto-updates · {nextStr}";
        }
        RefineDiagramCommand.NotifyCanExecuteChanged();
        ExportPngCommand.NotifyCanExecuteChanged();
        ExportExcalidrawCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Explains a flat-zero mic level. Checks the WINDOWS endpoint mute flag and
    /// names the offending device instead of guessing "your mic may be muted".
    /// Teams and headset vendor software (e.g. Poly) sync their mute button to this
    /// flag, so a user who is certain they never muted anything can still be
    /// capturing pure silence with no error anywhere.
    /// </summary>
    private string DescribeSilentMic()
    {
        try
        {
            var (muted, name) = _devices.GetCaptureMuteState();
            if (muted)
                return $"⚠ \"{name}\" is MUTED in Windows — Teams or your headset's mute button sets this. " +
                       "Unmute it in Sound settings › Input, or choose another device above.";
        }
        catch { /* fall through to the generic hint */ }
        return "no speech heard yet — just start talking. If the level stays at 0%, check the mic isn't muted.";
    }

    [RelayCommand]
    public void RefreshInputDevices()
    {
        AudioDeviceInfo? toSelect;
        try
        {
            var devices = _devices.GetInputDevices();
            InputDevices.Clear();
            foreach (var d in devices) InputDevices.Add(d);

            // Preserve current selection if it still exists, else follow default.
            toSelect = InputDevices.FirstOrDefault(d => d.Id == _devices.SelectedMicrophoneId)
                       ?? InputDevices.FirstOrDefault(d => d.Id == AudioDeviceService.DefaultId)
                       ?? InputDevices.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enumerating input devices failed");
            return;
        }
        if (!ReferenceEquals(SelectedInputDevice, toSelect))
            SelectedInputDevice = toSelect; // triggers OnSelectedInputDeviceChanged
    }

    partial void OnSelectedInputDeviceChanged(AudioDeviceInfo? value)
    {
        if (value is null) return;
        _devices.SelectedMicrophoneId = value.Id;
        _logger.LogInformation("Microphone selection set to {Name} ({Id})", value.Name, value.Id);

        // If we're already listening, restart capture so the new device takes
        // effect immediately without the user having to toggle Listen.
        if (IsListening)
            _ = RestartCaptureForDeviceChangeAsync();
    }

    private async Task RestartCaptureForDeviceChangeAsync()
    {
        try
        {
            StatusMessage = $"Switching microphone to {SelectedInputDevice?.Name}…";
            await _pipeline.StopAsync(CancellationToken.None);
            await _pipeline.StartAsync(CancellationToken.None);
            _listenStartedAt = DateTimeOffset.UtcNow;
            _maxPeakObserved = 0;
            StatusMessage = $"Listening on {SelectedInputDevice?.Name}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restart after mic change failed");
            StatusMessage = $"Could not switch microphone: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefine))]
    public async Task RefineDiagramAsync()
    {
        try
        {
            // Explicit user-driven refine uses the PRIMARY (deep) deployment.
            await _orchestrator.GenerateAsync(RefinementInstruction, isContinuous: false);
        }
        catch (Exception ex) { _logger.LogError(ex, "Refine failed"); }
    }

    [RelayCommand]
    public void ClearScene()
    {
        _orchestrator.Clear();
        Notes.Clear();
        Captions.Clear();
        LiveTranscript = "";
        InterimText = "";
        SceneRevision = Scene.Revision;
        SceneInvalidated?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Scene cleared.";
        _sessions.Clear();
        ExportPngCommand.NotifyCanExecuteChanged();
        ExportExcalidrawCommand.NotifyCanExecuteChanged();
        RefineDiagramCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    public void ExportPng()
    {
        try
        {
            var path = _exporter.ExportPng(Scene);
            if (path is not null) StatusMessage = $"Saved {path}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    public void ExportExcalidraw()
    {
        try
        {
            var path = _excalidrawExporter.Export(Scene);
            if (path is not null) StatusMessage = $"Saved {path} — open in Excalidraw to share & edit.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excalidraw export failed");
            StatusMessage = $"Excalidraw export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RetryHealthAsync()
    {
        StatusMessage = "Re-running health checks…";
        try { await _health.RunAllAsync(); }
        catch (Exception ex) { StatusMessage = $"Health probe failed: {ex.Message}"; }
    }

    private int _signInInFlight;

    [RelayCommand]
    public async Task SignInToAzureAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _signInInFlight, 1) == 1)
        {
            StatusMessage = "Sign-in already in progress — complete the browser prompt or cancel.";
            return;
        }
        try
        {
            StatusMessage = "Opening browser sign-in… complete it in your browser.";
            _logger.LogInformation("Sign-in command starting");
            var (ok, msg) = await _credentials.SignInInteractiveAsync(CancellationToken.None);
            _logger.LogInformation("Sign-in command returned ok={Ok}", ok);
            StatusMessage = ok ? msg : $"Sign-in failed: {msg}";
            if (ok)
            {
                // Run health refresh on a worker thread so the UI dispatcher stays free
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation("Triggering post-signin health refresh");
                        await _health.RunAllAsync();
                        _logger.LogInformation("Post-signin health refresh complete");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Post-signin health refresh failed");
                    }
                });
            }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _signInInFlight, 0);
        }
    }

    [RelayCommand]
    public void DismissHealthPanel() => IsHealthPanelVisible = false;

    private void OnSegment(TranscriptSegment segment)
    {
        var text = segment.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // A final result arrived — commit it and clear the live interim tail.
        InterimText = "";

        // One continuous flowing transcript (what the user asked for) rather than
        // a list of separate bubbles.
        LiveTranscript = string.IsNullOrEmpty(LiveTranscript) ? text : LiveTranscript + " " + text;
        if (LiveTranscript.Length > 40000) LiveTranscript = LiveTranscript[^40000..];

        Captions.Add(new CaptionViewModel(segment));
        while (Captions.Count > 200) Captions.RemoveAt(0);
    }

    private void RefreshNotes()
    {
        Notes.Clear();
        List<NoteViewModel> snapshot;
        lock (Scene.SyncRoot)
        {
            snapshot = Scene.Notes.Values
                .OrderByDescending(n => n.SourceTimestamp ?? DateTimeOffset.MinValue)
                .Select(n => new NoteViewModel(n))
                .ToList();
        }
        foreach (var nvm in snapshot) Notes.Add(nvm);
    }

    private static void UiInvoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

public sealed class CaptionViewModel
{
    public CaptionViewModel(TranscriptSegment seg)
    {
        Speaker = seg.Speaker == TranscriptSpeaker.Local ? "You" : "Remote";
        Text = seg.Text;
        Time = seg.End.LocalDateTime.ToString("HH:mm:ss");
    }
    public string Speaker { get; }
    public string Text { get; }
    public string Time { get; }
}

public sealed class NoteViewModel
{
    public NoteViewModel(SceneNote note)
    {
        Kind = note.Kind.ToString();
        Text = note.Text;
        Owner = note.Owner ?? string.Empty;
        Time = note.SourceTimestamp?.LocalDateTime.ToString("HH:mm:ss") ?? string.Empty;
    }
    public string Kind { get; }
    public string Text { get; }
    public string Owner { get; }
    public string Time { get; }
}
