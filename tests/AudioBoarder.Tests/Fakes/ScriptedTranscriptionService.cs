using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Tests.Fakes;

public sealed class ScriptedTranscriptionService : ITranscriptionService
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly Queue<(TranscriptSpeaker Speaker, string Text)> _script;
    private readonly TimeSpan _segmentSpacing;
    private DateTimeOffset _nextEmit;
    private bool _ready;

    public ScriptedTranscriptionService(
        IEnumerable<(TranscriptSpeaker Speaker, string Text)> script,
        TimeSpan? segmentSpacing = null,
        Func<DateTimeOffset>? clock = null)
    {
        _script = new Queue<(TranscriptSpeaker, string)>(script ?? throw new ArgumentNullException(nameof(script)));
        _segmentSpacing = segmentSpacing ?? TimeSpan.FromSeconds(2);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _nextEmit = _clock();
    }

    public string Name => "Scripted";
    public bool IsReady => _ready;

    public Task InitializeAsync(CancellationToken ct)
    {
        _ready = true;
        _nextEmit = _clock();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!_ready || _script.Count == 0)
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());

        var now = _clock();
        if (now < _nextEmit)
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());

        var (speaker, text) = _script.Dequeue();
        var start = now - _segmentSpacing;
        var segment = new TranscriptSegment(Guid.NewGuid(), speaker, text, start, now);
        _nextEmit = now + _segmentSpacing;
        return Task.FromResult<IReadOnlyList<TranscriptSegment>>(new[] { segment });
    }

    public Task<IReadOnlyList<TranscriptSegment>> FlushAsync(CancellationToken ct, bool force = false)
    {
        var remaining = new List<TranscriptSegment>();
        var now = _clock();
        while (_script.Count > 0)
        {
            var (speaker, text) = _script.Dequeue();
            remaining.Add(new TranscriptSegment(Guid.NewGuid(), speaker, text, now, now));
        }
        return Task.FromResult<IReadOnlyList<TranscriptSegment>>(remaining);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
