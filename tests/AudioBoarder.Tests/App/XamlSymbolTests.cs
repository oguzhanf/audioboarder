using System.IO;
using System.Text.RegularExpressions;
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
