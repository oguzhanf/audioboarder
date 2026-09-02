using AudioBoarder.App.Controls;

namespace AudioBoarder.Tests.App;

public class UiPerformanceTelemetryTests
{
    [Fact]
    public void UiTelemetryAcceptsOnlySafeNumericMeasurements()
    {
        var eventMethods = typeof(UiPerformanceTelemetry)
            .GetMethods()
            .Where(method => method.Name is "BridgeSerialization" or "SceneRefresh")
            .ToArray();

        eventMethods.Should().HaveCount(2);
        eventMethods.SelectMany(method => method.GetParameters())
            .Should().OnlyContain(parameter =>
                parameter.ParameterType == typeof(int) ||
                parameter.ParameterType == typeof(double));
    }

    [Fact]
    public void UiTelemetryIsOffByDefault()
    {
        UiPerformanceTelemetry.Enabled.Should().BeFalse();
    }
}
