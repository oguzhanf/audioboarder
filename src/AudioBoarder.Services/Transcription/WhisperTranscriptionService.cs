using System.Net.Http;
using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.net;
using Whisper.net.Ggml;

namespace AudioBoarder.Services.Transcription;

/// <summary>
/// Real Whisper.net-backed transcription. Lazily downloads the GGML model
/// to <c>%LOCALAPPDATA%\AudioBoarder\models</c> if it's missing.
/// </summary>
public sealed class WhisperTranscriptionService : ITranscriptionService, ITranscriptionDiagnosticsSource
{
    private readonly ILogger<WhisperTranscriptionService> _logger;
    private readonly WhisperOptions _options;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly Dictionary<AudioStreamRole, BufferedRole> _buffers = new();
    private readonly object _bufferGate = new();
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private long _droppedBytes;
    public event EventHandler<TranscriptionDiagnostics>? DiagnosticsChanged;
    public TranscriptionDiagnostics Diagnostics { get; private set; } = TranscriptionDiagnostics.Healthy;
    private readonly TimeSpan _windowDuration;

    /// <summary>Test hook: override to bypass real Whisper model loading.</summary>
    public Func<WhisperOptions, CancellationToken, Task>? ModelLoader { get; set; }
    public Func<AudioChunk, CancellationToken, Task<IReadOnlyList<TranscriptSegment>>>? Transcriber { get; set; }

    public WhisperTranscriptionService(WhisperOptions options, ILogger<WhisperTranscriptionService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<WhisperTranscriptionService>.Instance;
        _windowDuration = TimeSpan.FromSeconds(options.WindowSeconds);
    }

    public string Name => $"Whisper.net ({_options.ModelSize})";
    public bool IsReady { get; private set; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (IsReady) return;

        // Test-injection hook
        if (ModelLoader is not null)
        {
            await ModelLoader(_options, ct).ConfigureAwait(false);
            IsReady = true;
            return;
        }

        var modelPath = await EnsureModelAsync(ct).ConfigureAwait(false);
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(_options.Language) ? "auto" : _options.Language!)
            .Build();
        IsReady = true;
        _logger.LogInformation("Whisper.net ready: model={Model} lang={Lang}", _options.ModelSize, _options.Language);
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (Transcriber is not null) return await Transcriber(chunk, ct).ConfigureAwait(false);
        if (!IsReady || _processor is null) return Array.Empty<TranscriptSegment>();

        if (chunk.Format != AudioFormat.Mono16kPcm16)
            throw new ArgumentException("Local transcription requires mono 16 kHz PCM-16 audio.");
        lock (_bufferGate)
        {
            if (!_buffers.TryGetValue(chunk.Role, out var buffer)) _buffers[chunk.Role] = buffer = new BufferedRole();
            if (buffer.Audio.Length == 0) buffer.Start = chunk.CapturedAt;
            buffer.Audio.Write(chunk.Samples.Span);
            buffer.LastAppend = DateTimeOffset.UtcNow;
            const int maximum = 180 * 32000;
            if (buffer.Audio.Length > maximum)
            {
                var audio = buffer.Audio.ToArray();
                var discard = audio.Length - maximum;
                buffer.Audio.SetLength(0);
                buffer.Audio.Write(audio.AsSpan(discard));
                buffer.Start += TimeSpan.FromSeconds(discard / 32000d);
                _droppedBytes += discard;
            }
            PublishDiagnostics();
        }
        return Array.Empty<TranscriptSegment>();
    }

    public async Task<IReadOnlyList<TranscriptSegment>> FlushAsync(CancellationToken ct, bool force = false)
    {
        if (!IsReady || _processor is null) return Array.Empty<TranscriptSegment>();
        await _inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batches = new List<(AudioStreamRole Role, byte[] Audio, DateTimeOffset Start)>();
            lock (_bufferGate)
            {
                foreach (var (role, buffer) in _buffers)
                {
                    if (buffer.Audio.Length < 16000) continue;
                    if (!force && buffer.Audio.Length < _windowDuration.TotalSeconds * 32000 &&
                        DateTimeOffset.UtcNow - buffer.LastAppend < TimeSpan.FromMilliseconds(700)) continue;
                    batches.Add((role, buffer.Audio.ToArray(), buffer.Start));
                    buffer.Audio.SetLength(0);
                }
                PublishDiagnostics();
            }
            var segments = new List<TranscriptSegment>();
            foreach (var batch in batches)
            {
                var speaker = batch.Role == AudioStreamRole.Microphone ? TranscriptSpeaker.Local : TranscriptSpeaker.Remote;
                await foreach (var s in _processor.ProcessAsync(PcmToFloat(batch.Audio), ct).ConfigureAwait(false))
                {
                    var cleaned = CleanWhisperOutput(s.Text);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        segments.Add(new(Guid.NewGuid(), speaker, cleaned, batch.Start + s.Start, batch.Start + s.End));
                }
            }
            return segments;
        }
        finally { _inferenceGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null) await _processor.DisposeAsync().ConfigureAwait(false);
        _factory?.Dispose();
        _inferenceGate.Dispose();
        foreach (var buffer in _buffers.Values) buffer.Audio.Dispose();
    }

    private void PublishDiagnostics()
    {
        var pending = TimeSpan.FromSeconds(_buffers.Values.Sum(b => b.Audio.Length) / 32000d);
        Diagnostics = new(_droppedBytes > 0 ? TranscriptionRuntimeState.AudioDropped : TranscriptionRuntimeState.Healthy,
            pending, DroppedDuration: TimeSpan.FromSeconds(_droppedBytes / 32000d), DroppedBytes: _droppedBytes,
            StatusMessage: "Using explicitly selected local transcription.");
        DiagnosticsChanged?.Invoke(this, Diagnostics);
    }

    private sealed class BufferedRole
    {
        public MemoryStream Audio { get; } = new();
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset LastAppend { get; set; }
    }

    /// <summary>
    /// Whisper.net emits noise tokens like "[BLANK_AUDIO]", "(silence)", "[Music]",
    /// "[Applause]", "(typing)" etc. when it thinks the audio contains no speech or
    /// just background. These pollute the caption pane and confuse the LLM, so we
    /// strip them at source and treat the segment as empty.
    /// </summary>
    internal static string CleanWhisperOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // Remove any [bracketed] or (parenthetical) annotation
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            text, @"\[[^\]]*\]|\([^)]*\)", string.Empty).Trim();
        if (stripped.Length == 0) return string.Empty;
        // After stripping, ignore very short non-alphanumeric noise like ".", "-", "..."
        if (!System.Linq.Enumerable.Any(stripped, char.IsLetterOrDigit)) return string.Empty;
        return stripped;
    }

    private async Task<string> EnsureModelAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.ModelPath) && File.Exists(_options.ModelPath))
            return _options.ModelPath!;

        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBoarder", "models");
        Directory.CreateDirectory(modelDir);
        var ggmlType = ResolveGgmlType(_options.ModelSize);
        var modelPath = Path.Combine(modelDir, $"ggml-{_options.ModelSize.ToLowerInvariant()}.bin");
        if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 1024 * 1024)
            return modelPath;

        if (!_options.AutoDownload)
            throw new FileNotFoundException(
                $"Whisper model not found at {modelPath} and AutoDownload=false. Download a ggml model and set Whisper.ModelPath.",
                modelPath);

        _logger.LogInformation("Downloading Whisper model {Type}", ggmlType);
        // Whisper.net 1.9 made the downloader instance-based; the shared instance is
        // exposed as WhisperGgmlDownloader.Default.
        using var stream = await WhisperGgmlDownloader.Default
            .GetGgmlModelAsync(ggmlType, cancellationToken: ct).ConfigureAwait(false);
        await using var fs = File.OpenWrite(modelPath);
        await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
        return modelPath;
    }

    private static GgmlType ResolveGgmlType(string size) => size.ToLowerInvariant() switch
    {
        "tiny" => GgmlType.Tiny,
        "tiny.en" => GgmlType.TinyEn,
        "base" => GgmlType.Base,
        "base.en" => GgmlType.BaseEn,
        "small" => GgmlType.Small,
        "small.en" => GgmlType.SmallEn,
        "medium" => GgmlType.Medium,
        "medium.en" => GgmlType.MediumEn,
        "large" or "large-v3" or "large-v3-turbo" => GgmlType.LargeV3,
        _ => GgmlType.Base,
    };

    private static float[] PcmToFloat(byte[] pcm)
    {
        var count = pcm.Length / 2;
        var floats = new float[count];
        for (var i = 0; i < count; i++)
        {
            var s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            floats[i] = s / 32768f;
        }
        return floats;
    }
}

public sealed record WhisperOptions(
    string ModelSize = "base",
    string? ModelPath = null,
    string? Language = "en",
    double WindowSeconds = 3.0,
    bool AutoDownload = true);
