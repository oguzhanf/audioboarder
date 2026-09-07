namespace AudioBoarder.Core.Scene;

public sealed record ComponentIconVisual(string Svg, bool IsOfficial);

public static class ComponentIconVisuals
{
    private const string ResourcePrefix = "AudioBoarder.AzureIcons.";
    private static readonly IReadOnlyDictionary<string, string> Bundled = LoadBundled();

    public static ComponentIconVisual ForComponent(
        MicrosoftComponentDefinition component, AzureIconLibrary? custom = null)
    {
        var customPath = custom?.FindPath(component.Name);
        if (customPath is not null && custom!.ReadSvg(customPath) is { } customSvg)
            return new(customSvg, true);
        return Bundled.TryGetValue(component.Id, out var svg)
            ? new(svg, true)
            : new(IconRegistry.RenderSvg(component.Icon, "#0078d4", 32), false);
    }

    public static ComponentIconVisual ForNode(SceneNode node, AzureIconLibrary? custom = null)
    {
        if (node.Kind == NodeKind.Concept)
            return new(IconRegistry.RenderSvg(node.EffectiveIconName, "#0078d4", 32), false);
        var definition = MicrosoftComponentCatalog.FindMentionedProduct(node.Label);
        var customPath = definition is not null
            ? custom?.FindPath(definition.Name)
            : node.Label.StartsWith("Azure ", StringComparison.OrdinalIgnoreCase)
                ? custom?.FindPath(node.Label) : null;
        if (customPath is not null && custom!.ReadSvg(customPath) is { } customSvg)
            return new(customSvg, true);
        return definition is not null && Bundled.TryGetValue(definition.Id, out var svg)
            ? new(svg, true)
            : new(IconRegistry.RenderSvg(node.EffectiveIconName, "#0078d4", 32), false);
    }

    private static IReadOnlyDictionary<string, string> LoadBundled()
    {
        var assembly = typeof(ComponentIconVisuals).Assembly;
        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n =>
                     n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".svg", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                               ?? throw new InvalidOperationException($"Missing embedded architecture icon: {name}");
            using var reader = new StreamReader(stream);
            icons.Add(name[ResourcePrefix.Length..^4], reader.ReadToEnd());
        }
        return icons;
    }
}
