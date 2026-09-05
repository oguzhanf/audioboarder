using System.IO;
using System.Text.Json;
using AudioBoarder.App.ViewModels;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Audio;

namespace AudioBoarder.App.Demo;

public sealed class DemoTelemetry : IDisposable
{
    private readonly DemoSession _demo;
    private readonly AudioPipeline _pipeline;
    private readonly MainViewModel _viewModel;
    private readonly List<object> _events = [];
    private readonly object _gate = new();
    private bool _completed;

    public DemoTelemetry(DemoSession demo, AudioPipeline pipeline, MainViewModel viewModel)
    {
        _demo = demo; _pipeline = pipeline; _viewModel = viewModel;
        pipeline.SegmentEmitted += Final;
        pipeline.InterimEmitted += Interim;
        viewModel.SceneInvalidated += Scene;
    }
    private double Elapsed => _demo.Clock.StartedAt is { } start
        ? Math.Max(0, (DateTimeOffset.UtcNow - start).TotalSeconds) : 0;
    private void Final(object? sender, TranscriptSegment segment)
    {
        lock (_gate) _events.Add(new { type = "final-caption", elapsed = Elapsed, speaker = segment.Speaker.ToString(), text = segment.Text });
        Save();
    }
    private void Interim(object? sender, TranscriptSegment segment)
    {
        lock (_gate) _events.Add(new { type = "interim-caption", elapsed = Elapsed, speaker = segment.Speaker.ToString(), text = segment.Text });
    }
    private void Scene(object? sender, EventArgs args)
    {
        var snapshot = _viewModel.Scene.Clone();
        lock (_gate) _events.Add(new
        {
            type = "scene", elapsed = Elapsed, revision = snapshot.Revision,
            nodes = snapshot.Nodes.Values.Select(n => n.Label).ToArray(),
            edges = snapshot.Edges.Values.Select(edge => new
            {
                edge.Id, from = edge.FromNodeId, to = edge.ToNodeId, edge.Label,
                source = snapshot.Nodes.GetValueOrDefault(edge.FromNodeId)?.Label,
                target = snapshot.Nodes.GetValueOrDefault(edge.ToNodeId)?.Label,
            }).ToArray(),
            notes = snapshot.Notes.Values.Select(n => new { kind = n.Kind.ToString(), n.Text }).ToArray(),
        });
        Save();
    }
    public void Save()
    {
        Directory.CreateDirectory(_demo.OutputDirectory);
        lock (_gate) File.WriteAllText(Path.Combine(_demo.OutputDirectory, "live-demo-events.json"),
            JsonSerializer.Serialize(new
            {
                syntheticMeeting = true, completed = _completed, startedAtUtc = _demo.Clock.StartedAt, events = _events, diagnostics = _pipeline.Diagnostics,
                _pipeline.ChunksReceived, _pipeline.ChunksForwarded, _pipeline.SegmentsEmitted,
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public void Complete()
    {
        lock (_gate) _completed = true;
        Save();
    }
    public void Dispose()
    {
        _pipeline.SegmentEmitted -= Final;
        _pipeline.InterimEmitted -= Interim;
        _viewModel.SceneInvalidated -= Scene;
        Save();
    }
}
