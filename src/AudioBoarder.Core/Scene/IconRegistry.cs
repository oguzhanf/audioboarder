namespace AudioBoarder.Core.Scene;

/// <summary>
/// Maps concepts and product names mentioned in a meeting to a short glyph, so the
/// board reads like a stencil-based Visio drawing rather than a wall of identical
/// rectangles.
///
/// Resolution order:
/// 1. An explicit <see cref="SceneNode.Icon"/> set by the LLM always wins.
/// 2. Otherwise the label is matched against <see cref="ProductGlyphs"/> — longest
///    phrase first, so "Power BI" beats a bare "power" and "Microsoft Purview"
///    beats "Microsoft".
/// 3. Otherwise the node's <see cref="NodeKind"/> supplies a category default.
///
/// Glyphs are plain Unicode (emoji / symbols) so they render in Excalidraw, in the
/// SkiaSharp canvas, and in an exported .excalidraw opened on any machine — no
/// image assets, no licensing, no network fetch.
/// </summary>
public static class IconRegistry
{
    /// <summary>
    /// Product/technology phrases → glyph. Keys are lowercase; matching is
    /// case-insensitive substring matching against the node label.
    /// Ordered longest-first at lookup time so specific names win.
    /// </summary>
    private static readonly Dictionary<string, string> ProductGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        // Microsoft data & analytics
        ["power bi"] = "\U0001F4CA",            // bar chart
        ["fabric"] = "\U0001FA9A",              // loom/weave
        ["synapse"] = "\U0001F517",
        ["data factory"] = "\U0001F3ED",
        ["databricks"] = "\U0001F9F1",
        ["data lake"] = "\U0001F30A",
        ["lakehouse"] = "\U0001F3E0",
        ["onelake"] = "\U0001F30A",
        ["data catalog"] = "\U0001F4D6",
        ["dataverse"] = "\U0001F5C3",

        // Microsoft security & governance
        ["purview"] = "\U0001F50E",             // magnifying glass — discovery/classification
        ["defender"] = "\U0001F6E1",            // shield
        ["sentinel"] = "\U0001F441",
        ["entra"] = "\U0001F511",
        ["active directory"] = "\U0001F511",
        ["intune"] = "\U0001F4F1",
        ["compliance"] = "\u2696",
        ["dlp"] = "\U0001F6AB",
        ["information barrier"] = "\U0001F6A7",
        ["conditional access"] = "\U0001F6C2",
        ["rbac"] = "\U0001F510",
        ["encryption"] = "\U0001F512",
        ["sensitivity label"] = "\U0001F3F7",
        ["classification"] = "\U0001F3F7",
        ["retention"] = "\U0001F5C4",

        // Copilot / AI
        ["copilot"] = "\U0001F916",
        ["cowork"] = "\U0001F91D",
        ["openai"] = "\U0001F9E0",
        ["foundry"] = "\U0001F3ED",
        ["llm"] = "\U0001F9E0",
        ["agent"] = "\U0001F916",
        ["prompt"] = "\U0001F4AC",
        ["model"] = "\U0001F9E0",

        // Microsoft 365 / collaboration
        ["sharepoint"] = "\U0001F4C1",
        ["onedrive"] = "\u2601",
        ["teams"] = "\U0001F465",
        ["outlook"] = "\U0001F4E7",
        ["exchange"] = "\U0001F4E7",
        ["viva"] = "\U0001F331",
        ["loop"] = "\U0001F501",

        // Azure / infra
        ["azure"] = "\u2601",
        ["aws"] = "\u2601",
        ["gcp"] = "\u2601",
        ["kubernetes"] = "\u2388",
        ["container"] = "\U0001F4E6",
        ["docker"] = "\U0001F433",
        ["virtual machine"] = "\U0001F5A5",
        ["server"] = "\U0001F5A5",
        ["network"] = "\U0001F578",
        ["firewall"] = "\U0001F9F1",
        ["vpn"] = "\U0001F510",
        ["endpoint"] = "\U0001F50C",

        // Data stores
        ["sql"] = "\U0001F5C3",
        ["database"] = "\U0001F5C3",
        ["cosmos"] = "\U0001F30C",
        ["storage account"] = "\U0001F4BE",
        ["blob"] = "\U0001F4BE",
        ["warehouse"] = "\U0001F3E2",
        ["cache"] = "\u26A1",
        ["queue"] = "\U0001F4EC",

        // Integration
        ["api"] = "\U0001F50C",
        ["rest"] = "\U0001F50C",
        ["graph"] = "\U0001F578",
        ["webhook"] = "\U0001FA9D",
        ["power automate"] = "\u26A1",
        ["power apps"] = "\U0001F4F2",
        ["logic app"] = "\u26A1",
        ["connector"] = "\U0001F50C",
        ["pipeline"] = "\U0001F6E4",
        ["integration"] = "\U0001F517",

        // People & process
        ["customer"] = "\U0001F464",
        ["user"] = "\U0001F464",
        ["team"] = "\U0001F465",
        ["stakeholder"] = "\U0001F465",
        ["admin"] = "\U0001F477",
        ["engineer"] = "\U0001F477",
        ["approval"] = "\u2705",
        ["governance"] = "\u2696",
        ["policy"] = "\U0001F4DC",
        ["process"] = "\u2699",
        ["workflow"] = "\U0001F501",
        ["training"] = "\U0001F393",
        ["onboarding"] = "\U0001F6AA",
        ["budget"] = "\U0001F4B0",
        ["cost"] = "\U0001F4B0",
        ["licence"] = "\U0001F4C4",
        ["license"] = "\U0001F4C4",
        ["contract"] = "\U0001F4DD",
        ["report"] = "\U0001F4C8",
        ["dashboard"] = "\U0001F4CA",
        ["meeting"] = "\U0001F5E3",
        ["roadmap"] = "\U0001F5FA",
        ["timeline"] = "\U0001F4C5",
        ["deadline"] = "\u23F0",
        ["risk"] = "\u26A0",
        ["issue"] = "\u26A0",
        ["blocker"] = "\U0001F6D1",
        ["question"] = "\u2753",
        ["decision"] = "\u2696",
        ["migration"] = "\U0001F69A",
        ["backup"] = "\U0001F4BE",
        ["audit"] = "\U0001F4CB",
        ["monitoring"] = "\U0001F4C9",
        ["alert"] = "\U0001F514",
        ["incident"] = "\U0001F6A8",
    };

    /// <summary>Category fallback when no product phrase matches.</summary>
    private static readonly Dictionary<NodeKind, string> KindGlyphs = new()
    {
        [NodeKind.Process] = "\u2699",
        [NodeKind.Entity] = "\U0001F4E6",
        [NodeKind.Decision] = "\u2753",
        [NodeKind.DataStore] = "\U0001F5C3",
        [NodeKind.Actor] = "\U0001F464",
        [NodeKind.Note] = "\U0001F4DD",
        [NodeKind.System] = "\U0001F5A5",
        [NodeKind.Technology] = "\U0001F527",
        [NodeKind.Security] = "\U0001F6E1",
        [NodeKind.Cloud] = "\u2601",
        [NodeKind.Document] = "\U0001F4C4",
        [NodeKind.Milestone] = "\U0001F6A9",
        [NodeKind.Risk] = "\u26A0",
        [NodeKind.Metric] = "\U0001F4C8",
        [NodeKind.External] = "\U0001F310",
        [NodeKind.Callout] = "\U0001F4A1",
    };

    /// <summary>Phrases sorted longest-first so "power bi" wins over "power automate" prefixes etc.</summary>
    private static readonly string[] PhrasesByLength =
        ProductGlyphs.Keys.OrderByDescending(k => k.Length).ToArray();

    /// <summary>
    /// Resolves a glyph for a label. Returns null when nothing sensible applies, so
    /// callers can render a plain shape rather than a misleading icon.
    /// </summary>
    public static string? Resolve(string? label, NodeKind kind)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            foreach (var phrase in PhrasesByLength)
            {
                if (label.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    return ProductGlyphs[phrase];
            }
        }
        return KindGlyphs.TryGetValue(kind, out var g) ? g : null;
    }

    /// <summary>True when the label names a known product/technology (not just a category).</summary>
    public static bool IsKnownTechnology(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        return PhrasesByLength.Any(p => label.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
