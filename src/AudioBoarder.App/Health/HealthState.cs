namespace AudioBoarder.App.Health;

public enum ComponentStatus
{
    Unknown,
    Checking,
    Ready,
    Degraded,
    Failed,
}

public sealed record HealthState(
    ComponentStatus Status,
    string Title,
    string Detail,
    DateTimeOffset UpdatedAt,
    string Key = "")
{
    public bool IsReady => Status == ComponentStatus.Ready;
}
