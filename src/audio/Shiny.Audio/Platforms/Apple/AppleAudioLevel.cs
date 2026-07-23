using System.Runtime.InteropServices;
using AVFoundation;

namespace Shiny.Audio;

/// <summary>
/// Turns a tapped <see cref="AVAudioPcmBuffer"/> into a 0–1 meter value. Shared by everything that
/// installs an input tap — the monitor, the capture source, and Shiny.Speech's native recognizer —
/// so every Apple mic meter reads on the same scale.
/// </summary>
static class AppleAudioLevel
{
    public static double FromBuffer(AVAudioPcmBuffer buffer)
    {
        var frames = (int)buffer.FrameLength;
        if (frames == 0)
            return 0;

        // Taps hand back the input node's native format, which is float on every current device,
        // but a voice-processing or converted node can present int16 instead.
        if (buffer.FloatChannelData != IntPtr.Zero)
        {
            var channel = Marshal.ReadIntPtr(buffer.FloatChannelData); // first channel
            var data = new float[frames];
            Marshal.Copy(channel, data, 0, frames);
            return AudioLevel.FromSamples(data);
        }

        if (buffer.Int16ChannelData != IntPtr.Zero)
        {
            var channel = Marshal.ReadIntPtr(buffer.Int16ChannelData);
            var data = new byte[frames * 2];
            Marshal.Copy(channel, data, 0, data.Length);
            return AudioLevel.FromPcm16(data);
        }

        return 0;
    }
}
