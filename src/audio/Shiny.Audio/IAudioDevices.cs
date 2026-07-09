namespace Shiny.Audio;

/// <summary>
/// Enumerates the audio input/output routes the OS currently sees, reports which are active, and
/// raises <see cref="Changed"/> when the route changes (e.g. a Bluetooth speaker connects). Actual
/// selection of the capture/render device for a live session is applied through
/// <see cref="IAudioMonitor.SetInputDevice"/> / <see cref="IAudioMonitor.SetOutputDevice"/>, since
/// the OS binds a preferred device to the specific record/playback instance.
/// </summary>
public interface IAudioDevices
{
    /// <summary>Capture devices (microphones, Bluetooth headsets, wired headsets, …).</summary>
    IReadOnlyList<AudioDevice> GetInputs();

    /// <summary>Render devices (built-in speaker, Bluetooth speaker, headphones, …).</summary>
    IReadOnlyList<AudioDevice> GetOutputs();

    /// <summary>The input route currently in use, or null when none/unknown.</summary>
    AudioDevice? CurrentInput { get; }

    /// <summary>The output route currently in use, or null when none/unknown.</summary>
    AudioDevice? CurrentOutput { get; }

    /// <summary>
    /// Present the OS output/route picker (iOS <c>AVRoutePickerView</c> — AirPlay/Bluetooth). On
    /// platforms with no system picker this is a no-op. Selection there is applied by the OS.
    /// </summary>
    Task ShowOutputPicker();

    /// <summary>Fires when the active input or output route changes.</summary>
    event EventHandler? Changed;
}
