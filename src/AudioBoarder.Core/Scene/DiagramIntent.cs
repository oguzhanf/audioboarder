namespace AudioBoarder.Core.Scene;

public enum DiagramIntent
{
    SoftwareSystemArchitecture,
    SaaSMultiTenantArchitecture,
    SecurityZeroTrustArchitecture,
    CloudNetworkArchitecture,
    IntegrationDataFlowArchitecture,
    DiscussionSummary,
    MeetingWhiteboard,
}

public enum DiagramIntentSelectionMode
{
    Auto,
    PinnedByUser,
}

public sealed record DiagramIntentState(
    DiagramIntent AppliedIntent,
    DiagramIntentSelectionMode SelectionMode,
    double Confidence,
    string Reason,
    int AppliedRevision)
{
    public static DiagramIntentState Default { get; } = new(
        DiagramIntent.MeetingWhiteboard,
        DiagramIntentSelectionMode.Auto,
        0,
        "Adaptive meeting whiteboard",
        0);
}
