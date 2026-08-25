namespace AudioBoarder.Services.Transcription.Cloud;

public sealed class CloudTranscriptionOptions
{
    public string? Endpoint { get; set; }
    /// <summary>Deployment name. e.g. "gpt-4o-transcribe" or "MAI-Transcribe-1".</summary>
    public string? DeploymentName { get; set; }
    public string? TenantId { get; set; }
    public string? ApiKey { get; set; }
    public bool UseManagedIdentity { get; set; } = true;
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
    /// Hard cap on buffered audio per role. Once a backlog exceeds this the OLDEST
    /// audio is dropped rather than growing the payload without bound, which is how a
    /// transient outage used to snowball into a minute of missing transcript.
    /// </summary>
    public double MaxBufferedSeconds { get; set; } = 20.0;

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

