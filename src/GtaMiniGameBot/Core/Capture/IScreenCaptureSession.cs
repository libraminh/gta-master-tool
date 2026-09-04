namespace GtaMiniGameBot;

/// <summary>State of a synchronous, single-consumer capture session.</summary>
internal enum ScreenCaptureStatus
{
    Ready,
    TimedOut,
    Recovering,
    Unsupported,
    InvalidRequest,
    Failed,
    Disposed,
}

/// <summary>
/// One absolute screen region copied as tightly packed, row-major BGRA.
/// A caller-provided <see cref="Bgra"/> array may be returned unchanged and reused.
/// </summary>
internal sealed class CapturedRegion
{
    public CapturedRegion(
        Rectangle region,
        byte[] bgra,
        int stride,
        long frameId,
        long captureTimestamp,
        string backendName)
    {
        Region = region;
        Bgra = bgra;
        Stride = stride;
        FrameId = frameId;
        CaptureTimestamp = captureTimestamp;
        BackendName = backendName;
    }

    public Rectangle Region { get; }
    public byte[] Bgra { get; }
    public int Stride { get; }
    public long FrameId { get; }
    public long CaptureTimestamp { get; }
    public string BackendName { get; }
}

/// <summary>
/// Pull-based screen capture. Calls are synchronous and intended for one consumer;
/// implementations keep only reusable native resources, never a frame queue.
/// </summary>
internal interface IScreenCaptureSession : IDisposable
{
    string BackendName { get; }
    ScreenCaptureStatus Status { get; }
    string StatusDetail { get; }

    bool TryCapture(
        Rectangle region,
        byte[] reuseBuffer,
        int timeoutMilliseconds,
        out CapturedRegion captured);

    /// <summary>
    /// Captures every absolute screen rectangle from one acquired frame where the
    /// backend supports it. A reuse buffer must be exactly width * height * 4 bytes.
    /// </summary>
    bool TryCapture(
        IReadOnlyList<Rectangle> regions,
        IReadOnlyList<byte[]> reuseBuffers,
        int timeoutMilliseconds,
        out IReadOnlyList<CapturedRegion> captured);

    /// <summary>
    /// Waits for a frame newer than the last frame observed by this session.
    /// The frame is released immediately; a subsequent capture waits for another frame.
    /// </summary>
    bool WaitForNextFrame(int timeoutMilliseconds);
}
