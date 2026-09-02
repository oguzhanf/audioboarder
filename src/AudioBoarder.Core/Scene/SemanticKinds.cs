namespace AudioBoarder.Core.Scene;

public enum BoundaryKind
{
    Generic,
    System,
    Environment,
    Tenant,
    Network,
    TrustZone,
    CloudScope,
    External,
}

public enum InteractionMode
{
    Synchronous,
    Asynchronous,
    Batch,
    Stream,
}

public enum ElementLifecycleState
{
    Provisional,
    Confirmed,
    UserEdited,
}

public sealed record ElementLifecycleChange(
    string ElementType,
    string ElementId,
    ElementLifecycleState? Previous,
    ElementLifecycleState? Current);
