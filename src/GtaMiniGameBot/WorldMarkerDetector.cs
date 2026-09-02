namespace GtaMiniGameBot;

/// <summary><c>WorldMarker</c> của bản Python. <see cref="Locked"/> có thể true trong khi <see cref="Present"/> false (đang giữ 700 ms).</summary>
internal sealed class WorldMarker
{
    public bool Locked { get; init; }
    public bool Present { get; init; }

    /// <summary>Toạ độ màn (px thô), đã EMA, điểm tham chiếu ở CHÂN khối.</summary>
    public double? X { get; init; }

    public double? Y { get; init; }

    /// <summary>Đơn vị THAM CHIẾU (đã chia sx·sy), đã EMA.</summary>
    public double Area { get; init; }

    /// <summary>Đơn vị tham chiếu, KHÔNG EMA.</summary>
    public double Width { get; init; }

    public double Height { get; init; }
    public double Confidence { get; init; }
    public string Quality { get; init; }
    public double LastSeenAge { get; init; }

    public static readonly WorldMarker None = new()
    {
        Locked = false, Present = false, X = null, Y = null, Area = 0, Width = 0, Height = 0,
        Confidence = 0, Quality = "WORLD_NONE", LastSeenAge = 999.0
    };
}

/// <summary>
/// Dò đầu nối vàng 3D trong khung nhìn thế giới — <c>WorldMarkerDetector</c> (main.py 1467–1622).
///
/// Khác bản gốc một chỗ CÓ Ý THỨC: xử lý ở BƯỚC 2 (một pixel mỗi 2×2). Ở 2K vùng quét là 2560×1187
/// và <see cref="ImageOps"/> cấp phát mảng mới cho từng lượt hình thái học; chạy đủ độ phân giải là
/// hơn 100 ms mỗi khung, trong khi bộ dò va chạm world cần ≥ 18 mẫu trong 1.2 s. Khối nhỏ nhất
/// được nhận là 1200 đơn vị tham chiếu (≈ 2133 px ở 2K → 533 px ở nửa độ phân giải) nên bước 2
/// không làm mất khối nào; nhân CLOSE 7 hạ thành 3, OPEN 3 giữ nguyên, ngưỡng diện tích/bề rộng
/// quy đổi theo. Mean S/V và median chân khối lấy trên tập pixel đã lấy mẫu.
/// </summary>
internal sealed class WorldMarkerDetector
{
    private readonly NavScale _s;
    private int _hitStreak;
    private (double x, double y, double area, double bw, double bh, double score)? _last;
    private double? _lastSeenT;
    private double? _emaX, _emaY, _emaArea;

    /// <summary>Ứng viên tốt nhất của khung vừa xử lý (để <c>--verify-nav</c> in ra), null nếu không có.</summary>
    public string LastCandidateNote { get; private set; }

    public WorldMarkerDetector(NavScale s) => _s = s;

    public void Reset()
    {
        _hitStreak = 0;
        _last = null;
        _lastSeenT = null;
        _emaX = _emaY = _emaArea = null;
    }

    private static bool IsWorldYellow(int b, int g, int r)
    {
        int max = Math.Max(r, Math.Max(g, b));
        if (max < NavTuning.WorldVMin) return false;
        int min = Math.Min(r, Math.Min(g, b));
        int d = max - min;
        int s = (d * 255 + max / 2) / max;
        if (s < NavTuning.WorldSMin) return false;
        var (h, _, _) = ImageOps.HsvOf(b, g, r);
        return h >= NavTuning.WorldHLo && h <= NavTuning.WorldHHi;
    }

    /// <summary><c>_candidate(frame)</c>: khối vàng tốt nhất, hoặc null. Toạ độ trả về là toạ độ MÀN.</summary>
    public (double score, double tx, double ty, double an, double bwn, double bhn)? Candidate(NavFrame f)
    {
        LastCandidateNote = null;
        double sx = _s.Sx, sy = _s.Sy;
        var r = NavTuning.WorldRoiRef;
        var roiScreen = _s.RoiRef(r[0], r[1], r[2], r[3]);
        var local = f.ToLocal(roiScreen);
        if (local.IsEmpty) return null;

        const int step = 2;
        int hw = (local.Width + step - 1) / step, hh = (local.Height + step - 1) / step;
        var mask = new Mask(hw, hh);

        // Hai vung HUD bi xoa — tinh sang toa do cuc bo cua khung.
        var ex1 = f.ToLocal(_s.RoiRef(NavTuning.WorldExcludeBottomLeftRef[0], NavTuning.WorldExcludeBottomLeftRef[1],
                                       NavTuning.WorldExcludeBottomLeftRef[2], NavTuning.WorldExcludeBottomLeftRef[3]));
        var ex2 = f.ToLocal(_s.RoiRef(NavTuning.WorldExcludeTopRightRef[0], NavTuning.WorldExcludeTopRightRef[1],
                                       NavTuning.WorldExcludeTopRightRef[2], NavTuning.WorldExcludeTopRightRef[3]));

        for (int yy = 0; yy < hh; yy++)
        {
            int ly = local.Y + yy * step;
            int row = ly * f.Stride;
            int orow = yy * hw;
            for (int xx = 0; xx < hw; xx++)
            {
                int lx = local.X + xx * step;
                if (!ex1.IsEmpty && ex1.Contains(lx, ly)) continue;
                if (!ex2.IsEmpty && ex2.Contains(lx, ly)) continue;
                int i = row + lx * 4;
                if (IsWorldYellow(f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2])) mask.Data[orow + xx] = 1;
            }
        }

        int k = Math.Max(3, (int)Math.Round(5 * _s.Max));
        if (k % 2 == 0) k++;
        int kHalf = Math.Max(3, k / step);
        if (kHalf % 2 == 0) kHalf--;
        mask = ImageOps.Close(mask, kHalf);
        mask = ImageOps.Open(mask, 3);

        var lab = ImageOps.Label(mask);
        (double score, double tx, double ty, double an, double bwn, double bhn)? best = null;
        double areaScale = step * step;

        for (int i = 0; i < lab.Blobs.Count; i++)
        {
            var b = lab.Blobs[i];
            int id = i + 1;
            double areaPx = b.Area * areaScale;
            double an = areaPx / (sx * sy + 1e-9);
            double bw = b.Box.Width * step, bh = b.Box.Height * step;
            double bwn = bw / Math.Max(sx, 1e-9), bhn = bh / Math.Max(sy, 1e-9);

            if (an < NavTuning.WorldMinArea || an > NavTuning.WorldMaxArea) continue;
            if (bwn < NavTuning.WorldMinWidth || bhn < NavTuning.WorldMinHeight) continue;
            if (bwn > NavTuning.WorldMaxWidth || bhn > NavTuning.WorldMaxHeight) continue;

            // Toa do man cua bbox.
            double byScreen = f.OriginY + local.Y + b.Box.Y * step;
            double bxScreen = f.OriginX + local.X + b.Box.X * step;
            double bboxBottom = (byScreen + bh) / Math.Max(sy, 1e-9);
            if (bboxBottom < NavTuning.WorldMinBboxBottomRef) continue;

            double fill = areaPx / Math.Max(1.0, bw * bh);
            if (fill < NavTuning.WorldMinFill) continue;

            // Mean S/V va median chan khoi, quet ban do nhan trong hop bao cua khoi.
            long sumS = 0, sumV = 0; int n = 0;
            double cutoff = byScreen + bh * NavTuning.WorldBottomFractionStart;
            var lowXs = new List<double>(); var lowYs = new List<double>();
            for (int yy = b.Box.Top; yy < b.Box.Bottom; yy++)
            {
                int lrow = yy * hw;
                int ly = local.Y + yy * step;
                int frow = ly * f.Stride;
                double yScreen = f.OriginY + ly;
                for (int xx = b.Box.Left; xx < b.Box.Right; xx++)
                {
                    if (lab.Label[lrow + xx] != id) continue;
                    int lx = local.X + xx * step;
                    int pi = frow + lx * 4;
                    var (_, sv, vv) = ImageOps.HsvOf(f.Bgra[pi], f.Bgra[pi + 1], f.Bgra[pi + 2]);
                    sumS += sv; sumV += vv; n++;
                    if (yScreen >= cutoff)
                    {
                        lowXs.Add(f.OriginX + lx);
                        lowYs.Add(yScreen);
                    }
                }
            }
            if (n < 1) continue;
            double ms = sumS / (double)n, mv = sumV / (double)n;
            if (ms < NavTuning.WorldMinSat || mv < NavTuning.WorldMinVal) continue;

            double tx, ty;
            if (lowXs.Count * areaScale >= NavTuning.WorldBottomMinPixels)
            {
                tx = NavGeometry.Median(lowXs);
                ty = NavGeometry.Median(lowYs);
            }
            else
            {
                tx = f.OriginX + local.X + b.Cx * step;
                ty = f.OriginY + local.Y + b.Cy * step;
            }

            double areaScore = Math.Min(1.0, an / 10000.0);
            double hScore = Math.Min(1.0, bhn / 180.0);
            double satScore = Math.Min(1.0, ms / 220.0);
            double fillScore = Math.Min(1.0, fill / 0.55);
            double score = 0.45 * areaScore + 0.20 * hScore + 0.20 * satScore + 0.15 * fillScore;

            if (best is null || score > best.Value.score)
            {
                best = (score, tx, ty, an, bwn, bhn);
                LastCandidateNote = $"score={score:F2} area={an:F0} {bwn:F0}×{bhn:F0}ref S={ms:F0} V={mv:F0} fill={fill:F2} " +
                                    $"chân=({tx:F0},{ty:F0}) bbox@{bxScreen:F0},{byScreen:F0}";
            }
        }
        return best;
    }

    /// <summary><c>update(frame, now)</c>.</summary>
    public WorldMarker Update(NavFrame f, double now)
    {
        var c = Candidate(f);
        if (c is not null && c.Value.score >= NavTuning.WorldAcceptScore)
        {
            var (score, x, y, area, bw, bh) = c.Value;
            _hitStreak++;
            _lastSeenT = now;
            double a = NavTuning.WorldEmaAlpha;
            _emaX = _emaX is null ? x : (1 - a) * _emaX.Value + a * x;
            _emaY = _emaY is null ? y : (1 - a) * _emaY.Value + a * y;
            _emaArea = _emaArea is null ? area : (1 - a) * _emaArea.Value + a * area;
            _last = (_emaX.Value, _emaY.Value, _emaArea.Value, bw, bh, score);
            bool locked = _hitStreak >= NavTuning.WorldConfirmFrames;
            double conf = Math.Min(1.0, score * (0.90 + 0.10 * Math.Min(1.0, _hitStreak / 3.0)));
            return new WorldMarker
            {
                Locked = locked, Present = true, X = _emaX, Y = _emaY, Area = _emaArea.Value, Width = bw, Height = bh,
                Confidence = conf, Quality = locked ? "WORLD_LOCK" : "WORLD_ACQUIRE", LastSeenAge = 0.0
            };
        }

        _hitStreak = 0;
        if (_last is not null && _lastSeenT is not null)
        {
            double age = now - _lastSeenT.Value;
            if (age <= NavTuning.WorldGraceS)
            {
                var (x, y, area, bw, bh, score) = _last.Value;
                return new WorldMarker
                {
                    Locked = true, Present = false, X = x, Y = y, Area = area, Width = bw, Height = bh,
                    Confidence = Math.Max(0.25, score * 0.62), Quality = "WORLD_HOLD", LastSeenAge = age
                };
            }
        }
        return WorldMarker.None;
    }
}
