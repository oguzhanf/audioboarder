using System.IO;
using System.Text.Json;
using AudioBoarder.Core.Audio;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NAudio.Wave;

namespace AudioBoarder.App.Demo;

public sealed record DemoUtterance(double StartSeconds, string Speaker, string Role, string Voice, string Text, string WavFile);
public sealed record MeetingDemoManifest(double DurationSeconds, IReadOnlyList<DemoUtterance> Utterances);

public static class MeetingDemo
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static MeetingDemoManifest Load(string path) =>
        JsonSerializer.Deserialize<MeetingDemoManifest>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("The meeting demo manifest is empty.");

    public static async Task GenerateAsync(IServiceProvider services, string folder, CancellationToken ct)
    {
        var credentials = services.GetRequiredService<IAzureCredentialProvider>();
        if (!await credentials.TryRestoreAsync(ct) || !credentials.TryGetSignedInCredential(out var credential) || credential is null)
            throw new InvalidOperationException("Connect Azure first to generate the synthetic demo voices.");
        var settings = services.GetRequiredService<IOptions<AudioBoarderSettings>>().Value.AzureSpeech;
        if (string.IsNullOrWhiteSpace(settings.ResourceId) || string.IsNullOrWhiteSpace(settings.Region))
            throw new InvalidOperationException("Run automatic workspace setup before generating the demo.");
        Directory.CreateDirectory(folder);
        var lines = new DemoUtterance[]
        {
            new(0, "Narrator", "narration", "en-US-GuyNeural",
                "Meet AudioBoarder. Turn a live technical conversation into a shared architecture, while you speak.", "01-intro.wav"),
            new(10, "Alex - Solution engineer", "local", "en-US-GuyNeural",
                "Let's design our customer portal. Customers enter through Azure Front Door. Front Door sends requests to Azure App Service.", "02-architecture.wav"),
            new(24, "Maya - Customer", "remote", "en-US-JennyNeural",
                "How do we stop public access to the data?", "03-customer-question.wav"),
            new(32, "Alex - Solution engineer", "local", "en-US-GuyNeural",
                "App Service reaches Azure SQL Database through a private endpoint. Microsoft Entra ID handles sign-in. The database is not exposed to the internet.", "04-security-answer.wav"),
            new(49, "Jordan - Security lead", "remote", "en-US-AriaNeural",
                "What happens if an order processor goes offline?", "05-resilience-question.wav"),
            new(57, "Alex - Solution engineer", "local", "en-US-GuyNeural",
                "App Service puts orders on Azure Service Bus. Azure Functions processes the queue. Failed orders go to a dead-letter queue, with an alert for support.", "06-resilience-answer.wav"),
            new(75, "Maya - Customer", "remote", "en-US-JennyNeural",
                "We need auditability and a recovery time under fifteen minutes.", "07-requirement.wav"),
            new(84, "Alex - Solution engineer", "local", "en-US-GuyNeural",
                "Capture that as a requirement, not another server. Decision: use managed identities rather than stored passwords. Open question: confirm the recovery target with operations.", "08-decision.wav"),
            new(101, "Narrator", "narration", "en-US-GuyNeural",
                "The architecture grows in the meeting. Questions, answers, decisions and constraints stay with it. Export the diagram and leave with a shared understanding.", "09-product-value.wav"),
            new(114.5, "Narrator", "narration", "en-US-GuyNeural",
                "AudioBoarder. From conversation to clarity.", "10-close.wav"),
        };
        foreach (var line in lines)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
            var config = SpeechConfig.FromAuthorizationToken($"aad#{settings.ResourceId}#{token.Token}", settings.Region);
            config.SpeechSynthesisVoiceName = line.Voice;
            config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);
            using var audio = AudioConfig.FromWavFileOutput(Path.Combine(folder, line.WavFile));
            using var synth = new SpeechSynthesizer(config, audio);
            using var result = await synth.SpeakTextAsync(line.Text).WaitAsync(ct);
            if (result.Reason != ResultReason.SynthesizingAudioCompleted)
                throw new InvalidOperationException($"Synthetic voice generation failed for {line.WavFile}.");
            Console.WriteLine($"Generated {line.WavFile}");
        }
        var manifest = new MeetingDemoManifest(120, lines);
        await File.WriteAllTextAsync(Path.Combine(folder, "meeting.json"), JsonSerializer.Serialize(manifest, JsonOptions), ct);
    }
}

public sealed class MeetingReplayClock
{
    private readonly object _gate = new();
    private DateTimeOffset? _start;
    public DateTimeOffset Start { get { lock (_gate) return _start ??= DateTimeOffset.UtcNow.AddMilliseconds(500); } }
    public DateTimeOffset? StartedAt { get { lock (_gate) return _start; } }
}

public sealed record DemoSession(string ManifestPath, string OutputDirectory, MeetingReplayClock Clock);

public sealed class MeetingReplaySource : IAudioCaptureSource
{
    private readonly MeetingReplayClock _clock;
    private readonly byte[] _pcm;
    private CancellationTokenSource? _cancel;
    private Task? _pump;
    public AudioStreamRole Role { get; }
    public AudioFormat OutputFormat => AudioFormat.Mono16kPcm16;
    public bool IsRunning { get; private set; }
    public event EventHandler<AudioChunk>? ChunkCaptured;
    public event EventHandler<AudioCaptureError>? CaptureFailed { add { } remove { } }

    public MeetingReplaySource(string manifestPath, AudioStreamRole role, MeetingReplayClock clock)
    {
        _clock = clock;
        Role = role;
        var manifest = MeetingDemo.Load(manifestPath);
        _pcm = new byte[checked((int)(manifest.DurationSeconds * OutputFormat.BytesPerSecond))];
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        foreach (var utterance in manifest.Utterances.Where(u => u.Role == (role == AudioStreamRole.Microphone ? "local" : "remote")))
        {
            using var wav = new WaveFileReader(Path.Combine(root, utterance.WavFile));
            if (wav.WaveFormat.SampleRate != 16000 || wav.WaveFormat.BitsPerSample != 16 || wav.WaveFormat.Channels != 1)
                throw new InvalidDataException("Demo audio must be mono PCM-16 at 16 kHz.");
            var bytes = new byte[checked((int)wav.Length)];
            var read = wav.Read(bytes, 0, bytes.Length);
            var offset = checked((int)(utterance.StartSeconds * OutputFormat.BytesPerSecond));
            if (offset + read > _pcm.Length) throw new InvalidDataException("A demo utterance extends beyond the timeline.");
            bytes.AsSpan(0, read).CopyTo(_pcm.AsSpan(offset));
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (IsRunning) return Task.CompletedTask;
        IsRunning = true;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var start = _clock.Start;
        _pump = Task.Run(async () =>
        {
            const int bytesPerChunk = 960;
            try
            {
                for (var offset = 0; offset < _pcm.Length; offset += bytesPerChunk)
                {
                    var target = start + TimeSpan.FromSeconds(offset / 32000d);
                    var wait = target - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, _cancel.Token);
                    _cancel.Token.ThrowIfCancellationRequested();
                    ChunkCaptured?.Invoke(this, new AudioChunk
                    {
                        Role = Role, Format = OutputFormat, CapturedAt = target,
                        Samples = _pcm.AsMemory(offset, Math.Min(bytesPerChunk, _pcm.Length - offset)),
                    });
                }
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            finally { IsRunning = false; }
        }, ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cancel?.Cancel();
        if (_pump is not null) await _pump.WaitAsync(ct);
        IsRunning = false;
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _cancel?.Dispose();
    }
}
