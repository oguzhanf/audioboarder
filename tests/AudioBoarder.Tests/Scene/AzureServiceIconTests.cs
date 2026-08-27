using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Scene;

/// <summary>
/// Cover for icon resolution against the Azure services people actually name out
/// loud in a design review.
/// <para>
/// This is easy to get quietly wrong: a short generic phrase like "azure" will
/// swallow every service name unless the longest phrase wins, leaving a board where
/// Front Door, Functions and AI Search all render the same cloud glyph. That state
/// looks fine at a glance and is only visible by inspecting the whole diagram.
/// </para>
/// </summary>
public class AzureServiceIconTests
{
    /// <summary>Label as spoken, and the icon it must resolve to.</summary>
    public static TheoryData<string, NodeKind, string> SpokenServices() => new()
    {
        // Edge and networking — an ingress point must not look like the platform
        // it fronts, so these are deliberately distinct from "cloud".
        { "Azure Front Door", NodeKind.Technology, "globe-lock" },
        { "Application Gateway", NodeKind.Technology, "route" },
        { "ExpressRoute circuit", NodeKind.Technology, "route" },
        { "Web Application Firewall", NodeKind.Security, "shield" },
        { "DDoS Protection", NodeKind.Security, "shield" },
        { "Private endpoint", NodeKind.Security, "plug" },
        { "Private DNS zone", NodeKind.Technology, "globe" },
        { "Hub virtual network", NodeKind.System, "network" },

        // Compute and hosting
        { "App Service", NodeKind.Technology, "app-window" },
        { "Azure Functions", NodeKind.Technology, "zap" },
        { "Azure Kubernetes Service", NodeKind.System, "container" },
        { "Virtual machine scale set", NodeKind.System, "server" },

        // Data
        { "Azure SQL Database", NodeKind.DataStore, "database" },
        { "Cosmos DB", NodeKind.DataStore, "database" },
        { "Blob Storage", NodeKind.DataStore, "archive" },
        { "Event Hubs", NodeKind.Technology, "workflow" },

        // Identity, secrets, observability, AI
        { "Microsoft Entra ID", NodeKind.Security, "key" },
        { "Key Vault", NodeKind.Security, "key-round" },
        { "Application Insights", NodeKind.Metric, "trending-up" },
        { "Log Analytics workspace", NodeKind.DataStore, "trending-up" },
        { "Microsoft Foundry", NodeKind.Technology, "brain" },
        { "Azure AI Search", NodeKind.Technology, "search" },
    };

    [Theory]
    [MemberData(nameof(SpokenServices))]
    public void SpokenAzureServicesResolveToTheirOwnIcon(string label, NodeKind kind, string expected)
    {
        IconRegistry.Resolve(label, kind).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(SpokenServices))]
    public void SpokenAzureServicesAreRecognisedAsNamedTechnologies(string label, NodeKind kind, string expected)
    {
        _ = kind; _ = expected;
        IconRegistry.IsKnownTechnology(label).Should().BeTrue(
            $"'{label}' is a real product and must not fall back to a generic kind icon");
    }

    [Fact]
    public void EveryResolvedIconExistsInTheRegistry()
    {
        foreach (var row in SpokenServices())
        {
            var label = (string)row[0];
            var kind = (NodeKind)row[1];
            IconRegistry.Has(IconRegistry.Resolve(label, kind)).Should().BeTrue(
                $"'{label}' resolves to an icon name with no path data, which renders as a blank box");
        }
    }

    [Fact]
    public void AnArchitectureUsesManyDistinctIconsNotOneGenericGlyph()
    {
        var icons = SpokenServices()
            .Select(row => IconRegistry.Resolve((string)row[0], (NodeKind)row[1]))
            .ToList();

        // The failure this guards against is every service collapsing onto "cloud".
        icons.Distinct().Should().HaveCountGreaterThan(12,
            "a board where most services share one glyph conveys nothing");
        icons.Count(i => i == "cloud").Should().BeLessThan(3,
            "'azure' must never outrank a specific service name");
    }

    [Fact]
    public void TheBareAzurePrefixStillResolvesForUnknownServices()
    {
        // A service we have no specific icon for should still read as cloud rather
        // than falling through to a shapeless default.
        IconRegistry.Resolve("Azure Quantum Workspace", NodeKind.Technology).Should().Be("cloud");
    }
}
