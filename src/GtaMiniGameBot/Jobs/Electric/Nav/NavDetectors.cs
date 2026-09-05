namespace GtaMiniGameBot;

/// <summary>
/// Một khung ảnh đã chụp (BGRA) kèm vị trí của nó trên màn — toạ độ TƯƠNG ĐỐI góc màn, đúng hệ mà
/// bản Python dùng vì nó chụp cả màn chính. Mọi detector nhận khung này và trả toạ độ màn.
/// </summary>
internal sealed class NavFrame
{
    public byte[] Bgra { get; init; }
    public int Stride { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Toạ độ màn của pixel (0,0) trong khung.</summary>
    public int OriginX { get; init; }

    public int OriginY { get; init; }

    /// <summary>Lúc chụp (NavClock).</summary>
    public double T { get; init; }

    public Rectangle ScreenRect => new(OriginX, OriginY, Width, Height);

    /// <summary>Ô màn → ô cục bộ trong khung (đã cắt). Rỗng nếu không giao.</summary>
    public Rectangle ToLocal(Rectangle screen)
    {
        var c = Rectangle.Intersect(screen, ScreenRect);
        if (c.Width <= 0 || c.Height <= 0) return Rectangle.Empty;
        return new Rectangle(c.X - OriginX, c.Y - OriginY, c.Width, c.Height);
    }

    /// <summary>Dựng từ ảnh tĩnh cả màn (cho <c>--verify-nav</c>).</summary>
    public static NavFrame FromBitmap(Bitmap bmp, Rectangle screenRect, double t = 0)
    {
        using var src = new BitmapRegion(bmp, screenRect);
        int w = src.Region.Width, h = src.Region.Height;
        var bgr = src.BgrBuffer();
        var bgra = new byte[w * h * 4];
        for (int k = 0, i = 0, j = 0; k < w * h; k++, i += 3, j += 4)
        {
            bgra[j] = bgr[i];
            bgra[j + 1] = bgr[i + 1];
            bgra[j + 2] = bgr[i + 2];
            bgra[j + 3] = 255;
        }
        return new NavFrame
        {
            Bgra = bgra, Stride = w * 4, Width = w, Height = h,
            OriginX = src.Region.X, OriginY = src.Region.Y, T = t
        };
    }
}

/// <summary>Ứng viên chấm vàng / mảnh — <c>Candidate</c> của bản Python.</summary>
internal sealed class NavCandidate
{
    /// <summary>Toạ độ màn (tương đối góc màn), trọng tâm đa giác biên.</summary>
    public double X { get; init; }

    public double Y { get; init; }

    /// <summary>Diện tích contour đã chia <c>sx·sy</c> — ĐƠN VỊ THAM CHIẾU.</summary>
    public double Area { get; init; }

    /// <summary>Bề rộng/cao bbox bằng PIXEL THÔ — bản Python để nguyên (quirk giữ lại vì ngưỡng overlap đo theo nó).</summary>
    public double Width { get; init; }

    public double Height { get; init; }

    public double Circularity { get; init; }
    public double Fill { get; init; }
    public double Solidity { get; init; }
    public double Score { get; init; }

    public override string ToString() =>
        $"({X:F1},{Y:F1}) area={Area:F1} {Width:F0}×{Height:F0} circ={Circularity:F2} fill={Fill:F2} " +
        $"solid={Solidity:F2} score={Score:F2}";
}

/// <summary><c>TargetOutput</c> của bản Python.</summary>
internal sealed class TargetOutput
{
    public string State { get; init; }
    public bool Visible { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double Confidence { get; init; }
    public int CandidateCount { get; init; }
    public string Quality { get; init; }
    public double RawGeometry { get; init; }

    public bool HasPos => X.HasValue && Y.HasValue;

    public static readonly TargetOutput Lost = new()
    {
        State = "LOST", Visible = false, X = null, Y = null, Confidence = 0, CandidateCount = 0, Quality = "NONE", RawGeometry = 0
    };
}

/// <summary>
/// Dò chấm vàng đích trên minimap và mảnh còn lại của nó dưới mũi tên — <c>YellowDotDetector</c>
/// (main.py 829–1286). Hai đường dò dùng CÙNG mask vàng nhưng bộ lọc khác hẳn: chấm đầy đòi hình
/// tròn (độ tròn, fill, solidity, radial_cv) còn mảnh chỉ đòi khối lượng và vị trí — vì khi đích
/// nằm dưới mũi tên trắng, phần vàng còn lại là hình khuyên không bao giờ qua được bộ lọc tròn.
/// </summary>
internal static class YellowDotDetector
{
    /// <summary>Hợp của dải strict và relaxed của bản Python — relaxed bao trọn strict nên chỉ còn một dải.</summary>
    public static bool IsYellow(int b, int g, int r)
    {
        int max = Math.Max(r, Math.Max(g, b));
        if (max < NavTuning.YellowVMin) return false;
        int min = Math.Min(r, Math.Min(g, b));
        int d = max - min;
        int s = (d * 255 + max / 2) / max;
        if (s < NavTuning.YellowSMin) return false;
        var (h, _, _) = ImageOps.HsvOf(b, g, r);
        return h >= NavTuning.YellowHLo && h <= NavTuning.YellowHHi;
    }

    /// <summary>Mặt nạ vàng của một ô cục bộ trong khung.</summary>
    public static Mask YellowMask(NavFrame f, Rectangle local)
    {
        var m = new Mask(local.Width, local.Height);
        for (int y = 0; y < local.Height; y++)
        {
            int row = (local.Y + y) * f.Stride;
            int orow = y * local.Width;
            for (int x = 0; x < local.Width; x++)
            {
                int i = row + (local.X + x) * 4;
                if (IsYellow(f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2])) m.Data[orow + x] = 1;
            }
        }
        return m;
    }

    /// <summary><c>detect(frame)</c>: chấm vàng đầy trong <c>target_roi_ref</c>, đã sắp theo điểm giảm dần.</summary>
    public static List<NavCandidate> Detect(NavFrame f, NavScale s, double originX, double originY)
    {
        var outp = new List<NavCandidate>();
        var roiScreen = s.RoiRef(NavTuning.TargetRoiRef[0], NavTuning.TargetRoiRef[1], NavTuning.TargetRoiRef[2], NavTuning.TargetRoiRef[3]);
        var local = f.ToLocal(roiScreen);
        if (local.IsEmpty) return outp;

        var mask = YellowDotDetector.YellowMask(f, local);
        mask = ImageOps.Close(mask, 2);                       // morphologyEx(CLOSE, ones(2,2))

        double sx = s.Sx, sy = s.Sy;
        double pox = originX, poy = originY;
        double lax = NavTuning.LightningAnchorXRef * sx, lay = NavTuning.LightningAnchorYRef * sy;
        int offX = f.OriginX + local.X, offY = f.OriginY + local.Y;

        foreach (var cnt in NavGeometry.FindContours(mask))
        {
            double area = cnt.Area;
            double an = area / (sx * sy + 1e-9);
            if (an < NavTuning.DotAreaMin || an > NavTuning.DotAreaMax) continue;

            int bw = cnt.Box.Width, bh = cnt.Box.Height;
            double bwn = bw / Math.Max(sx, 1e-9), bhn = bh / Math.Max(sy, 1e-9);
            if (bwn < NavTuning.DotWMin || bwn > NavTuning.DotWMax || bhn < NavTuning.DotHMin || bhn > NavTuning.DotHMax) continue;

            double aspect = bwn / Math.Max(bhn, 1e-6);
            if (aspect < NavTuning.DotAspectMin || aspect > NavTuning.DotAspectMax) continue;

            double circ = cnt.Circularity, fill = cnt.Fill, solid = cnt.Solidity;
            if (circ < NavTuning.DotCircularityMin || fill < NavTuning.DotFillMin || solid < NavTuning.DotSolidityMin) continue;
            if (!cnt.HasCentroid) continue;

            double lcx = cnt.Cx, lcy = cnt.Cy;
            double cx = offX + lcx, cy = offY + lcy;

            double radialCv = cnt.RadialCv(lcx, lcy);
            if (radialCv > NavTuning.DotRadialCvMax) continue;

            // PLAYER/ARROW LIGHTNING GUARD: sat mui ten thi phai la vong tron rat dep.
            double nearArrowD = Math.Sqrt((cx - pox) * (cx - pox) + (cy - poy) * (cy - poy)) / Math.Max(s.Max, 1e-9);
            if (nearArrowD <= NavTuning.LightningGuardRadiusRef)
            {
                if (circ < NavTuning.LightningGuardCircularity
                    || aspect < NavTuning.LightningGuardAspectMin || aspect > NavTuning.LightningGuardAspectMax
                    || fill < NavTuning.LightningGuardFill
                    || solid < NavTuning.LightningGuardSolidity
                    || radialCv > NavTuning.LightningGuardRadialCvMax)
                    continue;
            }

            // FIXED LIGHTNING ANCHOR GUARD: khong phai mat na toa do — dich that co the di qua day.
            double anchorD = Math.Sqrt((cx - lax) * (cx - lax) + (cy - lay) * (cy - lay)) / Math.Max(s.Max, 1e-9);
            if (anchorD <= NavTuning.LightningAnchorRoundGuardRadiusRef)
            {
                bool perfectRound = circ >= NavTuning.LightningAnchorFullCircularity
                                    && aspect >= NavTuning.LightningAnchorFullAspectMin
                                    && aspect <= NavTuning.LightningAnchorFullAspectMax
                                    && fill >= NavTuning.LightningAnchorFullFill
                                    && solid >= NavTuning.LightningAnchorFullSolidity
                                    && radialCv <= NavTuning.LightningAnchorRadialCvMax;
                if (!perfectRound) continue;
            }

            double areaScore = Math.Exp(-Math.Abs(Math.Log((an + 1) / (NavTuning.DotIdealArea + 1))) * 1.15);
            double circScore = Math.Min(1, circ / NavTuning.DotIdealCircularity);
            double aspectScore = Math.Exp(-Math.Abs(Math.Log(Math.Max(aspect, 1e-6))) * 2.4);
            double fillScore = Math.Min(1, fill / NavTuning.DotIdealFill);
            double solidScore = Math.Min(1, solid / 0.96);
            double radialScore = Math.Exp(-radialCv * 3.2);
            double score = 0.25 * areaScore + 0.25 * circScore + 0.14 * aspectScore
                           + 0.11 * fillScore + 0.11 * solidScore + 0.14 * radialScore;

            outp.Add(new NavCandidate
            {
                X = cx, Y = cy, Area = an, Width = bw, Height = bh,
                Circularity = circ, Fill = fill, Solidity = solid, Score = score
            });
        }

        outp.Sort((a, b) => b.Score.CompareTo(a.Score));
        return outp;
    }

    /// <summary><c>detect_near_fragments(frame, player)</c>: phần vàng còn lại quanh gốc mũi tên.</summary>
    public static List<NavCandidate> DetectNearFragments(NavFrame f, NavScale s, double playerX, double playerY)
    {
        var outp = new List<NavCandidate>();
        double sx = s.Sx, sy = s.Sy;
        double rx = NavTuning.FragmentRadiusRef * sx, ry = NavTuning.FragmentRadiusRef * sy;

        int x0 = Math.Max(0, (int)(playerX - rx)), x1 = Math.Min(s.ScreenW, (int)(playerX + rx));
        int y0 = Math.Max(0, (int)(playerY - ry)), y1 = Math.Min(s.ScreenH, (int)(playerY + ry));
        if (x1 <= x0 || y1 <= y0) return outp;

        var local = f.ToLocal(new Rectangle(x0, y0, x1 - x0, y1 - y0));
        if (local.IsEmpty) return outp;

        var mask = YellowMask(f, local);                      // KHONG co CLOSE o day
        int offX = f.OriginX + local.X, offY = f.OriginY + local.Y;

        double lax = NavTuning.LightningAnchorXRef * sx, lay = NavTuning.LightningAnchorYRef * sy;
        double anchorR = NavTuning.LightningFragmentAnchorRadiusRef * s.Max;
        double overlapOverride = NavTuning.LightningFragmentPlayerOverrideDistRef * s.Max;
        var lb = NavTuning.LightningFragmentBoxRef;
        double lbx0 = lax + lb[0] * sx, lby0 = lay + lb[1] * sy, lbx1 = lax + lb[2] * sx, lby1 = lay + lb[3] * sy;
        double maxDist = NavTuning.FragmentBootstrapMaxDistRef * s.Max;

        foreach (var cnt in NavGeometry.FindContours(mask))
        {
            double area = cnt.Area;
            double an = area / (sx * sy + 1e-9);
            if (an < NavTuning.FragmentAreaMin || an > NavTuning.FragmentAreaMax) continue;

            int bw = cnt.Box.Width, bh = cnt.Box.Height;
            double bwn = bw / Math.Max(sx, 1e-9), bhn = bh / Math.Max(sy, 1e-9);
            if (bwn < NavTuning.FragmentWMin || bwn > NavTuning.FragmentWMax || bhn < NavTuning.FragmentHMin || bhn > NavTuning.FragmentHMax) continue;
            if (!cnt.HasCentroid) continue;

            double cx = offX + cnt.Cx, cy = offY + cnt.Cy;
            double dp = Math.Sqrt((cx - playerX) * (cx - playerX) + (cy - playerY) * (cy - playerY));
            if (dp > maxDist) continue;

            // Vung tia set: bo qua tru khi manh dang de len dung tam nguoi choi.
            double da = Math.Sqrt((cx - lax) * (cx - lax) + (cy - lay) * (cy - lay));
            bool inAnchor = da <= anchorR;
            bool inBox = lbx0 <= cx && cx <= lbx1 && lby0 <= cy && cy <= lby1;
            if ((inAnchor || inBox) && dp > overlapOverride) continue;

            double areaScore = Math.Exp(-Math.Abs(an - 64.0) / 55.0);
            double distScore = Math.Max(0.0, 1.0 - dp / (maxDist + 1e-6));
            double sizeScore = Math.Max(0.0, 1.0 - Math.Abs(Math.Max(bwn, bhn) - 12.0) / 12.0);
            double score = 0.48 * areaScore + 0.34 * distScore + 0.18 * sizeScore;

            outp.Add(new NavCandidate
            {
                X = cx, Y = cy, Area = an, Width = bw, Height = bh,
                Circularity = cnt.Circularity, Fill = cnt.Fill, Solidity = cnt.Solidity, Score = score
            });
        }

        outp.Sort((a, b) => b.Score.CompareTo(a.Score));
        return outp;
    }
}

/// <summary>
/// Bám chấm vàng qua các khung — <c>DotTracker</c> (main.py 1289–1464): bootstrap, lọc alpha-beta,
/// bám mảnh khi chấm đầy biến mất dưới mũi tên, và trượt theo dự đoán khi mất hẳn.
///
/// Mọi ngưỡng pixel ở đây là px THÔ của bản Python → nhân <see cref="NavScale.Px"/>.
/// </summary>
internal sealed class DotTracker
{
    private readonly NavScale _s;

    private double _px, _py;                 // pos (screen px), hop le khi _hasPos
    private bool _hasPos;
    private double _vx, _vy;                 // px/s
    private double? _lastT, _lastSeenT, _lastFullSeenT, _lastOverlapSeenT;
    private int _misses, _hitStreak;
    private double _lastGeometry, _lastDist = 999.0;

    public DotTracker(NavScale s) => _s = s;

    public void Reset()
    {
        _hasPos = false; _px = _py = 0; _vx = _vy = 0;
        _lastT = _lastSeenT = _lastFullSeenT = _lastOverlapSeenT = null;
        _misses = 0; _hitStreak = 0; _lastGeometry = 0; _lastDist = 999.0;
    }

    private bool Predict(double now, out double x, out double y)
    {
        if (!_hasPos || _lastT is null) { x = y = 0; return false; }
        double dt = Math.Min(0.15, Math.Max(0, now - _lastT.Value));
        x = _px + _vx * dt;
        y = _py + _vy * dt;
        return true;
    }

    public TargetOutput Update(List<NavCandidate> candidates, double playerX, double playerY, double now, List<NavCandidate> fragments)
    {
        bool hasPred = Predict(now, out double predX, out double predY);
        NavCandidate chosen = null;
        double chosenScore = -1.0;
        double gate = NavTuning.TrackGatePx * _s.Px;

        foreach (var c in candidates)
        {
            double temporal;
            if (!_hasPos)
            {
                temporal = 0.65;
                if (c.Score < NavTuning.BootstrapGeometryMin) continue;
            }
            else
            {
                double d = Math.Sqrt((c.X - predX) * (c.X - predX) + (c.Y - predY) * (c.Y - predY));
                double eg = gate * (1 + Math.Min(1.5, _misses * 0.12));
                if (d > eg && _misses < NavTuning.TrackRebootstrapAfterMisses) continue;
                temporal = Math.Exp(-0.5 * Math.Pow(d / Math.Max(eg, 1e-6), 2));
            }
            double total = 0.68 * c.Score + 0.32 * temporal;
            if (total > chosenScore) { chosenScore = total; chosen = c; }
        }

        if (chosen is not null && chosenScore >= NavTuning.TrackAcceptScore)
        {
            if (!_hasPos)
            {
                _px = chosen.X; _py = chosen.Y; _vx = _vy = 0; _hasPos = true;
            }
            else
            {
                double dt = Math.Max(1.0 / 240, Math.Min(0.2, now - (_lastT ?? now)));
                double ix = chosen.X - predX, iy = chosen.Y - predY;
                _px = predX + NavTuning.TrackAlpha * ix;
                _py = predY + NavTuning.TrackAlpha * iy;
                _vx += (NavTuning.TrackBeta / dt) * ix;
                _vy += (NavTuning.TrackBeta / dt) * iy;
                double vmax = NavTuning.TrackVelocityCapPxS * _s.Px;
                double vn = Math.Sqrt(_vx * _vx + _vy * _vy);
                if (vn > vmax) { _vx *= vmax / vn; _vy *= vmax / vn; }
            }
            _lastT = _lastSeenT = _lastFullSeenT = now;
            _misses = 0;
            _hitStreak++;
            _lastGeometry = chosen.Score;
            _lastDist = Math.Sqrt((_px - playerX) * (_px - playerX) + (_py - playerY) * (_py - playerY));
            double conf = Math.Min(1, chosenScore * (0.82 + 0.18 * Math.Min(1, _hitStreak / 4.0)));
            return new TargetOutput
            {
                State = "LOCKED", Visible = true, X = _px, Y = _py, Confidence = conf,
                CandidateCount = candidates.Count, Quality = _hitStreak >= 2 ? "FULL_LOCK" : "ACQUIRE",
                RawGeometry = chosen.Score
            };
        }

        // Cham day co the mat truoc khi toi vi mui ten trang che — chi dung manh SAT gốc nguoi choi.
        fragments ??= new List<NavCandidate>();
        NavCandidate fchosen = null;
        double fscore = -1.0;
        double overlapMax = NavTuning.OverlapBootstrapMaxDistPx * _s.Px;
        foreach (var f in fragments)
        {
            double dp = Math.Sqrt((f.X - playerX) * (f.X - playerX) + (f.Y - playerY) * (f.Y - playerY));
            double score;
            if (!_hasPos)
            {
                // V5.9 SAFE OVERLAP BOOTSTRAP: chi cho phep trong hanh lang rat nho quanh tam nguoi choi.
                bool overlapBoot = dp <= overlapMax
                                   && f.Area >= NavTuning.OverlapBootstrapMinAreaRef
                                   && Math.Min(f.Width, f.Height) >= NavTuning.OverlapBootstrapMinSidePx * _s.Px
                                   && f.Solidity >= NavTuning.OverlapBootstrapMinSolidity;
                if (!overlapBoot) continue;
                double distScore = Math.Max(0.0, 1.0 - dp / Math.Max(1.0, overlapMax));
                double areaScore = Math.Min(1.0, f.Area / Math.Max(1.0, NavTuning.OverlapBootstrapTargetAreaRef));
                score = 0.50 * f.Score + 0.30 * distScore + 0.20 * areaScore;
            }
            else
            {
                bool recentFull = _lastFullSeenT is not null && now - _lastFullSeenT.Value <= NavTuning.FragmentRequireRecentFullS;
                bool recentOverlap = _lastOverlapSeenT is not null && now - _lastOverlapSeenT.Value <= NavTuning.OverlapBridgeS;
                if (!(recentFull || recentOverlap)) continue;

                double p2x = hasPred ? predX : _px, p2y = hasPred ? predY : _py;
                double d = Math.Sqrt((f.X - p2x) * (f.X - p2x) + (f.Y - p2y) * (f.Y - p2y));
                double strictGate = NavTuning.FragmentTrackGateStrictPx * _s.Px;
                if (d > strictGate) continue;
                score = 0.55 * f.Score + 0.45 * Math.Exp(-0.5 * Math.Pow(d / Math.Max(1.0, strictGate), 2));
            }
            if (score > fscore) { fscore = score; fchosen = f; }
        }

        if (fchosen is not null)
        {
            if (!_hasPos)
            {
                _px = fchosen.X; _py = fchosen.Y; _vx = _vy = 0; _hasPos = true;
            }
            else
            {
                double a = NavTuning.FragmentAlpha;
                _px = (1 - a) * _px + a * fchosen.X;
                _py = (1 - a) * _py + a * fchosen.Y;
                _vx *= 0.55; _vy *= 0.55;
            }
            _lastT = _lastSeenT = now;
            _misses = 0;
            _hitStreak = Math.Max(1, _hitStreak);
            _lastGeometry = fchosen.Score;
            _lastDist = Math.Sqrt((_px - playerX) * (_px - playerX) + (_py - playerY) * (_py - playerY));
            if (_lastDist <= overlapMax) _lastOverlapSeenT = now;

            double conf = _lastFullSeenT is null
                ? NavTuning.OverlapBootConf
                : (_hitStreak > 1 ? NavTuning.FragmentTrackConf : NavTuning.FragmentBootConf);
            return new TargetOutput
            {
                State = "FRAGMENT", Visible = true, X = _px, Y = _py, Confidence = conf,
                CandidateCount = candidates.Count + fragments.Count, Quality = "NEAR_FRAGMENT",
                RawGeometry = fchosen.Score
            };
        }

        _misses++;
        _lastT = now;
        if (_hasPos)
        {
            if (hasPred) { _px = predX; _py = predY; }
            bool near = _lastDist <= NavTuning.OcclusionNearDistancePx * _s.Px;
            double recent = now - (_lastSeenT ?? 0);
            if (near && recent <= NavTuning.OcclusionHoldS)
            {
                double conf = Math.Max(0.05, 0.72 * (1 - recent / NavTuning.OcclusionHoldS));
                return new TargetOutput
                {
                    State = "OCCLUDED_NEAR", Visible = false, X = _px, Y = _py, Confidence = conf,
                    CandidateCount = candidates.Count, Quality = "HOLD_LAST_ID", RawGeometry = _lastGeometry
                };
            }
            if (_misses <= NavTuning.TrackRebootstrapAfterMisses)
            {
                double conf = Math.Max(0.03, 0.45 * (1 - _misses / (double)NavTuning.TrackRebootstrapAfterMisses));
                return new TargetOutput
                {
                    State = "PREDICT", Visible = false, X = _px, Y = _py, Confidence = conf,
                    CandidateCount = candidates.Count, Quality = "PREDICT_ONLY", RawGeometry = _lastGeometry
                };
            }
        }
        if (_misses > NavTuning.TrackForgetAfterMisses) Reset();
        return new TargetOutput
        {
            State = "LOST", Visible = false, X = null, Y = null, Confidence = 0,
            CandidateCount = candidates.Count, Quality = "NONE", RawGeometry = 0
        };
    }
}

/// <summary>
/// Dò prompt "[E] …" theo heuristic của bản Python (<c>_simple_prompt_visible</c>, main.py 5254–5283):
/// một ô vuông trắng (phím E) cộng ít nhất bốn khối trắng cỡ chữ ngay bên phải trên cùng dòng, trong
/// vùng giữa màn. Không cần mẫu ảnh, nhận mọi prompt tương tác — kể cả prompt của NPC, nên khâu bỏ
/// qua prompt NPC sau khi xin việc nằm ở lớp trên.
///
/// Mọi kích thước nhân <c>scale = max(0.55, H/1080)</c> — chỉ theo chiều CAO màn, đúng bản gốc.
/// </summary>
internal static class PromptHeuristic
{
    /// <summary>Vùng quét rộng trên màn (tỉ lệ 0.45–0.73 × 0.42–0.68) — mọi prompt [E], kể cả NPC.</summary>
    public static Rectangle Roi(int screenW, int screenH) =>
        RatioRoi(screenW, screenH, NavTuning.SimpleERoiX0, NavTuning.SimpleERoiX1,
                 NavTuning.SimpleERoiY0, NavTuning.SimpleERoiY1);

    /// <summary>ROI chặt quanh HUD cố định <c>[E] TƯƠNG TÁC</c>.</summary>
    public static Rectangle WorkRoi(int screenW, int screenH) =>
        RatioRoi(screenW, screenH, NavTuning.WorkERoiX0, NavTuning.WorkERoiX1,
                 NavTuning.WorkERoiY0, NavTuning.WorkERoiY1);

    public static Rectangle RatioRoi(int screenW, int screenH, double x0n, double x1n, double y0n, double y1n)
    {
        int x0 = (int)Math.Round(screenW * x0n);
        int x1 = (int)Math.Round(screenW * x1n);
        int y0 = (int)Math.Round(screenH * y0n);
        int y1 = (int)Math.Round(screenH * y1n);
        x0 = Math.Clamp(x0, 0, screenW - 1); x1 = Math.Clamp(x1, x0 + 1, screenW);
        y0 = Math.Clamp(y0, 0, screenH - 1); y1 = Math.Clamp(y1, y0 + 1, screenH);
        return new Rectangle(x0, y0, x1 - x0, y1 - y0);
    }

    public static bool Visible(NavFrame f, int screenW, int screenH) =>
        VisibleInRoi(f, Roi(screenW, screenH), screenH, NavTuning.SimpleEMinTextGlyphs, 0.70, 1.38);

    /// <summary>Prompt công việc: ROI hẹp, ô phím vuông hơn, cần nhiều chữ hơn bên phải.</summary>
    public static bool WorkVisible(NavFrame f, int screenW, int screenH) =>
        VisibleInRoi(f, WorkRoi(screenW, screenH), screenH, NavTuning.WorkEMinTextGlyphs, 0.80, 1.25);

    private static bool VisibleInRoi(NavFrame f, Rectangle roi, int screenH, int minGlyphs,
                                    double arLo, double arHi)
    {
        if (screenH < 540 || f.Width < 8) return false;
        var local = f.ToLocal(roi);
        if (local.IsEmpty) return false;

        var m = new Mask(local.Width, local.Height);
        for (int y = 0; y < local.Height; y++)
        {
            int row = (local.Y + y) * f.Stride;
            int orow = y * local.Width;
            for (int x = 0; x < local.Width; x++)
            {
                int i = row + (local.X + x) * 4;
                int gray = (f.Bgra[i + 2] * 299 + f.Bgra[i + 1] * 587 + f.Bgra[i] * 114 + 500) / 1000;
                if (gray > NavTuning.SimpleEWhiteThreshold) m.Data[orow + x] = 1;   // THRESH_BINARY: > thresh
            }
        }

        double scale = Math.Max(0.55, screenH / 1080.0);
        var comps = new List<Blob>();
        var keycaps = new List<Blob>();
        foreach (var b in ImageOps.Blobs(m))
        {
            if (b.Area < 5) continue;
            comps.Add(b);
            int ww = b.Box.Width, hh = b.Box.Height;
            double ar = ww / Math.Max(1.0, hh);
            double fill = b.Area / Math.Max(1.0, (double)ww * hh);
            if (18.0 * scale <= ww && ww <= 44.0 * scale && 18.0 * scale <= hh && hh <= 44.0 * scale
                && arLo <= ar && ar <= arHi && fill >= NavTuning.SimpleEKeycapMinFill)
                keycaps.Add(b);
        }

        foreach (var k in keycaps)
        {
            int x = k.Box.X, y = k.Box.Y, ww = k.Box.Width, hh = k.Box.Height;
            int glyphs = 0;
            foreach (var c in comps)
            {
                int cx = c.Box.X, cy = c.Box.Y, cw = c.Box.Width, ch = c.Box.Height;
                if (!(x + ww + 7.0 * scale <= cx && cx <= x + ww + 150.0 * scale)) continue;
                if (!(y - 10.0 * scale <= cy && cy <= y + hh + 10.0 * scale)) continue;
                if (!(2.0 * scale <= cw && cw <= 20.0 * scale && 5.0 * scale <= ch && ch <= 22.0 * scale
                      && c.Area >= 7.0 * scale * scale)) continue;
                glyphs++;
            }
            if (glyphs >= minGlyphs) return true;
        }
        return false;
    }
}
