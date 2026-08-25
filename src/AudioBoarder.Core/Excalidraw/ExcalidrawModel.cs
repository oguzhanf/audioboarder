using System.Text.Json.Serialization;

namespace AudioBoarder.Core.Excalidraw;

/// <summary>
/// Strongly-typed model of the <c>.excalidraw</c> file format (schema version 2).
/// Serialized to the exact JSON Excalidraw expects (camelCase, see
/// <see cref="ExcalidrawJson"/>). Kept in Core so the format stays UI-agnostic and
/// can be produced both for file export and for the live in-app WebView2 canvas.
/// </summary>
public sealed class ExcalidrawDocument
{
    public string Type { get; init; } = "excalidraw";
    public int Version { get; init; } = 2;
    public string Source { get; init; } = "audioboarder";
    public List<ExcalidrawElement> Elements { get; init; } = new();
    public ExcalidrawAppState AppState { get; init; } = new();

    /// <summary>Image blob registry. Empty for diagram-only exports.</summary>
    public Dictionary<string, object> Files { get; init; } = new();
}

public sealed class ExcalidrawAppState
{
    public string ViewBackgroundColor { get; set; } = "#ffffff";

    [JsonPropertyName("gridSize")]
    public int? GridSize { get; set; }
}

/// <summary>
/// A single Excalidraw element. One permissive shape covers every element type;
/// fields that don't apply to a given <see cref="Type"/> are left null and omitted
/// from the JSON (Excalidraw fills defaults on load).
/// </summary>
public sealed class ExcalidrawElement
{
    public required string Id { get; set; }

    /// <summary>rectangle | ellipse | diamond | arrow | line | text | frame.</summary>
    public required string Type { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Angle { get; set; }

    public string StrokeColor { get; set; } = "#1e1e1e";
    public string BackgroundColor { get; set; } = "transparent";

    /// <summary>hachure | cross-hatch | solid.</summary>
    public string FillStyle { get; set; } = "solid";

    public double StrokeWidth { get; set; } = 2;

    /// <summary>solid | dashed | dotted.</summary>
    public string StrokeStyle { get; set; } = "solid";

    /// <summary>0 = architect, 1 = artist (hand-drawn), 2 = cartoonist.</summary>
    public int Roughness { get; set; } = 1;

    public int Opacity { get; set; } = 100;

    public List<string> GroupIds { get; set; } = new();
    public string? FrameId { get; set; }

    public ExcalidrawRoundness? Roundness { get; set; }

    public int Seed { get; set; }
    public int Version { get; set; } = 1;
    public int VersionNonce { get; set; }
    public bool IsDeleted { get; set; }
    public long Updated { get; set; }
    public string? Link { get; set; }
    public bool Locked { get; set; }

    /// <summary>Bound text / arrows attached to this shape.</summary>
    public List<ExcalidrawBoundElement>? BoundElements { get; set; }

    // ---- text ----
    public string? Text { get; set; }
    public double? FontSize { get; set; }

    /// <summary>1 = Virgil (hand-drawn), 2 = Helvetica, 3 = Cascadia (mono).</summary>
    public int? FontFamily { get; set; }
    public string? TextAlign { get; set; }
    public string? VerticalAlign { get; set; }
    public string? ContainerId { get; set; }
    public string? OriginalText { get; set; }
    public double? LineHeight { get; set; }
    public bool? AutoResize { get; set; }

    // ---- arrow / line ----
    public double[][]? Points { get; set; }
    public double[]? LastCommittedPoint { get; set; }
    public ExcalidrawBinding? StartBinding { get; set; }
    public ExcalidrawBinding? EndBinding { get; set; }
    public string? StartArrowhead { get; set; }
    public string? EndArrowhead { get; set; }
    public bool? Elbowed { get; set; }

    // ---- frame ----
    public string? Name { get; set; }

    // ---- image ----
    /// <summary>Key into <see cref="ExcalidrawDocument.Files"/> for image elements.</summary>
    public string? FileId { get; set; }

    /// <summary>pending | saved | error.</summary>
    public string? Status { get; set; }

    public double[]? Scale { get; set; }
}

public sealed class ExcalidrawRoundness
{
    public int Type { get; set; } = 3;
}

/// <summary>An entry in <see cref="ExcalidrawDocument.Files"/> backing an image element.</summary>
public sealed class ExcalidrawFile
{
    public required string Id { get; set; }
    public required string MimeType { get; set; }

    [JsonPropertyName("dataURL")]
    public required string DataURL { get; set; }

    public long Created { get; set; }
}

public sealed class ExcalidrawBinding
{
    public required string ElementId { get; set; }
    public double Focus { get; set; }
    public double Gap { get; set; }
}

public sealed class ExcalidrawBoundElement
{
    public required string Id { get; set; }
    public required string Type { get; set; }
}
