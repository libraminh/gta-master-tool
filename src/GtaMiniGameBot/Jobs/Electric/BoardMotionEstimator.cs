using System.Diagnostics;

namespace GtaMiniGameBot;

/// <summary>
/// Bộ lọc alpha-beta một chiều cho đầu dây. Timestamp là timestamp của FRAME, không phải lúc CPU
/// xử lý xong, nên dự báo tự bù được frame cũ và frame bị bỏ.
/// </summary>
internal sealed class BoardMotionEstimator
{
    private const double Alpha = 0.72;
    private const double Beta = 0.10;
    private const double MinDtMs = 0.25;
    private const double MaxDtMs = 100.0;

    private bool _ready;
    private double _position;
    private double _speedPxPerMs;
    private long _timestamp;

    public bool Ready => _ready;
    public double Position => _position;
    public double SpeedPxPerMs => Math.Max(0.0, _speedPxPerMs);

    public void Reset(double position, long frameTimestamp, double initialSpeedPxPerMs)
    {
        _ready = true;
        _position = position;
        _speedPxPerMs = Math.Max(0.0, initialSpeedPxPerMs);
        _timestamp = frameTimestamp;
    }

    public void Reset()
    {
        _ready = false;
        _position = 0;
        _speedPxPerMs = 0;
        _timestamp = 0;
    }

    public bool Update(double measuredPosition, long frameTimestamp)
    {
        if (!_ready)
        {
            Reset(measuredPosition, frameTimestamp, 0);
            return true;
        }
        if (frameTimestamp <= _timestamp) return false;

        double dtMs = TicksToMs(frameTimestamp - _timestamp);
        if (dtMs < MinDtMs) return false;
        if (dtMs > MaxDtMs)
        {
            // Khoảng mù dài: tin vị trí mới nhưng không suy một tốc độ rất nhỏ/sai từ nó.
            _position = measuredPosition;
            _timestamp = frameTimestamp;
            return true;
        }

        double predicted = _position + _speedPxPerMs * dtMs;
        double residual = measuredPosition - predicted;
        _position = predicted + Alpha * residual;
        _speedPxPerMs = Math.Max(0.0, _speedPxPerMs + Beta * residual / dtMs);
        _timestamp = frameTimestamp;
        return true;
    }

    public double Predict(long timestamp)
    {
        if (!_ready || timestamp <= _timestamp) return _position;
        double dtMs = Math.Min(MaxDtMs, TicksToMs(timestamp - _timestamp));
        return _position + _speedPxPerMs * dtMs;
    }

    public double LeadDistance(long nowTimestamp, double processingMs, double inputLatencyMs,
                               double maxLeadPx)
    {
        if (!_ready) return 0;
        double ageMs = nowTimestamp > _timestamp ? TicksToMs(nowTimestamp - _timestamp) : 0;
        double horizon = Math.Max(0, ageMs + processingMs + inputLatencyMs);
        return Math.Clamp(SpeedPxPerMs * horizon, 0, Math.Max(0, maxLeadPx));
    }

    public static double TicksToMs(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;
}
