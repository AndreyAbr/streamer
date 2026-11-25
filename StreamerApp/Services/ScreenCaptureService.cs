using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace StreamerApp.Services;

public class MonitorItem
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public Rectangle Bounds { get; set; }
    public bool IsPrimary { get; set; }

    public override string ToString() => Name;
}

public class ScreenCaptureService : IDisposable
{
    #region Win32 API
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(IntPtr hdc, int x, int y, IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest,
        IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc, int dwRop);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;
    private const int SRCCOPY = 0x00CC0020;
    #endregion

    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private int _targetFps = 30;
    private int _selectedMonitorIndex = 0;
    private string _resolution = "1920x1080";
    private string _captureEngine = "DXGI";
    private bool _showCursor = true;
    private readonly object _lock = new();

    // Stats
    private int _frameCount = 0;
    private DateTime _lastFpsCalc = DateTime.UtcNow;
    private DateTime _lastJpegBroadcast = DateTime.MinValue;
    private volatile bool _isEncodingJpeg = false;

    public event Action<int, int, byte[]>? OnFrameCaptured;
    public event Action<byte[]>? OnJpegFrameCaptured;
    public event Action<int>? OnFpsUpdated;

    public int CurrentFps { get; private set; }

    public static List<MonitorItem> GetMonitors()
    {
        var list = new List<MonitorItem>();
        int index = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                int width = mi.rcMonitor.right - mi.rcMonitor.left;
                int height = mi.rcMonitor.bottom - mi.rcMonitor.top;
                bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;

                list.Add(new MonitorItem
                {
                    Index = index,
                    Bounds = new Rectangle(mi.rcMonitor.left, mi.rcMonitor.top, width, height),
                    IsPrimary = isPrimary,
                    Name = $"Монитор {index + 1} ({width}x{height}){(isPrimary ? " [Основной]" : "")}"
                });
                index++;
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            list.Add(new MonitorItem
            {
                Index = 0,
                Bounds = new Rectangle(0, 0, 1920, 1080),
                IsPrimary = true,
                Name = "Монитор 1 (1920x1080) [Основной]"
            });
        }

        return list;
    }

    public void Configure(int monitorIndex, string resolution, int fps, string captureEngine = "DXGI", bool showCursor = true)
    {
        lock (_lock)
        {
            _selectedMonitorIndex = monitorIndex;
            _resolution = resolution;
            _targetFps = Math.Clamp(fps, 5, 60);
            _captureEngine = captureEngine;
            _showCursor = showCursor;
        }
    }

    public void Start()
    {
        Stop();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _captureThread = new Thread(() => CaptureLoop(token))
        {
            IsBackground = true,
            Name = "ScreenCaptureThread",
            Priority = ThreadPriority.Highest
        };
        _captureThread.Start();
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _captureThread?.Join(1500);
        }
        catch { }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _captureThread = null;
        }
    }

    private static void DrawCursor(IntPtr hdc, Rectangle bounds, int targetW, int targetH)
    {
        var ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
        if (GetCursorInfo(out ci) && ci.flags == CURSOR_SHOWING && ci.hCursor != IntPtr.Zero)
        {
            int curX = ci.ptScreenPos.x - bounds.X;
            int curY = ci.ptScreenPos.y - bounds.Y;

            if (targetW != bounds.Width || targetH != bounds.Height)
            {
                curX = (int)((float)curX * targetW / bounds.Width);
                curY = (int)((float)curY * targetH / bounds.Height);
            }

            int drawX = curX;
            int drawY = curY;

            if (GetIconInfo(ci.hCursor, out ICONINFO iconInfo))
            {
                drawX -= iconInfo.xHotspot;
                drawY -= iconInfo.yHotspot;

                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            }

            if (drawX >= -64 && drawX <= targetW + 64 && drawY >= -64 && drawY <= targetH + 64)
            {
                DrawIcon(hdc, drawX, drawY, ci.hCursor);
            }
        }
    }

    private static void DownsampleBgra(byte[] src, int srcW, int srcH, IntPtr dstBits, int dstW, int dstH)
    {
        float xRatio = (float)srcW / dstW;
        float yRatio = (float)srcH / dstH;
        int dstRowBytes = dstW * 4;
        byte[] rowBuffer = new byte[dstRowBytes];

        for (int y = 0; y < dstH; y++)
        {
            int srcY = (int)(y * yRatio);
            int srcRowOffset = srcY * srcW * 4;

            for (int x = 0; x < dstW; x++)
            {
                int srcX = (int)(x * xRatio);
                int srcPixelOffset = srcRowOffset + (srcX * 4);
                int dstPixelOffset = x * 4;

                rowBuffer[dstPixelOffset] = src[srcPixelOffset];
                rowBuffer[dstPixelOffset + 1] = src[srcPixelOffset + 1];
                rowBuffer[dstPixelOffset + 2] = src[srcPixelOffset + 2];
                rowBuffer[dstPixelOffset + 3] = src[srcPixelOffset + 3];
            }

            IntPtr dstRow = IntPtr.Add(dstBits, y * dstRowBytes);
            Marshal.Copy(rowBuffer, 0, dstRow, dstRowBytes);
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        var monitors = GetMonitors();
        byte[]? rawBuffer = null;
        byte[]? reusableJpegBuffer = null;

        // DXGI engine state
        DxgiScreenCapture? dxgiCapture = null;
        int dxgiOutputIndex = -1;
        DateTime nextDxgiRetry = DateTime.MinValue;
        byte[]? dxgiNativeBuffer = null;

        // Cache JPEG encoder
        ImageCodecInfo? jpegCodec = null;
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
            {
                jpegCodec = codec;
                break;
            }
        }

        EncoderParameters? encoderParams = null;
        if (jpegCodec != null)
        {
            encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 65L);
        }

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hDib = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;
        IntPtr pBits = IntPtr.Zero;
        int dibWidth = 0;
        int dibHeight = 0;

        void CleanupGdi()
        {
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero)
            {
                SelectObject(hdcMem, hOld);
                hOld = IntPtr.Zero;
            }
            if (hDib != IntPtr.Zero)
            {
                DeleteObject(hDib);
                hDib = IntPtr.Zero;
            }
            if (hdcMem != IntPtr.Zero)
            {
                DeleteDC(hdcMem);
                hdcMem = IntPtr.Zero;
            }
            if (hdcScreen != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, hdcScreen);
                hdcScreen = IntPtr.Zero;
            }
            pBits = IntPtr.Zero;
            dibWidth = 0;
            dibHeight = 0;
        }

        TimeBeginPeriod(1);
        long nextFrameTick = Stopwatch.GetTimestamp();

        try
        {
            while (!token.IsCancellationRequested)
            {
                int monIndex;
                string resMode;
                int fps;
                string requestedEngine;
                bool showCursor;

                lock (_lock)
                {
                    monIndex = _selectedMonitorIndex;
                    resMode = _resolution;
                    fps = _targetFps;
                    requestedEngine = _captureEngine;
                    showCursor = _showCursor;
                }

                if (monIndex < 0 || monIndex >= monitors.Count)
                {
                    monIndex = 0;
                }

                var monitor = monitors[monIndex];
                var bounds = monitor.Bounds;

                int targetW = bounds.Width;
                int targetH = bounds.Height;

                if (resMode == "1280x720")
                {
                    targetW = 1280;
                    targetH = 720;
                }
                else if (resMode == "1920x1080")
                {
                    targetW = 1920;
                    targetH = 1080;
                }

                // Ensure even dimensions
                if (targetW % 2 != 0) targetW--;
                if (targetH % 2 != 0) targetH--;

                int bufferSize = targetW * targetH * 4;
                if (rawBuffer == null || rawBuffer.Length != bufferSize)
                {
                    rawBuffer = new byte[bufferSize];
                }

                // Initialize or recreate persistent DIB Section when dimensions change
                if (hdcMem == IntPtr.Zero || dibWidth != targetW || dibHeight != targetH)
                {
                    CleanupGdi();

                    hdcScreen = GetDC(IntPtr.Zero);
                    if (hdcScreen != IntPtr.Zero)
                    {
                        hdcMem = CreateCompatibleDC(hdcScreen);

                        var bmi = new BITMAPINFO();
                        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                        bmi.bmiHeader.biWidth = targetW;
                        bmi.bmiHeader.biHeight = -targetH; // Top-down
                        bmi.bmiHeader.biPlanes = 1;
                        bmi.bmiHeader.biBitCount = 32;
                        bmi.bmiHeader.biCompression = 0; // BI_RGB

                        hDib = CreateDIBSection(hdcMem, ref bmi, 0, out pBits, IntPtr.Zero, 0);
                        if (hDib != IntPtr.Zero)
                        {
                            hOld = SelectObject(hdcMem, hDib);
                            dibWidth = targetW;
                            dibHeight = targetH;
                        }
                    }
                }

                bool frameCaptured = false;

                if (hdcMem != IntPtr.Zero && pBits != IntPtr.Zero)
                {
                    try
                    {
                        bool dxgiSuccess = false;

                        // 1. Try DXGI Desktop Duplication if requested
                        if (requestedEngine == "DXGI")
                        {
                            if (dxgiCapture == null || !dxgiCapture.IsInitialized || dxgiOutputIndex != monIndex)
                            {
                                if (DateTime.UtcNow >= nextDxgiRetry)
                                {
                                    dxgiCapture?.Cleanup();
                                    dxgiCapture = new DxgiScreenCapture();
                                    if (dxgiCapture.Initialize(monIndex))
                                    {
                                        dxgiOutputIndex = monIndex;
                                        BroadcastStateService.Instance.SetActiveBackend("DXGI (GPU)");
                                    }
                                    else
                                    {
                                        dxgiCapture.Cleanup();
                                        dxgiCapture = null;
                                        nextDxgiRetry = DateTime.UtcNow.AddSeconds(3);
                                        BroadcastStateService.Instance.SetActiveBackend("GDI (CPU)");
                                    }
                                }
                            }

                            if (dxgiCapture != null && dxgiCapture.IsInitialized)
                            {
                                if (targetW == bounds.Width && targetH == bounds.Height)
                                {
                                    var acq = dxgiCapture.AcquireFrameToMemory(pBits, bufferSize, timeoutMs: 15, out bool hasNewPixels);
                                    if (acq == DxgiAcquireResult.Success || acq == DxgiAcquireResult.Timeout)
                                    {
                                        dxgiSuccess = true;
                                        BroadcastStateService.Instance.SetActiveBackend("DXGI (GPU)");
                                    }
                                    else if (acq == DxgiAcquireResult.AccessLost || acq == DxgiAcquireResult.Error)
                                    {
                                        dxgiCapture.Cleanup();
                                        dxgiCapture = null;
                                        nextDxgiRetry = DateTime.UtcNow.AddSeconds(3);
                                        BroadcastStateService.Instance.SetActiveBackend("GDI (CPU)");
                                    }
                                }
                                else
                                {
                                    int nativeSize = bounds.Width * bounds.Height * 4;
                                    if (dxgiNativeBuffer == null || dxgiNativeBuffer.Length != nativeSize)
                                    {
                                        dxgiNativeBuffer = new byte[nativeSize];
                                    }

                                    var acq = dxgiCapture.AcquireFrame(dxgiNativeBuffer, timeoutMs: 15, out bool hasNewPixels);
                                    if (acq == DxgiAcquireResult.Success || acq == DxgiAcquireResult.Timeout)
                                    {
                                        DownsampleBgra(dxgiNativeBuffer, bounds.Width, bounds.Height, pBits, targetW, targetH);
                                        dxgiSuccess = true;
                                        BroadcastStateService.Instance.SetActiveBackend("DXGI (GPU)");
                                    }
                                    else if (acq == DxgiAcquireResult.AccessLost || acq == DxgiAcquireResult.Error)
                                    {
                                        dxgiCapture.Cleanup();
                                        dxgiCapture = null;
                                        nextDxgiRetry = DateTime.UtcNow.AddSeconds(3);
                                        BroadcastStateService.Instance.SetActiveBackend("GDI (CPU)");
                                    }
                                }
                            }
                        }

                        // 2. Fallback to GDI if DXGI not used or failed
                        if (!dxgiSuccess)
                        {
                            BroadcastStateService.Instance.SetActiveBackend("GDI (CPU)");

                            bool bltOk;
                            if (targetW == bounds.Width && targetH == bounds.Height)
                            {
                                bltOk = BitBlt(hdcMem, 0, 0, targetW, targetH, hdcScreen, bounds.X, bounds.Y, SRCCOPY);
                            }
                            else
                            {
                                bltOk = StretchBlt(hdcMem, 0, 0, targetW, targetH, hdcScreen, bounds.X, bounds.Y, bounds.Width, bounds.Height, SRCCOPY);
                            }

                            if (!bltOk)
                            {
                                CleanupGdi();
                            }
                        }

                        // 3. Render Cursor if enabled
                        if (showCursor)
                        {
                            DrawCursor(hdcMem, bounds, targetW, targetH);
                        }

                        // 4. Copy frame from DIB to managed buffer
                        Marshal.Copy(pBits, rawBuffer, 0, bufferSize);
                        frameCaptured = true;

                        // 5. JPEG fallback generation for non-WebRTC viewers
                        if (!BroadcastStateService.Instance.IsPaused)
                        {
                            var now = DateTime.UtcNow;
                            if ((now - _lastJpegBroadcast).TotalMilliseconds >= 1000 && !_isEncodingJpeg && jpegCodec != null && encoderParams != null)
                            {
                                _lastJpegBroadcast = now;
                                _isEncodingJpeg = true;

                                if (reusableJpegBuffer == null || reusableJpegBuffer.Length != bufferSize)
                                {
                                    reusableJpegBuffer = new byte[bufferSize];
                                }
                                Buffer.BlockCopy(rawBuffer, 0, reusableJpegBuffer, 0, bufferSize);
                                int jW = targetW;
                                int jH = targetH;

                                ThreadPool.QueueUserWorkItem(_ =>
                                {
                                    try
                                    {
                                        using var bmp = new Bitmap(jW, jH, PixelFormat.Format32bppArgb);
                                        var bmpData = bmp.LockBits(new Rectangle(0, 0, jW, jH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                                        Marshal.Copy(reusableJpegBuffer, 0, bmpData.Scan0, bufferSize);
                                        bmp.UnlockBits(bmpData);

                                        using var ms = new MemoryStream();
                                        bmp.Save(ms, jpegCodec, encoderParams);
                                        OnJpegFrameCaptured?.Invoke(ms.ToArray());
                                    }
                                    catch { }
                                    finally
                                    {
                                        _isEncodingJpeg = false;
                                    }
                                });
                            }
                        }

                        OnFrameCaptured?.Invoke(targetW, targetH, rawBuffer);
                    }
                    catch
                    {
                        CleanupGdi();
                    }
                }

                if (frameCaptured)
                {
                    _frameCount++;
                    var now = DateTime.UtcNow;
                    if ((now - _lastFpsCalc).TotalMilliseconds >= 1000)
                    {
                        CurrentFps = _frameCount;
                        _frameCount = 0;
                        _lastFpsCalc = now;
                        OnFpsUpdated?.Invoke(CurrentFps);
                    }
                }

                // Sub-millisecond pacing for rock-solid target FPS
                long targetFrameTicks = Stopwatch.Frequency / Math.Clamp(fps, 5, 60);
                nextFrameTick += targetFrameTicks;
                long nowTicks = Stopwatch.GetTimestamp();
                long waitTicks = nextFrameTick - nowTicks;
                if (waitTicks > 0)
                {
                    int sleepMs = (int)(waitTicks * 1000 / Stopwatch.Frequency);
                    if (sleepMs > 2)
                    {
                        Thread.Sleep(sleepMs - 2);
                    }
                    while (Stopwatch.GetTimestamp() < nextFrameTick)
                    {
                        Thread.SpinWait(10);
                    }
                }
                else
                {
                    nextFrameTick = nowTicks;
                }
            }
        }
        finally
        {
            dxgiCapture?.Dispose();
            CleanupGdi();
            encoderParams?.Dispose();
            TimeEndPeriod(1);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
