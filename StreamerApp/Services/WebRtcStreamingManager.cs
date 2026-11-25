using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using vpxmd;
using StreamerApp.Server;

namespace StreamerApp.Services;

public class WebRtcStreamingManager : IDisposable
{
    [DllImport("vpxmd", CallingConvention = CallingConvention.Cdecl)]
    private static extern int vpx_codec_control_(IntPtr ctx, int ctrl_id, int arg);

    [DllImport("vpxmd", CallingConvention = CallingConvention.Cdecl)]
    private static extern int vpx_codec_control_(IntPtr ctx, int ctrl_id, uint arg);

    private static WebRtcStreamingManager? _instance;
    public static WebRtcStreamingManager Instance => _instance ??= new WebRtcStreamingManager();

    private readonly ConcurrentDictionary<string, RTCPeerConnection> _peers = new();
    private VpxVideoEncoder? _vpxEncoder;
    private readonly object _encoderLock = new();
    private int _frameCounter = 0;
    private volatile bool _forceKeyFrame = true;

    private int _encoderWidth = 0;
    private int _encoderHeight = 0;
    private int _encoderBitrate = 0;
    private bool _isTuned = false;

    // Dedicated background video encoding pipeline
    private readonly Thread _encoderThread;
    private readonly AutoResetEvent _frameReadyEvent = new(false);
    private readonly object _frameQueueLock = new();
    private volatile bool _isEncoderRunning = true;
    private byte[]? _pendingBuffer;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _pendingFps = 30;
    private bool _hasPendingFrame = false;

    private long _bytesSentInSecond = 0;
    private DateTime _lastBitrateCalc = DateTime.UtcNow;

    public event Action<string, RTCIceCandidateInit>? OnIceCandidateGenerated;

    public int ActiveConnectionsCount => _peers.Count;

    public WebRtcStreamingManager()
    {
        _vpxEncoder = new VpxVideoEncoder();

        _encoderThread = new Thread(EncoderLoop)
        {
            IsBackground = true,
            Name = "WebRtcVideoEncoderThread",
            Priority = ThreadPriority.AboveNormal
        };
        _encoderThread.Start();
    }

    public async Task<string> HandleOfferAsync(string connectionId, string offerSdp)
    {
        // Close existing peer connection for this connectionId if any
        ClosePeer(connectionId);

        // Force a keyframe immediately for the newly connected viewer
        _forceKeyFrame = true;
        lock (_encoderLock)
        {
            _vpxEncoder?.ForceKeyFrame();
        }

        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };

        var pc = new RTCPeerConnection(config);

        // Add Video Track (VP8, payload type 96)
        var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, 96);
        var videoTrack = new MediaStreamTrack(videoFormat);
        pc.addTrack(videoTrack);

        // Add Audio Track (PCMU, payload type 0, 8000Hz)
        var audioFormat = new AudioFormat(AudioCodecsEnum.PCMU, 0, 8000);
        var audioTrack = new MediaStreamTrack(audioFormat);
        pc.addTrack(audioTrack);

        string serverLanIp = EmbeddedWebServer.GetLocalIpAddress();

        pc.onicecandidate += (candidate) =>
        {
            if (candidate != null && !string.IsNullOrEmpty(candidate.candidate))
            {
                var init = new RTCIceCandidateInit
                {
                    candidate = candidate.candidate,
                    sdpMid = candidate.sdpMid,
                    sdpMLineIndex = (ushort)candidate.sdpMLineIndex
                };
                OnIceCandidateGenerated?.Invoke(connectionId, init);

                // If SIPSorcery generated a candidate on a virtual/Docker/WSL IP, also forward a candidate with the server's real LAN IP!
                if (!string.IsNullOrEmpty(serverLanIp) && serverLanIp != "127.0.0.1" && !candidate.candidate.Contains(serverLanIp))
                {
                    string lanCand = System.Text.RegularExpressions.Regex.Replace(
                        candidate.candidate,
                        @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",
                        serverLanIp);

                    if (lanCand != candidate.candidate)
                    {
                        var lanInit = new RTCIceCandidateInit
                        {
                            candidate = lanCand,
                            sdpMid = candidate.sdpMid,
                            sdpMLineIndex = (ushort)candidate.sdpMLineIndex
                        };
                        try
                        {
                            pc.addLocalIceCandidate(new RTCIceCandidate(lanInit));
                        }
                        catch { }
                        OnIceCandidateGenerated?.Invoke(connectionId, lanInit);
                    }
                }
            }
        };

        pc.onconnectionstatechange += (state) =>
        {
            if (state == RTCPeerConnectionState.closed ||
                state == RTCPeerConnectionState.failed)
            {
                ClosePeer(connectionId);
            }
        };

        _peers[connectionId] = pc;

        // Set remote offer
        var offerInit = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerSdp
        };
        var setRemoteResult = pc.setRemoteDescription(offerInit);
        if (setRemoteResult != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Failed to set remote description: {setRemoteResult}");
        }

        // Create answer
        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);

        string answerSdp = answer.sdp;
        if (!string.IsNullOrEmpty(serverLanIp) && serverLanIp != "127.0.0.1")
        {
            var lines = answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            var newLines = new List<string>();
            foreach (var line in lines)
            {
                newLines.Add(line);
                if (line.StartsWith("a=candidate:") && !line.Contains(serverLanIp))
                {
                    string lanLine = System.Text.RegularExpressions.Regex.Replace(
                        line,
                        @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",
                        serverLanIp);
                    if (lanLine != line)
                    {
                        newLines.Add(lanLine);
                    }
                }
            }
            answerSdp = string.Join("\r\n", newLines);
        }

        return answerSdp;
    }

    public void HandleIceCandidate(string connectionId, RTCIceCandidateInit candidateInit)
    {
        if (_peers.TryGetValue(connectionId, out var pc))
        {
            pc.addIceCandidate(candidateInit);
        }
    }

    /// <summary>
    /// Non-blocking frame submission (< 0.05ms) - updates latest frame buffer and notifies dedicated encoder thread.
    /// </summary>
    public void SendVideoFrame(int width, int height, byte[] bgraBuffer, int targetFps)
    {
        if (_peers.IsEmpty || bgraBuffer == null || bgraBuffer.Length == 0 || BroadcastStateService.Instance.IsPaused)
            return;

        int requiredSize = width * height * 4;
        lock (_frameQueueLock)
        {
            if (_pendingBuffer == null || _pendingBuffer.Length != requiredSize)
            {
                _pendingBuffer = new byte[requiredSize];
            }
            Buffer.BlockCopy(bgraBuffer, 0, _pendingBuffer, 0, requiredSize);
            _pendingWidth = width;
            _pendingHeight = height;
            _pendingFps = targetFps;
            _hasPendingFrame = true;
        }
        _frameReadyEvent.Set();
    }

    private void EncoderLoop()
    {
        byte[]? localBuffer = null;
        long lastFrameTimestamp = Stopwatch.GetTimestamp();

        while (_isEncoderRunning)
        {
            if (!_frameReadyEvent.WaitOne(100))
                continue;

            int w, h, fps;
            lock (_frameQueueLock)
            {
                if (!_hasPendingFrame || _pendingBuffer == null)
                    continue;

                w = _pendingWidth;
                h = _pendingHeight;
                fps = _pendingFps;

                int size = w * h * 4;
                if (localBuffer == null || localBuffer.Length != size)
                {
                    localBuffer = new byte[size];
                }
                Buffer.BlockCopy(_pendingBuffer, 0, localBuffer, 0, size);
                _hasPendingFrame = false;
            }

            if (_peers.IsEmpty || localBuffer == null)
                continue;

            byte[]? encoded = null;
            lock (_encoderLock)
            {
                try
                {
                    int targetBitrate = BroadcastStateService.Instance.Settings.BitrateKbps;

                    // Recreate encoder on resolution or bitrate change
                    if (_vpxEncoder == null || _encoderWidth != w || _encoderHeight != h || _encoderBitrate != targetBitrate)
                    {
                        _vpxEncoder?.Dispose();
                        _vpxEncoder = new VpxVideoEncoder();
                        _encoderWidth = w;
                        _encoderHeight = h;
                        _encoderBitrate = targetBitrate;
                        _isTuned = false;
                        _forceKeyFrame = true;
                    }

                    _vpxEncoder.TargetKbps = (uint)targetBitrate;

                    _frameCounter++;
                    if (_forceKeyFrame || _frameCounter % 30 == 0)
                    {
                        _vpxEncoder.ForceKeyFrame();
                        _forceKeyFrame = false;
                    }

                    encoded = _vpxEncoder.EncodeVideo(w, h, localBuffer, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);

                    // Low-level hardware / real-time tuning
                    if (!_isTuned && _vpxEncoder != null)
                    {
                        TuneVp8Realtime(_vpxEncoder, w, h, targetBitrate);
                        _isTuned = true;
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }

            if (encoded == null || encoded.Length == 0)
                continue;

            // Compute elapsed wall-clock time for RTP timestamp: exactly 90,000 units per second
            long currentFrameTimestamp = Stopwatch.GetTimestamp();
            double elapsedSeconds = (double)(currentFrameTimestamp - lastFrameTimestamp) / Stopwatch.Frequency;
            lastFrameTimestamp = currentFrameTimestamp;

            // Clamp duration to reasonable boundaries (approx 10ms to 250ms) to handle initial frame or pauses
            uint duration = (uint)Math.Clamp((int)(elapsedSeconds * 90000), 900, 22500);

            foreach (var kvp in _peers.ToArray())
            {
                var pc = kvp.Value;
                try
                {
                    if (pc.connectionState != RTCPeerConnectionState.closed &&
                        pc.connectionState != RTCPeerConnectionState.failed)
                    {
                        pc.SendVideo(duration, encoded);
                        _bytesSentInSecond += encoded.Length;
                    }
                }
                catch
                {
                    ClosePeer(kvp.Key);
                }
            }

            // Calculate bitrate
            var now = DateTime.UtcNow;
            if ((now - _lastBitrateCalc).TotalMilliseconds >= 1000)
            {
                int kbps = (int)(_bytesSentInSecond * 8 / 1024);
                BroadcastStateService.Instance.OutgoingBitrateKbps = kbps;
                _bytesSentInSecond = 0;
                _lastBitrateCalc = now;
            }
        }
    }

    private void TuneVp8Realtime(VpxVideoEncoder vpx, int width, int height, int bitrateKbps)
    {
        try
        {
            var vp8EncoderField = typeof(VpxVideoEncoder).GetField("_vp8Encoder", BindingFlags.NonPublic | BindingFlags.Instance);
            var vp8Codec = vp8EncoderField?.GetValue(vpx);
            if (vp8Codec == null) return;

            var ctxField = vp8Codec.GetType().GetField("_vpxEncodeCtx", BindingFlags.NonPublic | BindingFlags.Instance);
            var ctxObj = ctxField?.GetValue(vp8Codec);
            if (ctxObj == null) return;

            var prop = ctxObj.GetType().GetProperty("__Instance");
            if (prop == null) return;
            IntPtr ctxPtr = (IntPtr)prop.GetValue(ctxObj)!;
            if (ctxPtr == IntPtr.Zero) return;

            // Multithreaded libvpx config
            var cfg = new VpxCodecEncCfg();
            if (vpx_encoder.VpxCodecEncConfigDefault(vp8cx.VpxCodecVp8Cx(), cfg, 0) == 0)
            {
                cfg.GW = (uint)width;
                cfg.GH = (uint)height;
                cfg.GThreads = (uint)Math.Clamp(Environment.ProcessorCount, 2, 8);
                cfg.RcTargetBitrate = (uint)bitrateKbps;
                vpx_encoder.VpxCodecEncConfigSet((VpxCodecCtx)ctxObj, cfg);
            }

            // Real-time Controls
            vpx_codec_control_(ctxPtr, (int)Vp8eEncControlId.VP8E_SET_CPUUSED, 12); // Maximum real-time speed
            vpx_codec_control_(ctxPtr, (int)Vp8eEncControlId.VP8E_SET_TOKEN_PARTITIONS, (int)Vp8eTokenPartitions.VP8FOUR_TOKENPARTITION); // Multi-threaded partition slices
            vpx_codec_control_(ctxPtr, (int)Vp8eEncControlId.VP8E_SET_STATIC_THRESHOLD, 1000u); // Skip unchanged screen macroblocks
            vpx_codec_control_(ctxPtr, (int)Vp8eEncControlId.VP8E_SET_SCREEN_CONTENT_MODE, 1u); // Optimize for screen sharing
        }
        catch { }
    }

    public void SendAudioSample(byte[] audioSample)
    {
        if (_peers.IsEmpty || audioSample == null || audioSample.Length == 0)
            return;

        // 160 samples at 8000Hz = 20ms duration
        const uint audioDuration = 160;

        foreach (var kvp in _peers.ToArray())
        {
            var pc = kvp.Value;
            try
            {
                if (pc.connectionState != RTCPeerConnectionState.closed &&
                    pc.connectionState != RTCPeerConnectionState.failed)
                {
                    pc.SendAudio(audioDuration, audioSample);
                    _bytesSentInSecond += audioSample.Length;
                }
            }
            catch
            {
                ClosePeer(kvp.Key);
            }
        }
    }

    public void ClosePeer(string connectionId)
    {
        if (_peers.TryRemove(connectionId, out var pc))
        {
            try
            {
                pc.Close("Closed by server");
                pc.Dispose();
            }
            catch { }
        }
    }

    public void CloseAllPeers()
    {
        foreach (var key in _peers.Keys.ToArray())
        {
            ClosePeer(key);
        }
    }

    public void Dispose()
    {
        _isEncoderRunning = false;
        _frameReadyEvent.Set();
        try
        {
            _encoderThread.Join(500);
        }
        catch { }

        _frameReadyEvent.Dispose();

        CloseAllPeers();
        lock (_encoderLock)
        {
            _vpxEncoder?.Dispose();
            _vpxEncoder = null;
        }
    }
}
