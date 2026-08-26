using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Scene;

/// <summary>
/// Cover for the optional official Azure icon set.
/// <para>
/// The icons are deliberately NOT redistributed — Microsoft's terms permit copying
/// and displaying them only for architectural diagrams, training material and
/// documentation, so the user downloads the set and points the app at it. Every
/// miss must therefore degrade silently to the bundled icons rather than fail.
/// </para>
/// </summary>
public class AzureIconLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ab-icons-" + Guid.NewGuid().ToString("N"));

    public AzureIconLibraryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "networking"));
        Directory.CreateDirectory(Path.Combine(_root, "databases"));
        // Mirrors the real archive's naming: <number>-icon-service-<Service-Name>.svg
        Write("networking", "10063-icon-service-Front-Doors.svg");
        Write("networking", "10076-icon-service-Application-Gateways.svg");
        Write("databases", "10130-icon-service-SQL-Database.svg");
        Write("databases", "10137-icon-service-Azure-Cosmos-DB.svg");
        Write("databases", "10141-icon-service-SQL-Managed-Instance.svg");
    }

    private void Write(string folder, string file) =>
        File.WriteAllText(Path.Combine(_root, folder, file),
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 18 18'><rect width='18' height='18'/></svg>");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void IndexesEveryIconBeneathTheFolder()
    {
        var library = AzureIconLibrary.Load(_root);

        library.Count.Should().BeGreaterThanOrEqualTo(5);
        library.Root.Should().Be(_root);
    }

    [Theory]
    [InlineData("Front Doors")]
    [InlineData("Application Gateways")]
    [InlineData("SQL Database")]
    [InlineData("Azure Cosmos DB")]
    public void ResolvesIconsByServiceName(string label)
    {
        AzureIconLibrary.Load(_root).FindPath(label).Should().NotBeNull();
    }

    [Fact]
    public void ResolvesWhetherOrNotTheLabelSaysAzure()
    {
        var library = AzureIconLibrary.Load(_root);

        // The model may say either; both must find the same artwork.
        library.FindPath("Cosmos DB").Should().NotBeNull();
        library.FindPath("Azure Cosmos DB").Should().NotBeNull();
    }

    [Fact]
    public void PrefersTheMoreSpecificService()
    {
        var library = AzureIconLibrary.Load(_root);

        var managed = library.FindPath("Azure SQL Managed Instance for payments");

        managed.Should().NotBeNull();
        Path.GetFileName(managed!).Should().Contain("SQL-Managed-Instance",
            "a longer service name is a better match than the generic SQL Database icon");
    }

    [Fact]
    public void ReadsTheIconMarkupVerbatim()
    {
        var library = AzureIconLibrary.Load(_root);
        var path = library.FindPath("SQL Database")!;

        // Microsoft's terms forbid cropping, flipping, rotating or recolouring, so
        // the markup must pass through untouched.
        library.ReadSvg(path).Should().StartWith("<svg").And.Contain("viewBox");
    }

    [Fact]
    public void UnknownServiceFallsBackRatherThanGuessing()
    {
        AzureIconLibrary.Load(_root).FindPath("Some Internal Tool").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\definitely\not\a\real\folder")]
    public void AMissingFolderDegradesToTheBundledIcons(string? folder)
    {
        // A bad path in configuration must not fail startup — the app simply uses
        // its own icons, since the Azure set is optional by design.
        var library = AzureIconLibrary.Load(folder);

        library.Count.Should().Be(0);
        library.FindPath("SQL Database").Should().BeNull();
    }

    [Fact]
    public void TheEmptyLibraryNeverResolvesAnything()
    {
        AzureIconLibrary.Empty.FindPath("Azure Front Door").Should().BeNull();
    }
}
