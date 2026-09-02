using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioBoarder.Tests.Semantic;

public static class SemanticFixtureLoader
{
    private const string ResourceSuffix = "Semantic.Fixtures.semantic-golden-cases.json";

    private static readonly Lazy<SemanticFixtureDocument> Document = new(LoadCore);

    public static SemanticFixtureDocument Load() => Document.Value;

    private static SemanticFixtureDocument LoadCore()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded fixture '{resourceName}' was not found.");

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<SemanticFixtureDocument>(stream, options)
            ?? throw new InvalidOperationException("Semantic fixture JSON deserialized to null.");
    }
}
