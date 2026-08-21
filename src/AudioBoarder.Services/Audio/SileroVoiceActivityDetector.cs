using AudioBoarder.Core.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioBoarder.Services.Audio;

/// <summary>
/// Silero VAD via ONNX Runtime. The model expects 16 kHz mono PCM float
/// samples. We hold a single InferenceSession and rolling LSTM state across
/// calls so detection is stable across consecutive 30 ms windows.
/// </summary>
/// <remarks>
/// The Silero model file is not bundled. Construct with an explicit
/// <paramref name="modelPath"/> pointing at a downloaded <c>silero_vad.onnx</c>,
/// or call <see cref="TryCreate"/> which returns null if the model is missing
/// — letting callers fall back to <see cref="EnergyVoiceActivityDetector"/>.
/// </remarks>
public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector, IDisposable
{
    private readonly InferenceSession _session;
    private readonly float _threshold;
    private readonly ILogger<SileroVoiceActivityDetector> _logger;
    private float[] _state;
    private float[] _context;

    public SileroVoiceActivityDetector(string modelPath, float threshold = 0.5f,
        ILogger<SileroVoiceActivityDetector>? logger = null)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Silero ONNX model not found", modelPath);
        _session = new InferenceSession(modelPath);
        _threshold = threshold;
        _logger = logger ?? NullLogger<SileroVoiceActivityDetector>.Instance;
        _state = new float[2 * 1 * 128]; // LSTM (h,c) per Silero v5
        _context = Array.Empty<float>();
    }

    public static SileroVoiceActivityDetector? TryCreate(string? modelPath, float threshold = 0.5f,
        ILogger<SileroVoiceActivityDetector>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return null;
        try { return new SileroVoiceActivityDetector(modelPath, threshold, logger); }
        catch { return null; }
    }

    public bool IsSpeech(AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Format.SampleRate != 16_000 || chunk.Format.Channels != 1 || chunk.Format.BitsPerSample != 16)
            throw new NotSupportedException("Silero VAD expects 16 kHz mono PCM-16 audio");

        // Convert PCM-16 -> float[-1..1]
        var span = chunk.Samples.Span;
        var sampleCount = span.Length / 2;
        if (sampleCount == 0) return false;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var s = (short)(span[i * 2] | (span[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }

        // Silero v5 expects exactly 512 samples per call at 16 kHz.
        const int windowSize = 512;
        var maxProb = 0f;

        // Concatenate prior partial-window leftover with this chunk
        var input = _context.Length == 0
            ? samples
            : Concat(_context, samples);
        var offset = 0;
        while (offset + windowSize <= input.Length)
        {
            var window = new float[windowSize];
            Array.Copy(input, offset, window, 0, windowSize);
            var prob = InferOne(window);
            if (prob > maxProb) maxProb = prob;
            offset += windowSize;
        }

        // Stash any leftover samples to combine with the next chunk
        var leftover = input.Length - offset;
        _context = leftover > 0 ? input.AsSpan(offset).ToArray() : Array.Empty<float>();

        return maxProb >= _threshold;
    }

    private float InferOne(float[] window)
    {
        var samplesTensor = new DenseTensor<float>(window, new[] { 1, window.Length });
        var srTensor = new DenseTensor<long>(new[] { 16_000L }, new[] { 1 });
        var stateTensor = new DenseTensor<float>(_state, new[] { 2, 1, 128 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", samplesTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
        };

        try
        {
            using var results = _session.Run(inputs);
            var output = results.First(r => r.Name == "output" || r.Name == "logits");
            var prob = output.AsEnumerable<float>().First();
            var newState = results.First(r => r.Name == "stateN" || r.Name == "state").AsEnumerable<float>().ToArray();
            Array.Copy(newState, _state, Math.Min(newState.Length, _state.Length));
            return prob;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Silero inference failed; treating as non-speech");
            return 0f;
        }
    }

    private static float[] Concat(float[] a, float[] b)
    {
        var c = new float[a.Length + b.Length];
        Array.Copy(a, 0, c, 0, a.Length);
        Array.Copy(b, 0, c, a.Length, b.Length);
        return c;
    }

    public void Dispose() => _session.Dispose();
}
