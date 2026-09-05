using System.Text.Json;
using System.Text.Json.Serialization;
using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Core.Excalidraw;

/// <summary>
/// Serialises a <see cref="SceneGraph"/> for the in-app canvas renderer.
/// <para>
/// Geometry is resolved by .NET and expressed as centre coordinates. The SVG
/// renderer only converts centres to top-left drawing coordinates.
/// </para>
/// <para>
/// The Excalidraw converter is retained for file export, where a real
/// <c>.excalidraw</c> document is the point.
/// </para>
/// </summary>
public static class SceneToCanvasJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(SceneGraph graph, int revision, AzureIconLibrary? icons = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        icons ??= AzureIconLibrary.Empty;

        CanvasScene payload;
        lock (graph.SyncRoot)
        {
            var layout = LayoutSnapshot.Capture(graph);
            payload = new CanvasScene
            {
                SceneRevision = revision,
                Intent = ToSnake(graph.IntentState.AppliedIntent.ToString()),
                Nodes = graph.Nodes.Values
                    .OrderBy(n => n.Sequence)
                    .ThenBy(n => n.Id, StringComparer.Ordinal)
                    .Select(n => new CanvasNode
                    {
                        Id = n.Id,
                        Label = n.Label,
                        Desc = string.IsNullOrWhiteSpace(n.Description) ? null : n.Description,
                        Kind = ToSnake(n.Kind.ToString()),
                        Group = n.GroupId,
                        Locked = n.Locked ? true : null,
                        Lifecycle = ToSnake(n.LifecycleState.ToString()),
                        CenterX = layout.Nodes[n.Id].CenterX,
                        CenterY = layout.Nodes[n.Id].CenterY,
                        Width = layout.Nodes[n.Id].Width,
                        Height = layout.Nodes[n.Id].Height,
                        Svg = ComponentIconVisuals.ForNode(n, icons).Svg,
                    })
                    .ToArray(),
                Edges = graph.Edges.Values
                    .OrderBy(e => e.Id, StringComparer.Ordinal)
                    .Select(e => new CanvasEdge
                    {
                        Id = e.Id,
                        From = e.FromNodeId,
                        To = e.ToNodeId,
                        Kind = ToSnake(e.Kind.ToString()),
                        Label = string.IsNullOrWhiteSpace(e.Label) ? null : e.Label,
                        Step = e.Step,
                        Protocol = e.Protocol,
                        Payload = e.Payload,
                        DataClassification = e.DataClassification,
                        Authentication = e.Authentication,
                        InteractionMode = e.InteractionMode.HasValue
                            ? ToSnake(e.InteractionMode.Value.ToString()) : null,
                        Lifecycle = ToSnake(e.LifecycleState.ToString()),
                    })
                    .ToArray(),
                Groups = graph.Groups.Values
                    .OrderBy(g => layout.Groups[g.Id].Depth)
                    .ThenBy(g => g.Id, StringComparer.Ordinal)
                    .Select(g => new CanvasGroup
                    {
                        Id = g.Id,
                        Label = g.Label,
                        Parent = g.ParentGroupId,
                        Subtitle = string.IsNullOrWhiteSpace(g.Subtitle) ? null : g.Subtitle,
                        BoundaryKind = ToSnake(g.BoundaryKind.ToString()),
                        Lifecycle = ToSnake(g.LifecycleState.ToString()),
                        CenterX = layout.Groups[g.Id].CenterX,
                        CenterY = layout.Groups[g.Id].CenterY,
                        Width = layout.Groups[g.Id].Width,
                        Height = layout.Groups[g.Id].Height,
                        Depth = layout.Groups[g.Id].Depth,
                    })
                    .ToArray(),
            };
        }

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>NodeKind.DataStore -> "data_store", matching the LLM DSL vocabulary.</summary>
    private static string ToSnake(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }

    private sealed class CanvasScene
    {
        public int SceneRevision { get; init; }
        public required string Intent { get; init; }
        public CanvasNode[] Nodes { get; init; } = Array.Empty<CanvasNode>();
        public CanvasEdge[] Edges { get; init; } = Array.Empty<CanvasEdge>();
        public CanvasGroup[] Groups { get; init; } = Array.Empty<CanvasGroup>();
    }

    private sealed class CanvasNode
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public string? Desc { get; init; }
        public required string Kind { get; init; }
        public string? Group { get; init; }
        public bool? Locked { get; init; }
        public string? Lifecycle { get; init; }
        public double CenterX { get; init; }
        public double CenterY { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }

        /// <summary>Official Azure icon markup, when the user has the icon set.</summary>
        public string? Svg { get; init; }
    }

    private sealed class CanvasEdge
    {
        public required string Id { get; init; }
        public required string From { get; init; }
        public required string To { get; init; }
        public required string Kind { get; init; }
        public string? Label { get; init; }
        public int? Step { get; init; }
        public string? Protocol { get; init; }
        public string? Payload { get; init; }
        public string? DataClassification { get; init; }
        public string? Authentication { get; init; }
        public string? InteractionMode { get; init; }
        public string? Lifecycle { get; init; }
    }

    private sealed class CanvasGroup
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public string? Parent { get; init; }
        public string? Subtitle { get; init; }
        public string? BoundaryKind { get; init; }
        public string? Lifecycle { get; init; }
        public double CenterX { get; init; }
        public double CenterY { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public int Depth { get; init; }
    }
}
