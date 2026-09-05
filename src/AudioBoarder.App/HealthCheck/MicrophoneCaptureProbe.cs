using System.Buffers.Binary;
using AudioBoarder.Core.Audio;

namespace AudioBoarder.App.HealthCheck;

internal sealed record MicrophoneCaptureResult(long Chunks, long Bytes, double Peak, bool CaptureFailed);

internal static class MicrophoneCaptureProbe
{
    public static async Task<MicrophoneCaptureResult> MeasureAsync(
        IAudioCaptureSource source, TimeSpan duration, CancellationToken ct)
    {
        var gate = new object();
        long chunks = 0, bytes = 0;
        double peak = 0;
        var failed = false;
        void Captured(object? sender, AudioChunk chunk)
        {
            double maximum = 0;
            var samples = chunk.Samples.Span;
            for (var index = 0; index + 1 < samples.Length; index += 2)
                maximum = Math.Max(maximum, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(samples[index..])) / 32768d);
            lock (gate)
            {
                chunks++;
                bytes += samples.Length;
                peak = Math.Max(peak, maximum);
            }
        }
        void Failed(object? sender, AudioCaptureError error) { lock (gate) failed = true; }
        source.ChunkCaptured += Captured;
        source.CaptureFailed += Failed;
        try
        {
            await source.StartAsync(ct);
            await Task.Delay(duration, ct);
        }
        finally
        {
            try { await source.StopAsync(CancellationToken.None); }
            finally
            {
                source.ChunkCaptured -= Captured;
                source.CaptureFailed -= Failed;
            }
        }
        lock (gate) return new(chunks, bytes, peak, failed);
    }
}
