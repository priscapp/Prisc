using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Prisc.UI.Services
{
    /// <summary>
    /// The Windows <see cref="MicCapture"/>: WASAPI shared mode through NAudio's
    /// <see cref="WasapiRecorder"/>, which hands us buffers in the endpoint's mix format. The
    /// recorder converts from there; nothing here resamples.
    /// </summary>
    /// <remarks>The recorder raises one <see cref="MicCapture.DataAvailable"/> per WASAPI packet
    /// (one engine period, a few milliseconds), whatever the buffer length; the length only sets
    /// how much the endpoint can hold before the capture thread drains it. RecordingStopped is
    /// posted to the SynchronizationContext of the thread that called <see cref="Create"/>, if
    /// it has one, and raised on the capture thread otherwise.</remarks>
    [SupportedOSPlatform("windows")]
    internal sealed class WasapiMicCapture : MicCapture
    {
        private readonly MMDevice _device;
        private readonly WasapiRecorder _capture;
        private readonly string _name;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly MicSampleFormat _format;

        private WasapiMicCapture(MMDevice device, WasapiRecorder capture, int channels, int sampleRate,
            MicSampleFormat format)
        {
            _device = device;
            _capture = capture;
            _name = device.FriendlyName;
            _channels = channels;
            _sampleRate = sampleRate;
            _format = format;

            _capture.DataAvailable += Capture_DataAvailable;
            _capture.RecordingStopped += Capture_RecordingStopped;
        }

        public override string FriendlyName => _name;
        public override int Channels => _channels;
        public override int SampleRate => _sampleRate;
        public override MicSampleFormat SampleFormat => _format;

        /// <summary>See <see cref="MicCapture.Open"/>.</summary>
        public static WasapiMicCapture Create(string deviceId, int bufferMs)
        {
            var device = ResolveDevice(deviceId);
            WasapiRecorder capture = null;
            try
            {
                capture = new WasapiRecorderBuilder()
                    .WithDevice(device)
                    .WithSharedMode()
                    .WithBufferLength(bufferMs)
                    .Build();

                if (!TryResolveFormat(capture.WaveFormat, out var channels, out var sampleRate, out var format))
                    throw new NotSupportedException("Unsupported microphone format " + capture.WaveFormat);

                return new WasapiMicCapture(device, capture, channels, sampleRate, format);
            }
            catch
            {
                capture?.Dispose();
                device.Dispose();
                throw;
            }
        }

        public override void Start() => _capture.StartRecording();

        /// <summary>Setting the stop flag is all that is safe from the capture thread; the client
        /// raises RecordingStopped once that thread has ended.</summary>
        public override void RequestStop() => _capture.StopRecording();

        /// <summary>Disposing the client waits for its capture thread, which may be inside a
        /// <see cref="MicCapture.DataAvailable"/> handler: see the base class remarks.</summary>
        public override void Dispose()
        {
            try
            {
                _capture.DataAvailable -= Capture_DataAvailable;
                _capture.RecordingStopped -= Capture_RecordingStopped;
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to close the microphone: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "voiceover.close-mic");
            }

            try
            {
                _device.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to release the microphone endpoint: " + ex.Message);
            }
        }

        /// <summary>The span is WASAPI's own packet (or zeros for a silent one), valid only for
        /// this call; the positions are unused.</summary>
        private void Capture_DataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition,
            long qpcPosition) => RaiseDataAvailable(buffer);

        private void Capture_RecordingStopped(object sender, StoppedEventArgs e) => RaiseStopped(e.Exception);

        /// <summary>Opens the endpoint for an id from <see cref="AudioDeviceManager.GetMicrophones"/>.
        /// An id that no longer exists falls back to the default input.</summary>
        private static MMDevice ResolveDevice(string deviceId)
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!String.IsNullOrEmpty(deviceId) && deviceId != AudioDeviceManager.DefaultDeviceId)
            {
                try
                {
                    return enumerator.GetDevice(deviceId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Microphone '" + deviceId + "' is gone, using the default: " + ex.Message);
                }
            }

            // Role.Multimedia to match the endpoint AudioDeviceManager names its "Default - ..."
            // row after, so the picker's label and the device we actually open agree.
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }

        /// <summary>Reads the device mix format into the few numbers the recorder's conversion
        /// loop needs. Returns false for a format we cannot decode.</summary>
        private static bool TryResolveFormat(WaveFormat format, out int channels, out int sampleRate,
            out MicSampleFormat sampleFormat)
        {
            channels = 0;
            sampleRate = 0;
            sampleFormat = MicSampleFormat.Float32;

            // Shared-mode mix formats are usually WAVE_FORMAT_EXTENSIBLE, whose Encoding reads as
            // "Extensible"; the encoding that matters is in the subformat GUID.
            if (format is WaveFormatExtensible extensible)
            {
                try
                {
                    format = extensible.ToStandardWaveFormat();
                }
                catch (InvalidOperationException ex)
                {
                    Debug.WriteLine("Unrecognized microphone subformat: " + ex.Message);
                    return false;
                }
            }

            channels = format.Channels;
            sampleRate = format.SampleRate;
            if (channels < 1 || sampleRate < 1)
                return false;

            switch (format.Encoding, format.BitsPerSample)
            {
                case (WaveFormatEncoding.IeeeFloat, 32):
                    sampleFormat = MicSampleFormat.Float32;
                    return true;
                case (WaveFormatEncoding.Pcm, 16):
                    sampleFormat = MicSampleFormat.Pcm16;
                    return true;
                case (WaveFormatEncoding.Pcm, 24):
                    sampleFormat = MicSampleFormat.Pcm24;
                    return true;
                case (WaveFormatEncoding.Pcm, 32):
                    sampleFormat = MicSampleFormat.Pcm32;
                    return true;
                default:
                    return false;
            }
        }
    }
}
