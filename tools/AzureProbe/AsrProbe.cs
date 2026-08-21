using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using AudioBoarder.Core.Audio;

namespace AzureProbe;

/// <summary>
/// Reproduces exactly what AudioPipeline feeds the transcription API, so we can
/// tell an audio-handling bug apart from model quality.
///
/// The pipeline splits capture into 30 ms chunks, runs each through the energy
/// VAD, and DROPS every chunk that scores below the threshold before appending to
/// the utterance buffer. That splices out inter-word pauses, quiet fricatives and
/// word onsets. This probe transcribes the same audio twice — continuous, and
/// VAD-spliced using the real <see cref="EnergyVoiceActivityDetector"/> — and
/// prints both so the difference is measurable rather than asserted.
/// </summary>
public static class AsrProbe
{
    public static async Task<int> RunAsync(string endpoint, string deployment, string wavPath,
        double threshold, int chunkMs, string? apiVersion)
    {
        apiVersion ??= "2025-04-01-preview";
        var (pcm, sampleRate, channels, bits) = ReadWav(wavPath);
        Console.WriteLine($"[asr] {Path.GetFileName(wavPath)}: {sampleRate} Hz, {channels}ch, {bits}-bit, {pcm.Length:N0} bytes ({pcm.Length / (double)(sampleRate * channels * bits / 8):F1}s)");

        if (bits != 16 || channels != 1)
        {
            Console.WriteLine("[asr] Expected 16-bit mono PCM. Convert first.");
            return 2;
        }

        var fmt = new AudioFormat(sampleRate, 1, 16);
        var bytesPerChunk = (int)(fmt.BytesPerSecond * (chunkMs / 1000.0));
        if (bytesPerChunk % 2 != 0) bytesPerChunk--;

        var vad = new EnergyVoiceActivityDetector(threshold);
        var kept = new MemoryStream();
        int total = 0, passed = 0;

        for (var off = 0; off + bytesPerChunk <= pcm.Length; off += bytesPerChunk)
        {
            total++;
            var slice = new byte[bytesPerChunk];
            Buffer.BlockCopy(pcm, off, slice, 0, bytesPerChunk);
            var chunk = new AudioChunk
            {
                Role = AudioStreamRole.Microphone,
                Format = fmt,
                CapturedAt = DateTimeOffset.UtcNow,
                Samples = slice,
            };
            if (vad.IsSpeech(chunk)) { passed++; kept.Write(slice); }
        }

        var spliced = kept.ToArray();
        var dropPct = total == 0 ? 0 : 100.0 * (total - passed) / total;
        Console.WriteLine($"[asr] VAD threshold={threshold} chunk={chunkMs}ms -> {passed}/{total} chunks kept, {dropPct:F1}% DROPPED");
        Console.WriteLine($"[asr] continuous={pcm.Length:N0}B  spliced={spliced.Length:N0}B  ({100.0 * spliced.Length / pcm.Length:F1}% of original audio survives)");

        // Third variant: the FIXED behaviour. The VAD only opens/closes an utterance;
        // every chunk inside it is kept, gaps included, plus a short pre-roll.
        var holdoverChunks = (int)Math.Ceiling(700.0 / chunkMs);
        var preRollChunks = (int)Math.Ceiling(180.0 / chunkMs);
        var flags = new bool[total];
        for (int i = 0, off = 0; i < total; i++, off += bytesPerChunk)
        {
            var slice = new byte[bytesPerChunk];
            Buffer.BlockCopy(pcm, off, slice, 0, bytesPerChunk);
            flags[i] = vad.IsSpeech(new AudioChunk
            {
                Role = AudioStreamRole.Microphone, Format = fmt,
                CapturedAt = DateTimeOffset.UtcNow, Samples = slice,
            });
        }
        var keepIdx = new bool[total];
        var lastSpeech = -1;
        for (var i = 0; i < total; i++)
        {
            if (flags[i]) lastSpeech = i;
            if (lastSpeech >= 0 && i - lastSpeech <= holdoverChunks) keepIdx[i] = true;
        }
        for (var i = 0; i < total; i++)
            if (flags[i])
                for (var p = Math.Max(0, i - preRollChunks); p < i; p++) keepIdx[p] = true;

        var held = new MemoryStream();
        var heldCount = 0;
        for (var i = 0; i < total; i++)
        {
            if (!keepIdx[i]) continue;
            heldCount++;
            held.Write(pcm, i * bytesPerChunk, bytesPerChunk);
        }
        var fixedPcm = held.ToArray();
        Console.WriteLine($"[asr] FIXED (utterance holdover) -> {heldCount}/{total} chunks kept, {100.0 * fixedPcm.Length / pcm.Length:F1}% of original audio survives");
        Console.WriteLine();

        var token = await GetTokenAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        await TranscribeAsync(http, token, endpoint, deployment, apiVersion, pcm, fmt, "CONTINUOUS (reference / what Teams sends)");
        await TranscribeAsync(http, token, endpoint, deployment, apiVersion, spliced, fmt, $"OLD: VAD-SPLICED ({dropPct:F0}% removed)");
        await TranscribeAsync(http, token, endpoint, deployment, apiVersion, fixedPcm, fmt, "NEW: utterance holdover + pre-roll");
        return 0;
    }

    private static async Task TranscribeAsync(HttpClient http, string token, string endpoint,
        string deployment, string apiVersion, byte[] pcm, AudioFormat fmt, string label)
    {
        if (pcm.Length == 0) { Console.WriteLine($"--- {label}\n    (no audio)\n"); return; }
        var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/audio/transcriptions?api-version={apiVersion}";
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(WrapWav(pcm, fmt));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(deployment), "model");
        form.Add(new StringContent("json"), "response_format");

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var sw = Stopwatch.StartNew();
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        sw.Stop();

        Console.WriteLine($"--- {label}  [{sw.ElapsedMilliseconds} ms]");
        if (!resp.IsSuccessStatusCode) { Console.WriteLine($"    HTTP {(int)resp.StatusCode}: {Trim(body)}"); Console.WriteLine(); return; }
        try
        {
            using var doc = JsonDocument.Parse(body);
            Console.WriteLine($"    {doc.RootElement.GetProperty("text").GetString()}");
        }
        catch { Console.WriteLine($"    {Trim(body)}"); }
        Console.WriteLine();
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];

    private static async Task<string> GetTokenAsync()
    {
        // az is a .cmd shim on Windows, so it must be launched through the shell.
        var isWindows = OperatingSystem.IsWindows();
        var psi = isWindows
            ? new ProcessStartInfo("cmd.exe",
                "/c az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken -o tsv")
            : new ProcessStartInfo("az",
                "account get-access-token --resource https://cognitiveservices.azure.com --query accessToken -o tsv");
        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var p = Process.Start(psi)!;
        var tok = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();
        return tok;
    }

    private static (byte[] Pcm, int SampleRate, int Channels, int Bits) ReadWav(string path)
    {
        var all = File.ReadAllBytes(path);
        int sampleRate = 16000, channels = 1, bits = 16, pos = 12; // skip RIFF....WAVE
        byte[]? data = null;
        while (pos + 8 <= all.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(all, pos, 4);
            var size = BitConverter.ToInt32(all, pos + 4);
            var body = pos + 8;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(all, body + 2);
                sampleRate = BitConverter.ToInt32(all, body + 4);
                bits = BitConverter.ToInt16(all, body + 14);
            }
            else if (id == "data")
            {
                var len = Math.Min(size, all.Length - body);
                data = new byte[len];
                Buffer.BlockCopy(all, body, data, 0, len);
                break;
            }
            pos = body + size + (size % 2);
        }
        return (data ?? Array.Empty<byte>(), sampleRate, channels, bits);
    }

    private static byte[] WrapWav(byte[] pcm, AudioFormat f)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var byteRate = f.SampleRate * f.Channels * f.BitsPerSample / 8;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + pcm.Length);
        w.Write("WAVE"u8.ToArray()); w.Write("fmt "u8.ToArray());
        w.Write(16); w.Write((short)1); w.Write((short)f.Channels);
        w.Write(f.SampleRate); w.Write(byteRate);
        w.Write((short)(f.Channels * f.BitsPerSample / 8)); w.Write((short)f.BitsPerSample);
        w.Write("data"u8.ToArray()); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
