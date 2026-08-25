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
    /// Optional icon name from <see cref="IconRegistry"/> (for example "database").
    /// Normally left null — the icon is resolved deterministically from the label and
    /// kind, so the model cannot inject arbitrary glyphs.
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
    /// Monotonic recency stamp maintained by <see cref="SceneGraph"/>: bumped whenever
    /// the node is added or touched by a patch. Eviction drops the least recently
    /// discussed nodes first, so a topic under active discussion outlives one that
    /// was mentioned once and abandoned.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// True if the user has manually positioned the node; layout engines must
    /// leave locked nodes alone.
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>
    /// Name of the vector icon to draw inside the shape. Uses an explicit
    /// <see cref="Icon"/> only when it names a real registry icon; otherwise it is
    /// resolved from the label and kind.
    /// </summary>
    public string EffectiveIconName =>
        !string.IsNullOrWhiteSpace(Icon) && IconRegistry.Has(Icon)
            ? Icon
            : IconRegistry.Resolve(Label, Kind);

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
        Sequence = Sequence,
    };
}
