namespace AudioBoarder.Core.Scene;

public enum DiagramIntent
{
    SoftwareSystemArchitecture,
    SaaSMultiTenantArchitecture,
    SecurityZeroTrustArchitecture,
    CloudNetworkArchitecture,
    IntegrationDataFlowArchitecture,
    DiscussionSummary,
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
        DiagramIntent.SoftwareSystemArchitecture,
        DiagramIntentSelectionMode.Auto,
        0,
        "Default auto intent",
        0);
}
