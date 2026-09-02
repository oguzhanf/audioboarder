using AudioBoarder.Core.Scene;

namespace AudioBoarder.Core.Excalidraw;

/// <summary>A stroke/fill colour pair for an Excalidraw shape.</summary>
public readonly record struct ExcalidrawColors(string Stroke, string Fill);

/// <summary>
/// Maps scene element kinds to Excalidraw-native colours (the Open Color palette
/// Excalidraw itself ships). Border and fill come from the same hue family so the
/// hand-drawn diagram reads cleanly. Text always uses <see cref="Ink"/>.
/// </summary>
public static class ExcalidrawPalette
{
    /// <summary>Excalidraw's default near-black ink, used for all label text and connectors.</summary>
    public const string Ink = "#1e1e1e";

    /// <summary>Connector (arrow) colour — soft charcoal so flow lines recede behind nodes.</summary>
    public const string Edge = "#343a40";

    private static readonly ExcalidrawColors Blue = new("#1971c2", "#a5d8ff");
    private static readonly ExcalidrawColors Cyan = new("#0c8599", "#99e9f2");
    private static readonly ExcalidrawColors Orange = new("#e8590c", "#ffd8a8");
    private static readonly ExcalidrawColors Grape = new("#9c36b5", "#eebefa");
    private static readonly ExcalidrawColors Green = new("#2f9e44", "#b2f2bb");
    private static readonly ExcalidrawColors Yellow = new("#f08c00", "#ffec99");
    private static readonly ExcalidrawColors Red = new("#e03131", "#ffc9c9");
    private static readonly ExcalidrawColors Indigo = new("#3b5bdb", "#dbe4ff");
    private static readonly ExcalidrawColors Teal = new("#099268", "#c3fae8");
    private static readonly ExcalidrawColors Gray = new("#495057", "#e9ecef");
    private static readonly ExcalidrawColors Pink = new("#c2255c", "#ffdeeb");
    private static readonly ExcalidrawColors Lime = new("#66a80f", "#e9fac8");

    public static ExcalidrawColors For(NodeKind kind) => kind switch
    {
        NodeKind.Process => Blue,
        NodeKind.Entity => Cyan,
        NodeKind.Decision => Orange,
        NodeKind.DataStore => Grape,
        NodeKind.Actor => Green,
        NodeKind.Note => Yellow,
        NodeKind.System => Indigo,
        NodeKind.Technology => Teal,
        NodeKind.Security => Red,
        NodeKind.Identity => Blue,
        NodeKind.Cloud => Indigo,
        NodeKind.Document => Gray,
        NodeKind.Milestone => Lime,
        NodeKind.Risk => Red,
        NodeKind.Metric => Pink,
        NodeKind.External => Gray,
        NodeKind.Callout => Yellow,
        _ => Blue,
    };

    public static ExcalidrawColors ForNote(NoteKind kind) => kind switch
    {
        NoteKind.ActionItem => Green,
        NoteKind.Decision => Blue,
        NoteKind.Question => Grape,
        NoteKind.Risk => Red,
        _ => Yellow,
    };
}
