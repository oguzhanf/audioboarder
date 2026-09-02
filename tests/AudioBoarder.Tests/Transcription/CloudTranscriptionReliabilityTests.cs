using System.Net;
using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task RetrySuppressionBacklogStaysBoundedAndReportsDrops()
        {
            var handler = new FixedResponseHandler(HttpStatusCode.TooManyRequests, "PRIVATE RATE BODY");
            var logger = new CaptureLogger<OpenAITranscribeService>();
            await using var service = new OpenAITranscribeService(
                Options.Create(new CloudTranscriptionOptions
                {
                    Endpoint = "https://example.test",
                    DeploymentName = "transcribe",
                    ApiKey = "test-key",
                    MaxBufferedSeconds = 0.2,
                    MaxRetryBackoffSeconds = 0.2,
                }),
                new HttpClient(handler),
                logger);
            await service.InitializeAsync(CancellationToken.None);
            var capturedAt = DateTimeOffset.UtcNow;
            await service.TranscribeAsync(Chunk(6_400, capturedAt), CancellationToken.None);
            await service.FlushAsync(CancellationToken.None, force: true);

            for (var i = 0; i < 10; i++)
                await service.TranscribeAsync(Chunk(6_400, capturedAt.AddSeconds((i + 1) * 0.2)),
                    CancellationToken.None);

            service.Diagnostics.PendingDuration.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(0.2));
            service.Diagnostics.DroppedBytes.Should().BeGreaterThan(0);
            service.Diagnostics.State.Should().Be(TranscriptionRuntimeState.AudioDropped);
            service.Diagnostics.SafeErrorCode.Should().Be("rate_limited");
            logger.Text.Should().NotContain("PRIVATE RATE BODY");
        }

        [Fact]
        public async Task RequeuePreservesLongerServerRetryAfter()
        {
            var serverRetryAfter = TimeSpan.FromMilliseconds(350);
            var handler = new FixedResponseHandler(
                HttpStatusCode.TooManyRequests,
                "PRIVATE RATE BODY",
                serverRetryAfter);
            await using var service = new OpenAITranscribeService(
                Options.Create(new CloudTranscriptionOptions
                {
                    Endpoint = "https://example.test",
                    DeploymentName = "transcribe",
                    ApiKey = "test-key",
                    MaxRetryBackoffSeconds = 0.05,
                }),
                new HttpClient(handler));
            await service.InitializeAsync(CancellationToken.None);
            await service.TranscribeAsync(
                Chunk(4_800, DateTimeOffset.UtcNow), CancellationToken.None);

            await service.FlushAsync(CancellationToken.None, force: true);

            service.Diagnostics.State.Should().Be(TranscriptionRuntimeState.RateLimited);
            service.Diagnostics.RetryAt.Should().NotBeNull();
            service.Diagnostics.RetryAt!.Value.Should().BeAfter(
                DateTimeOffset.UtcNow.AddMilliseconds(200),
                "the local requeue backoff must not shorten a nonzero server Retry-After");
        }

        [Theory]
        [InlineData(30)]
        [InlineData(60)]
        public async Task ServerRetryAfterIsNeverShortened(int retrySeconds)
        {
            // The first 429 permits the built-in retry immediately; the second asks
            // for the long server delay and causes the batch to be requeued.
            var handler = new RetryAfterSequenceHandler(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(retrySeconds));
            await using var service = new OpenAITranscribeService(
                Options.Create(new CloudTranscriptionOptions
                {
                    Endpoint = "https://example.test",
                    DeploymentName = "transcribe",
                    ApiKey = "test-key",
                    MaxRetryBackoffSeconds = 0.05,
                }),
                new HttpClient(handler));
            await service.InitializeAsync(CancellationToken.None);
            await service.TranscribeAsync(
                Chunk(4_800, DateTimeOffset.UtcNow), CancellationToken.None);

            await service.FlushAsync(CancellationToken.None, force: true);

            service.Diagnostics.State.Should().Be(TranscriptionRuntimeState.RateLimited);
            service.Diagnostics.RetryAt.Should().NotBeNull();
            service.Diagnostics.RetryAt!.Value.Should().BeAfter(
                DateTimeOffset.UtcNow.AddSeconds(retrySeconds - 2),
                "a valid Retry-After from Azure must be honored in full");

            var requestsAfterRateLimit = handler.RequestCount;
            await service.FlushAsync(CancellationToken.None);
            await service.FlushAsync(CancellationToken.None, force: true);
            handler.RequestCount.Should().Be(
                requestsAfterRateLimit,
                "neither normal nor stop-flush requests may bypass Azure's Retry-After");
        }

        [Fact]
        public async Task CancellationDuringFirstRateLimitWaitCannotBypassServerDeadline()
        {
            var handler = new RetryAfterSequenceHandler(TimeSpan.FromSeconds(30));
            await using var service = new OpenAITranscribeService(
                Options.Create(new CloudTranscriptionOptions
                {
                    Endpoint = "https://example.test",
                    DeploymentName = "transcribe",
                    ApiKey = "test-key",
                    MaxRetryBackoffSeconds = 0.05,
                }),
                new HttpClient(handler));
            await service.InitializeAsync(CancellationToken.None);
            await service.TranscribeAsync(
                Chunk(4_800, DateTimeOffset.UtcNow), CancellationToken.None);

            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(50));
            var flush = async () =>
                await service.FlushAsync(cancellation.Token, force: true);
            await flush.Should().ThrowAsync<OperationCanceledException>();

            service.Diagnostics.SafeErrorCode.Should().Be("rate_limited");
            service.Diagnostics.RetryAt.Should().NotBeNull();
            service.Diagnostics.RetryAt!.Value.Should().BeAfter(
                DateTimeOffset.UtcNow.AddSeconds(27));

            await service.FlushAsync(CancellationToken.None, force: true);
            handler.RequestCount.Should().Be(
                1,
                "stop-flush must honor the first server Retry-After after cancellation");
        }

        [Fact]
        public async Task SuccessfulSiblingBatchSurvivesOtherStreamCancellation()
        {
            var handler = new MixedBatchHandler();
            await using var service = new OpenAITranscribeService(
                Options.Create(new CloudTranscriptionOptions
                {
                    Endpoint = "https://example.test",
                    DeploymentName = "transcribe",
                    ApiKey = "test-key",
                }),
                new HttpClient(handler));
            await service.InitializeAsync(CancellationToken.None);
            var capturedAt = DateTimeOffset.UtcNow;
            await service.TranscribeAsync(
                Chunk(4_800, capturedAt, AudioStreamRole.Microphone),
                CancellationToken.None);
            await service.TranscribeAsync(
                Chunk(4_800, capturedAt, AudioStreamRole.Loopback),
                CancellationToken.None);

            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(100));
            var result = await service.FlushAsync(
                cancellation.Token, force: true);

            result.Should().ContainSingle();
            result[0].Text.Should().Be("surviving transcript");
            handler.RequestCount.Should().Be(2);
            service.Diagnostics.PendingDuration.Should().BeGreaterThan(TimeSpan.Zero,
                "the canceled sibling batch must be requeued");
        }

        [Fact]
        public async Task DroppingPrefixAdjustsTranscriptTimestampOnFrameBoundary()
        {
            var capturedAt = new DateTimeOffset(2026, 1, 1, 1, 2, 3, TimeSpan.Zero);
            var handler = new FixedResponseHandler(HttpStatusCode.OK, """{"text":"kept speech"}""");
            await using var service = new OpenAITranscribeService(
                Options.Create(new CloudTranscriptionOptions
                {
                    Endpoint = "https://example.test",
                    DeploymentName = "transcribe",
                    ApiKey = "test-key",
                    MaxBufferedSeconds = 0.1,
                }),
                new HttpClient(handler));
            await service.InitializeAsync(CancellationToken.None);

            await service.TranscribeAsync(Chunk(6_400, capturedAt), CancellationToken.None);
            var result = await service.FlushAsync(CancellationToken.None, force: true);

            result.Should().ContainSingle();
            result[0].Start.Should().Be(capturedAt.AddSeconds(0.1));
            result[0].End.Should().Be(capturedAt.AddSeconds(0.2));
            service.Diagnostics.DroppedBytes.Should().Be(3_200);
            service.Diagnostics.DroppedDuration.Should().Be(TimeSpan.FromSeconds(0.1));
        }

        [Fact]
        public async Task RequeueTrimKeepsNewestAudioAndAdjustedTimestamp()
        {
            var capturedAt = new DateTimeOffset(2026, 1, 1, 1, 2, 3, TimeSpan.Zero);
            var handler = new SequenceHandler(
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.OK);
            await using var service = new OpenAITranscribeService(
                Options.Create(new CloudTranscriptionOptions
                {
                    Endpoint = "https://example.test",
                    DeploymentName = "transcribe",
                    ApiKey = "test-key",
                    MaxBufferedSeconds = 0.2,
                    MaxRetryBackoffSeconds = 0,
                }),
                new HttpClient(handler));
            await service.InitializeAsync(CancellationToken.None);

            await service.TranscribeAsync(Chunk(6_400, capturedAt), CancellationToken.None);
            await service.FlushAsync(CancellationToken.None, force: true);
            await service.TranscribeAsync(Chunk(6_400, capturedAt.AddSeconds(0.2)), CancellationToken.None);
            var result = await service.FlushAsync(CancellationToken.None, force: true);

            result.Should().ContainSingle();
            result[0].Start.Should().Be(capturedAt.AddSeconds(0.2));
            result[0].End.Should().Be(capturedAt.AddSeconds(0.4));
            service.Diagnostics.PendingDuration.Should().Be(TimeSpan.Zero);
        }

        [Fact]
        public async Task DefaultLogsContainNeitherTranscriptNorRawFailureBody()
        {
            var transcriptLogger = new CaptureLogger<OpenAITranscribeService>();
            await using (var successful = new OpenAITranscribeService(
                Options.Create(new CloudTranscriptionOptions
                {
                    Endpoint = "https://example.test",
                    DeploymentName = "transcribe",
                    ApiKey = "test-key",
                }),
                new HttpClient(new FixedResponseHandler(
                    HttpStatusCode.OK, """{"text":"TOP SECRET TRANSCRIPT"}""")),
                transcriptLogger))
            {
                await successful.InitializeAsync(CancellationToken.None);
                await successful.TranscribeAsync(Chunk(4_800, DateTimeOffset.UtcNow), CancellationToken.None);
                await successful.FlushAsync(CancellationToken.None, force: true);
            }

            var failureLogger = new CaptureLogger<OpenAITranscribeService>();
            await using (var failed = new OpenAITranscribeService(
                Options.Create(new CloudTranscriptionOptions
                {
                    Endpoint = "https://example.test",
                    DeploymentName = "transcribe",
                    ApiKey = "test-key",
                }),
                new HttpClient(new FixedResponseHandler(
                    HttpStatusCode.ServiceUnavailable, "PRIVATE SERVER BODY")),
                failureLogger))
            {
                await failed.InitializeAsync(CancellationToken.None);
                await failed.TranscribeAsync(Chunk(4_800, DateTimeOffset.UtcNow), CancellationToken.None);
                await failed.FlushAsync(CancellationToken.None, force: true);
            }

            transcriptLogger.Text.Should().NotContain("TOP SECRET TRANSCRIPT");
            failureLogger.Text.Should().NotContain("PRIVATE SERVER BODY");
        }

        private static AudioChunk Chunk(
            int bytes,
            DateTimeOffset capturedAt,
            AudioStreamRole role = AudioStreamRole.Microphone) => new()
        {
            Role = role,
            Format = AudioFormat.Mono16kPcm16,
            CapturedAt = capturedAt,
            Samples = new byte[bytes],
        };

        private sealed class FixedResponseHandler(
            HttpStatusCode status,
            string body,
            TimeSpan? retryAfter = null) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
                if (status == HttpStatusCode.TooManyRequests)
                    response.Headers.RetryAfter =
                        new System.Net.Http.Headers.RetryConditionHeaderValue(
                            retryAfter ?? TimeSpan.Zero);
                response.Headers.TryAddWithoutValidation("x-request-id", "request-123");
                return Task.FromResult(response);
            }

        }

        private sealed class RetryAfterSequenceHandler(
            params TimeSpan[] retryAfterValues) : HttpMessageHandler
        {
            private int _requestCount;
            public int RequestCount => Volatile.Read(ref _requestCount);

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var index = Interlocked.Increment(ref _requestCount) - 1;
                var retryAfter = retryAfterValues[
                    Math.Min(index, retryAfterValues.Length - 1)];
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("PRIVATE RATE BODY"),
                };
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
                return Task.FromResult(response);
            }

        }

        private sealed class MixedBatchHandler : HttpMessageHandler
        {
            private int _requestCount;
            public int RequestCount => Volatile.Read(ref _requestCount);

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var requestNumber = Interlocked.Increment(ref _requestCount);
                if (requestNumber == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"text":"surviving transcript"}"""),
                    });
                }

                var rateLimited = new HttpResponseMessage(
                    HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("PRIVATE RATE BODY"),
                };
                rateLimited.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(30));
                return Task.FromResult(rateLimited);
            }
        }

        private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
        {
            private int _index;
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var index = Interlocked.Increment(ref _index) - 1;
                var status = statuses[Math.Min(index, statuses.Length - 1)];
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(status == HttpStatusCode.OK
                        ? """{"text":"newest speech"}"""
                        : "PRIVATE FAILURE"),
                });
            }
        }

        private sealed class CaptureLogger<T> : ILogger<T>
        {
            private readonly List<string> _messages = new();
            public string Text { get { lock (_messages) return string.Join("\n", _messages); } }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_messages) _messages.Add(formatter(state, exception));
            }
    }
}
