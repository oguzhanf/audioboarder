namespace AudioBoarder.Core.Scene;

/// <summary>
/// A node in the diagram. Coordinates are logical (pixels in the scene's
/// coordinate space); a layout engine populates them when null.
/// </summary>
public sealed class SceneNode
{
    public required string Id { get; init; }
    public NodeKind Kind { get; set; } = NodeKind.Process;
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Short glyph rendered before the label (emoji or symbol) so technologies and
    /// concepts read like a Visio stencil instead of an unlabelled box. Supplied by
    /// the LLM, or auto-resolved from the label by <see cref="IconRegistry"/>.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Optional one-line explanation rendered under the label in smaller type.
    /// Gives the diagram supporting detail without a separate callout.
    /// </summary>
    public string? Description { get; set; }

    public double? X { get; set; }
    public double? Y { get; set; }
    public double Width { get; set; } = 140;
    public double Height { get; set; } = 60;
    public string? GroupId { get; set; }

    /// <summary>
    /// True if the user has manually positioned the node; layout engines must
    /// leave locked nodes alone.
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>Glyph actually used for rendering: explicit icon, else one inferred from the label/kind.</summary>
    public string? EffectiveIcon => !string.IsNullOrWhiteSpace(Icon) ? Icon : IconRegistry.Resolve(Label, Kind);

    public SceneNode Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Label = Label,
        Icon = Icon,
        Description = Description,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        GroupId = GroupId,
        Locked = Locked,
    };
}
