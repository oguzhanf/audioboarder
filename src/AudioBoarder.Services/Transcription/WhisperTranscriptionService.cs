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
public sealed class WhisperTranscriptionService : ITranscriptionService
{
    private readonly ILogger<WhisperTranscriptionService> _logger;
    private readonly WhisperOptions _options;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly List<byte> _pcmBuffer = new();
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
            .WithSegmentEventHandler(seg => _logger.LogTrace("whisper seg: {Text}", seg.Text))
            .Build();
        IsReady = true;
        _logger.LogInformation("Whisper.net ready: model={Model} lang={Lang}", _options.ModelSize, _options.Language);
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (Transcriber is not null) return await Transcriber(chunk, ct).ConfigureAwait(false);
        if (!IsReady || _processor is null) return Array.Empty<TranscriptSegment>();

        lock (_pcmBuffer)
        {
            var span = chunk.Samples.Span;
            foreach (var b in span) _pcmBuffer.Add(b);
        }

        // Accumulate at least WindowSeconds of audio before invoking Whisper.
        var bytesNeeded = (int)(chunk.Format.BytesPerSecond * _windowDuration.TotalSeconds);
        byte[]? toProcess = null;
        lock (_pcmBuffer)
        {
            if (_pcmBuffer.Count >= bytesNeeded)
            {
                toProcess = _pcmBuffer.ToArray();
                _pcmBuffer.Clear();
            }
        }
        if (toProcess is null) return Array.Empty<TranscriptSegment>();

        var role = chunk.Role;
        var speaker = role == AudioStreamRole.Loopback ? TranscriptSpeaker.Remote : TranscriptSpeaker.Local;
        var start = chunk.CapturedAt - _windowDuration;
        var end = chunk.CapturedAt;

        var floats = PcmToFloat(toProcess);
        var segments = new List<TranscriptSegment>();
        await foreach (var s in _processor.ProcessAsync(floats, ct).ConfigureAwait(false))
        {
            var cleaned = CleanWhisperOutput(s.Text);
            if (string.IsNullOrWhiteSpace(cleaned)) continue;
            segments.Add(new TranscriptSegment(Guid.NewGuid(), speaker, cleaned,
                start + s.Start, start + s.End));
        }
        return segments;
    }

    public async Task<IReadOnlyList<TranscriptSegment>> FlushAsync(CancellationToken ct, bool force = false)
    {
        if (!IsReady || _processor is null) return Array.Empty<TranscriptSegment>();
        byte[] buffered;
        lock (_pcmBuffer)
        {
            // Only flush if we have enough audio to make Whisper happy.
            // Whisper.net needs ~0.5s minimum; otherwise it produces nothing useful.
            var minBytes = 16_000 * 2 / 2; // 0.5s at 16kHz mono PCM-16
            if (_pcmBuffer.Count < minBytes) return Array.Empty<TranscriptSegment>();
            buffered = _pcmBuffer.ToArray();
            _pcmBuffer.Clear();
        }
        var floats = PcmToFloat(buffered);
        var now = DateTimeOffset.UtcNow;
        var segments = new List<TranscriptSegment>();
        await foreach (var s in _processor.ProcessAsync(floats, ct).ConfigureAwait(false))
        {
            var cleaned = CleanWhisperOutput(s.Text);
            if (string.IsNullOrWhiteSpace(cleaned)) continue;
            segments.Add(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, cleaned, now, now));
        }
        return segments;
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null) await _processor.DisposeAsync().ConfigureAwait(false);
        _factory?.Dispose();
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
