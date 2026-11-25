using System;
using System.IO;
using NAudio.Wave;
using SIPSorcery.Media;

#pragma warning disable CS0618

namespace StreamerApp.Services;

public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly object _lock = new();
    private bool _isEnabled = true;
    private bool _isCapturing = false;
    private volatile bool _disposed = false;

    // Resampling buffer: 8000Hz 16-bit mono PCM -> MuLaw 8-bit
    private readonly MemoryStream _accumulatedSamples = new();
    private const int TargetSampleRate = 8000;
    private const int TargetPacketSamples = 160; // 20ms at 8000Hz

    public event Action<byte[]>? OnAudioSample;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_isCapturing || _disposed) return;

            try
            {
                _capture = new WasapiLoopbackCapture();

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += (s, e) =>
                {
                    lock (_lock)
                    {
                        _isCapturing = false;
                        _capture?.Dispose();
                        _capture = null;
                    }

                    // Attempt auto-recovery if audio output endpoint changed or re-initialized
                    if (_isEnabled && !_disposed)
                    {
                        Task.Delay(1500).ContinueWith(_ =>
                        {
                            if (_isEnabled && !_disposed && !_isCapturing)
                            {
                                Start();
                            }
                        });
                    }
                };

                _capture.StartRecording();
                _isCapturing = true;
            }
            catch (Exception)
            {
                // In case of no audio endpoint or headless system
                _capture?.Dispose();
                _capture = null;
                _isCapturing = false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isCapturing) return;

            try
            {
                _capture?.StopRecording();
            }
            catch { }
            finally
            {
                _capture?.Dispose();
                _capture = null;
                _isCapturing = false;
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isEnabled || e.BytesRecorded == 0 || _capture == null)
            return;

        var fmt = _capture.WaveFormat;
        int channels = fmt.Channels;
        int sampleRate = fmt.SampleRate;

        // Extract mono samples in float [-1.0f, 1.0f]
        float[] monoSamples;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       fmt.Encoding == WaveFormatEncoding.Extensible ||
                       fmt.BitsPerSample == 32;

        if (isFloat && fmt.BitsPerSample == 32)
        {
            int floatCount = e.BytesRecorded / 4;
            int frames = floatCount / channels;
            monoSamples = new float[frames];

            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int index = (f * channels + c) * 4;
                    if (index + 4 <= e.Buffer.Length)
                    {
                        float sample = BitConverter.ToSingle(e.Buffer, index);
                        sum += sample;
                    }
                }
                monoSamples[f] = sum / channels;
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            int sampleCount = e.BytesRecorded / 2;
            int frames = sampleCount / channels;
            monoSamples = new float[frames];

            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int index = (f * channels + c) * 2;
                    if (index + 2 <= e.Buffer.Length)
                    {
                        short sample = BitConverter.ToInt16(e.Buffer, index);
                        sum += sample / 32768.0f;
                    }
                }
                monoSamples[f] = sum / channels;
            }
        }
        else
        {
            return;
        }

        // Simple downsampling to 8000Hz
        double step = (double)sampleRate / TargetSampleRate;
        int outCount = (int)(monoSamples.Length / step);
        if (outCount <= 0) return;

        byte[] encodedPacket = new byte[TargetPacketSamples];
        List<byte[]>? packetsToSend = null;

        lock (_accumulatedSamples)
        {
            for (int i = 0; i < outCount; i++)
            {
                int srcIdx = (int)(i * step);
                if (srcIdx >= monoSamples.Length) break;

                float sample = Math.Clamp(monoSamples[srcIdx], -1.0f, 1.0f);
                short linearPcm = (short)(sample * 32767);
                byte muLaw = MuLawEncoder.LinearToMuLawSample(linearPcm);

                _accumulatedSamples.WriteByte(muLaw);

                if (_accumulatedSamples.Length >= TargetPacketSamples)
                {
                    _accumulatedSamples.Position = 0;
                    _accumulatedSamples.Read(encodedPacket, 0, TargetPacketSamples);

                    // Reset stream
                    _accumulatedSamples.SetLength(0);

                    // Collect 160-sample audio packet
                    byte[] copy = new byte[TargetPacketSamples];
                    Buffer.BlockCopy(encodedPacket, 0, copy, 0, TargetPacketSamples);
                    packetsToSend ??= new List<byte[]>();
                    packetsToSend.Add(copy);
                }
            }
        }

        if (packetsToSend != null)
        {
            foreach (var packet in packetsToSend)
            {
                OnAudioSample?.Invoke(packet);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
