namespace AudioBoarder.Core.Scene;

public sealed class SceneGroup
{
    public required string Id { get; init; }
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Enclosing boundary, enabling nested containers — a subnet inside a virtual
    /// network inside a subscription. Nesting is what makes an architecture diagram
    /// read as an architecture rather than a flat bag of boxes.
    /// </summary>
    public string? ParentGroupId { get; set; }

    /// <summary>
    /// Optional short qualifier rendered under the name, e.g. an address range
    /// ("10.1.0.0/24") or a region ("West Europe").
    /// </summary>
    public string? Subtitle { get; set; }
    public BoundaryKind BoundaryKind { get; set; } = BoundaryKind.Generic;
    public ElementLifecycleState LifecycleState { get; set; } = ElementLifecycleState.Provisional;

    public SceneGroup Clone() => new()
    {
        Id = Id,
        Label = Label,
        ParentGroupId = ParentGroupId,
        Subtitle = Subtitle,
        BoundaryKind = BoundaryKind,
        LifecycleState = LifecycleState,
    };
}
