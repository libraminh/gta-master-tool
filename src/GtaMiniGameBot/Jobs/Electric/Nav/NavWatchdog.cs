namespace GtaMiniGameBot;

/// <summary>
/// Bộ dò va chạm theo bán kính tới đích — <c>VectorStuckWatchdog.impact_stuck</c> (main.py 2168–2246),
/// nhánh sống DUY NHẤT trong năm bộ dò kẹt của bản Python.
///
/// Ý tưởng: trên minimap xoay theo người chơi, quay camera đổi GÓC tới đích chứ không đổi BÁN KÍNH.
/// Đâm vào vật cản thì bán kính đứng phẳng suốt 0.9 s dù W đang xuống — đó là dấu hiệu kẹt, không
/// phải "trông có vật cản gần" hay "tiến chậm lúc đang cua".
/// </summary>
internal sealed class NavWatchdog
{
    private readonly NavScale _s;
    private readonly LinkedList<(double t, double dx, double dy)> _hist = new();
    private double _cooldownUntil;
    private double? _candidateSince;

    public NavWatchdog(NavScale s) => _s = s;

    public void Reset()
    {
        _hist.Clear();
        _candidateSince = null;
    }

    /// <summary>Bản Python xoá ứng viên trực tiếp khi world marker đang che kẹt minimap.</summary>
    public void ClearCandidate() => _candidateSince = null;

    /// <summary><c>add(now, dx, dy)</c> — lịch sử giữ <c>stuck_window_ms × 1.35</c> = 0.972 s (khoá KHÁC với cửa sổ 0.9 s, giữ đúng).</summary>
    public void Add(double now, double dx, double dy)
    {
        _hist.AddLast((now, dx, dy));
        double keep = NavTuning.StuckWindowS * 1.35;
        while (_hist.Count > 0 && now - _hist.First.Value.t > keep) _hist.RemoveFirst();
    }

    public bool ImpactStuck(double now, bool forwardRequested, bool targetAvailable, double currentDist, double currentAngle)
    {
        if (!forwardRequested || !targetAvailable || now < _cooldownUntil) { _candidateSince = null; return false; }
        if (!double.IsFinite(currentDist)) { _candidateSince = null; return false; }

        double angle = double.IsFinite(currentAngle) ? Math.Abs(currentAngle) : 999.0;
        if (angle > NavTuning.ImpactMaxHeadingErrorDeg) { _candidateSince = null; return false; }
        if (currentDist < NavTuning.ImpactMinDistancePx * _s.Px) { _candidateSince = null; return false; }

        double win = NavTuning.ImpactWindowS;
        var arr = new List<(double t, double dx, double dy)>();
        foreach (var x in _hist) if (now - x.t <= win) arr.Add(x);
        if (arr.Count < NavTuning.ImpactMinSamples || arr[^1].t - arr[0].t < win * 0.78) { _candidateSince = null; return false; }

        var d = new double[arr.Count];
        for (int i = 0; i < arr.Count; i++) d[i] = Math.Sqrt(arr[i].dx * arr[i].dx + arr[i].dy * arr[i].dy);
        int n = d.Length, k = Math.Max(3, n / 3);
        double d0 = NavGeometry.Median(d.Take(k));
        double d1 = NavGeometry.Median(d.Skip(n - k));
        double improvement = d0 - d1;

        var sorted = (double[])d.Clone();
        Array.Sort(sorted);
        double radialSpan = NavGeometry.Percentile(sorted, 90) - NavGeometry.Percentile(sorted, 10);

        bool raw = improvement < NavTuning.ImpactRequiredProgressPx * _s.Px
                   && radialSpan < NavTuning.ImpactMaxRadialSpanPx * _s.Px;
        if (!raw) { _candidateSince = null; return false; }
        if (_candidateSince is null) { _candidateSince = now; return false; }
        return now - _candidateSince.Value >= NavTuning.ImpactConfirmS;
    }

    /// <summary><c>cooldown(now)</c> — luôn 950 ms trong chuỗi sống, rồi xoá lịch sử.</summary>
    public void Cooldown(double now)
    {
        _cooldownUntil = now + NavTuning.StuckPostCooldownS;
        Reset();
    }
}

/// <summary>
/// Phân loại vật cản trước mặt — <c>ObstacleClassifier</c> (main.py 1848–1966), CHỈ giữ phần còn được
/// dùng: mật độ biên Canny nửa trái so với nửa phải để chọn BÊN thoát kẹt (+1 = nửa trái nhiều biên
/// hơn → thoát sang phải). Phân loại WALL/TRANSFORMER, HoughLinesP và độ tin cậy đều bị vòng lặp
/// chính ghi đè thành "IMPACT" rồi vứt, nên không port; <c>strong/weak</c> vì thế chỉ còn theo mật độ biên.
/// </summary>
internal sealed class ObstacleClassifier
{
    private readonly NavScale _s;
    private readonly LinkedList<(double t, double edge, double left, double right, bool strong, bool weak)> _hist = new();
    private double _lastObserveT;

    public ObstacleClassifier(NavScale s) => _s = s;

    public void Clear()
    {
        _hist.Clear();
        _lastObserveT = 0;
    }

    private (double edge, double left, double right, bool strong, bool weak)? Features(NavFrame f)
    {
        var r = NavTuning.ObstacleRoiRef;
        int w = _s.ScreenW, h = _s.ScreenH;
        int x0 = (int)(r[0] * _s.Sx), y0 = (int)(r[1] * _s.Sy), x1 = (int)(r[2] * _s.Sx), y1 = (int)(r[3] * _s.Sy);
        x0 = Math.Max(0, Math.Min(w - 2, x0)); x1 = Math.Max(x0 + 2, Math.Min(w, x1));
        y0 = Math.Max(0, Math.Min(h - 2, y0)); y1 = Math.Max(y0 + 2, Math.Min(h, y1));
        var local = f.ToLocal(new Rectangle(x0, y0, x1 - x0, y1 - y0));
        if (local.Width < 8 || local.Height < 8) return null;

        var gray = NavGeometry.GrayOf(f.Bgra, f.Stride, local);
        var edges = NavGeometry.Canny(gray, local.Width, local.Height, NavTuning.ObstacleCannyLow, NavTuning.ObstacleCannyHigh);

        int half = Math.Max(1, local.Width / 2);
        long total = 0, leftN = 0;
        for (int y = 0; y < local.Height; y++)
        {
            int row = y * local.Width;
            for (int x = 0; x < local.Width; x++)
            {
                if (edges.Data[row + x] == 0) continue;
                total++;
                if (x < half) leftN++;
            }
        }
        double area = (double)local.Width * local.Height;
        double edge = total / area;
        double left = leftN / (double)(half * local.Height);
        double right = (total - leftN) / (double)((local.Width - half) * local.Height);
        bool strong = edge >= NavTuning.ObstacleStrongEdgeDensity;
        bool weak = edge >= NavTuning.ObstacleWeakEdgeDensity;
        return (edge, left, right, strong, weak);
    }

    /// <summary><c>observe(frame, now)</c> — tự giới hạn 180 ms một lần, giữ 6.5 s lịch sử.</summary>
    public void Observe(NavFrame f, double now)
    {
        if (now - _lastObserveT < NavTuning.ObstacleObserveIntervalS) return;
        _lastObserveT = now;
        var feat = Features(f);
        if (feat is null) return;
        var (edge, left, right, strong, weak) = feat.Value;
        _hist.AddLast((now, edge, left, right, strong, weak));
        while (_hist.Count > 0 && now - _hist.First.Value.t > NavTuning.ObstacleHistoryS) _hist.RemoveFirst();
    }

    /// <summary>
    /// <c>analyze(frame, now).side</c>: +1 / −1 / 0. Lệch trong deadzone 0.003 thì tra mẫu có cấu trúc gần
    /// nhất trong 5.2 s; vẫn trong deadzone thì 0 (KET1 tự chọn theo góc tới đích).
    /// </summary>
    public int AnalyzeSide(NavFrame f, double now, out string note)
    {
        var cur = Features(f);
        note = "";
        if (cur is null) return 0;

        double diff = cur.Value.left - cur.Value.right;
        if (Math.Abs(diff) <= NavTuning.ObstacleSideDeadzone)
        {
            for (var node = _hist.Last; node is not null; node = node.Previous)
            {
                var x = node.Value;
                if (now - x.t > NavTuning.TransformerMemoryS) break;
                if (x.strong || x.weak) { diff = x.left - x.right; break; }
            }
        }
        int side = Math.Abs(diff) <= NavTuning.ObstacleSideDeadzone ? 0 : (diff > 0 ? 1 : -1);
        note = $"edge={cur.Value.edge:F4} L={cur.Value.left:F4} R={cur.Value.right:F4}";
        return side;
    }
}
