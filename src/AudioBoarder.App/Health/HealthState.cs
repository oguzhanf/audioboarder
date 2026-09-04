namespace AudioBoarder.App.Health;

public enum ComponentStatus
{
    Unknown,
    Checking,
    Ready,
    Degraded,
    ActionRequired,
    RateLimited,
    Failed,
}

public enum HealthAction
{
    None,
    SignIn,
    Configure,
    Retry,
}

public enum HealthCondition
{
    Unknown,
    Restoring,
    Ready,
    Degraded,
    SignInRequired,
    ConfigurationRequired,
    NetworkFailure,
    ServiceFailure,
    RateLimited,
    AccessDenied,
    Failed,
}

public sealed record HealthState(
    ComponentStatus Status,
    string Title,
    string Detail,
    DateTimeOffset UpdatedAt,
    string Key = "",
    HealthAction Action = HealthAction.None,
    HealthCondition Condition = HealthCondition.Unknown)
{
    public bool IsReady => Status == ComponentStatus.Ready;

    public string StatusLabel => Condition switch
    {
        HealthCondition.Restoring => "Restoring",
        HealthCondition.SignInRequired => "Sign in required",
        HealthCondition.ConfigurationRequired => "Configuration required",
        HealthCondition.NetworkFailure => "Network unavailable",
        HealthCondition.ServiceFailure => "Service unavailable",
        HealthCondition.RateLimited => "Rate limited",
        HealthCondition.AccessDenied => "Access denied",
        HealthCondition.Ready => "Ready",
        HealthCondition.Degraded => "Degraded",
        _ => Status switch
        {
            ComponentStatus.Checking => "Checking",
            ComponentStatus.ActionRequired => "Action required",
            ComponentStatus.RateLimited => "Rate limited",
            ComponentStatus.Failed => "Failed",
            _ => "",
        },
    };
}
