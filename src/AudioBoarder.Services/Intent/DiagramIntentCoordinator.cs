using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Services.Intent;

/// <summary>
/// Owns the host-side intent state machine. Detection may update an empty scene,
/// but a populated scene receives an explicit suggestion that UI code can accept
/// or reject.
/// </summary>
public sealed class DiagramIntentCoordinator
{
    private readonly DiagramIntentDetector _detector;

    public DiagramIntentCoordinator(DiagramIntentDetector detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    public DiagramIntentDetection? Evaluate(
        SceneGraph scene,
        IReadOnlyList<TranscriptSegment> finalizedTranscript)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.IntentState.SelectionMode == DiagramIntentSelectionMode.PinnedByUser)
            return null;

        var detection = _detector.Detect(finalizedTranscript);
        if (detection is null) return null;

        lock (scene.SyncRoot)
        {
            var state = new DiagramIntentState(
                detection.Intent,
                DiagramIntentSelectionMode.Auto,
                detection.Confidence,
                detection.Evidence,
                scene.Revision);
            var hasGraphContent = scene.Nodes.Count > 0 || scene.Edges.Count > 0 ||
                                  scene.Groups.Count > 0 || scene.Notes.Count > 0 ||
                                  scene.Images.Count > 0;
            if (!hasGraphContent || detection.Intent == scene.IntentState.AppliedIntent)
            {
                scene.SetIntentState(state);
                scene.SetSuggestedIntentState(null);
            }
            else
            {
                scene.SetSuggestedIntentState(state);
            }
        }
        return detection;
    }

    public void Pin(SceneGraph scene, DiagramIntent intent, string reason = "Pinned by user")
    {
        ArgumentNullException.ThrowIfNull(scene);
        scene.SetIntentState(new DiagramIntentState(
            intent,
            DiagramIntentSelectionMode.PinnedByUser,
            1,
            SafeReason(reason),
            scene.Revision));
        scene.SetSuggestedIntentState(null);
    }

    public void UseAuto(SceneGraph scene, string reason = "Automatic intent detection")
    {
        ArgumentNullException.ThrowIfNull(scene);
        scene.SetIntentState(scene.IntentState with
        {
            SelectionMode = DiagramIntentSelectionMode.Auto,
            Confidence = 0,
            Reason = SafeReason(reason),
            AppliedRevision = scene.Revision,
        });
        scene.SetSuggestedIntentState(null);
    }

    public bool ApplySuggestion(SceneGraph scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (scene.SyncRoot)
        {
            if (scene.SuggestedIntentState is not { } suggestion) return false;
            scene.SetIntentState(suggestion with
            {
                SelectionMode = DiagramIntentSelectionMode.Auto,
                AppliedRevision = scene.Revision,
            });
            scene.SetSuggestedIntentState(null);
            return true;
        }
    }

    public bool RejectSuggestion(SceneGraph scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (scene.SyncRoot)
        {
            if (scene.SuggestedIntentState is null) return false;
            scene.SetSuggestedIntentState(null);
            return true;
        }
    }

    private static string SafeReason(string? reason)
    {
        var value = string.IsNullOrWhiteSpace(reason) ? "Intent state updated" : reason.Trim();
        return value.Length <= 160 ? value : value[..160];
    }
}
