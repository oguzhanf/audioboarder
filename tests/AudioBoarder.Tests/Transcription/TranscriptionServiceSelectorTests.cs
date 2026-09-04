using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Transcription;

namespace AudioBoarder.Tests.Transcription;

public class TranscriptionServiceSelectorTests
{
    [Fact]
    public async Task FallsBackFromCloudToAzureSpeechInOrder()
    {
        var calls = new List<string>();
        var cloud = new FakeService("cloud", calls, "authentication_required");
        var speech = new FakeService("speech", calls);
        var local = new FakeService("local", calls);
        var selector = CreateSelector(cloud, speech, local);

        var result = await selector.SelectAsync(CancellationToken.None);

        result.Service.Should().BeSameAs(speech);
        result.IsFallback.Should().BeTrue();
        result.SafeErrorCode.Should().Be("authentication_required");
        result.StatusMessage.Should().Be("cloud authentication required, using Azure Speech");
        calls.Should().Equal("cloud", "speech");
    }

    [Fact]
    public async Task FallsBackFromCloudAndSpeechToLocalWhisper()
    {
        var calls = new List<string>();
        var cloud = new FakeService("cloud", calls, "network");
        var speech = new FakeService("speech", calls, "credential_unavailable");
        var local = new FakeService("local", calls);
        var selector = CreateSelector(cloud, speech, local);

        var result = await selector.SelectAsync(CancellationToken.None);

        result.Service.Should().BeSameAs(local);
        result.IsFallback.Should().BeTrue();
        result.StatusMessage.Should().Be("cloud unavailable, using local Whisper");
        calls.Should().Equal("cloud", "speech", "local");
    }

    [Fact]
    public async Task PreferredCloudIsRetriedOnEverySelection()
    {
        var calls = new List<string>();
        var cloud = new FakeService("cloud", calls, "network");
        var local = new FakeService("local", calls);
        var selector = new TranscriptionServiceSelector(() =>
            new[]
            {
                new TranscriptionCandidate(TranscriptionBackendKind.Cloud, cloud),
                new TranscriptionCandidate(TranscriptionBackendKind.LocalWhisper, local),
            });

        await selector.SelectAsync(CancellationToken.None);
        await selector.SelectAsync(CancellationToken.None);

        calls.Should().Equal("cloud", "local", "cloud");
    }

    private static TranscriptionServiceSelector CreateSelector(
        ITranscriptionService cloud,
        ITranscriptionService speech,
        ITranscriptionService local) =>
        new(() =>
            new[]
            {
                new TranscriptionCandidate(TranscriptionBackendKind.Cloud, cloud),
                new TranscriptionCandidate(TranscriptionBackendKind.AzureSpeech, speech),
                new TranscriptionCandidate(TranscriptionBackendKind.LocalWhisper, local),
            });

    private sealed class FakeService(
        string name,
        List<string> calls,
        string? failureCode = null) : ITranscriptionService
    {
        public string Name => name;
        public bool IsReady { get; private set; }

        public Task InitializeAsync(CancellationToken ct)
        {
            calls.Add(name);
            if (failureCode is not null)
                throw new TranscriptionInitializationException(
                    $"{name} unavailable",
                    failureCode);
            IsReady = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioChunk chunk,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());

        public Task<IReadOnlyList<TranscriptSegment>> FlushAsync(
            CancellationToken ct,
            bool force = false) =>
            Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
