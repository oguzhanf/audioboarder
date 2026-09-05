using System.Xml.Linq;

namespace AudioBoarder.Tests.Updates;

public sealed class InstallerAuthoringTests
{
    [Fact]
    public void ShortcutAndMachineRegistrationHaveSeparateCorrectlyScopedKeyPaths()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AudioBoarder.sln")))
            directory = directory.Parent;
        directory.Should().NotBeNull();
        var document = XDocument.Load(Path.Combine(directory!.FullName, "installer", "Package.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var components = document.Descendants(wix + "Component").ToArray();
        var shortcuts = components.Single(c => (string?)c.Attribute("Id") == "ApplicationShortcuts");
        ((string?)shortcuts.Attribute("Directory")).Should().Be("ApplicationProgramsFolder");
        shortcuts.Elements(wix + "RegistryValue").Should().OnlyContain(
            value => (string?)value.Attribute("Root") == "HKCU",
            "ICE38/43/57 prohibit mixing per-machine registry data with non-advertised Start menu shortcuts");
        shortcuts.Elements(wix + "RegistryValue").Should().Contain(
            value => (string?)value.Attribute("KeyPath") == "yes");

        var registration = components.Single(c => (string?)c.Attribute("Id") == "MachineRegistration");
        ((string?)registration.Attribute("Directory")).Should().Be("INSTALLFOLDER");
        registration.Elements(wix + "RegistryValue").Should().OnlyContain(
            value => (string?)value.Attribute("Root") == "HKLM");
        registration.Elements(wix + "Shortcut").Should().BeEmpty();
        document.Descendants(wix + "ComponentRef").Should().Contain(
            reference => (string?)reference.Attribute("Id") == "MachineRegistration");
    }
}
