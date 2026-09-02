namespace AudioBoarder.Tests.Updates;

public sealed class ReleaseWorkflowSecurityTests
{
    private static string WorkflowText()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, ".github", "workflows", "release-build.yml"));
    }

    [Fact]
    public void SigningSecretsAreAbsentFromBuildJob()
    {
        var workflow = WorkflowText();
        var split = workflow.IndexOf("\n  sign:", StringComparison.Ordinal);
        split.Should().BeGreaterThan(0);

        var buildJob = workflow[..split];
        buildJob.Should().NotContain("WINDOWS_SIGNING_PFX_BASE64");
        buildJob.Should().NotContain("WINDOWS_SIGNING_PFX_PASSWORD");
    }

    [Fact]
    public void SigningJobIsProtectedAndDoesNotCheckoutOrRunDependencies()
    {
        var workflow = WorkflowText();
        var split = workflow.IndexOf("\n  sign:", StringComparison.Ordinal);
        var signingJob = workflow[split..];

        signingJob.Should().Contain("environment: release-signing");
        signingJob.Should().Contain("secrets.WINDOWS_SIGNING_PFX_BASE64");
        signingJob.Should().Contain("secrets.WINDOWS_SIGNING_PFX_PASSWORD");
        signingJob.Should().NotContain("actions/checkout");
        signingJob.Should().NotContain("npm ci");
        signingJob.Should().NotContain("dotnet test");
        signingJob.Should().Contain("SIGNING-STAGE-SHA256.txt");
        signingJob.Should().Contain("Verify final signed artifact set without signing secrets");
        workflow.Should().Contain("src/AudioBoarder.App/Assets/AudioBoarder.ico");
    }

    [Fact]
    public void SignedBuildsRequireImmutableCommitReachableFromMain()
    {
        var workflow = WorkflowText();

        workflow.Should().Contain("source_commit");
        workflow.Should().Contain("^[0-9a-fA-F]{40}$");
        workflow.Should().Contain("git merge-base --is-ancestor");
        workflow.Should().Contain("origin/main");
    }

    [Fact]
    public void SigningCertificateIsDeletedInEverySecretBearingStep()
    {
        var workflow = WorkflowText();
        var secretStepCount = workflow.Split(
            "secrets.WINDOWS_SIGNING_PFX_BASE64",
            StringSplitOptions.None).Length - 1;
        var cleanupCount = workflow.Split(
            "Remove-Item $pfx -Force -ErrorAction SilentlyContinue",
            StringSplitOptions.None).Length - 1;

        secretStepCount.Should().Be(2);
        cleanupCount.Should().Be(2);
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
