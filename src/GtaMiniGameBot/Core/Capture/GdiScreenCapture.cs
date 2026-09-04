using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// GDI fallback for systems where desktop duplication is unavailable. Each bitmap
/// is reused by region size; batch regions are sampled consecutively because GDI
/// has no atomic multi-region desktop-frame API.
/// </summary>
internal sealed class GdiScreenCapture : IScreenCaptureSession
{
    private readonly Dictionary<Size, CaptureSurface> _surfaces = new();
    private long _frameId;
    private long _lastTimestamp;
    private bool _disposed;

    public string BackendName => "GDI";
    public ScreenCaptureStatus Status { get; private set; } = ScreenCaptureStatus.Ready;
    public string StatusDetail { get; private set; } = "Ready";

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

        try
        {
            long frameId = checked(++_frameId);
            long timestamp = NextTimestamp();
            var result = new CapturedRegion[regions.Count];

            for (int i = 0; i < regions.Count; i++)
            {
                Rectangle region = regions[i];
                if (!_surfaces.TryGetValue(region.Size, out CaptureSurface surface))
                {
                    surface = new CaptureSurface(region.Size);
                    _surfaces.Add(region.Size, surface);
                }

                byte[] requested = reuseBuffers is not null && i < reuseBuffers.Count
                    ? reuseBuffers[i]
                    : null;
                int stride = checked(region.Width * 4);
                int length = checked(stride * region.Height);
                byte[] buffer = requested is not null && requested.Length == length
                    ? requested
                    : new byte[length];

                surface.Capture(region, buffer, stride);
                result[i] = new CapturedRegion(region, buffer, stride, frameId, timestamp, BackendName);
            }

            captured = result;
            SetStatus(ScreenCaptureStatus.Ready, "Ready");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(ScreenCaptureStatus.Failed, ex.Message);
            return false;
        }
    }

    public bool WaitForNextFrame(int timeoutMilliseconds)
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

        // GDI exposes no presentation event. Yield briefly when allowed, then mark a
        // new sampling instant; the following TryCapture performs the actual read.
        if (timeoutMilliseconds > 0)
            Thread.Sleep(1);

        _ = checked(++_frameId);
        _lastTimestamp = NextTimestamp();
        SetStatus(ScreenCaptureStatus.Ready, "Ready (GDI sampling boundary)");
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (CaptureSurface surface in _surfaces.Values)
            surface.Dispose();
        _surfaces.Clear();
        SetStatus(ScreenCaptureStatus.Disposed, "Capture session is disposed.");
    }

    private bool ValidateRequest(
        IReadOnlyList<Rectangle> regions,
        IReadOnlyList<byte[]> reuseBuffers,
        int timeoutMilliseconds)
    {
        if (_disposed)
        {
            SetStatus(ScreenCaptureStatus.Disposed, "Capture session is disposed.");
            return false;
        }
        if (regions is null || regions.Count == 0)
        {
            SetStatus(ScreenCaptureStatus.InvalidRequest, "At least one region is required.");
            return false;
        }
        if (timeoutMilliseconds < 0)
        {
            SetStatus(ScreenCaptureStatus.InvalidRequest, "Timeout must be non-negative.");
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

    private long NextTimestamp()
    {
        long now = Stopwatch.GetTimestamp();
        if (now <= _lastTimestamp)
            now = _lastTimestamp + 1;
        _lastTimestamp = now;
        return now;
    }

    private void SetStatus(ScreenCaptureStatus status, string detail)
    {
        Status = status;
        StatusDetail = detail;
    }

    private sealed class CaptureSurface : IDisposable
    {
        private readonly Bitmap _bitmap;
        private readonly Graphics _graphics;

        public CaptureSurface(Size size)
        {
            _bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            _graphics = Graphics.FromImage(_bitmap);
        }

        public void Capture(Rectangle region, byte[] destination, int destinationStride)
        {
            _graphics.CopyFromScreen(
                region.Left,
                region.Top,
                0,
                0,
                region.Size,
                CopyPixelOperation.SourceCopy);

            BitmapData data = _bitmap.LockBits(
                new Rectangle(Point.Empty, region.Size),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < region.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, destination, y * destinationStride, destinationStride);
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }
        }

        public void Dispose()
        {
            _graphics.Dispose();
            _bitmap.Dispose();
        }
    }
}
