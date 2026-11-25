using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StreamerApp.Services;

public enum DxgiAcquireResult
{
    Success,
    Timeout,
    AccessLost,
    Error
}

public class DxgiScreenCapture : IDisposable
{
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private int _outputIndex = -1;
    private int _monitorWidth = 0;
    private int _monitorHeight = 0;

    public int MonitorWidth => _monitorWidth;
    public int MonitorHeight => _monitorHeight;
    public bool IsInitialized => _duplication != null;

    public bool Initialize(int outputIndex)
    {
        Cleanup();
        _outputIndex = outputIndex;

        try
        {
            var creationFlags = DeviceCreationFlags.BgraSupport;
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };

            var d3dResult = D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                creationFlags,
                featureLevels,
                out _device,
                out _context);

            if (d3dResult.Failure || _device == null || _context == null)
            {
                Cleanup();
                return false;
            }

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            if (dxgiDevice == null)
            {
                Cleanup();
                return false;
            }

            using var adapter = dxgiDevice.GetAdapter();
            if (adapter == null)
            {
                Cleanup();
                return false;
            }

            IDXGIOutput? output = null;
            if (adapter.EnumOutputs((uint)Math.Max(0, outputIndex), out output).Failure || output == null)
            {
                // Fallback to output 0 if requested index is not found
                if (adapter.EnumOutputs(0, out output).Failure || output == null)
                {
                    Cleanup();
                    return false;
                }
            }

            using var output1 = output.QueryInterface<IDXGIOutput1>();
            output.Dispose();

            if (output1 == null)
            {
                Cleanup();
                return false;
            }

            var desc = output1.Description;
            _monitorWidth = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            _monitorHeight = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

            _duplication = output1.DuplicateOutput(_device);
            if (_duplication == null)
            {
                Cleanup();
                return false;
            }

            // Create staging texture for CPU readback
            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)_monitorWidth,
                Height = (uint)_monitorHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            _stagingTexture = _device.CreateTexture2D(stagingDesc);
            return true;
        }
        catch
        {
            Cleanup();
            return false;
        }
    }

    public DxgiAcquireResult AcquireFrameToMemory(IntPtr targetPtr, int bufferSize, int timeoutMs, out bool hasNewPixels)
    {
        hasNewPixels = false;
        if (_duplication == null || _device == null || _context == null || _stagingTexture == null)
        {
            return DxgiAcquireResult.Error;
        }

        var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);

        if (result.Success)
        {
            try
            {
                if (desktopResource != null)
                {
                    using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                    if (desktopTexture != null)
                    {
                        _context.CopyResource(_stagingTexture, desktopTexture);

                        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            int rowBytes = _monitorWidth * 4;
                            int totalSize = rowBytes * _monitorHeight;
                            if (bufferSize >= totalSize && targetPtr != IntPtr.Zero)
                            {
                                int srcPitch = (int)mapped.RowPitch;
                                if (srcPitch == rowBytes)
                                {
                                    CopyMemory(targetPtr, mapped.DataPointer, (uint)totalSize);
                                }
                                else
                                {
                                    for (int y = 0; y < _monitorHeight; y++)
                                    {
                                        IntPtr srcRow = IntPtr.Add(mapped.DataPointer, y * srcPitch);
                                        IntPtr dstRow = IntPtr.Add(targetPtr, y * rowBytes);
                                        CopyMemory(dstRow, srcRow, (uint)rowBytes);
                                    }
                                }
                                hasNewPixels = true;
                            }
                        }
                        finally
                        {
                            _context.Unmap(_stagingTexture, 0);
                        }
                    }
                }
                return DxgiAcquireResult.Success;
            }
            finally
            {
                desktopResource?.Dispose();
                _duplication.ReleaseFrame();
            }
        }

        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
        {
            return DxgiAcquireResult.Timeout;
        }

        if (result.Code == Vortice.DXGI.ResultCode.AccessLost.Code ||
            result.Code == Vortice.DXGI.ResultCode.DeviceReset.Code ||
            result.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code)
        {
            Cleanup();
            return DxgiAcquireResult.AccessLost;
        }

        return DxgiAcquireResult.Error;
    }

    public DxgiAcquireResult AcquireFrame(byte[] targetBuffer, int timeoutMs, out bool hasNewPixels)
    {
        hasNewPixels = false;
        GCHandle handle = GCHandle.Alloc(targetBuffer, GCHandleType.Pinned);
        try
        {
            return AcquireFrameToMemory(handle.AddrOfPinnedObject(), targetBuffer.Length, timeoutMs, out hasNewPixels);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Cleanup()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _duplication?.Dispose();
        _duplication = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
