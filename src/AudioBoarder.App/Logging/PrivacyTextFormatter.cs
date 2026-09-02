using System.IO;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace AudioBoarder.App.Logging;

internal sealed class PrivacyTextFormatter : ITextFormatter
{
    private static readonly HashSet<string> SensitivePropertyNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Account", "Device", "DeviceId", "DeviceName", "Endpoint", "File",
            "FilePath", "Id", "Label", "Path", "Prompt", "ResourceId", "Response",
            "SubscriptionId", "TenantId", "Text", "Transcript", "Username",
        };

    private static readonly LogEventPropertyValue Redacted =
        new ScalarValue("[redacted]");

    private readonly MessageTemplateTextFormatter _inner = new(
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}");

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var properties = logEvent.Properties.Select(property =>
            new LogEventProperty(
                property.Key,
                SensitivePropertyNames.Contains(property.Key) ? Redacted : property.Value));
        var safeEvent = new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception: null,
            logEvent.MessageTemplate,
            properties);
        _inner.Format(safeEvent, output);
    }
}
