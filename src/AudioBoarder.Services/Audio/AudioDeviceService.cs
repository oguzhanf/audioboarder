using NAudio.CoreAudioApi;

namespace AudioBoarder.Services.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault, bool IsMuted = false);

/// <summary>
/// Enumerates input (capture) devices and remembers the user's explicit choice.
/// Capture sources resolve their device through here at start time, so the app
/// no longer blindly follows the default Communications endpoint — which
/// silently switches to a dead built-in mic whenever a headset disconnects.
/// </summary>
public sealed class AudioDeviceService
{
    private const string DefaultSentinel = "__default__";

    /// <summary>
    /// Selected capture device id, or null/<see cref="DefaultSentinel"/> to follow
    /// the system default Communications endpoint.
    /// </summary>
    public string? SelectedMicrophoneId { get; set; }

    public bool IsFollowingDefault =>
        string.IsNullOrEmpty(SelectedMicrophoneId) || SelectedMicrophoneId == DefaultSentinel;

    public static string DefaultId => DefaultSentinel;

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        var list = new List<AudioDeviceInfo> { new(DefaultSentinel, "Default (system communications)", true) };
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defId = null;
            try { defId = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { /* none */ }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                string name; string id; var muted = false;
                try { name = d.FriendlyName; id = d.ID; }
                catch { continue; }
                try { muted = d.AudioEndpointVolume.Mute; } catch { /* not all endpoints expose volume */ }
                list.Add(new AudioDeviceInfo(id, name, id == defId, muted));
            }
        }
        catch { /* return at least the default sentinel */ }
        return list;
    }

    /// <summary>
    /// Reports whether the endpoint we would actually capture from is muted at the
    /// WINDOWS level, and its name.
    /// <para>
    /// This is a distinct failure from "no one is speaking", and it is easy to hit
    /// without realising: Teams and headset vendor software (e.g. Poly) sync their
    /// mute button to the capture endpoint's mute flag. Muting yourself in a meeting
    /// therefore mutes the endpoint AudioBoarder is reading, and capture silently
    /// returns pure silence with no error.
    /// </para>
    /// <para>
    /// We deliberately only REPORT this. Clearing the flag would un-mute the user in
    /// whatever meeting they are in, which is not a side effect a diagramming tool
    /// should ever cause.
    /// </para>
    /// </summary>
    public (bool IsMuted, string? DeviceName) GetCaptureMuteState()
    {
        try
        {
            using var device = ResolveMicrophone();
            var name = device.FriendlyName;
            bool muted;
            try { muted = device.AudioEndpointVolume.Mute; }
            catch { return (false, name); }
            return (muted, name);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Resolves the MMDevice to capture from. Honours an explicit active selection,
    /// otherwise falls back to the default Communications endpoint. Caller owns
    /// disposal of the returned device.
    /// </summary>
    public MMDevice ResolveMicrophone()
    {
        var en = new MMDeviceEnumerator();
        if (!IsFollowingDefault)
        {
            try
            {
                var d = en.GetDevice(SelectedMicrophoneId);
                if (d.State == DeviceState.Active) return d;
            }
            catch { /* fall through to default */ }
        }
        return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    }
}
