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
        var customPath = custom?.FindPath(node.Label);
        if (customPath is not null && custom!.ReadSvg(customPath) is { } customSvg)
            return new(customSvg, true);
        var definition = MicrosoftComponentCatalog.All
            .Where(c => Matches(node.Label, c.Name) || c.Aliases.Any(a => a.Length >= 4 && Matches(node.Label, a)))
            .OrderByDescending(c => c.Name.Length)
            .FirstOrDefault();
        return definition is not null && Bundled.TryGetValue(definition.Id, out var svg)
            ? new(svg, true)
            : new(IconRegistry.RenderSvg(node.EffectiveIconName, "#0078d4", 32), false);
    }

    private static bool Matches(string label, string name)
    {
        var index = label.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        var end = index + name.Length;
        return (index == 0 || !char.IsLetterOrDigit(label[index - 1])) &&
               (end == label.Length || !char.IsLetterOrDigit(label[end]));
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
