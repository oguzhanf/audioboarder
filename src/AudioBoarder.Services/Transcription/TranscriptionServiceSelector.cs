using AudioBoarder.Core.Transcript;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.Services.Transcription;

public enum TranscriptionBackendKind
{
    Cloud,
    AzureSpeech,
    LocalWhisper,
}

public sealed record TranscriptionCandidate(
    TranscriptionBackendKind Kind,
    ITranscriptionService Service);

public sealed record TranscriptionSelection(
    ITranscriptionService Service,
    bool IsFallback = false,
    string? SafeErrorCode = null,
    string? StatusMessage = null);

public interface ITranscriptionServiceSelector
{
    Task<TranscriptionSelection> SelectAsync(CancellationToken ct);
}

/// <summary>
/// Selects a backend before capture starts. A failed backend is never swapped into
/// an active utterance; the preferred backend is retried on the next selection.
/// </summary>
public sealed class TranscriptionServiceSelector : ITranscriptionServiceSelector
{
    private readonly Func<IReadOnlyList<TranscriptionCandidate>> _candidateFactory;
    private readonly ILogger<TranscriptionServiceSelector> _logger;

    public TranscriptionServiceSelector(
        Func<IReadOnlyList<TranscriptionCandidate>> candidateFactory,
        ILogger<TranscriptionServiceSelector>? logger = null)
    {
        _candidateFactory = candidateFactory ?? throw new ArgumentNullException(nameof(candidateFactory));
        _logger = logger ?? NullLogger<TranscriptionServiceSelector>.Instance;
    }

    public async Task<TranscriptionSelection> SelectAsync(CancellationToken ct)
    {
        var candidates = _candidateFactory();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No transcription backends are available.");

        TranscriptionCandidate? firstFailed = null;
        string? firstErrorCode = null;
        Exception? lastFailure = null;

        foreach (var candidate in candidates)
        {
            try
            {
                // Revalidate remote singletons for every health/listen selection.
                // A prior probe or session may have left IsReady=true before a
                // runtime authentication failure. Local Whisper initialization is
                // expensive and its readiness is process-local, so it can be reused.
                if (candidate.Kind != TranscriptionBackendKind.LocalWhisper ||
                    !candidate.Service.IsReady)
                    await candidate.Service.InitializeAsync(ct).ConfigureAwait(false);

                if (!candidate.Service.IsReady)
                    throw new TranscriptionInitializationException(
                        "The transcription backend did not become ready.",
                        "credential_unavailable");

                if (firstFailed is null)
                    return new TranscriptionSelection(candidate.Service);

                var message = BuildFallbackMessage(firstFailed.Kind, candidate.Kind, firstErrorCode);
                _logger.LogWarning(
                    "Transcription backend fallback selected; preferred={Preferred} selected={Selected} category={Category}",
                    firstFailed.Kind, candidate.Kind, firstErrorCode);
                return new TranscriptionSelection(
                    candidate.Service,
                    IsFallback: true,
                    SafeErrorCode: firstErrorCode,
                    StatusMessage: message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstFailed ??= candidate;
                firstErrorCode ??= TranscriptionInitializationException.SafeCode(ex);
                lastFailure = ex;
                _logger.LogWarning(
                    "Transcription backend unavailable; backend={Backend} category={Category}",
                    candidate.Kind, TranscriptionInitializationException.SafeCode(ex));
            }
        }

        throw new TranscriptionInitializationException(
            "No transcription backend could be initialized.",
            firstErrorCode ?? "transcription_initialization",
            lastFailure);
    }

    private static string BuildFallbackMessage(
        TranscriptionBackendKind unavailable,
        TranscriptionBackendKind selected,
        string? safeErrorCode)
    {
        var unavailableName = unavailable switch
        {
            TranscriptionBackendKind.Cloud => "cloud",
            TranscriptionBackendKind.AzureSpeech => "Azure Speech",
            _ => "local Whisper",
        };
        var selectedName = selected switch
        {
            TranscriptionBackendKind.Cloud => "cloud transcription",
            TranscriptionBackendKind.AzureSpeech => "Azure Speech",
            _ => "local Whisper",
        };
        var reason = safeErrorCode switch
        {
            "authentication_required" => "authentication required",
            "credential_unavailable" => "credential unavailable",
            "authentication_failed" => "authentication failed",
            _ => "unavailable",
        };
        return $"{unavailableName} {reason}, using {selectedName}";
    }
}

public sealed class TranscriptionInitializationException : InvalidOperationException
{
    public TranscriptionInitializationException(
        string message,
        string safeErrorCode,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SafeErrorCode = safeErrorCode;
    }

    public string SafeErrorCode { get; }

    public static string SafeCode(Exception ex) => ex switch
    {
        TranscriptionInitializationException initialization => initialization.SafeErrorCode,
        Azure.Identity.AuthenticationRequiredException => "authentication_required",
        Azure.Identity.CredentialUnavailableException => "credential_unavailable",
        Azure.Identity.AuthenticationFailedException => "authentication_failed",
        HttpRequestException => "network",
        _ => "transcription_initialization",
    };
}
