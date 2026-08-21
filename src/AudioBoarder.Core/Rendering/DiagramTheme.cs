namespace AudioBoarder.Core.Rendering;

/// <summary>
/// Theme palette used by the renderer. Kept primitive (RGBA hex strings) so
/// the Core project stays UI-agnostic; the Services renderer converts to SKColor.
/// Hex may be #RRGGBB or #AARRGGBB (alpha first).
/// </summary>
public sealed record DiagramTheme(
    string Background,
    string GridLine,
    string NodeFill,
    string NodeStroke,
    string NodeText,
    string DecisionFill,
    string EntityFill,
    string DataStoreFill,
    string ActorFill,
    string EdgeStroke,
    string EdgeLabel,
    string GroupStroke,
    string GroupFill,
    string SelectionStroke,
    // --- modern whiteboard additions ---
    string DotGrid,
    string NodeShadow,
    string NodeBorder,
    string EdgeLabelBg,
    string ProcessAccent,
    string EntityAccent,
    string DataStoreAccent,
    string ActorAccent,
    string DecisionAccent,
    string NoteAccent,
    string GroupLabelBg)
{
    public static DiagramTheme Light { get; } = new(
        Background: "#FBFBFC",
        GridLine: "#F0F0F0",
        NodeFill: "#FFFFFF",
        NodeStroke: "#D0D5DD",
        NodeText: "#1D2433",
        DecisionFill: "#FEF3C7",
        EntityFill: "#DBEAFE",
        DataStoreFill: "#E0E7FF",
        ActorFill: "#FAE8FF",
        EdgeStroke: "#A9B2C0",
        EdgeLabel: "#5B6472",
        GroupStroke: "#D7CCF0",
        GroupFill: "#1A6D5AE6",       // ~10% indigo wash
        SelectionStroke: "#6366F1",
        DotGrid: "#E1E5EC",
        NodeShadow: "#1E101828",      // ~12% navy
        NodeBorder: "#E4E7EC",
        EdgeLabelBg: "#F7F8FA",
        ProcessAccent: "#6366F1",     // indigo
        EntityAccent: "#0EA5E9",      // sky
        DataStoreAccent: "#14B8A6",   // teal
        ActorAccent: "#F59E0B",       // amber
        DecisionAccent: "#EC4899",    // pink
        NoteAccent: "#EAB308",        // yellow
        GroupLabelBg: "#EEF0FE");

    public static DiagramTheme Dark { get; } = new(
        Background: "#0F141B",
        GridLine: "#1F2937",
        NodeFill: "#1B2230",
        NodeStroke: "#33405A",
        NodeText: "#EAF0FA",
        DecisionFill: "#78350F",
        EntityFill: "#1E3A8A",
        DataStoreFill: "#312E81",
        ActorFill: "#6B21A8",
        EdgeStroke: "#5A6678",
        EdgeLabel: "#AEB7C6",
        GroupStroke: "#3B3457",
        GroupFill: "#22312A6E",
        SelectionStroke: "#818CF8",
        DotGrid: "#1C2533",
        NodeShadow: "#55000000",
        NodeBorder: "#2B3447",
        EdgeLabelBg: "#161C26",
        ProcessAccent: "#818CF8",
        EntityAccent: "#38BDF8",
        DataStoreAccent: "#2DD4BF",
        ActorAccent: "#FBBF24",
        DecisionAccent: "#F472B6",
        NoteAccent: "#FACC15",
        GroupLabelBg: "#222a3f");
}
