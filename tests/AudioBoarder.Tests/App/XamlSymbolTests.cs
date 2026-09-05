using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wpf.Ui.Controls;

namespace AudioBoarder.Tests.App;

/// <summary>
/// Guards against invalid <see cref="SymbolRegular"/> names in XAML.
/// <para>
/// A bad symbol compiles cleanly and only fails at runtime, as a XamlParseException
/// that takes the whole window down — so it must be caught by a test rather than by
/// launching the app.
/// </para>
/// </summary>
public class XamlSymbolTests
{
    private static readonly Regex SymbolAttribute =
        new(@"Symbol\s*=\s*""(?<name>[A-Za-z0-9]+)""", RegexOptions.Compiled);

    public static TheoryData<string> XamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(AppRoot(), "*.xaml", SearchOption.AllDirectories))
            data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void EverySymbolNameInXamlIsAValidSymbolRegular(string xamlPath)
    {
        var content = File.ReadAllText(xamlPath);
        var invalid = SymbolAttribute.Matches(content)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !Enum.IsDefined(typeof(SymbolRegular), name))
            .ToList();

        invalid.Should().BeEmpty(
            $"{Path.GetFileName(xamlPath)} references SymbolRegular values that do not exist; " +
            "the app would crash on load with a XamlParseException");
    }

    [Fact]
    public void MainWindowHasAccessiblePhaseFiveProductSurface()
    {
        var path = Path.Combine(AppRoot(), "MainWindow.xaml");
        var content = File.ReadAllText(path);

        XDocument.Load(path).Root.Should().NotBeNull();
        content.Should().Contain("Live architecture canvas");
        content.Should().Contain("Export to Excalidraw");
        content.Should().Contain("AutomationProperties.Name=\"Diagram intent selector\"");
        content.Should().Contain("AutomationProperties.Name=\"Reflow unpinned nodes\"");
        content.Should().Contain("AutomationProperties.Name=\"Toggle full transcript drawer\"");
        content.Should().Contain("AutomationProperties.Name=\"Toggle notes drawer\"");
        content.Should().Contain("AutomationProperties.Name=\"Runtime state\"");
        content.Should().Contain("Visibility=\"{Binding IsAzureSignInRequired, Converter={StaticResource BoolVis}}\"");
        content.Should().Contain("Visibility=\"{Binding IsAzureRetryAvailable, Converter={StaticResource BoolVis}}\"");
        content.Should().Contain("Visibility=\"{Binding IsAzureConfigurationRequired, Converter={StaticResource BoolVis}}\"");
        content.Should().Contain("Text=\"{Binding StatusLabel, StringFormat=' · {0}'}\"");
        content.Should().NotContain("Visibility=\"{Binding IsAzureReady, Converter={StaticResource InverseBoolVis}}\"");
        content.Should().NotContain("ShowWhiteboard");
        content.Should().NotContain("Classic canvas");
    }

    [Fact]
    public void MainCommandBarIsSingleRowAndHasSettingsAndOverflow()
    {
        var content = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.xaml"));

        content.Should().Contain("x:Name=\"CommandBar\"");
        content.Should().NotContain("<WrapPanel");
        content.Should().Contain("MinWidth=\"940\"",
            "the command bar must remain usable on a 1920px display at 200% scaling");
        content.Should().Contain("AutomationProperties.Name=\"Open Settings\"");
        content.Should().Contain("AutomationProperties.Name=\"More commands\"");
        content.Should().Contain("AutomationProperties.Name=\"Choose audio input\"");
        content.Should().Contain("Header=\"Reflow unpinned nodes\"");
        content.Should().Contain("Key=\"OemComma\"");
        content.Should().Contain("OpenSettingsCommand");
        content.Should().Contain("Import transcript…");
        content.Should().Contain("Transcript drawer ({0})");
        content.Should().Contain("Notes drawer ({0})");
    }

    [Fact]
    public void SettingsWindowHasAllRequiredSectionsAndActions()
    {
        var path = Path.Combine(AppRoot(), "SettingsWindow.xaml");
        var content = File.ReadAllText(path);

        XDocument.Load(path).Root.Should().NotBeNull();
        content.Should().Contain("General / Appearance");
        content.Should().Contain("Header=\"Diagram\"");
        content.Should().Contain("Header=\"Audio\"");
        content.Should().Contain("Header=\"Transcription\"");
        content.Should().Contain("Header=\"Azure\"");
        content.Should().Contain("Privacy &amp; Data");
        content.Should().Contain("Save &amp; Restart");
        content.Should().Contain("Open data folder");
        content.Should().Contain("Delete local data…");
        content.Should().Contain("PasswordBox");
    }

    [Fact]
    public void MainWindowHasNoDecorativeGradientResources()
    {
        var content = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.xaml"));

        content.Should().NotContain("LinearGradientBrush");
        content.Should().NotContain("RadialGradientBrush");
        content.Should().NotContain("DropShadowEffect");
    }

    [Fact]
    public void NativeAzureSetupAndSettingsExposeTheSameResourceAndModelPicker()
    {
        var path = Path.Combine(AppRoot(), "Setup", "AzureSetupWindow.xaml");
        XDocument.Load(path).Root.Should().NotBeNull();
        var content = File.ReadAllText(path);
        content.Should().Contain("1. Services");
        content.Should().Contain("2. Models");
        content.Should().Contain("3. Review");
        content.Should().Contain("AutomationProperties.Name=\"Primary diagram model\"");
        content.Should().Contain("AutomationProperties.Name=\"Cloud transcription model\"");
        content.Should().Contain("AutomationProperties.Name=\"Image generation model\"");
        content.Should().Contain("AutomationProperties.Name=\"Cancel Azure setup\"");
        content.Should().Contain("never provisions billable resources automatically");
        File.ReadAllText(Path.Combine(AppRoot(), "SettingsWindow.xaml")).Should()
            .Contain("AutomationProperties.Name=\"Choose Azure resources and models\"");
    }

    [Theory]
    [InlineData("SettingsWindow.xaml")]
    [InlineData("Setup\\AzureSetupWindow.xaml")]
    [InlineData("Setup\\AzureProvisioningWindow.xaml")]
    public void DropdownsDoNotReplaceTheApplicationThemeWithAnUnbasedStyle(string relativePath)
    {
        var document = XDocument.Load(Path.Combine(AppRoot(), relativePath));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        document.Descendants(xaml + "Style")
            .Where(style => (string?)style.Attribute("TargetType") == "ComboBox")
            .Should().NotContain(style => style.Attribute("BasedOn") == null,
                "a local unbased ComboBox style restores a white system popup but leaves dark-theme item text white");
    }

    /// <summary>Walks up from the test binaries to the app project.</summary>
    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AudioBoarder.App");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/AudioBoarder.App from the test output folder.");
    }
}
