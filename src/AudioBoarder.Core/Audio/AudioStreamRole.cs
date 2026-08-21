namespace AudioBoarder.Core.Audio;

/// <summary>
/// Logical audio stream identifier. Mic = local speaker, Loopback = far-end speakers.
/// </summary>
public enum AudioStreamRole
{
    Microphone,
    Loopback,
}
