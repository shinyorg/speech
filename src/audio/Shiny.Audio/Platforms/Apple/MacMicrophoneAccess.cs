#if MACOS
using AVFoundation;

namespace Shiny.Audio;

/// <summary>
/// The macOS microphone permission (TCC). AVAudioSession doesn't exist on macOS, so it goes through AVCaptureDevice; the
/// app's Info.plist needs NSMicrophoneUsageDescription for the prompt to appear.
/// </summary>
static class MacMicrophoneAccess
{
    public static async Task<AccessState> Request()
    {
        switch (AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio))
        {
            case AVAuthorizationStatus.Authorized:
                return AccessState.Available;

            case AVAuthorizationStatus.Denied:
                return AccessState.Denied;

            case AVAuthorizationStatus.Restricted:
                return AccessState.Restricted;
        }

        var granted = await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Audio).ConfigureAwait(false);
        return granted ? AccessState.Available : AccessState.Denied;
    }
}
#endif
