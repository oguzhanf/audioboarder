using System.Net;
using AudioBoarder.Core.Audio;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Tests.Transcription;

public class CloudTranscriptionReliabilityTests
{
    [Fact]
    public async Task FailedOpenAIRequestRetainsAudioForLaterRetry()
    {
        var handler = new AlwaysFailHandler();
        await using var service = new OpenAITranscribeService(
            Options.Create(new CloudTranscriptionOptions
            {
                Endpoint = "https://example.test",
                DeploymentName = "transcribe",
                ApiKey = "test-key",
            }),
            new HttpClient(handler));
        await service.InitializeAsync(CancellationToken.None);
        await service.TranscribeAsync(new AudioChunk
        {
            Role = AudioStreamRole.Microphone,
            Format = AudioFormat.Mono16kPcm16,
            CapturedAt = DateTimeOffset.UtcNow,
            Samples = new byte[4_800],
        }, CancellationToken.None);

        await service.FlushAsync(CancellationToken.None, force: true);
        await service.FlushAsync(CancellationToken.None, force: true);

        handler.RequestCount.Should().Be(6,
            "each forced flush retries the retained audio instead of discarding it after the first failure");
    }

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("temporarily unavailable"),
            });
        }
    }
}
