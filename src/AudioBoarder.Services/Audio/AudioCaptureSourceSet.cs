using AudioBoarder.Core.Audio;

namespace AudioBoarder.Services.Audio;

public sealed record AudioCaptureSourceSet(IReadOnlyList<IAudioCaptureSource> Sources);
