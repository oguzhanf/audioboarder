using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.Intent;

/// <summary>
/// Auto leaves visual modeling to the language model; explicit user selections
/// are the only way to constrain the whole board to a specialized diagram type.
/// </summary>
public sealed class DiagramIntentCoordinator
{
    public void EnsureAutomaticIntent(SceneGraph scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (scene.SyncRoot)
        {
            if (scene.IntentState.SelectionMode == DiagramIntentSelectionMode.PinnedByUser) return;
            // Older sessions may contain an automatically chosen architecture intent.
            if (scene.IntentState.AppliedIntent != DiagramIntent.MeetingWhiteboard)
                UseAuto(scene);
            scene.SetSuggestedIntentState(null);
        }
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

    public void UseAuto(SceneGraph scene, string reason = "Adapt to the meeting without constraining its subject")
    {
        ArgumentNullException.ThrowIfNull(scene);
        scene.SetIntentState(scene.IntentState with
        {
            AppliedIntent = DiagramIntent.MeetingWhiteboard,
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
                SelectionMode = DiagramIntentSelectionMode.PinnedByUser,
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
