using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AppleAudioSource(ILogger<AppleAudioSource> logger) : IAudioSource
{
    AVAudioEngine? audioEngine;
    AVAudioConverter? converter;
    Stream? outputStream;

    public event EventHandler<double>? InputLevelChanged;

#if !MACOS
    // Snapshot of the shared session's profile before we switch it into the record-oriented
    // PlayAndRecord + VoiceChat configuration, so StopCaptureAsync can restore it. Without this,
    // the telephony VoiceChat profile lingers after recording and makes later playback quiet and
    // earpiece-routed (AppleAudioPlayer deliberately leaves an existing PlayAndRecord session alone).
    string? priorCategory;
    AVAudioSessionCategoryOptions priorOptions;
    string? priorMode;
#endif

    public Task<Stream> StartCaptureAsync(AudioProcessingOptions? processing = null, CancellationToken cancellationToken = default)
    {
        audioEngine = new AVAudioEngine();

#if !MACOS
        // The input node has no valid hardware format until the session is switched to a
        // record-capable category and activated. Installing the tap before this point throws
        // "required condition is false: IsFormatSampleRateAndChannelCountValid(format)" because
        // the node still reports a 0 Hz / 0-channel format. Configure the session first.
        var audioSession = AVAudioSession.SharedInstance();
        // Remember the current profile so StopCaptureAsync can put it back exactly as we found it.
        priorCategory = audioSession.Category;
        priorOptions = audioSession.CategoryOptions;
        priorMode = audioSession.Mode;
        // A Bluetooth mic runs over HFP, which caps capture at 8 kHz narrowband — so whatever is paired
        // silently decides the bandwidth you record. Callers analysing the signal (speaker embeddings,
        // wake words) opt out with AudioProcessingOptions.AllowBluetooth = false.
        var categoryOptions = AVAudioSessionCategoryOptions.DefaultToSpeaker;
        if (processing?.AllowBluetooth != false)
        {
            categoryOptions |= AVAudioSessionCategoryOptions.AllowBluetooth
                | AVAudioSessionCategoryOptions.AllowBluetoothA2DP;
        }

        // PlayAndRecord (not Record) mirrors the known-good SpeechToText capture path: it carries an
        // output route, so the output-oriented Bluetooth/speaker options are all valid. Pairing those
        // options with the input-only Record category instead makes SetCategory fail with OSStatus -50.
        audioSession.SetCategory(
            AVAudioSessionCategory.PlayAndRecord,
            categoryOptions,
            out var categoryError
        );
        if (categoryError != null)
            throw new InvalidOperationException($"Failed to set audio session category: {categoryError.LocalizedDescription}");

        // VoiceChat engages Apple's voice-processing chain (AEC/NS/AGC) at the session level, so it must
        // only be used when the caller asked for processing — same rule AppleAudioMonitor follows. When
        // nothing is requested, Measurement is the mode that tells iOS to apply as little input
        // processing as it can, which is what "capture raw input" has to mean for a source whose output
        // may feed a model rather than a listener. AGC in particular is adaptive and non-linear: it
        // normalises away exactly the speaker/channel characteristics an embedding model measures, so
        // two recordings of one person come back different.
        var mode = processing?.AnyEnabled == true
            ? AVAudioSessionMode.VoiceChat
            : AVAudioSessionMode.Measurement;
        audioSession.SetMode(mode.GetConstant()!, out _);

        audioSession.SetActive(true, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var activeError);
        if (activeError != null)
            throw new InvalidOperationException($"Failed to activate audio session: {activeError.LocalizedDescription}");
#endif

        var inputNode = audioEngine.InputNode;

        // Apple's voice-processing I/O unit bundles AEC + noise suppression + AGC and cannot
        // toggle them independently, so any requested effect enables the whole chain. Must be
        // set before the engine is prepared/started, otherwise the format is already locked.
        if (processing?.AnyEnabled == true)
        {
            if (!inputNode.SetVoiceProcessingEnabled(true, out var vpError))
                logger.LogWarning("Failed to enable voice processing (AEC/NS/AGC): {Error}", vpError?.LocalizedDescription);
        }

        // Read the mic's native format only after the session is active (and voice processing,
        // which alters it, is applied) so the tap gets the real hardware format.
        var inputFormat = inputNode.GetBusOutputFormat(0);
        if (inputFormat.SampleRate <= 0 || inputFormat.ChannelCount == 0)
            throw new InvalidOperationException(
                $"Microphone input format is invalid (sampleRate={inputFormat.SampleRate}, channels={inputFormat.ChannelCount}). " +
                "The audio session may not be active, or no audio input is available on this device."
            );

        // IAudioSource's contract is 16 kHz / mono / PCM16 (matching AndroidAudioSource), but the
        // mic runs at its own native rate/format (typically 48 kHz float). Convert every tapped
        // buffer down to the contract format before it reaches the consumer's stream.
        var outputFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, 16000, 1, false);
        converter = new AVAudioConverter(inputFormat, outputFormat);

        var pipe = new PipeStream();
        outputStream = pipe;

        var throttle = new AudioLevelThrottle();

        inputNode.InstallTapOnBus(0, 4096, inputFormat, (buffer, when) =>
        {
            try
            {
                var data = Convert(buffer, outputFormat);
                if (data.Length == 0)
                    return;

                // Metered after conversion so the level reflects the PCM the consumer actually
                // receives (and so the same code path covers every input hardware format).
                if (throttle.TryEmit(AudioLevel.FromPcm16(data), out var level))
                    InputLevelChanged?.Invoke(this, level);

                pipe.Write(data, 0, data.Length);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to convert/write a captured audio buffer");
            }
        });

        audioEngine.Prepare();
        audioEngine.StartAndReturnError(out var error);
        if (error != null)
            throw new InvalidOperationException($"Failed to start audio engine: {error.LocalizedDescription}");

#if MACOS
        logger.LogDebug("Apple audio capture started (native {InRate}Hz/{InChannels}ch → 16000Hz/1ch PCM16)", inputFormat.SampleRate, inputFormat.ChannelCount);
#else
        // The session mode and the actual input route are logged because they are what silently changes
        // the captured signal: a Bluetooth route means 8 kHz HFP, and VoiceChat means AGC/NS/AEC.
        var route = AVAudioSession.SharedInstance().CurrentRoute?.Inputs?.FirstOrDefault();
        logger.LogDebug(
            "Apple audio capture started (native {InRate}Hz/{InChannels}ch → 16000Hz/1ch PCM16, mode={Mode}, input={Input})",
            inputFormat.SampleRate,
            inputFormat.ChannelCount,
            mode,
            route?.PortType ?? "unknown"
        );
#endif
        return Task.FromResult<Stream>(pipe);
    }

    byte[] Convert(AVAudioPcmBuffer input, AVAudioFormat outputFormat)
    {
        // Allow room for the resampled frame count plus a margin for the converter's internal state.
        var capacity = (uint)(input.FrameLength * outputFormat.SampleRate / input.Format.SampleRate) + 1024;
        var output = new AVAudioPcmBuffer(outputFormat, capacity);

        var supplied = false;
        var status = converter!.ConvertToBuffer(output, out var error, (uint packets, out AVAudioConverterInputStatus inputStatus) =>
        {
            // The converter pulls until it has enough input; hand it this buffer once, then report dry.
            if (supplied)
            {
                inputStatus = AVAudioConverterInputStatus.NoDataNow;
                return null;
            }
            supplied = true;
            inputStatus = AVAudioConverterInputStatus.HaveData;
            return input;
        });

        if (status == AVAudioConverterOutputStatus.Error)
        {
            logger.LogDebug("Audio conversion failed: {Error}", error?.LocalizedDescription);
            return [];
        }

        var frames = (int)output.FrameLength;
        if (frames == 0)
            return [];

        var byteCount = frames * 2; // mono, 16-bit
        var data = new byte[byteCount];
        var channelPtr = Marshal.ReadIntPtr(output.Int16ChannelData); // first (only) channel
        Marshal.Copy(channelPtr, data, 0, byteCount);
        return data;
    }

    public Task StopCaptureAsync()
    {
        if (audioEngine != null)
        {
            if (audioEngine.Running)
            {
                audioEngine.Stop();
                audioEngine.InputNode.RemoveTapOnBus(0);
            }

#if !MACOS
            var session = AVAudioSession.SharedInstance();
            // Restore the pre-capture profile before deactivating so we don't leave the shared
            // session stuck in PlayAndRecord + VoiceChat, which attenuates and mis-routes later
            // playback. Reactivating happens on the next PlayAsync/StartCapture.
            if (priorCategory != null)
            {
                session.SetCategory(new NSString(priorCategory), priorOptions, out _);
                if (priorMode != null)
                    session.SetMode(new NSString(priorMode), out _);
            }
            session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
#endif
        }

        converter?.Dispose();
        converter = null;

        outputStream?.Dispose();
        logger.LogDebug("Apple audio capture stopped");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        GC.SuppressFinalize(this);
    }
}
