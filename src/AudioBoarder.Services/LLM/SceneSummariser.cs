using System.Text;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Services.LLM;

public static class SceneSummariser
{
    private const int MaxLabelLength = 160;
    private const int MaxDescriptionLength = 240;
    private const int MaxNoteLength = 320;
    private const int MaxNotes = 24;

    public static string Summarise(SceneGraph graph)
    {
        var sb = new StringBuilder();
        sb.Append("intent=").Append(graph.IntentState.AppliedIntent)
          .Append(" selection=").Append(graph.IntentState.SelectionMode)
          .Append(" confidence=").Append(graph.IntentState.Confidence.ToString("F3"))
          .Append(" applied_revision=").Append(graph.IntentState.AppliedRevision).AppendLine();
        if (graph.SuggestedIntentState is { } suggestion)
            sb.Append("suggested_intent=").Append(suggestion.AppliedIntent)
              .Append(" confidence=").Append(suggestion.Confidence.ToString("F3"))
              .Append(" reason=\"").Append(Clean(suggestion.Reason, 160)).AppendLine("\"");
        sb.Append("nodes=").Append(graph.Nodes.Count)
          .Append(" edges=").Append(graph.Edges.Count)
          .Append(" groups=").Append(graph.Groups.Count)
          .Append(" notes=").Append(graph.Notes.Count).AppendLine();
        foreach (var node in graph.Nodes.Values)
        {
            sb.Append($"  N {Clean(node.Id, 128)} ({node.Kind}) {Clean(node.Label, MaxLabelLength)}");
            if (!string.IsNullOrWhiteSpace(node.Description))
                sb.Append($" desc=\"{Clean(node.Description, MaxDescriptionLength)}\"");
            if (!string.IsNullOrWhiteSpace(node.GroupId)) sb.Append($" group={Clean(node.GroupId, 128)}");
            if (node.Locked) sb.Append(" locked=true");
            sb.AppendLine();
        }
        foreach (var edge in graph.Edges.Values)
        {
            sb.Append($"  E {Clean(edge.Id, 128)}: {Clean(edge.FromNodeId, 128)} -> {Clean(edge.ToNodeId, 128)} {edge.Kind}");
            if (edge.Step is > 0) sb.Append($" step={edge.Step}");
            Append(sb, "label", edge.Label, MaxLabelLength);
            Append(sb, "protocol", edge.Protocol, 80);
            Append(sb, "payload", edge.Payload, 160);
            Append(sb, "classification", edge.DataClassification, 80);
            Append(sb, "authentication", edge.Authentication, 120);
            if (edge.InteractionMode.HasValue) sb.Append($" mode={edge.InteractionMode}");
            sb.AppendLine();
        }
        foreach (var group in graph.Groups.Values)
        {
            sb.Append($"  G {Clean(group.Id, 128)}: {Clean(group.Label, MaxLabelLength)}");
            sb.Append($" boundary={group.BoundaryKind}");
            if (!string.IsNullOrWhiteSpace(group.ParentGroupId))
                sb.Append($" parent={Clean(group.ParentGroupId, 128)}");
            Append(sb, "subtitle", group.Subtitle, MaxLabelLength);
            sb.AppendLine();
        }
        foreach (var note in graph.Notes.Values.Take(MaxNotes))
        {
            sb.Append($"  T {Clean(note.Id, 128)} ({note.Kind}) {Clean(note.Text, MaxNoteLength)}");
            if (!string.IsNullOrWhiteSpace(note.Owner)) sb.Append($" owner={Clean(note.Owner, 80)}");
            if (note.SourceTimestamp.HasValue) sb.Append($" at={note.SourceTimestamp.Value:O}");
            sb.AppendLine();
        }
        if (graph.Notes.Count > MaxNotes)
            sb.AppendLine($"  T ... {graph.Notes.Count - MaxNotes} note(s) omitted");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string name, string? value, int maxLength)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.Append($" {name}=\"{Clean(value, maxLength)}\"");
    }

    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        while (normalized.Contains("  ", StringComparison.Ordinal))
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        normalized = normalized.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }
}
