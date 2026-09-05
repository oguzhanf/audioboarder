using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using AudioBoarder.App.Configuration;
using AudioBoarder.App.Continuous;
using AudioBoarder.App.Health;
using AudioBoarder.App.Sessions;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;
using AudioBoarder.Services.Audio;
using AudioBoarder.Services.Intent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly Auth.AzureSignInCoordinator _azureSignIn;
    private readonly AudioDeviceService _devices;
    private readonly ILogger<MainViewModel> _logger;
    private readonly bool _autoSave;
    private readonly DiagramIntentCoordinator _intentCoordinator;
    private readonly IUiStateStore _uiStateStore;
    private DispatcherTimer? _captureTimer;
    private DateTimeOffset _listenStartedAt;
    private DateTimeOffset? _latestCaptionTimestamp;
    private double _maxPeakObserved;
    private long _lastInterimUpdateAt;
    private CancellationTokenSource? _userEditSaveCts;
    private bool _syncingIntentSelection;

    public ObservableCollection<CaptionViewModel> Captions { get; } = new();
    public ObservableCollection<NoteViewModel> Notes { get; } = new();
    public ObservableCollection<HealthState> HealthStates { get; } = new();
    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();
    public SceneGraph Scene => _orchestrator.Scene;
    public DiagramIntent AppliedDiagramIntent => Scene.IntentState.AppliedIntent;
    public DiagramIntentSelectionMode IntentSelectionMode => Scene.IntentState.SelectionMode;
    public double IntentConfidence => Scene.IntentState.Confidence;
    public string IntentReason => Scene.IntentState.Reason;
    public DiagramIntent? SuggestedDiagramIntent => Scene.SuggestedIntentState?.AppliedIntent;
    public bool HasIntentSuggestion => Scene.SuggestedIntentState is not null;
    public string SuggestedIntentDisplay => SuggestedDiagramIntent is { } intent
        ? IntentOption.DisplayName(intent)
        : string.Empty;
    public string AppliedIntentDisplay => IntentOption.DisplayName(AppliedDiagramIntent);
    public string IntentModeDisplay => IntentSelectionMode == DiagramIntentSelectionMode.Auto
        ? IntentConfidence > 0
            ? $"Auto · {IntentConfidence:P0}"
            : "Auto"
        : "Pinned";
    public IReadOnlyList<IntentOption> IntentOptions { get; } = IntentOption.All;
    public int NoteCount => Notes.Count;
    public int CaptionCount => Captions.Count;

    [ObservableProperty] private string statusMessage = "Checking for updates…";
    [ObservableProperty] private UiRuntimeStatus runtimeStatus = UiRuntimeStatus.Initializing();
    [ObservableProperty] private bool isListening;
    [ObservableProperty] private bool isGenerating;
    [ObservableProperty] private string? refinementInstruction;
    [ObservableProperty] private int sceneRevision;
    [ObservableProperty] private bool isAudioReady;
    [ObservableProperty] private bool isTranscriptionReady;
    [ObservableProperty] private bool isAzureReady;
    [ObservableProperty] private HealthAction healthAction;
    [ObservableProperty] private bool isAzureSignInRequired;
    [ObservableProperty] private bool isAzureConfigurationRequired;
    [ObservableProperty] private bool isAzureRetryAvailable;
    [ObservableProperty] private bool isHealthPanelVisible = true;
    [ObservableProperty] private long continuousUpdates;
    [ObservableProperty] private double micLevel;
    [ObservableProperty] private AudioDeviceInfo? selectedInputDevice;
    [ObservableProperty] private string liveTranscript = "";
    [ObservableProperty] private string interimText = "";
    [ObservableProperty] private bool isTranscriptPaneOpen;
    [ObservableProperty] private bool isNotesPaneOpen;
    [ObservableProperty] private bool isReflowAllConfirmationVisible;
    [ObservableProperty] private IntentOption? selectedIntentOption;

    public bool IsRuntimeWarning => RuntimeStatus.IsWarning;
    public bool IsRuntimeError => RuntimeStatus.IsError;
    public bool IsRuntimeFaultVisible => RuntimeStatus.IsWarning || RuntimeStatus.IsError;

    /// <summary>Committed transcript plus the live in-progress hypothesis, so the
    /// caption panel updates word-by-word as you speak (Teams-style).</summary>
    public string TranscriptDisplay =>
        string.IsNullOrEmpty(InterimText) ? LiveTranscript
        : (string.IsNullOrEmpty(LiveTranscript) ? InterimText : LiveTranscript + " " + InterimText);

    public string LiveCaptionDisplay
    {
        get
        {
            var lines = Captions.TakeLast(2).Select(c => c.Text).ToList();
            if (!string.IsNullOrWhiteSpace(InterimText)) lines.Add(InterimText);
            return string.Join(Environment.NewLine, lines.TakeLast(3));
        }

    }

    partial void OnLiveTranscriptChanged(string value)
    {
        OnPropertyChanged(nameof(TranscriptDisplay));
        OnPropertyChanged(nameof(LiveCaptionDisplay));
    }

    partial void OnInterimTextChanged(string value)
    {
        OnPropertyChanged(nameof(TranscriptDisplay));
        OnPropertyChanged(nameof(LiveCaptionDisplay));
    }

    partial void OnRuntimeStatusChanged(UiRuntimeStatus value)
    {
        OnPropertyChanged(nameof(IsRuntimeWarning));
        OnPropertyChanged(nameof(IsRuntimeError));
        OnPropertyChanged(nameof(IsRuntimeFaultVisible));
    }

    partial void OnIsTranscriptPaneOpenChanged(bool value) => SaveUiState();
    partial void OnIsNotesPaneOpenChanged(bool value) => SaveUiState();

    partial void OnSelectedIntentOptionChanged(IntentOption? value)
    {
        if (_syncingIntentSelection || value is null) return;
        if (value.Intent is { } intent) PinDiagramIntent(intent);
        else UseAutomaticDiagramIntent();
    }

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
        Auth.AzureSignInCoordinator azureSignIn,
        AudioDeviceService devices,
        DiagramIntentCoordinator intentCoordinator,
        IUiStateStore uiStateStore,
        IOptions<AudioBoarderSettings> settings,
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
        _azureSignIn = azureSignIn;
        _devices = devices;
        _intentCoordinator = intentCoordinator;
        _uiStateStore = uiStateStore;
        _autoSave = settings.Value.Sessions.AutoSave;
        _logger = logger;

        if (settings.Value.DiagramIntent.SelectionMode == DiagramIntentSelectionMode.PinnedByUser)
            _intentCoordinator.Pin(Scene, settings.Value.DiagramIntent.PinnedIntent, "Pinned in app settings");

        var uiState = _uiStateStore.Load();
        isTranscriptPaneOpen = uiState.IsTranscriptPaneOpen;
        isNotesPaneOpen = uiState.IsNotesPaneOpen;
        SyncSelectedIntentOption();

        RefreshInputDevices();
        _pipeline.SegmentEmitted += (_, seg) => UiInvoke(() => OnSegment(seg));
        _pipeline.InterimEmitted += (_, seg) => OnInterim(seg);
        _pipeline.CaptureFailed += (_, err) => UiInvoke(() =>
        {
            RuntimeStatus = new UiRuntimeStatus(
                UiRuntimeState.Error,
                "Error",
                $"Capture fault ({err.Role}). Check the selected audio device.",
                IsWarning: true,
                IsError: true);
            StatusMessage = RuntimeStatus.Details;
        });
        _pipeline.DiagnosticsChanged += (_, diagnostics) => UiInvoke(() =>
        {
            if (IsListening) RefreshRuntimeStatus();
        });

        _orchestrator.GenerationStarted += (_, e) => UiInvoke(() =>
        {
            IsGenerating = true;
            SetRuntimeActivity(e.Mode == GenerationMode.ContinuousExtraction
                ? new UiRuntimeStatus(
                    UiRuntimeState.Analyzing,
                    $"Analyzing {Math.Max(1, _continuous.PendingNewSegments)} statements",
                    "Applying the next safe incremental update.")
                : new UiRuntimeStatus(
                    UiRuntimeState.DeepRefining,
                    "Deep refining",
                    "Consolidating the current architecture."));
            StatusMessage = e.Mode == GenerationMode.ContinuousExtraction
                ? $"Extracting diagram changes via {e.GeneratorName}…"
                : $"Deeply synthesizing diagram via {e.GeneratorName}…";
            ToggleListenCommand.NotifyCanExecuteChanged();
            RefineDiagramCommand.NotifyCanExecuteChanged();
        });
        _orchestrator.GenerationCompleted += (_, e) => UiInvoke(() =>
        {
            IsGenerating = _orchestrator.RuntimeSnapshot.FastInFlight > 0 ||
                           _orchestrator.RuntimeSnapshot.DeepInFlight > 0;
            var skipped = e.Result.ApplyResult.OperationsSkipped;
            var budget = e.Result.BudgetResult;
            StatusMessage = budget is { IsWithinBudget: false }
                ? $"Updated with budget warning: {budget.RemainingNodeOverage} locked node(s) and {budget.RemainingNoteOverage} note(s) remain over the configured cap."
                : skipped == 0
                    ? $"Updated: {e.Result.ApplyResult.OperationsApplied} ops in {e.Result.Response.Elapsed.TotalMilliseconds:F0} ms."
                    : $"Updated with warnings: {e.Result.ApplyResult.OperationsApplied} ops, {skipped} rejected.";
            RefreshNotes();
            SceneRevision = Scene.Revision;
            NotifyIntentStateChanged();
            RefreshRuntimeStatus();
            SceneInvalidated?.Invoke(this, EventArgs.Empty);
            // Snapshot on the UI thread (Clone locks SyncRoot) then save off-thread,
            // so the serializer never enumerates the live graph while a background
            // patch mutates it.
            if (_autoSave)
            {
                var snapshot = Scene.Clone();
                _ = SaveSnapshotAsync(snapshot);
            }
            RefineDiagramCommand.NotifyCanExecuteChanged();
            ExportPngCommand.NotifyCanExecuteChanged();
            ExportExcalidrawCommand.NotifyCanExecuteChanged();
        });
        _orchestrator.GenerationFailed += (_, e) => UiInvoke(() =>
        {
            IsGenerating = _orchestrator.RuntimeSnapshot.FastInFlight > 0 ||
                           _orchestrator.RuntimeSnapshot.DeepInFlight > 0;
            StatusMessage = e.Error is OperationCanceledException
                ? "Diagram update cancelled."
                : "Diagram update failed. The pending transcript will be retried safely.";
            RuntimeStatus = e.Error is OperationCanceledException
                ? new UiRuntimeStatus(UiRuntimeState.Degraded, "Degraded", StatusMessage, IsWarning: true)
                : new UiRuntimeStatus(
                    UiRuntimeState.Error, "Error", StatusMessage, IsWarning: true, IsError: true);
            ToggleListenCommand.NotifyCanExecuteChanged();
            RefineDiagramCommand.NotifyCanExecuteChanged();
        });
        _orchestrator.ImageUpdated += (_, _) => UiInvoke(() =>
        {
            SceneRevision = Scene.Revision;
            NotifyIntentStateChanged();
            SceneInvalidated?.Invoke(this, EventArgs.Empty);
            if (_autoSave)
                _ = SaveSnapshotAsync(Scene.Clone());
        });
        _orchestrator.RuntimeChanged += (_, runtime) => UiInvoke(() =>
        {
            IsGenerating = runtime.Stage is GenerationRuntimeStage.Queued or
                GenerationRuntimeStage.Extracting or GenerationRuntimeStage.DeepSynthesizing;
            if (IsListening) RefreshRuntimeStatus();
            ToggleListenCommand.NotifyCanExecuteChanged();
            RefineDiagramCommand.NotifyCanExecuteChanged();
        });

        _continuous.GenerationTriggered += (_, e) => UiInvoke(() =>
        {
            SetRuntimeActivity(e.Mode == GenerationMode.ContinuousExtraction
                ? new UiRuntimeStatus(
                    UiRuntimeState.Analyzing,
                    $"Analyzing {e.SegmentsConsumed} statements",
                    "Queued captions are being applied to the canvas.")
                : new UiRuntimeStatus(
                    UiRuntimeState.DeepRefining,
                    "Deep refining",
                    "Consolidating the current architecture."));
            StatusMessage = RuntimeStatus.Details;
        });
        _continuous.GenerationCompleted += (_, _) => UiInvoke(() => ContinuousUpdates = _continuous.TotalGenerations);
        _continuous.GenerationFailed += (_, _) => UiInvoke(() => { /* status set by orchestrator failed handler */ });
        _continuous.RuntimeChanged += (_, runtime) => UiInvoke(() =>
        {
            if (!IsListening) return;
            RefreshRuntimeStatus();
            StatusMessage = RuntimeStatus.Details;
        });

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
                // A successfully initialized fallback (Azure Speech or local
                // Whisper) is intentionally Degraded, but it is still fully usable.
                IsTranscriptionReady =
                    state.IsReady || state.Status == ComponentStatus.Degraded;
                break;
            case StartupHealthService.LlmKey:
                IsAzureReady = state.IsReady;
                HealthAction = state.Action;
                var visibility = MapHealthAction(state.Action);
                IsAzureSignInRequired = visibility.ShowSignIn;
                IsAzureConfigurationRequired = visibility.ShowConfigure;
                IsAzureRetryAvailable = visibility.ShowRetry;
                break;
        }

        var anyChecking = _health.States.Values.Any(s => s.Status == ComponentStatus.Checking);
        var actionRequired = _health.States.Values.FirstOrDefault(s =>
            s.Status == ComponentStatus.ActionRequired);
        var anyFailed = _health.States.Values.Any(s =>
            s.Status is ComponentStatus.Failed or ComponentStatus.RateLimited);
        if (IsListening)
        {
            RefreshRuntimeStatus();
            StatusMessage = RuntimeStatus.Details;
        }
        else if (anyChecking)
        {
            RuntimeStatus = UiRuntimeStatus.Initializing("Health checks running…");
            StatusMessage = RuntimeStatus.Details;
        }
        else if (actionRequired is not null)
        {
            RuntimeStatus = new UiRuntimeStatus(
                UiRuntimeState.Degraded,
                "Action required",
                actionRequired.Detail,
                IsWarning: true);
            StatusMessage = RuntimeStatus.Details;
        }
        else if (anyFailed)
        {
            RuntimeStatus = new UiRuntimeStatus(
                UiRuntimeState.Degraded,
                "Degraded",
                "Some components are unavailable. Review component health.",
                IsWarning: true);
            StatusMessage = RuntimeStatus.Details;
        }
        else if (IsAudioReady && IsTranscriptionReady && IsAzureReady)
        {
            RuntimeStatus = UiRuntimeStatus.Ready();
            StatusMessage = RuntimeStatus.Details;
        }

        ToggleListenCommand.NotifyCanExecuteChanged();
        RefineDiagramCommand.NotifyCanExecuteChanged();
        ImportTranscriptCommand.NotifyCanExecuteChanged();
    }

    internal static AzureHealthActionVisibility MapHealthAction(HealthAction action) => action switch
    {
        HealthAction.SignIn => new(true, false, false),
        HealthAction.Configure => new(false, true, false),
        HealthAction.Retry => new(false, false, true),
        _ => new(false, false, false),
    };

    public bool CanListen => IsListening || (!IsGenerating && IsAudioReady && IsTranscriptionReady);
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
                await _pipeline.StopAsync(CancellationToken.None);
                await _continuous.StopAsync(synthesizeDeep: true);
                IsListening = false;
                InterimText = "";
                RuntimeStatus = UiRuntimeStatus.Ready(
                    $"{Captions.Count} captions · {ContinuousUpdates} canvas updates.");
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
                RuntimeStatus = new UiRuntimeStatus(
                    UiRuntimeState.Listening,
                    "Listening",
                    "Waiting for the first caption.");
                StatusMessage = RuntimeStatus.Details;
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
            RuntimeStatus = new UiRuntimeStatus(
                UiRuntimeState.Error, "Error", StatusMessage, IsWarning: true, IsError: true);
            IsListening = false;
        }
    }

    public async Task PrepareForUpdateAsync(bool forceSave = false)
    {
        if (IsListening)
            await ToggleListenAsync();

        var pendingSave = Interlocked.Exchange(ref _userEditSaveCts, null);
        if (pendingSave is not null)
        {
            try { pendingSave.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        if (_autoSave || forceSave)
            await _sessions.SaveAsync(Scene.Clone());
    }

    private void RefreshListenStatus()
    {
        if (!IsListening) return;
        var chunks = _pipeline.ChunksReceived;
        var segments = _pipeline.SegmentsEmitted;
        var pending = _continuous.PendingNewSegments;
        var totalUpdates = _continuous.TotalGenerations;
        var nextEligible = _continuous.TimeUntilNextEligible;
        var listeningFor = DateTimeOffset.UtcNow - _listenStartedAt;
        var peak = _pipeline.RecentPeakAmplitude;
        if (peak > _maxPeakObserved) _maxPeakObserved = peak;
        // Live meter (0..100), gently amplified so normal speech fills the bar.
        MicLevel = Math.Min(100.0, peak * 400.0);

        RefreshRuntimeStatus();

        if (listeningFor > TimeSpan.FromSeconds(3) && chunks == 0)
        {
            RuntimeStatus = new UiRuntimeStatus(
                UiRuntimeState.Degraded,
                "Degraded",
                $"No microphone signal. {DescribeSilentMic()}",
                IsWarning: true);
            StatusMessage = RuntimeStatus.Details;
            return;
        }

        if (segments == 0)
        {
            if (_maxPeakObserved >= 0.006)
                StatusMessage = "Audio is active; waiting for a finalized caption.";
            else if (listeningFor > TimeSpan.FromSeconds(6))
                StatusMessage = DescribeSilentMic();
            else
                StatusMessage = "Capture is warming up.";
        }
        else
        {
            var nextStr = nextEligible.HasValue && nextEligible.Value > TimeSpan.Zero
                ? $"next update in ~{nextEligible.Value.TotalSeconds:F0}s"
                : $"next update when {Math.Max(0, 3 - pending)} more caption(s) arrive";
            StatusMessage = $"{segments} captions · {totalUpdates} canvas updates · {nextStr}.";
        }

        RefineDiagramCommand.NotifyCanExecuteChanged();
        ExportPngCommand.NotifyCanExecuteChanged();
        ExportExcalidrawCommand.NotifyCanExecuteChanged();
    }

    private void RefreshRuntimeStatus()
    {
        RuntimeStatus = UiRuntimeStatusMapper.Map(
            _pipeline.Diagnostics,
            _continuous.RuntimeSnapshot,
            IsListening,
            DateTimeOffset.UtcNow,
            _latestCaptionTimestamp);
    }

    private void SetRuntimeActivity(UiRuntimeStatus activity)
    {
        if (!IsListening)
        {
            RuntimeStatus = activity;
            return;
        }

        var observed = UiRuntimeStatusMapper.Map(
            _pipeline.Diagnostics,
            _continuous.RuntimeSnapshot,
            true,
            DateTimeOffset.UtcNow,
            _latestCaptionTimestamp);
        RuntimeStatus = observed.State is
            UiRuntimeState.Error or
            UiRuntimeState.RateLimited or
            UiRuntimeState.AudioGap or
            UiRuntimeState.Retrying
            ? observed
            : activity;
    }

    private async Task SaveSnapshotAsync(SceneGraph snapshot)
    {
        try
        {
            await _sessions.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Autosave failed; category={Category}",
                ex is UnauthorizedAccessException ? "access_denied" : "io_failure");
            UiInvoke(() => StatusMessage = "Autosave failed. The current board is still open but was not written to disk.");
        }
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
        _logger.LogInformation("Microphone selection changed");

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
            await _orchestrator.GenerateAsync(
                RefinementInstruction,
                mode: GenerationMode.ManualRefine);
        }
        catch (Exception ex)
        {
            _logger.LogError("Refine failed; category={Category}",
                ex is HttpRequestException ? "model_request_failure" : "generation_failure");
        }
    }

    public bool CanImportTranscript => !IsGenerating && !IsListening && IsAzureReady;

    /// <summary>
    /// Builds a diagram from an exported meeting transcript — no audio, no live
    /// capture. This is how you get a board out of a Teams/Zoom meeting that already
    /// happened, and it sidesteps transcription cost and rate limits entirely.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportTranscript))]
    public async Task ImportTranscriptAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a meeting transcript",
            Filter = TranscriptImporter.FileFilter,
            CheckFileExists = true,
        };
        // Own the dialog, otherwise it can open behind the main window.
        if (dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) != true) return;

        try
        {
            StatusMessage = "Reading transcript…";
            var content = await File.ReadAllTextAsync(dialog.FileName);
            var segments = TranscriptImporter.Parse(content);
            if (segments.Count == 0)
            {
                StatusMessage = "That file didn't contain any readable transcript text.";
                return;
            }

            // Replace rather than append: importing is "diagram THIS meeting", so
            // mixing it with whatever was already buffered would blur two meetings.
            _orchestrator.Clear();
            _buffer.Clear();
            _continuous.ResetTranscriptProgress();
            Captions.Clear();
            LiveTranscript = "";
            foreach (var segment in segments)
            {
                _buffer.Append(segment);
                Captions.Add(new CaptionViewModel(segment));
            }
            _latestCaptionTimestamp = segments[^1].End;
            OnPropertyChanged(nameof(CaptionCount));
            OnPropertyChanged(nameof(LiveCaptionDisplay));
            LiveTranscript = string.Join(" ", segments.Select(s => s.Text));
            if (LiveTranscript.Length > 40000) LiveTranscript = LiveTranscript[^40000..];

            var name = Path.GetFileName(dialog.FileName);
            StatusMessage = $"Read {segments.Count} lines from {name}. Building the diagram…";

            IsGenerating = true;
            ImportTranscriptCommand.NotifyCanExecuteChanged();
            // Deep pass: an imported transcript is complete, so there is no reason to
            // use the fast incremental path meant for live speech.
            await _orchestrator.GenerateAsync(
                "Diagram this complete meeting transcript.",
                mode: GenerationMode.DeepSynthesis);
            _continuous.ResetTranscriptProgress();
            StatusMessage = $"Diagram built from {name}.";
        }
        catch (Exception ex)
        {
            _logger.LogError("Transcript import failed; category={Category}",
                ex is HttpRequestException ? "model_request_failure" : "transcript_import_failure");
            StatusMessage = "Could not import and diagram that transcript. Check the file and model connection.";
        }
        finally
        {
            IsGenerating = false;
            ImportTranscriptCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    public async Task ClearSceneAsync()
    {
        _orchestrator.Clear();
        _buffer.Clear();
        _continuous.ResetTranscriptProgress();
        Notes.Clear();
        Captions.Clear();
        _latestCaptionTimestamp = null;
        LiveTranscript = "";
        InterimText = "";
        OnPropertyChanged(nameof(CaptionCount));
        OnPropertyChanged(nameof(NoteCount));
        SceneRevision = Scene.Revision;
        NotifyIntentStateChanged();
        SceneInvalidated?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Scene cleared.";
        await _sessions.ClearAsync();
        ExportPngCommand.NotifyCanExecuteChanged();
        ExportExcalidrawCommand.NotifyCanExecuteChanged();
        RefineDiagramCommand.NotifyCanExecuteChanged();
    }

    public void RestoreSession(SessionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _sessions.Apply(Scene, payload);
        // Repair only the exact undersized defaults produced by the first library
        // release; keep positions and every other saved/user-defined size unchanged.
        var repairedDrops = MicrosoftComponentCatalog.RepairLegacyDropSizes(Scene);
        // Only legacy v0 sessions need geometry modernization. V1 geometry is an
        // explicit user/session field and must round-trip without being overwritten.
        if (payload.WasMigratedFromV0)
            _orchestrator.Relayout();
        // The user explicitly chose to bring this board back, so the size cap must
        // not quietly delete most of it on the next automatic pass.
        _orchestrator.RaiseBudgetFloorToCurrentScene();
        RefreshNotes();
        SceneRevision = Scene.Revision;
        NotifyIntentStateChanged();
        SceneInvalidated?.Invoke(this, EventArgs.Empty);
        ExportPngCommand.NotifyCanExecuteChanged();
        ExportExcalidrawCommand.NotifyCanExecuteChanged();
        RefineDiagramCommand.NotifyCanExecuteChanged();
        StatusMessage = $"Restored session from {payload.SavedAt.LocalDateTime:g}.";
        if (repairedDrops > 0) NotifyUserSceneEdited();
    }

    public void NotifyUserSceneEdited()
    {
        SceneRevision = Scene.Revision;
        if (!_autoSave) return;

        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _userEditSaveCts, next);
        if (previous is not null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _ = SaveUserEditAfterDelayAsync(next);
    }

    [RelayCommand]
    public void ReflowUnpinned()
    {
        _orchestrator.ReflowUnpinned();
        SceneRevision = Scene.Revision;
        SceneInvalidated?.Invoke(this, EventArgs.Empty);
        NotifyUserSceneEdited();
        StatusMessage = "Reflowed unpinned nodes.";
    }

    public void ReflowAll()
    {
        _orchestrator.ReflowAll();
        SceneRevision = Scene.Revision;
        SceneInvalidated?.Invoke(this, EventArgs.Empty);
        NotifyUserSceneEdited();
        StatusMessage = "Reflowed all nodes.";
    }

    [RelayCommand]
    public void RequestReflowAll() => IsReflowAllConfirmationVisible = true;

    [RelayCommand]
    public void ConfirmReflowAll()
    {
        ReflowAll();
        IsReflowAllConfirmationVisible = false;
    }

    [RelayCommand]
    public void CancelReflowAll() => IsReflowAllConfirmationVisible = false;

    [RelayCommand]
    public void ToggleTranscriptPane() => IsTranscriptPaneOpen = !IsTranscriptPaneOpen;

    [RelayCommand]
    public void ToggleNotesPane() => IsNotesPaneOpen = !IsNotesPaneOpen;

    [RelayCommand]
    public void DismissRuntimeFault()
    {
        RuntimeStatus = IsListening
            ? new UiRuntimeStatus(UiRuntimeState.Listening, "Listening", "Runtime notice dismissed.")
            : UiRuntimeStatus.Ready();
    }

    public void PinDiagramIntent(DiagramIntent intent)
    {
        _intentCoordinator.Pin(Scene, intent);
        NotifyIntentStateChanged();
        NotifyUserSceneEdited();
    }

    public void UseAutomaticDiagramIntent()
    {
        _intentCoordinator.UseAuto(Scene);
        NotifyIntentStateChanged();
        NotifyUserSceneEdited();
    }

    public bool ApplyIntentSuggestion()
    {
        var applied = _intentCoordinator.ApplySuggestion(Scene);
        if (applied)
        {
            NotifyIntentStateChanged();
            NotifyUserSceneEdited();
        }
        return applied;
    }

    public bool RejectIntentSuggestion()
    {
        var rejected = _intentCoordinator.RejectSuggestion(Scene);
        if (rejected)
        {
            NotifyIntentStateChanged();
            NotifyUserSceneEdited();
        }
        return rejected;
    }

    [RelayCommand]
    private void AcceptIntentSuggestion() => ApplyIntentSuggestion();

    [RelayCommand]
    private void DismissIntentSuggestion() => RejectIntentSuggestion();

    private void NotifyIntentStateChanged()
    {
        OnPropertyChanged(nameof(AppliedDiagramIntent));
        OnPropertyChanged(nameof(IntentSelectionMode));
        OnPropertyChanged(nameof(IntentConfidence));
        OnPropertyChanged(nameof(IntentReason));
        OnPropertyChanged(nameof(SuggestedDiagramIntent));
        OnPropertyChanged(nameof(HasIntentSuggestion));
        OnPropertyChanged(nameof(SuggestedIntentDisplay));
        OnPropertyChanged(nameof(AppliedIntentDisplay));
        OnPropertyChanged(nameof(IntentModeDisplay));
        SyncSelectedIntentOption();
    }

    private void SyncSelectedIntentOption()
    {
        _syncingIntentSelection = true;
        SelectedIntentOption = IntentSelectionMode == DiagramIntentSelectionMode.Auto
            ? IntentOptions[0]
            : IntentOptions.First(option => option.Intent == AppliedDiagramIntent);
        _syncingIntentSelection = false;
    }

    private void SaveUiState() =>
        _uiStateStore.Save(new UiStateSnapshot(IsTranscriptPaneOpen, IsNotesPaneOpen));

    private async Task SaveUserEditAfterDelayAsync(CancellationTokenSource delayCts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), delayCts.Token);
            await _sessions.SaveAsync(Scene.Clone(), delayCts.Token);
        }
        catch (OperationCanceledException) when (delayCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Autosave after edit failed; category={Category}",
                ex is UnauthorizedAccessException ? "access_denied" : "io_failure");
            UiInvoke(() => StatusMessage = "Autosave failed. The current board is still open but was not written to disk.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _userEditSaveCts, null, delayCts);
            delayCts.Dispose();
        }
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
            var (ok, msg) = await _azureSignIn.SignInAndRefreshAsync(CancellationToken.None);
            _logger.LogInformation("Sign-in command returned ok={Ok}", ok);
            StatusMessage = ok ? msg : $"Sign-in failed: {msg}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-signin health refresh failed");
            StatusMessage = "Signed in, but Azure health checks could not be refreshed. Retry health checks.";
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
        _latestCaptionTimestamp = segment.End;
        OnPropertyChanged(nameof(CaptionCount));
        OnPropertyChanged(nameof(LiveCaptionDisplay));
        RefreshRuntimeStatus();
    }

    private void OnInterim(TranscriptSegment segment)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastInterimUpdateAt);
        if (now - previous < 100 ||
            Interlocked.CompareExchange(ref _lastInterimUpdateAt, now, previous) != previous)
            return;
        UiInvoke(() => InterimText = segment.Text?.Trim() ?? "");
    }

    private void RefreshNotes()
    {
        Notes.Clear();
        List<NoteViewModel> snapshot;
        lock (Scene.SyncRoot)
        {
            snapshot = Scene.Notes.Values
                .Select(n => new NoteViewModel(n))
                .OrderBy(n => n.KindRank)
                .ThenByDescending(n => n.Time, StringComparer.Ordinal)
                .ToList();
        }
        foreach (var nvm in snapshot) Notes.Add(nvm);
        OnPropertyChanged(nameof(NoteCount));
    }

    private static void UiInvoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

public sealed record IntentOption(string Label, DiagramIntent? Intent)
{
    public static IReadOnlyList<IntentOption> All { get; } =
    [
        new("Auto", null),
        new(DisplayName(DiagramIntent.SoftwareSystemArchitecture), DiagramIntent.SoftwareSystemArchitecture),
        new(DisplayName(DiagramIntent.SaaSMultiTenantArchitecture), DiagramIntent.SaaSMultiTenantArchitecture),
        new(DisplayName(DiagramIntent.SecurityZeroTrustArchitecture), DiagramIntent.SecurityZeroTrustArchitecture),
        new(DisplayName(DiagramIntent.CloudNetworkArchitecture), DiagramIntent.CloudNetworkArchitecture),
        new(DisplayName(DiagramIntent.IntegrationDataFlowArchitecture), DiagramIntent.IntegrationDataFlowArchitecture),
        new(DisplayName(DiagramIntent.DiscussionSummary), DiagramIntent.DiscussionSummary),
    ];

    public static string DisplayName(DiagramIntent intent) => intent switch
    {
        DiagramIntent.SoftwareSystemArchitecture => "Software Architecture",
        DiagramIntent.SaaSMultiTenantArchitecture => "SaaS Multi-tenant",
        DiagramIntent.SecurityZeroTrustArchitecture => "Security Architecture",
        DiagramIntent.CloudNetworkArchitecture => "Cloud Network",
        DiagramIntent.IntegrationDataFlowArchitecture => "Integration Data Flow",
        DiagramIntent.DiscussionSummary => "Discussion Summary",
        _ => intent.ToString(),
    };
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
        KindDisplay = Humanize(note.Kind);
        KindRank = Rank(note.Kind);
        Text = note.Text;
        Owner = note.Owner ?? string.Empty;
        Time = note.SourceTimestamp?.LocalDateTime.ToString("HH:mm:ss") ?? string.Empty;
    }

    public string Kind { get; }
    public string KindDisplay { get; }
    public int KindRank { get; }
    public string Text { get; }
    public string Owner { get; }
    public string Time { get; }
    public bool HasOwner => !string.IsNullOrWhiteSpace(Owner);

    // What the meeting committed to reads before what it merely mentioned.
    private static int Rank(NoteKind kind) => kind switch
    {
        NoteKind.ActionItem => 0,
        NoteKind.Decision => 1,
        NoteKind.Risk => 2,
        NoteKind.Question => 3,
        _ => 4,
    };

    private static string Humanize(NoteKind kind) => kind switch
    {
        NoteKind.ActionItem => "Action items",
        NoteKind.Decision => "Decisions",
        NoteKind.Risk => "Risks",
        NoteKind.Question => "Open questions",
        _ => "Notes",
    };
}

internal readonly record struct AzureHealthActionVisibility(
    bool ShowSignIn,
    bool ShowConfigure,
    bool ShowRetry);
