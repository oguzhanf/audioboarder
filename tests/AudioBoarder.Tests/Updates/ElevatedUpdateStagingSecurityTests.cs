namespace AudioBoarder.Tests.Updates;

public sealed class ElevatedUpdateStagingSecurityTests
{
    [Fact]
    public void ElevatedInstallerUsesRandomAdminOwnedNonReparseStaging()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AudioBoarder.App",
            "Updates",
            "GitHubUpdateService.cs"));

        source.Should().Contain("updates-secure");
        source.Should().Contain("[Guid]::NewGuid().ToString('N')");
        source.Should().Contain("SetAccessRuleProtection($true, $false)");
        source.Should().Contain("SetOwner($administrators)");
        source.Should().Contain("[IO.FileAttributes]::ReparsePoint");
        source.Should().NotContain(
            "AudioBoarder\\updates\\$($payload.TagName)",
            "a predictable user-creatable path reintroduces the MSI replacement race");
    }

    [Fact]
    public void ElevatedInstallerReverifiesHashAndExactSignerAfterCopy()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AudioBoarder.App",
            "Updates",
            "GitHubUpdateService.cs"));

        var copy = source.IndexOf(
            "Copy-Item -LiteralPath $payload.MsiPath",
            StringComparison.Ordinal);
        var stagedHash = source.IndexOf(
            "Get-FileHash -LiteralPath $stagedMsi",
            StringComparison.Ordinal);
        var stagedSignature = source.IndexOf(
            "Get-AuthenticodeSignature -LiteralPath $stagedMsi",
            StringComparison.Ordinal);
        var install = source.IndexOf(
            "Start-Process -FilePath \"$env:SystemRoot\\System32\\msiexec.exe\"",
            StringComparison.Ordinal);

        copy.Should().BeGreaterThan(0);
        stagedHash.Should().BeGreaterThan(copy);
        stagedSignature.Should().BeGreaterThan(stagedHash);
        install.Should().BeGreaterThan(stagedSignature);
    }

    [Fact]
    public void UnsignedPathRequiresExplicitApprovalAndNeverUsesTheAutomaticCountdown()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "AudioBoarder.App", "Updates", "GitHubUpdateService.cs"));
        service.Should().Contain("ValidatePreviewApproval(release, approveUnsignedPreview)");
        service.Should().Contain("UserApprovedUnsignedPreview = release.IsUnsignedPreview && approveUnsignedPreview");
        service.Should().Contain("if ($payload.UserApprovedUnsignedPreview)");
        service.Should().Contain("AudioBoarder-$($payload.TagName)-win-x64-unsigned.msi");
        var window = File.ReadAllText(Path.Combine(root, "src", "AudioBoarder.App", "Updates", "UpdateWindow.xaml.cs"));
        window.IndexOf("if (_release.IsUnsignedPreview || _release.RequiresManualInstaller)", StringComparison.Ordinal)
            .Should().BeLessThan(window.IndexOf("for (var seconds", StringComparison.Ordinal));
        window.Should().Contain("PreviewConsent.IsChecked != true");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AudioBoarder.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
