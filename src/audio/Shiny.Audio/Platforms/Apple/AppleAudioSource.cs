using System.Runtime.InteropServices;
using AVFoundation;
using Microsoft.Extensions.Logging;

namespace Shiny.Audio;

public class AppleAudioSource(ILogger<AppleAudioSource> logger) : IAudioSource
{
    AVAudioEngine? audioEngine;
    AVAudioConverter? converter;
    Stream? outputStream;

    public Task<Stream> StartCaptureAsync(AudioProcessingOptions? processing = null, CancellationToken cancellationToken = default)
    {
        audioEngine = new AVAudioEngine();

#if !MACOS
        // The input node has no valid hardware format until the session is switched to a
        // record-capable category and activated. Installing the tap before this point throws
        // "required condition is false: IsFormatSampleRateAndChannelCountValid(format)" because
        // the node still reports a 0 Hz / 0-channel format. Configure the session first.
        var audioSession = AVAudioSession.SharedInstance();
        // PlayAndRecord (not Record) mirrors the known-good SpeechToText capture path: it carries an
        // output route, so the output-oriented Bluetooth/speaker options are all valid. Pairing those
        // options with the input-only Record category instead makes SetCategory fail with OSStatus -50.
        audioSession.SetCategory(
            AVAudioSessionCategory.PlayAndRecord,
            AVAudioSessionCategoryOptions.AllowBluetooth
                | AVAudioSessionCategoryOptions.AllowBluetoothA2DP
                | AVAudioSessionCategoryOptions.DefaultToSpeaker,
            out var categoryError
        );
        if (categoryError != null)
            throw new InvalidOperationException($"Failed to set audio session category: {categoryError.LocalizedDescription}");

        audioSession.SetMode(AVAudioSessionMode.VoiceChat.GetConstant()!, out _);

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

        inputNode.InstallTapOnBus(0, 4096, inputFormat, (buffer, when) =>
        {
            try
            {
                var data = Convert(buffer, outputFormat);
                if (data.Length > 0)
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

        logger.LogDebug("Apple audio capture started (native {InRate}Hz/{InChannels}ch → 16000Hz/1ch PCM16)", inputFormat.SampleRate, inputFormat.ChannelCount);
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
