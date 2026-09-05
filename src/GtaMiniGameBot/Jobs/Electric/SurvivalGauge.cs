namespace GtaMiniGameBot;

/// <summary>
/// Cài đặt tự ăn/uống — khoá phụ thuộc HUD/máy và khẩu vị người dùng.
/// Hình học + màu đã học nằm trên <see cref="SurvivalHudProfile"/> theo từng độ phân giải;
/// <see cref="NavTuning"/> chỉ giữ ngưỡng an toàn.
/// </summary>
internal sealed class SurvivalSettings
{
    /// <summary>
    /// Mặc định TẮT. Chỉ có tác dụng khi <see cref="ElectricConfig.AutoWalk"/> bật.
    /// </summary>
    public bool Enabled { get; set; }

    public double FoodCenterXRef { get; set; } = 160.0;
    public double FoodCenterYRef { get; set; } = 1047.0;
    public double WaterCenterXRef { get; set; } = 210.0;
    public double WaterCenterYRef { get; set; } = 1047.0;

    /// <summary>Ô hotbar bánh: phím chính rồi (tuỳ chọn) ô dự phòng đã test riêng.</summary>
    public string FoodSlots { get; set; } = "6";

    /// <summary>Ô hotbar nước. Không được trùng ô bánh.</summary>
    public string WaterSlots { get; set; } = "7";

    public void Normalize()
    {
        FoodCenterXRef = ClampRef(FoodCenterXRef, 160.0, ElectricConfig.RefW);
        FoodCenterYRef = ClampRef(FoodCenterYRef, 1047.0, ElectricConfig.RefH);
        WaterCenterXRef = ClampRef(WaterCenterXRef, 210.0, ElectricConfig.RefW);
        WaterCenterYRef = ClampRef(WaterCenterYRef, 1047.0, ElectricConfig.RefH);
        FoodSlots = NormalizeSlots(FoodSlots, "6");
        WaterSlots = NormalizeSlots(WaterSlots, "7");
        DropOverlap();
    }

    /// <summary>Ô chụp hai đồng hồ: ROI đã khoanh trên profile, không thì suy từ hai tâm.</summary>
    public Rectangle CaptureRoi(NavScale s, SurvivalHudProfile hud = null)
    {
        if (hud is { HasRois: true })
            return hud.CaptureRoi(s.ScreenW, s.ScreenH);

        const double pad = NavTuning.SurvivalRingRmaxRef + 12.0;
        double x0 = Math.Min(FoodCenterXRef, WaterCenterXRef) - pad;
        double y0 = Math.Min(FoodCenterYRef, WaterCenterYRef) - pad;
        double x1 = Math.Max(FoodCenterXRef, WaterCenterXRef) + pad;
        double y1 = Math.Max(FoodCenterYRef, WaterCenterYRef) + pad;
        return s.RoiRef(x0, y0, x1, y1);
    }

    public bool CanRun(SurvivalHudProfile hud) => Enabled && hud is { IsReady: true };

    public ushort[] KeysFor(bool food, SurvivalHudProfile hud)
    {
        string raw = food ? FoodSlots : WaterSlots;
        if (hud is not null)
        {
            string verified = food ? hud.FoodVerifiedSlots : hud.WaterVerifiedSlots;
            if (!string.IsNullOrWhiteSpace(verified)) raw = verified;
        }
        return SlotKeys(raw);
    }

    public char PrimarySlot(bool food)
    {
        string slots = food ? FoodSlots : WaterSlots;
        foreach (char c in slots ?? "")
            if (c >= '1' && c <= '9') return c;
        return food ? '6' : '7';
    }

    public void SetPrimarySlot(bool food, char slot)
    {
        if (slot < '1' || slot > '9') return;
        if (food) FoodSlots = KeepBackup(slot, FoodSlots);
        else WaterSlots = KeepBackup(slot, WaterSlots);
        DropOverlap();
    }

    private static string KeepBackup(char primary, string raw)
    {
        char backup = '\0';
        foreach (char c in raw ?? "")
        {
            if (c < '1' || c > '9' || c == primary) continue;
            backup = c;
            break;
        }
        return backup == '\0' ? primary.ToString() : primary + "," + backup;
    }

    private void DropOverlap()
    {
        var food = new List<char>(2);
        foreach (char c in FoodSlots ?? "")
            if (c >= '1' && c <= '9' && !food.Contains(c)) food.Add(c);
        var water = new List<char>(2);
        foreach (char c in WaterSlots ?? "")
        {
            if (c < '1' || c > '9' || water.Contains(c) || food.Contains(c)) continue;
            water.Add(c);
        }
        if (food.Count == 0) food.Add('6');
        if (water.Count == 0)
        {
            char fallback = food.Contains('7') ? '4' : '7';
            water.Add(fallback);
        }
        FoodSlots = string.Join(",", food);
        WaterSlots = string.Join(",", water);
    }

    private static double ClampRef(double v, double fallback, double max)
        => double.IsNaN(v) || v <= 0 || v > max ? fallback : v;

    private static string NormalizeSlots(string raw, string fallback)
    {
        var keep = new List<char>(2);
        foreach (char c in raw ?? "")
        {
            if (c < '1' || c > '9' || keep.Contains(c)) continue;
            keep.Add(c);
            if (keep.Count == 2) break;
        }
        return keep.Count == 0 ? fallback : string.Join(",", keep);
    }

    public static ushort[] SlotKeys(string slots)
    {
        var list = new List<ushort>(2);
        foreach (char c in slots ?? "")
            if (c >= '1' && c <= '9') list.Add((ushort)c);
        return list.ToArray();
    }
}

/// <summary>ROI, hình học, màu và phím đã xác minh — theo đúng một độ phân giải.</summary>
internal sealed class SurvivalHudProfile
{
    public FishingRect FoodRoi { get; set; } = new();
    public FishingRect WaterRoi { get; set; } = new();

    public double FoodCx { get; set; }
    public double FoodCy { get; set; }
    public double FoodRmin { get; set; }
    public double FoodRmax { get; set; }
    public int FoodHue { get; set; }
    public int FoodHueSpread { get; set; } = 12;
    public int FoodSMin { get; set; } = 80;
    public int FoodVMin { get; set; } = 70;

    public double WaterCx { get; set; }
    public double WaterCy { get; set; }
    public double WaterRmin { get; set; }
    public double WaterRmax { get; set; }
    public int WaterHue { get; set; }
    public int WaterHueSpread { get; set; } = 12;
    public int WaterSMin { get; set; } = 80;
    public int WaterVMin { get; set; } = 70;

    public bool FoodHudReady { get; set; }
    public bool WaterHudReady { get; set; }
    public bool FoodSlotVerified { get; set; }
    public bool WaterSlotVerified { get; set; }
    public string FoodVerifiedSlots { get; set; } = "";
    public string WaterVerifiedSlots { get; set; } = "";

    public bool HasRois => FoodRoi.IsSet && WaterRoi.IsSet;
    public bool IsHudReady => FoodHudReady && WaterHudReady;
    public bool IsReady => IsHudReady && FoodSlotVerified && WaterSlotVerified;

    public void Normalize()
    {
        FoodRoi ??= new FishingRect();
        WaterRoi ??= new FishingRect();
        FoodHueSpread = Math.Clamp(FoodHueSpread <= 0 ? 12 : FoodHueSpread, 4, 40);
        WaterHueSpread = Math.Clamp(WaterHueSpread <= 0 ? 12 : WaterHueSpread, 4, 40);
        FoodSMin = Math.Clamp(FoodSMin <= 0 ? 80 : FoodSMin, 20, 255);
        FoodVMin = Math.Clamp(FoodVMin <= 0 ? 70 : FoodVMin, 20, 255);
        WaterSMin = Math.Clamp(WaterSMin <= 0 ? 80 : WaterSMin, 20, 255);
        WaterVMin = Math.Clamp(WaterVMin <= 0 ? 70 : WaterVMin, 20, 255);
        FoodVerifiedSlots = SurvivalSettings.SlotKeys(FoodVerifiedSlots).Length == 0
            ? "" : string.Join(",", SurvivalSettings.SlotKeys(FoodVerifiedSlots).Select(k => (char)k));
        WaterVerifiedSlots = SurvivalSettings.SlotKeys(WaterVerifiedSlots).Length == 0
            ? "" : string.Join(",", SurvivalSettings.SlotKeys(WaterVerifiedSlots).Select(k => (char)k));
        if (!HasRois)
        {
            FoodHudReady = false;
            WaterHudReady = false;
        }
        if (!FoodHudReady) FoodSlotVerified = false;
        if (!WaterHudReady) WaterSlotVerified = false;
    }

    public Rectangle CaptureRoi(int screenW, int screenH)
    {
        var a = FoodRoi.ToRectangle();
        var b = WaterRoi.ToRectangle();
        var u = Rectangle.Union(a, b);
        u.Inflate(8, 8);
        var screen = new Rectangle(0, 0, Math.Max(1, screenW), Math.Max(1, screenH));
        var clip = Rectangle.Intersect(u, screen);
        return clip.Width < 8 || clip.Height < 8 ? Rectangle.Empty : clip;
    }

    public void ApplyRing(bool food, SurvivalRing ring)
    {
        if (ring is null) return;
        if (food)
        {
            FoodCx = ring.Cx; FoodCy = ring.Cy;
            FoodRmin = ring.Rmin; FoodRmax = ring.Rmax;
            FoodHue = ring.Hue; FoodHueSpread = ring.HueSpread;
            FoodSMin = ring.SMin; FoodVMin = ring.VMin;
        }
        else
        {
            WaterCx = ring.Cx; WaterCy = ring.Cy;
            WaterRmin = ring.Rmin; WaterRmax = ring.Rmax;
            WaterHue = ring.Hue; WaterHueSpread = ring.HueSpread;
            WaterSMin = ring.SMin; WaterVMin = ring.VMin;
        }
    }

    public void MarkSlotVerified(bool food, char slot)
    {
        if (slot < '1' || slot > '9') return;
        if (food)
        {
            FoodSlotVerified = true;
            FoodVerifiedSlots = MergeSlot(FoodVerifiedSlots, slot);
        }
        else
        {
            WaterSlotVerified = true;
            WaterVerifiedSlots = MergeSlot(WaterVerifiedSlots, slot);
        }
    }

    private static string MergeSlot(string raw, char slot)
    {
        var keys = SurvivalSettings.SlotKeys(raw).Select(k => (char)k).ToList();
        if (!keys.Contains(slot)) keys.Insert(0, slot);
        if (keys.Count > 2) keys.RemoveRange(2, keys.Count - 2);
        return string.Join(",", keys);
    }
}

/// <summary>Một vòng cung đã đo trên một khung. <c>Pct</c> là NaN khi không đọc được icon.</summary>
internal readonly struct SurvivalReading
{
    public bool FoodValid { get; init; }
    public double FoodPct { get; init; }
    public double FoodRawPct { get; init; }
    public double FoodConfidence { get; init; }
    public int FoodFragments { get; init; }
    public bool WaterValid { get; init; }
    public double WaterPct { get; init; }
    public double WaterRawPct { get; init; }
    public double WaterConfidence { get; init; }
    public int WaterFragments { get; init; }
    public bool FoodLow { get; init; }
    public bool WaterLow { get; init; }

    public static SurvivalReading None => new()
    {
        FoodPct = double.NaN,
        FoodRawPct = double.NaN,
        WaterPct = double.NaN,
        WaterRawPct = double.NaN
    };
}

/// <summary>Hình học + màu của một đồng hồ, toạ độ màn (pixel thật).</summary>
internal sealed class SurvivalRing
{
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double Rmin { get; set; }
    public double Rmax { get; set; }
    public int Hue { get; set; }
    public int HueSpread { get; set; } = 12;
    public int SMin { get; set; } = 80;
    public int VMin { get; set; } = 70;
    public bool Learned { get; set; }

    public SurvivalRing Clone() => new()
    {
        Cx = Cx, Cy = Cy, Rmin = Rmin, Rmax = Rmax,
        Hue = Hue, HueSpread = HueSpread, SMin = SMin, VMin = VMin, Learned = Learned
    };

    public static SurvivalRing FromSettings(SurvivalSettings cfg, NavScale s, SurvivalHudProfile hud, bool food)
    {
        if (hud is not null && (food ? hud.FoodHudReady && hud.FoodRmax > hud.FoodRmin
                                    : hud.WaterHudReady && hud.WaterRmax > hud.WaterRmin))
        {
            return food
                ? new SurvivalRing
                {
                    Cx = hud.FoodCx, Cy = hud.FoodCy, Rmin = hud.FoodRmin, Rmax = hud.FoodRmax,
                    Hue = hud.FoodHue, HueSpread = hud.FoodHueSpread,
                    SMin = hud.FoodSMin, VMin = hud.FoodVMin, Learned = true
                }
                : new SurvivalRing
                {
                    Cx = hud.WaterCx, Cy = hud.WaterCy, Rmin = hud.WaterRmin, Rmax = hud.WaterRmax,
                    Hue = hud.WaterHue, HueSpread = hud.WaterHueSpread,
                    SMin = hud.WaterSMin, VMin = hud.WaterVMin, Learned = true
                };
        }

        return new SurvivalRing
        {
            Cx = (food ? cfg.FoodCenterXRef : cfg.WaterCenterXRef) * s.Sx,
            Cy = (food ? cfg.FoodCenterYRef : cfg.WaterCenterYRef) * s.Sy,
            Rmin = NavTuning.SurvivalRingRminRef * s.Max,
            Rmax = NavTuning.SurvivalRingRmaxRef * s.Max,
            Hue = food ? (NavTuning.FoodHLo + NavTuning.FoodHHi) / 2 : (NavTuning.WaterHLo + NavTuning.WaterHHi) / 2,
            HueSpread = food ? Math.Max(8, (NavTuning.FoodHHi - NavTuning.FoodHLo + 1) / 2)
                             : Math.Max(8, (NavTuning.WaterHHi - NavTuning.WaterHLo + 1) / 2),
            SMin = food ? NavTuning.FoodSMin : NavTuning.WaterSMin,
            VMin = food ? NavTuning.FoodVMin : NavTuning.WaterVMin,
            Learned = false
        };
    }
}

internal readonly struct SurvivalArc
{
    public bool Valid { get; init; }
    public double Pct { get; init; }
    public double Confidence { get; init; }
    public int Fragments { get; init; }
    public int ArcBins { get; init; }

    public static SurvivalArc Hidden => new() { Pct = double.NaN };
}

internal enum SurvivalUseVerdict { Animating, Watching, Success, Failed }

internal enum SurvivalActKind { Start, Pending, Wait, Blocked }

/// <summary>State khác chỉ quyết định KHI NÀO được bấm, không làm detector mù.</summary>
internal static class SurvivalGate
{
    public static bool IsNpcBoard(string jobPhase) =>
        jobPhase is "WAIT_EMPLOYED_BOARD" or "WAIT_UNEMPLOYED_BOARD" or "WAIT_OUTSIDE_PROMPT";

    public static bool CanPauseJob(string jobPhase) => jobPhase == "SEEK_LIGHTNING";

    public static SurvivalActKind Decide(string jobPhase, string simplePhase, string cameraPhase,
        string ixPhase, bool panelOpen, bool ePressing)
    {
        if (panelOpen || cameraPhase is not null || ePressing) return SurvivalActKind.Blocked;
        if (ixPhase == NavInteraction.Settle) return SurvivalActKind.Blocked;
        if (IsNpcBoard(jobPhase)) return SurvivalActKind.Pending;
        if (CanPauseJob(jobPhase)) return SurvivalActKind.Start;
        if (jobPhase is not null) return SurvivalActKind.Wait;
        if (!string.Equals(simplePhase, "WORLD", StringComparison.Ordinal)) return SurvivalActKind.Wait;
        return SurvivalActKind.Start;
    }

    public static string WaitReason(SurvivalActKind kind, bool foodLow, bool waterLow, double foodPct, double waterPct)
    {
        string who = foodLow && waterLow ? $"BÁNH={foodPct:F0}% NƯỚC={waterPct:F0}%"
            : foodLow ? $"BÁNH={foodPct:F0}%"
            : $"NƯỚC={waterPct:F0}%";
        return kind switch
        {
            SurvivalActKind.Blocked => $"{who} — chờ panel/camera/E xong",
            SurvivalActKind.Pending => $"{who} — chờ bảng NPC đóng",
            SurvivalActKind.Wait => $"{who} — chờ state an toàn",
            SurvivalActKind.Start => $"{who} — mở bữa",
            _ => who
        };
    }
}

/// <summary>Xác nhận sau khi bấm vật phẩm: bỏ qua animation, chỉ nhận mức ổn định tăng thật.</summary>
internal sealed class SurvivalUseWatch
{
    private double _baseline;
    private double _start;
    private double? _highSince;
    private double _peak;

    public void Start(double baseline, double now)
    {
        _baseline = baseline;
        _start = now;
        _highSince = null;
        _peak = baseline;
    }

    public SurvivalUseVerdict Observe(double? stablePct, double now, out double after)
    {
        after = stablePct ?? double.NaN;
        if (now < _start + NavTuning.SurvivalAnimMinS) return SurvivalUseVerdict.Animating;

        if (stablePct is null)
            return now >= _start + NavTuning.SurvivalUseTimeoutS
                ? SurvivalUseVerdict.Failed
                : SurvivalUseVerdict.Watching;

        double v = stablePct.Value;
        if (v > _peak) _peak = v;

        bool good = v >= _baseline + NavTuning.SurvivalSuccessDeltaPct
                    || v >= NavTuning.SurvivalSuccessAbsPct;
        if (good)
        {
            _highSince ??= now;
            if (now - _highSince.Value >= NavTuning.SurvivalConfirmS)
                return SurvivalUseVerdict.Success;
            return SurvivalUseVerdict.Watching;
        }

        if (_highSince is not null && v < _baseline + 5.0)
            return SurvivalUseVerdict.Failed;

        _highSince = null;
        return now >= _start + NavTuning.SurvivalUseTimeoutS
            ? SurvivalUseVerdict.Failed
            : SurvivalUseVerdict.Watching;
    }
}

/// <summary>
/// Unwrap ROI thành dải cực, chấm theo màu đã học, chỉ giữ cung liên tục lớn nhất.
/// </summary>
internal static class SurvivalPolar
{
    public static SurvivalArc Read(NavFrame f, SurvivalRing ring)
    {
        if (f?.Bgra is null || ring is null || ring.Rmax <= ring.Rmin) return SurvivalArc.Hidden;

        double cx = ring.Cx - f.OriginX;
        double cy = ring.Cy - f.OriginY;
        if (cx < 1 || cy < 1 || cx >= f.Width - 1 || cy >= f.Height - 1)
            return SurvivalArc.Hidden;

        double coreR = Math.Min(ring.Rmin * 0.62, NavTuning.SurvivalCoreRadiusRef);
        if (coreR < 4) coreR = 4;
        int core = CountCore(f, cx, cy, coreR, ring);
        int need = Math.Max(3, NavTuning.SurvivalCoreMinPixels);
        if (core < need) return SurvivalArc.Hidden;

        int bins = NavTuning.SurvivalPolarBins;
        int samples = NavTuning.SurvivalPolarRadialSamples;
        double rmin = ring.Rmin, rmax = ring.Rmax;
        double step = samples <= 1 ? 0 : (rmax - rmin) / (samples - 1);

        var hit = new bool[bins];
        var score = new double[bins];
        int hitCount = 0;
        for (int i = 0; i < bins; i++)
        {
            double th = 2.0 * Math.PI * i / bins;
            double ct = Math.Cos(th), st = Math.Sin(th);
            double best = 0;
            for (int j = 0; j < samples; j++)
            {
                double rr = rmin + step * j;
                int x = (int)Math.Round(cx + rr * ct);
                int y = (int)Math.Round(cy + rr * st);
                if (x < 0 || x >= f.Width || y < 0 || y >= f.Height) continue;
                int k = y * f.Stride + x * 4;
                double sc = Score(f.Bgra[k], f.Bgra[k + 1], f.Bgra[k + 2], ring);
                if (sc > best) best = sc;
            }
            score[i] = best;
            if (best >= NavTuning.SurvivalScoreThreshold)
            {
                hit[i] = true;
                hitCount++;
            }
        }

        FillGaps(hit, NavTuning.SurvivalGapFillBins);
        DropIsolated(hit);

        var (longest, start, fragments) = LongestRun(hit);
        if (fragments > NavTuning.SurvivalMaxFragments && longest * 2 < hitCount)
            return SurvivalArc.Hidden;

        double mean = 0;
        if (longest > 0)
        {
            for (int k = 0; k < longest; k++)
                mean += score[(start + k) % bins];
            mean /= longest;
        }

        double pct = Math.Clamp(100.0 * longest / bins, 0.0, 100.0);
        double conf = mean * Math.Max(0.25, 1.0 - Math.Max(0, fragments - 1) * 0.14);
        if (conf < NavTuning.SurvivalMinConfidence && longest > 0)
            return SurvivalArc.Hidden;

        return new SurvivalArc
        {
            Valid = true,
            Pct = pct,
            Confidence = Math.Clamp(conf, 0, 1),
            Fragments = Math.Max(1, fragments),
            ArcBins = longest
        };
    }

    public static int HueDist(int a, int b)
    {
        int d = Math.Abs(a - b);
        return Math.Min(d, 180 - d);
    }

    public static double Score(int b, int g, int r, SurvivalRing ring)
    {
        int max = Math.Max(r, Math.Max(g, b));
        if (max < ring.VMin) return 0;
        int min = Math.Min(r, Math.Min(g, b));
        int d = max - min;
        int s = max == 0 ? 0 : (d * 255 + max / 2) / max;
        if (s < ring.SMin) return 0;
        var (h, _, _) = ImageOps.HsvOf(b, g, r);
        if (!HueOk(h, ring)) return 0;

        double huePart = ring.Learned
            ? 1.0 - HueDist(h, ring.Hue) / (double)Math.Max(1, ring.HueSpread)
            : 1.0;
        double satPart = Math.Clamp((s - ring.SMin) / 80.0, 0, 1);
        double valPart = Math.Clamp((max - ring.VMin) / 80.0, 0, 1);
        return Math.Clamp(huePart * 0.60 + satPart * 0.20 + valPart * 0.20, 0, 1);
    }

    private static bool HueOk(int h, SurvivalRing ring)
    {
        if (ring.Learned) return HueDist(h, ring.Hue) <= ring.HueSpread;
        bool food = ring.Hue < 60;
        return food
            ? h >= NavTuning.FoodHLo && h <= NavTuning.FoodHHi
            : h >= NavTuning.WaterHLo && h <= NavTuning.WaterHHi;
    }

    private static int CountCore(NavFrame f, double cx, double cy, double coreR, SurvivalRing ring)
    {
        int x0 = Math.Max(0, (int)(cx - coreR - 1));
        int x1 = Math.Min(f.Width, (int)(cx + coreR + 2));
        int y0 = Math.Max(0, (int)(cy - coreR - 1));
        int y1 = Math.Min(f.Height, (int)(cy + coreR + 2));
        if (x1 <= x0 || y1 <= y0) return 0;
        double r2 = coreR * coreR;
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * f.Stride;
            double dy = y - cy;
            for (int x = x0; x < x1; x++)
            {
                double dx = x - cx;
                if (dx * dx + dy * dy > r2) continue;
                int i = row + x * 4;
                if (Score(f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2], ring) >= NavTuning.SurvivalScoreThreshold)
                    n++;
            }
        }
        return n;
    }

    private static void FillGaps(bool[] hit, int maxGap)
    {
        int n = hit.Length;
        for (int gap = 1; gap <= maxGap; gap++)
        {
            for (int i = 0; i < n; i++)
            {
                if (hit[i]) continue;
                if (hit[(i - 1 + n) % n] && hit[(i + gap) % n])
                {
                    bool ok = true;
                    for (int k = 1; k < gap; k++)
                        if (hit[(i + k) % n]) { ok = false; break; }
                    if (ok)
                    {
                        for (int k = 0; k < gap; k++)
                            hit[(i + k) % n] = true;
                    }
                }
            }
        }
    }

    private static void DropIsolated(bool[] hit)
    {
        int n = hit.Length;
        var drop = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!hit[i]) continue;
            if (!hit[(i - 1 + n) % n] && !hit[(i + 1) % n]) drop[i] = true;
        }
        for (int i = 0; i < n; i++)
            if (drop[i]) hit[i] = false;
    }

    private static (int longest, int start, int fragments) LongestRun(bool[] hit)
    {
        int n = hit.Length;
        int fragments = 0, longest = 0, start = 0;
        int i = 0;
        while (i < n)
        {
            if (!hit[i]) { i++; continue; }
            int j = i;
            while (j < n && hit[j]) j++;
            // wrap only for the run that starts at 0 and ends at n-1 — handled below
            int len = j - i;
            fragments++;
            if (len > longest) { longest = len; start = i; }
            i = j;
        }

        if (hit[0] && hit[n - 1] && fragments >= 2)
        {
            int head = 0;
            while (head < n && hit[head]) head++;
            int tail = 0;
            while (tail < n && hit[n - 1 - tail]) tail++;
            int wrap = head + tail;
            if (wrap > longest && wrap <= n) { longest = wrap; start = n - tail; }
            fragments--;
        }

        if (longest == n) fragments = 1;
        return (longest, start, fragments);
    }
}

/// <summary>Học tâm / bán kính / màu từ ROI và cặp mẫu LOW/HIGH.</summary>
internal static class SurvivalCalibrator
{
    public static bool TryLearnGeometry(NavFrame f, Rectangle roi, bool food, out SurvivalRing ring)
    {
        ring = null;
        if (f?.Bgra is null || roi.Width < 24 || roi.Height < 24) return false;
        var local = f.ToLocal(roi);
        if (local.Width < 24 || local.Height < 24) return false;

        var seed = new SurvivalRing
        {
            Hue = food ? (NavTuning.FoodHLo + NavTuning.FoodHHi) / 2
                       : (NavTuning.WaterHLo + NavTuning.WaterHHi) / 2,
            HueSpread = 22,
            SMin = 50,
            VMin = 50,
            Learned = false
        };

        double cx = local.X + local.Width / 2.0;
        double cy = local.Y + local.Height / 2.0;
        double half = Math.Min(local.Width, local.Height) / 2.0;
        double coreR = half * 0.38;

        double sx = 0, sy = 0;
        int coreN = 0;
        Collect(f, local, seed, (x, y, _) =>
        {
            double dx = x - cx, dy = y - cy;
            if (dx * dx + dy * dy <= coreR * coreR)
            {
                sx += x; sy += y; coreN++;
            }
        });
        if (coreN >= 8)
        {
            cx = sx / coreN;
            cy = sy / coreN;
        }

        int maxR = Math.Max(8, (int)Math.Ceiling(half));
        var hist = new int[maxR + 1];
        int colorful = 0;
        Collect(f, local, seed, (x, y, _) =>
        {
            int r = (int)Math.Round(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)));
            if (r >= 0 && r <= maxR) { hist[r]++; colorful++; }
        });
        if (colorful < 20) return false;

        int loR = Math.Max(4, (int)Math.Ceiling(coreR + 3));
        int hiR = Math.Min(maxR, (int)(half * 0.95));
        if (loR >= hiR) loR = Math.Max(4, hiR - 6);
        int peak = loR, peakV = 0;
        for (int r = loR; r <= hiR; r++)
            if (hist[r] > peakV) { peakV = hist[r]; peak = r; }
        if (peakV < 3) return false;

        int need = Math.Max(1, peakV / 3);
        int rmin = peak, rmax = peak;
        while (rmin > loR && hist[rmin - 1] >= need) rmin--;
        while (rmax < hiR && hist[rmax + 1] >= need) rmax++;
        rmin = Math.Max(3, rmin - 1);
        rmax = Math.Min(maxR, rmax + 1);
        if (rmax - rmin < 2) { rmin = Math.Max(3, peak - 3); rmax = Math.Min(maxR, peak + 3); }

        ring = new SurvivalRing
        {
            Cx = cx + f.OriginX,
            Cy = cy + f.OriginY,
            Rmin = rmin,
            Rmax = rmax,
            Hue = seed.Hue,
            HueSpread = food ? 12 : 12,
            SMin = food ? NavTuning.FoodSMin : NavTuning.WaterSMin,
            VMin = food ? NavTuning.FoodVMin : NavTuning.WaterVMin,
            Learned = false
        };
        return true;
    }

    public static bool TryLearnColor(NavFrame low, NavFrame high, SurvivalRing geo, bool food, out SurvivalRing learned)
    {
        learned = null;
        if (geo is null || low?.Bgra is null || high?.Bgra is null) return false;

        var hues = new List<int>(256);
        var sats = new List<int>(256);
        var vals = new List<int>(256);
        SampleRing(high, geo, hues, sats, vals);
        SampleRing(low, geo, hues, sats, vals);
        if (hues.Count < 12) return false;

        hues.Sort(); sats.Sort(); vals.Sort();
        int hue = hues[hues.Count / 2];
        int spread = 8;
        int over = 0;
        foreach (int h in hues)
            if (SurvivalPolar.HueDist(h, hue) > spread) over++;
        while (spread < 28 && over > hues.Count / 10)
        {
            spread++;
            over = 0;
            foreach (int h in hues)
                if (SurvivalPolar.HueDist(h, hue) > spread) over++;
        }

        int sMin = Math.Max(40, Percentile(sats, 0.10) - 15);
        int vMin = Math.Max(40, Percentile(vals, 0.10) - 15);

        learned = geo.Clone();
        learned.Hue = hue;
        learned.HueSpread = spread;
        learned.SMin = sMin;
        learned.VMin = vMin;
        learned.Learned = true;
        return true;
    }

    public static bool SamplesAcceptable(SurvivalArc low, SurvivalArc high)
    {
        if (!low.Valid || !high.Valid) return false;
        if (low.Fragments > 3 || high.Fragments > 3) return false;
        if (low.Confidence < 0.30 || high.Confidence < 0.30) return false;
        if (high.Pct + 0.5 < low.Pct + NavTuning.SurvivalSampleDeltaPct) return false;
        if (low.Pct > 55.0) return false;
        if (high.Pct < 55.0) return false;
        return true;
    }

    private static void SampleRing(NavFrame f, SurvivalRing geo, List<int> hues, List<int> sats, List<int> vals)
    {
        double cx = geo.Cx - f.OriginX;
        double cy = geo.Cy - f.OriginY;
        int x0 = Math.Max(0, (int)(cx - geo.Rmax - 1));
        int x1 = Math.Min(f.Width, (int)(cx + geo.Rmax + 2));
        int y0 = Math.Max(0, (int)(cy - geo.Rmax - 1));
        int y1 = Math.Min(f.Height, (int)(cy + geo.Rmax + 2));
        double r0 = geo.Rmin * geo.Rmin, r1 = geo.Rmax * geo.Rmax;
        for (int y = y0; y < y1; y++)
        {
            int row = y * f.Stride;
            double dy = y - cy;
            for (int x = x0; x < x1; x++)
            {
                double dx = x - cx;
                double rr = dx * dx + dy * dy;
                if (rr < r0 || rr > r1) continue;
                int i = row + x * 4;
                var (h, s, v) = ImageOps.HsvOf(f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2]);
                if (s < 40 || v < 40) continue;
                if (SurvivalPolar.HueDist(h, geo.Hue) > 28) continue;
                hues.Add(h); sats.Add(s); vals.Add(v);
            }
        }
    }

    private static void Collect(NavFrame f, Rectangle local, SurvivalRing seed, Action<int, int, (int h, int s, int v)> visit)
    {
        for (int y = local.Y; y < local.Bottom; y++)
        {
            int row = y * f.Stride;
            for (int x = local.X; x < local.Right; x++)
            {
                int i = row + x * 4;
                var hsv = ImageOps.HsvOf(f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2]);
                if (hsv.S < seed.SMin || hsv.V < seed.VMin) continue;
                if (SurvivalPolar.HueDist(hsv.H, seed.Hue) > seed.HueSpread) continue;
                visit(x, y, hsv);
            }
        }
    }

    private static int Percentile(List<int> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int i = (int)Math.Round((sorted.Count - 1) * p);
        return sorted[Math.Clamp(i, 0, sorted.Count - 1)];
    }
}

/// <summary>
/// Đọc hai đồng hồ HUD: polar unwrap từng khung, quyết định bằng median cửa sổ 9 frame.
/// </summary>
internal sealed class SurvivalGauge
{
    private readonly SurvivalRing _food;
    private readonly SurvivalRing _water;

    private double _nextScan;
    private readonly Queue<double> _foodWin = new();
    private readonly Queue<double> _waterWin = new();
    private double _foodStable = double.NaN, _waterStable = double.NaN;
    private int _foodLowStreak, _waterLowStreak;
    private bool _using;
    private SurvivalReading _last = SurvivalReading.None;

    public SurvivalGauge(SurvivalSettings cfg, NavScale s, SurvivalHudProfile hud = null)
    {
        cfg ??= new SurvivalSettings();
        _food = SurvivalRing.FromSettings(cfg, s, hud, food: true);
        _water = SurvivalRing.FromSettings(cfg, s, hud, food: false);
    }

    public SurvivalReading Last => _last;
    public bool Due(double now) => now >= _nextScan;

    public void Reset()
    {
        _nextScan = 0;
        _foodWin.Clear();
        _waterWin.Clear();
        _foodStable = _waterStable = double.NaN;
        _foodLowStreak = _waterLowStreak = 0;
        _using = false;
        _last = SurvivalReading.None;
    }

    public void BeginUse()
    {
        _using = true;
        _foodWin.Clear();
        _waterWin.Clear();
        _foodStable = _waterStable = double.NaN;
        _foodLowStreak = _waterLowStreak = 0;
    }

    public void EndUse() => _using = false;

    public SurvivalArc ReadRaw(NavFrame f, bool food)
        => SurvivalPolar.Read(f, food ? _food : _water);

    public SurvivalReading Update(NavFrame f, double now)
    {
        _nextScan = now + NavTuning.SurvivalScanIntervalS;
        var food = SurvivalPolar.Read(f, _food);
        var water = SurvivalPolar.Read(f, _water);

        bool foodLow = Push(_foodWin, food, ref _foodStable, ref _foodLowStreak);
        bool waterLow = Push(_waterWin, water, ref _waterStable, ref _waterLowStreak);

        _last = new SurvivalReading
        {
            FoodValid = food.Valid,
            WaterValid = water.Valid,
            FoodRawPct = food.Valid ? food.Pct : double.NaN,
            WaterRawPct = water.Valid ? water.Pct : double.NaN,
            FoodPct = food.Valid ? (double.IsNaN(_foodStable) ? food.Pct : _foodStable) : double.NaN,
            WaterPct = water.Valid ? (double.IsNaN(_waterStable) ? water.Pct : _waterStable) : double.NaN,
            FoodConfidence = food.Confidence,
            WaterConfidence = water.Confidence,
            FoodFragments = food.Fragments,
            WaterFragments = water.Fragments,
            FoodLow = foodLow,
            WaterLow = waterLow
        };
        return _last;
    }

    private bool Push(Queue<double> win, SurvivalArc arc, ref double stable, ref int streak)
    {
        if (!arc.Valid)
        {
            streak = 0;
            return false;
        }

        if (!_using && !double.IsNaN(stable) && Math.Abs(arc.Pct - stable) > NavTuning.SurvivalJumpRejectPct)
            return win.Count >= NavTuning.SurvivalMedianMinValid && streak >= NavTuning.SurvivalLowConfirmScans;

        win.Enqueue(arc.Pct);
        while (win.Count > NavTuning.SurvivalMedianWindow) win.Dequeue();
        stable = Median(win);

        if (win.Count >= NavTuning.SurvivalMedianMinValid && stable < NavTuning.SurvivalLowThresholdPct)
            streak++;
        else
            streak = 0;

        return win.Count >= NavTuning.SurvivalMedianMinValid && streak >= NavTuning.SurvivalLowConfirmScans;
    }

    internal static double Median(IReadOnlyCollection<double> xs)
    {
        var a = xs.ToArray();
        Array.Sort(a);
        int n = a.Length;
        if (n == 0) return double.NaN;
        return n % 2 == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
    }
}
