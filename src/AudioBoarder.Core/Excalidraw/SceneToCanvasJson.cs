using System.Text.Json;
using System.Text.Json.Serialization;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Core.Excalidraw;

/// <summary>
/// Serialises a <see cref="SceneGraph"/> for the in-app canvas renderer.
/// <para>
/// The renderer does its own layout and styling, so unlike
/// <see cref="SceneToExcalidrawConverter"/> this emits the graph's *meaning*
/// (labels, kinds, relationships) rather than drawing instructions. That keeps
/// the payload two orders of magnitude smaller — which matters when it is pushed
/// across the WebView2 bridge several times a minute during a live meeting.
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

    public static string Serialize(SceneGraph graph, int revision)
    {
        ArgumentNullException.ThrowIfNull(graph);

        CanvasScene payload;
        lock (graph.SyncRoot)
        {
            payload = new CanvasScene
            {
                SceneRevision = revision,
                Nodes = graph.Nodes.Values
                    .OrderBy(n => n.Sequence)
                    .Select(n => new CanvasNode
                    {
                        Id = n.Id,
                        Label = n.Label,
                        Desc = string.IsNullOrWhiteSpace(n.Description) ? null : n.Description,
                        Kind = ToSnake(n.Kind.ToString()),
                        Group = n.GroupId,
                        Locked = n.Locked ? true : null,
                        // Pinned nodes carry their coordinates so the renderer can honour
                        // the user's placement instead of re-laying them out every pass.
                        X = n.Locked ? n.X : null,
                        Y = n.Locked ? n.Y : null,
                    })
                    .ToArray(),
                Edges = graph.Edges.Values
                    .Select(e => new CanvasEdge
                    {
                        Id = e.Id,
                        From = e.FromNodeId,
                        To = e.ToNodeId,
                        Kind = ToSnake(e.Kind.ToString()),
                        Label = string.IsNullOrWhiteSpace(e.Label) ? null : e.Label,
                        Step = e.Step,
                    })
                    .ToArray(),
                Groups = graph.Groups.Values
                    .Select(g => new CanvasGroup
                    {
                        Id = g.Id,
                        Label = g.Label,
                        Parent = g.ParentGroupId,
                        Subtitle = string.IsNullOrWhiteSpace(g.Subtitle) ? null : g.Subtitle,
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
        public double? X { get; init; }
        public double? Y { get; init; }
    }

    private sealed class CanvasEdge
    {
        public required string Id { get; init; }
        public required string From { get; init; }
        public required string To { get; init; }
        public required string Kind { get; init; }
        public string? Label { get; init; }
        public int? Step { get; init; }
    }

    private sealed class CanvasGroup
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public string? Parent { get; init; }
        public string? Subtitle { get; init; }
    }
}
