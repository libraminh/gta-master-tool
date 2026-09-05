namespace GtaMiniGameBot;

/// <summary>
/// Cổng giao quyền panel dây: hai lần <see cref="WireReader.ConfirmPanel"/> trên khung độc lập,
/// hộp/số dây phải khớp. Một hit viền hoặc cùng một lần thăm dò không đủ.
/// </summary>
internal sealed class WirePanelGate
{
    public const int StableHits = 2;
    public const double MinIndependentGapS = 0.12;
    public const double SkipLogThrottleS = 0.50;

    private int _streak;
    private Rectangle _lastBox;
    private int _lastCount;
    private double _lastHitAt = double.NegativeInfinity;
    private double _lastSkipLog = double.NegativeInfinity;

    public int Streak => _streak;

    public void Reset()
    {
        _streak = 0;
        _lastBox = default;
        _lastCount = 0;
        _lastHitAt = double.NegativeInfinity;
    }

    /// <summary>
    /// Ghi một lần thăm dò. Trả true khi đủ hai khung độc lập. <paramref name="skip"/> là lý do
    /// chưa giao quyền — null khi vừa tăng streak nhưng chưa đủ hit.
    /// </summary>
    public bool Note(WireProbeHit hit, double now, out string skip)
    {
        if (!hit.Ok)
        {
            Reset();
            skip = hit.Reject ?? "không thấy panel";
            return false;
        }

        if (_streak > 0 && now + 1e-9 < _lastHitAt + MinIndependentGapS)
        {
            skip = "cùng lần thăm dò";
            return false;
        }

        if (_streak > 0 && !Compatible(_lastBox, hit.Panel, _lastCount, hit.Round.Count))
        {
            _streak = 1;
            Remember(hit, now);
            skip = "hộp/số dây lệch — đếm lại";
            return false;
        }

        _streak++;
        Remember(hit, now);
        skip = null;
        return _streak >= StableHits;
    }

    public bool ShouldLogSkip(double now, string skip)
    {
        if (string.IsNullOrEmpty(skip)) return false;
        if (skip == "không thấy viền") return false;
        if (now - _lastSkipLog < SkipLogThrottleS) return false;
        _lastSkipLog = now;
        return true;
    }

    private void Remember(WireProbeHit hit, double now)
    {
        _lastBox = hit.Panel;
        _lastCount = hit.Round.Count;
        _lastHitAt = now;
    }

    public static bool Compatible(Rectangle a, Rectangle b, int countA, int countB)
    {
        if (countA != countB) return false;
        int slop = Math.Max(40, Math.Max(a.Width, b.Width) / 8);
        return Math.Abs(a.X - b.X) <= slop
               && Math.Abs(a.Y - b.Y) <= slop
               && Math.Abs(a.Width - b.Width) <= slop
               && Math.Abs(a.Height - b.Height) <= slop;
    }
}
