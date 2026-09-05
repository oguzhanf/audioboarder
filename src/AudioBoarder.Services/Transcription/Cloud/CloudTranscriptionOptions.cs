using Azure.Core;

namespace AudioBoarder.Services.Transcription.Cloud;

public sealed class CloudTranscriptionOptions
{
    public string? Endpoint { get; set; }
    /// <summary>Deployment name. e.g. "gpt-4o-transcribe" or "MAI-Transcribe-1".</summary>
    public string? DeploymentName { get; set; }
    public AudioBoarder.Services.LLM.DeployedModelIdentity? Model { get; set; }
    public bool IsMaiModel => (Model?.Resolve(Endpoint, DeploymentName) ?? DeploymentName)?
        .StartsWith("MAI-", StringComparison.OrdinalIgnoreCase) == true;
    public string? TenantId { get; set; }
    public string? ApiKey { get; set; }
    public bool UseManagedIdentity { get; set; } = true;
    /// <summary>Optional application-provided credential sharing the signed-in token cache.</summary>
    public TokenCredential? Credential { get; set; }
    public string Language { get; set; } = "en";
    public string OpenAIApiVersion { get; set; } = "2025-04-01-preview";

    /// <summary>Which transcription stack to use: "auto"/"cloud" (gpt-4o-transcribe),
    /// "speech" (Azure Speech streaming), or "local"/"whisper".</summary>
    public string Backend { get; set; } = "auto";

    /// <summary>Max length of a single buffered utterance before it is force-flushed
    /// even if the speaker hasn't paused.
    /// <para>
    /// This is the dominant latency term for the batch backend: a continuous speaker
    /// never triggers the silence flush, so EVERY utterance waits this long before it
    /// is even sent. Keep it short — the transcript appearing promptly matters more
    /// than the marginal accuracy gained from a longer context window.
    /// </para></summary>
    public double WindowSeconds { get; set; } = 4.0;

    /// <summary>Trailing silence (no VAD-passed audio) that marks the end of an
    /// utterance and triggers a flush. Lower = snappier, higher = fewer cuts.</summary>
    public int SilenceFlushMs { get; set; } = 380;

    /// <summary>
    /// Ceiling on the backoff applied after a failed batch. A long backoff is correct
    /// for a durable queue but wrong for a live transcript — the user sees a growing
    /// gap and audio piles up behind it, making the next payload larger and slower.
    /// </summary>
    public double MaxRetryBackoffSeconds { get; set; } = 2.0;

    /// <summary>
    /// Hard cap on buffered audio per role. 180 seconds of 16 kHz mono PCM-16 is
    /// 16,000 samples/s × 2 bytes × 180 = 5,760,000 bytes (about 5.8 MB) per role.
    /// Values above <see cref="MaximumMaxBufferedSeconds"/> are clamped so the queue
    /// remains explicitly bounded. Only the oldest PCM is dropped after this limit.
    /// </summary>
    public double MaxBufferedSeconds { get; set; } = DefaultMaxBufferedSeconds;
    public const double DefaultMaxBufferedSeconds = 180.0;
    public const double MaximumMaxBufferedSeconds = 180.0;
    public double EffectiveMaxBufferedSeconds =>
        Math.Clamp(MaxBufferedSeconds, 0, MaximumMaxBufferedSeconds);

    /// <summary>
    /// Domain prompt biasing recognition toward expected vocabulary, sent as the
    /// transcriptions API "prompt" field.
    /// <para>
    /// This is not cosmetic. Measured against a synthesised sample, an empty prompt
    /// left <c>gpt-transcribe</c> hearing "per-view" for "Purview" (6/7 domain terms),
    /// and left both models lower-casing product names. With the prompt below both
    /// scored 7/7 with correct casing. Meetings are full of product nouns, so this
    /// ships switched on.
    /// </para>
    /// </summary>
    public string? Prompt { get; set; } = DefaultVocabularyPrompt;

    /// <summary>
    /// Seed vocabulary for technical/business meetings. Override via
    /// <c>CloudTranscription.Prompt</c> to bias toward your own domain, or set it to
    /// an empty string to disable biasing entirely.
    /// </summary>
    public const string DefaultVocabularyPrompt =
        "Technical meeting audio. Expect product and technology names such as: " +
        "Microsoft Purview, Microsoft Fabric, Power BI, Copilot, Entra, Defender, " +
        "Sentinel, Intune, OneLake, Synapse, Databricks, Azure, SharePoint, Teams, " +
        "Outlook, Kubernetes, Terraform, Postgres, Cosmos DB, Data Catalog, " +
        "and acronyms such as DLP, RBAC, SSO, MFA, API, SLA, KPI, PII, CI/CD, LLM, RAG. " +
        "Preserve the capitalisation of product names.";

    /// <summary>Sampling temperature for the transcription model. 0 = most literal,
    /// least prone to inventing words on noisy audio.</summary>
    public double Temperature { get; set; } = 0.0;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(DeploymentName);
}
