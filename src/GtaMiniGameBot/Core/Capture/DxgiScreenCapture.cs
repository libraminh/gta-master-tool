using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using DrawingSize = System.Drawing.Size;

namespace GtaMiniGameBot;

/// <summary>
/// Pull-based Desktop Duplication capture for one display output. It acquires one
/// frame per call and copies all requested ROIs before releasing that frame.
/// </summary>
internal sealed class DxgiScreenCapture : IScreenCaptureSession
{
    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    };

    private readonly Rectangle _targetBounds;
    private readonly Dictionary<DrawingSize, ID3D11Texture2D> _stagingTextures = new();

    private IDXGIAdapter1 _adapter;
    private IDXGIOutput1 _output;
    private IDXGIOutputDuplication _duplication;
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;
    private Rectangle _outputBounds;
    private bool _needsRecreate;
    private bool _disposed;
    private long _frameId;

    public DxgiScreenCapture(Screen targetScreen)
    {
        ArgumentNullException.ThrowIfNull(targetScreen);
        _targetBounds = targetScreen.Bounds;
        Recreate();
    }

    public string BackendName => "DXGI Desktop Duplication";
    public ScreenCaptureStatus Status { get; private set; } = ScreenCaptureStatus.Recovering;
    public string StatusDetail { get; private set; } = "Initializing";

    public bool TryCapture(
        Rectangle region,
        byte[] reuseBuffer,
        int timeoutMilliseconds,
        out CapturedRegion captured)
    {
        bool ok = TryCapture(
            new[] { region },
            reuseBuffer is null ? null : new[] { reuseBuffer },
            timeoutMilliseconds,
            out IReadOnlyList<CapturedRegion> batch);
        captured = ok ? batch[0] : null;
        return ok;
    }

    public bool TryCapture(
        IReadOnlyList<Rectangle> regions,
        IReadOnlyList<byte[]> reuseBuffers,
        int timeoutMilliseconds,
        out IReadOnlyList<CapturedRegion> captured)
    {
        captured = Array.Empty<CapturedRegion>();
        if (!ValidateRequest(regions, reuseBuffers, timeoutMilliseconds))
            return false;
        if (!EnsureReady())
            return false;
        if (!ValidateOutputBounds(regions))
            return false;

        IDXGIResource desktopResource = null;
        bool frameAcquired = false;
        try
        {
            Result acquire = _duplication.AcquireNextFrame(
                (uint)timeoutMilliseconds,
                out var frameInfo,
                out desktopResource);
            if (acquire == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                SetStatus(ScreenCaptureStatus.TimedOut, "No new desktop frame arrived before the timeout.");
                return false;
            }
            if (acquire.Failure)
            {
                HandleAcquireFailure(acquire);
                return false;
            }

            frameAcquired = true;
            using (ID3D11Texture2D source = desktopResource.QueryInterface<ID3D11Texture2D>())
            {
                Texture2DDescription sourceDescription = source.Description;
                if (sourceDescription.Format != Format.B8G8R8A8_UNorm)
                {
                    _needsRecreate = true;
                    SetStatus(
                        ScreenCaptureStatus.Unsupported,
                        $"Desktop format {sourceDescription.Format} is not tightly convertible to BGRA8.");
                    return false;
                }

                long frameId = checked(++_frameId);
                long timestamp = frameInfo.LastPresentTime > 0
                    ? frameInfo.LastPresentTime
                    : Stopwatch.GetTimestamp();
                var result = new CapturedRegion[regions.Count];
                for (int i = 0; i < regions.Count; i++)
                {
                    Rectangle region = regions[i];
                    int stride = checked(region.Width * 4);
                    int length = checked(stride * region.Height);
                    byte[] requested = reuseBuffers is not null ? reuseBuffers[i] : null;
                    byte[] buffer = requested is not null && requested.Length == length
                        ? requested
                        : new byte[length];

                    ID3D11Texture2D staging = GetStagingTexture(region.Size);
                    int left = region.Left - _outputBounds.Left;
                    int top = region.Top - _outputBounds.Top;
                    var sourceBox = new Box(
                        left,
                        top,
                        0,
                        left + region.Width,
                        top + region.Height,
                        1);

                    _context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, 0, sourceBox);
                    CopyMappedBgra(staging, buffer, stride, region.Height);
                    result[i] = new CapturedRegion(region, buffer, stride, frameId, timestamp, BackendName);
                }

                captured = result;
                SetStatus(ScreenCaptureStatus.Ready, "Ready");
                return true;
            }
        }
        catch (Exception ex)
        {
            _needsRecreate = true;
            SetStatus(ScreenCaptureStatus.Recovering, ex.Message);
            return false;
        }
        finally
        {
            desktopResource?.Dispose();
            if (frameAcquired && _duplication is not null)
            {
                Result release = _duplication.ReleaseFrame();
                if (release.Failure)
                    _needsRecreate = true;
            }
        }
    }

    public bool WaitForNextFrame(int timeoutMilliseconds)
    {
        if (!ValidateTimeout(timeoutMilliseconds) || !EnsureReady())
            return false;

        IDXGIResource desktopResource = null;
        bool frameAcquired = false;
        try
        {
            Result acquire = _duplication.AcquireNextFrame(
                (uint)timeoutMilliseconds,
                out _,
                out desktopResource);
            if (acquire == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                SetStatus(ScreenCaptureStatus.TimedOut, "No new desktop frame arrived before the timeout.");
                return false;
            }
            if (acquire.Failure)
            {
                HandleAcquireFailure(acquire);
                return false;
            }

            frameAcquired = true;
            _ = checked(++_frameId);
            SetStatus(ScreenCaptureStatus.Ready, "Ready");
            return true;
        }
        catch (Exception ex)
        {
            _needsRecreate = true;
            SetStatus(ScreenCaptureStatus.Recovering, ex.Message);
            return false;
        }
        finally
        {
            desktopResource?.Dispose();
            if (frameAcquired && _duplication is not null)
            {
                Result release = _duplication.ReleaseFrame();
                if (release.Failure)
                    _needsRecreate = true;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeDxgiResources();
        SetStatus(ScreenCaptureStatus.Disposed, "Capture session is disposed.");
    }

    private bool EnsureReady()
    {
        if (_disposed)
        {
            SetStatus(ScreenCaptureStatus.Disposed, "Capture session is disposed.");
            return false;
        }
        if (!_needsRecreate)
            return true;

        try
        {
            Recreate();
            return true;
        }
        catch (Exception ex)
        {
            _needsRecreate = true;
            SetStatus(ScreenCaptureStatus.Recovering, ex.Message);
            return false;
        }
    }

    private void Recreate()
    {
        DisposeDxgiResources();
        SetStatus(ScreenCaptureStatus.Recovering, "Creating desktop duplication session.");

        try
        {
            FindOutput(out IDXGIAdapter1 adapter, out IDXGIOutput1 output, out Rectangle outputBounds);
            _adapter = adapter;
            _output = output;
            _outputBounds = outputBounds;

            Result createDevice = D3D11CreateDevice(
                _adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                FeatureLevels,
                out _device,
                out _,
                out _context);
            createDevice.CheckError();

            _duplication = _output.DuplicateOutput(_device);
            _needsRecreate = false;
            SetStatus(ScreenCaptureStatus.Ready, "Ready");
        }
        catch
        {
            DisposeDxgiResources();
            _needsRecreate = true;
            throw;
        }
    }

    private void FindOutput(
        out IDXGIAdapter1 selectedAdapter,
        out IDXGIOutput1 selectedOutput,
        out Rectangle selectedBounds)
    {
        selectedAdapter = null;
        selectedOutput = null;
        selectedBounds = Rectangle.Empty;
        IDXGIOutput retainedOutput = null;
        long bestArea = 0;

        try
        {
            using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0;
                 factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success;
                 adapterIndex++)
            {
                using (adapter)
                {
                    if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                        continue;

                    for (uint outputIndex = 0;
                         adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success;
                         outputIndex++)
                    {
                        OutputDescription description = output.Description;
                        Rectangle bounds = Rectangle.FromLTRB(
                            description.DesktopCoordinates.Left,
                            description.DesktopCoordinates.Top,
                            description.DesktopCoordinates.Right,
                            description.DesktopCoordinates.Bottom);
                        Rectangle intersection = Rectangle.Intersect(_targetBounds, bounds);
                        long area = (long)intersection.Width * intersection.Height;
                        if (!description.AttachedToDesktop || area <= bestArea)
                        {
                            output.Dispose();
                            continue;
                        }

                        IDXGIAdapter1 adapterReference;
                        try
                        {
                            adapterReference = adapter.QueryInterface<IDXGIAdapter1>();
                        }
                        catch
                        {
                            output.Dispose();
                            throw;
                        }

                        retainedOutput?.Dispose();
                        selectedAdapter?.Dispose();
                        retainedOutput = output;
                        selectedAdapter = adapterReference;
                        selectedBounds = bounds;
                        bestArea = area;
                    }
                }
            }

            if (retainedOutput is null || selectedAdapter is null)
                throw new NotSupportedException("No hardware DXGI output intersects the target screen.");

            OutputDescription selectedDescription = retainedOutput.Description;
            if (selectedDescription.Rotation != ModeRotation.Identity)
            {
                throw new NotSupportedException(
                    $"DXGI output rotation {selectedDescription.Rotation} is unsupported.");
            }
            selectedOutput = retainedOutput.QueryInterface<IDXGIOutput1>();
        }
        catch
        {
            selectedOutput?.Dispose();
            selectedOutput = null;
            selectedAdapter?.Dispose();
            selectedAdapter = null;
            throw;
        }
        finally
        {
            retainedOutput?.Dispose();
        }
    }

    private ID3D11Texture2D GetStagingTexture(DrawingSize size)
    {
        if (_stagingTextures.TryGetValue(size, out ID3D11Texture2D texture))
            return texture;

        var description = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)size.Width,
            (uint)size.Height,
            1,
            1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read);
        texture = _device.CreateTexture2D(description);
        _stagingTextures.Add(size, texture);
        return texture;
    }

    private void CopyMappedBgra(
        ID3D11Texture2D staging,
        byte[] destination,
        int destinationStride,
        int height)
    {
        Result map = _context.Map(
            staging,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None,
            out MappedSubresource mapped);
        map.CheckError();
        try
        {
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(
                    mapped.DataPointer + y * (int)mapped.RowPitch,
                    destination,
                    y * destinationStride,
                    destinationStride);
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private bool ValidateRequest(
        IReadOnlyList<Rectangle> regions,
        IReadOnlyList<byte[]> reuseBuffers,
        int timeoutMilliseconds)
    {
        if (!ValidateTimeout(timeoutMilliseconds))
            return false;
        if (regions is null || regions.Count == 0)
        {
            SetStatus(ScreenCaptureStatus.InvalidRequest, "At least one region is required.");
            return false;
        }
        if (reuseBuffers is not null && reuseBuffers.Count != regions.Count)
        {
            SetStatus(ScreenCaptureStatus.InvalidRequest, "Reuse buffer count must match region count.");
            return false;
        }
        foreach (Rectangle region in regions)
        {
            if (region.Width < 1 || region.Height < 1)
            {
                SetStatus(ScreenCaptureStatus.InvalidRequest, "Capture regions must have positive dimensions.");
                return false;
            }
        }
        return true;
    }

    private bool ValidateOutputBounds(IReadOnlyList<Rectangle> regions)
    {
        foreach (Rectangle region in regions)
        {
            if (_outputBounds.Contains(region))
                continue;

            SetStatus(
                ScreenCaptureStatus.InvalidRequest,
                $"Region {region} is outside DXGI output {_outputBounds}.");
            return false;
        }
        return true;
    }

    private bool ValidateTimeout(int timeoutMilliseconds)
    {
        if (_disposed)
        {
            SetStatus(ScreenCaptureStatus.Disposed, "Capture session is disposed.");
            return false;
        }
        if (timeoutMilliseconds < 0)
        {
            SetStatus(ScreenCaptureStatus.InvalidRequest, "Timeout must be non-negative.");
            return false;
        }
        return true;
    }

    private void HandleAcquireFailure(Result result)
    {
        if (result == Vortice.DXGI.ResultCode.AccessLost ||
            result == Vortice.DXGI.ResultCode.ModeChangeInProgress ||
            result == Vortice.DXGI.ResultCode.DeviceRemoved ||
            result == Vortice.DXGI.ResultCode.DeviceReset)
        {
            _needsRecreate = true;
            SetStatus(ScreenCaptureStatus.Recovering, $"Desktop duplication must be recreated ({result}).");
            return;
        }

        SetStatus(ScreenCaptureStatus.Failed, $"AcquireNextFrame failed ({result}).");
    }

    private void DisposeDxgiResources()
    {
        foreach (ID3D11Texture2D staging in _stagingTextures.Values)
            staging.Dispose();
        _stagingTextures.Clear();

        _duplication?.Dispose();
        _duplication = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _output?.Dispose();
        _output = null;
        _adapter?.Dispose();
        _adapter = null;
    }

    private void SetStatus(ScreenCaptureStatus status, string detail)
    {
        Status = status;
        StatusDetail = detail;
    }
}
