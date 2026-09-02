using System.Diagnostics.Tracing;

namespace AudioBoarder.App.Controls;

[EventSource(Name = "AudioBoarder-UI")]
internal sealed class UiPerformanceTelemetry : EventSource
{
    public static UiPerformanceTelemetry Log { get; } = new();
    public static bool Enabled { get; set; }

    [Event(1, Level = EventLevel.Informational)]
    public void BridgeSerialization(double durationMilliseconds, int revision, int nodeCount, int payloadBytes)
    {
        if (Enabled)
            WriteEvent(1, durationMilliseconds, revision, nodeCount, payloadBytes);
    }

    [Event(2, Level = EventLevel.Informational)]
    public void SceneRefresh(double durationMilliseconds, int revision, int nodeCount, int payloadBytes)
    {
        if (Enabled)
            WriteEvent(2, durationMilliseconds, revision, nodeCount, payloadBytes);
    }
}
