namespace GtaMiniGameBot;

/// <summary>Creates an Electric Board capture session with transparent GDI fallback.</summary>
internal static class ScreenCaptureFactory
{
    public static IScreenCaptureSession Create(
        Screen targetScreen,
        Action<string> log = null,
        int failuresBeforeFallback = 3)
    {
        ArgumentNullException.ThrowIfNull(targetScreen);
        if (failuresBeforeFallback < 1)
            throw new ArgumentOutOfRangeException(
                nameof(failuresBeforeFallback),
                "Failure threshold must be positive.");

        return new FailoverSession(targetScreen, log, failuresBeforeFallback);
    }

    private sealed class FailoverSession : IScreenCaptureSession
    {
        private readonly Action<string> _log;
        private readonly HashSet<string> _loggedMessages = new(StringComparer.Ordinal);
        private readonly int _failuresBeforeFallback;
        private IScreenCaptureSession _active;
        private int _consecutiveFailures;
        private bool _disposed;

        public FailoverSession(
            Screen targetScreen,
            Action<string> log,
            int failuresBeforeFallback)
        {
            _log = log;
            _failuresBeforeFallback = failuresBeforeFallback;
            try
            {
                _active = new DxgiScreenCapture(targetScreen);
                LogOnce($"Screen capture backend: {_active.BackendName}.");
            }
            catch (Exception ex)
            {
                _active = new GdiScreenCapture();
                LogOnce($"DXGI capture unavailable ({ex.Message}); using GDI.");
            }
        }

        public string BackendName => _active.BackendName;
        public ScreenCaptureStatus Status =>
            _disposed ? ScreenCaptureStatus.Disposed : _active.Status;
        public string StatusDetail =>
            _disposed ? "Capture session is disposed." : _active.StatusDetail;

        public bool TryCapture(
            Rectangle region,
            byte[] reuseBuffer,
            int timeoutMilliseconds,
            out CapturedRegion captured)
        {
            ThrowIfDisposed();
            bool ok = _active.TryCapture(
                region,
                reuseBuffer,
                timeoutMilliseconds,
                out captured);
            if (ok)
            {
                _consecutiveFailures = 0;
                return true;
            }
            if (!RegisterFailure())
                return false;

            return _active.TryCapture(
                region,
                reuseBuffer,
                timeoutMilliseconds,
                out captured);
        }

        public bool TryCapture(
            IReadOnlyList<Rectangle> regions,
            IReadOnlyList<byte[]> reuseBuffers,
            int timeoutMilliseconds,
            out IReadOnlyList<CapturedRegion> captured)
        {
            ThrowIfDisposed();
            bool ok = _active.TryCapture(
                regions,
                reuseBuffers,
                timeoutMilliseconds,
                out captured);
            if (ok)
            {
                _consecutiveFailures = 0;
                return true;
            }
            if (!RegisterFailure())
                return false;

            return _active.TryCapture(
                regions,
                reuseBuffers,
                timeoutMilliseconds,
                out captured);
        }

        public bool WaitForNextFrame(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            bool ok = _active.WaitForNextFrame(timeoutMilliseconds);
            if (ok)
            {
                _consecutiveFailures = 0;
                return true;
            }
            if (!RegisterFailure())
                return false;

            return _active.WaitForNextFrame(timeoutMilliseconds);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _active.Dispose();
        }

        private bool RegisterFailure()
        {
            if (_active is GdiScreenCapture)
                return false;

            if (_active.Status is ScreenCaptureStatus.TimedOut or
                ScreenCaptureStatus.InvalidRequest)
            {
                return false;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures < _failuresBeforeFallback)
                return false;

            string reason = _active.StatusDetail;
            _active.Dispose();
            _active = new GdiScreenCapture();
            _consecutiveFailures = 0;
            LogOnce($"DXGI capture failed repeatedly ({reason}); switched to GDI.");
            return true;
        }

        private void LogOnce(string message)
        {
            if (_log is null || !_loggedMessages.Add(message))
                return;

            try
            {
                _log(message);
            }
            catch
            {
                // Diagnostics must never make capture initialization or failover fatal.
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(IScreenCaptureSession));
        }
    }
}
