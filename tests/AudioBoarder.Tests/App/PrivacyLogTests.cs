using AudioBoarder.App.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace AudioBoarder.Tests.App;

public sealed class PrivacyLogTests
{
    [Fact]
    public void DefaultFormatterRedactsContentAndOmitsExceptionPayloads()
    {
        const string sentinel =
            "SENTINEL transcript model-response label tenant resource device C:\\private\\file";
        var template = new MessageTemplateParser().Parse(
            "safe count={Count} transcript={Transcript} response={Response} label={Label} " +
            "tenant={TenantId} resource={ResourceId} device={Device} path={Path}");
        var properties = new[]
        {
            new LogEventProperty("Count", new ScalarValue(42)),
            new LogEventProperty("Transcript", new ScalarValue(sentinel)),
            new LogEventProperty("Response", new ScalarValue(sentinel)),
            new LogEventProperty("Label", new ScalarValue(sentinel)),
            new LogEventProperty("TenantId", new ScalarValue(sentinel)),
            new LogEventProperty("ResourceId", new ScalarValue(sentinel)),
            new LogEventProperty("Device", new ScalarValue(sentinel)),
            new LogEventProperty("Path", new ScalarValue(sentinel)),
        };
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            new InvalidOperationException(sentinel),
            template,
            properties);
        using var writer = new StringWriter();

        new PrivacyTextFormatter().Format(logEvent, writer);

        var output = writer.ToString();
        output.Should().NotContain(sentinel);
        output.Should().Contain("42");
        output.Should().Contain("[redacted]");
    }

    [Fact]
    public void ShippedDefaultsDisablePayloadAndPerformanceTelemetry()
    {
        var settingsPath = Path.Combine(
            FindRepositoryRoot(), "src", "AudioBoarder.App", "appsettings.json");
        var text = File.ReadAllText(settingsPath);

        text.Should().Contain("\"VerbosePayloadLogging\": false");
        text.Should().Contain("\"EnableLocalPerformanceTelemetry\": false");
        text.Should().Contain("\"Enabled\": false");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AudioBoarder.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
