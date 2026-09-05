using System.Drawing.Imaging;

namespace GtaMiniGameBot;

/// <summary>
/// Kiểm bộ đọc đồng hồ đói/khát ngoài game: polar unwrap, ổn định thời gian,
/// cổng state, và mẫu LOW/HIGH do wizard lưu.
/// Chạy: GtaMiniGameBot.exe --verify-survival
/// </summary>
internal static class VerifySurvival
{
    public static int Run(string[] args)
    {
        Console.WriteLine("== kiểm tra bộ đọc đồng hồ đói/khát (polar + ổn định) ==");

        int fail = SelfTest();

        var cfg = ElectricConfig.Load();
        if (cfg.Profiles.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("chưa có profile nào trong electric.json — phần ảnh thật bỏ qua.");
            return fail == 0 ? 0 : 1;
        }

        foreach (var (key, profile) in cfg.Profiles.OrderBy(kv => kv.Key))
        {
            Console.WriteLine();
            Console.WriteLine($"-- {key} --");
            var s = new NavScale(profile.Width, profile.Height, cfg.Nav.ScreenPxScale);
            var roi = cfg.Survival.CaptureRoi(s, profile.SurvivalHud);
            Console.WriteLine($"  vùng chụp {roi.Width}×{roi.Height} @ {roi.X},{roi.Y}");
            if (profile.SurvivalHud.IsHudReady)
            {
                Console.WriteLine($"  HUD đã học: bánh ({profile.SurvivalHud.FoodCx:F0},{profile.SurvivalHud.FoodCy:F0}) " +
                                  $"r {profile.SurvivalHud.FoodRmin:F0}–{profile.SurvivalHud.FoodRmax:F0} " +
                                  $"H={profile.SurvivalHud.FoodHue}±{profile.SurvivalHud.FoodHueSpread}; " +
                                  $"nước ({profile.SurvivalHud.WaterCx:F0},{profile.SurvivalHud.WaterCy:F0})");
            }
            else
            {
                Console.WriteLine($"  chưa hiệu chuẩn HUD — fallback tâm " +
                                  $"({cfg.Survival.FoodCenterXRef * s.Sx:F1},{cfg.Survival.FoodCenterYRef * s.Sy:F1}) / " +
                                  $"({cfg.Survival.WaterCenterXRef * s.Sx:F1},{cfg.Survival.WaterCenterYRef * s.Sy:F1})");
            }
            fail += RealShots(cfg, profile, s, roi);
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "TẤT CẢ ĐẠT" : $"HỎNG {fail} ca");
        return fail == 0 ? 0 : 1;
    }

    private static void Check(ref int fail, bool ok, string name, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "ĐẠT" : "HỎNG")}] {name}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
        if (!ok) fail++;
    }

    private static readonly Color Food = Color.FromArgb(255, 220, 40);
    private static readonly Color Water = Color.FromArgb(40, 200, 220);
    private static readonly Color FoodHud = Color.FromArgb(0xE6, 0x7E, 0x22);
    private static readonly Color WaterHud = Color.FromArgb(0x5D, 0xAD, 0xE2);
    private static readonly Color Dark = Color.FromArgb(30, 30, 30);

    private static int SelfTest()
    {
        Console.WriteLine();
        Console.WriteLine("-- tự kiểm tra --");
        int fail = 0;
        fail += SlotCases();
        fail += NormalizeCases();
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440) })
            fail += GaugeCases(w, h);
        fail += RealisticGaugeCases();
        fail += PolarExtraCases();
        fail += RoiCases();
        fail += TemporalCases();
        fail += UseWatchCases();
        fail += GateCases();
        Console.WriteLine(fail == 0 ? "  tự kiểm tra: ĐẠT" : $"  tự kiểm tra: HỎNG {fail} ca");
        return fail;
    }

    private static int SlotCases()
    {
        int fail = 0;
        Console.WriteLine("  · ô hotbar");
        Check(ref fail, SurvivalSettings.SlotKeys("5,7").SequenceEqual(new ushort[] { 0x35, 0x37 }),
              "\"5,7\" → mã phím 0x35,0x37", "");
        Check(ref fail, SurvivalSettings.SlotKeys("6").SequenceEqual(new ushort[] { 0x36 }),
              "\"6\" → một phím", "");
        Check(ref fail, SurvivalSettings.SlotKeys("").Length == 0, "chuỗi rỗng → không có phím nào", "");
        return fail;
    }

    private static int NormalizeCases()
    {
        int fail = 0;
        Console.WriteLine("  · kẹp cấu hình");

        var c = new SurvivalSettings { FoodSlots = "x5!!7y9", WaterSlots = "4,4,8" };
        c.Normalize();
        Check(ref fail, c.FoodSlots == "5,7", "lọc rác + cắt còn 2 phím", c.FoodSlots);
        Check(ref fail, c.WaterSlots == "4,8", "bỏ phím trùng trong cùng loại", c.WaterSlots);

        var overlap = new SurvivalSettings { FoodSlots = "6,5", WaterSlots = "5,7" };
        overlap.Normalize();
        Check(ref fail, overlap.FoodSlots.Contains('5') && !overlap.WaterSlots.Contains('5'),
              "ô trùng giữa bánh/nước → giữ bánh, bỏ khỏi nước",
              $"bánh={overlap.FoodSlots} nước={overlap.WaterSlots}");

        var d = new SurvivalSettings { FoodSlots = "abc", FoodCenterXRef = -1, WaterCenterYRef = 99999 };
        d.Normalize();
        Check(ref fail, d.FoodSlots == "6", "không còn phím hợp lệ → về mặc định", d.FoodSlots);
        Check(ref fail, Math.Abs(d.FoodCenterXRef - 160.0) < 1e-9, "tâm âm → về mặc định", $"{d.FoodCenterXRef}");
        Check(ref fail, Math.Abs(d.WaterCenterYRef - 1047.0) < 1e-9, "tâm vượt màn → về mặc định", $"{d.WaterCenterYRef}");

        bool threw = false;
        try { new SurvivalSettings { FoodSlots = null, WaterSlots = null }.Normalize(); }
        catch { threw = true; }
        Check(ref fail, !threw, "Normalize() với chuỗi null không ném", "");
        return fail;
    }

    private static void DrawGauge(Bitmap bmp, NavScale s, double cxRef, double cyRef, double sweepDeg, Color c,
        float? penPx = null)
    {
        double cx = cxRef * s.Sx, cy = cyRef * s.Sy;
        double coreR = NavTuning.SurvivalCoreRadiusRef * s.Max;
        double ringR = 0.5 * (NavTuning.SurvivalRingRminRef + NavTuning.SurvivalRingRmaxRef) * s.Max;
        float pen = penPx ?? (float)(9.0 * s.Max);

        using var g = Graphics.FromImage(bmp);
        using var br = new SolidBrush(c);
        g.FillEllipse(br, (float)(cx - coreR - 2), (float)(cy - coreR - 2),
            (float)(2 * coreR + 4), (float)(2 * coreR + 4));
        if (sweepDeg <= 0) return;

        using var p = new Pen(c, pen);
        g.DrawArc(p, (float)(cx - ringR), (float)(cy - ringR), (float)(2 * ringR), (float)(2 * ringR),
            0f, (float)sweepDeg);
    }

    private static void Sprinkle(Bitmap bmp, Color c, int n, int seed)
    {
        var rng = new Random(seed);
        for (int i = 0; i < n; i++)
            bmp.SetPixel(rng.Next(bmp.Width), rng.Next(bmp.Height), c);
    }

    private static SurvivalReading Read(SurvivalSettings cfg, NavScale s, Bitmap bmp, double now,
        SurvivalHudProfile hud = null)
    {
        var gauge = new SurvivalGauge(cfg, s, hud);
        return gauge.Update(NavFrame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)), now);
    }

    private static Bitmap Blank(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Dark);
        return bmp;
    }

    private static int GaugeCases(int w, int h)
    {
        int fail = 0;
        Console.WriteLine($"  · đo vòng cung {w}×{h}");
        var s = new NavScale(w, h, 0);
        var cfg = new SurvivalSettings();
        cfg.Normalize();

        foreach (double sweep in new double[] { 360, 180, 90 })
        {
            using var bmp = Blank(w, h);
            DrawGauge(bmp, s, cfg.FoodCenterXRef, cfg.FoodCenterYRef, sweep, Food);
            DrawGauge(bmp, s, cfg.WaterCenterXRef, cfg.WaterCenterYRef, sweep, Water);
            var r = Read(cfg, s, bmp, 1.0);

            double want = sweep / 360.0 * 100.0;
            Check(ref fail, r.FoodValid && Math.Abs(r.FoodPct - want) <= 8.0,
                  $"bánh cung {sweep:F0}° → {want:F0}%", $"đọc {r.FoodPct:F1}%");
            Check(ref fail, r.WaterValid && Math.Abs(r.WaterPct - want) <= 8.0,
                  $"nước cung {sweep:F0}° → {want:F0}%", $"đọc {r.WaterPct:F1}%");
        }

        using (var bmp = Blank(w, h))
        {
            DrawGauge(bmp, s, cfg.FoodCenterXRef, cfg.FoodCenterYRef, 0, Food);
            var r = Read(cfg, s, bmp, 1.0);
            Check(ref fail, r.FoodValid && r.FoodPct <= 4.0, "chỉ có lõi, không vành → 0%", $"{r.FoodPct:F1}%");
        }

        using (var bmp = Blank(w, h))
        {
            var r = Read(cfg, s, bmp, 1.0);
            Check(ref fail, !r.FoodValid && !r.WaterValid && double.IsNaN(r.FoodPct),
                  "màn trống → KHÔNG đọc được (không phải 0%)", $"food={r.FoodPct}");
            Check(ref fail, !r.FoodLow && !r.WaterLow, "màn trống → không kích hoạt ăn uống", "");
        }

        using (var bmp = Blank(w, h))
        {
            DrawGauge(bmp, s, cfg.WaterCenterXRef, cfg.WaterCenterYRef, 360, Food);
            var r = Read(cfg, s, bmp, 1.0);
            Check(ref fail, !r.WaterValid, "vàng ở ô nước → không nhận nhầm", "");
        }

        return fail;
    }

    private static int RealisticGaugeCases()
    {
        int fail = 0;
        Console.WriteLine("  · màu HUD + vành mỏng");
        var s = new NavScale(1920, 1080, 0);
        var cfg = new SurvivalSettings();
        cfg.Normalize();
        float thin = (float)(3.0 * s.Max);

        foreach (double sweep in new double[] { 360, 180, 270 })
        {
            using var bmp = Blank(1920, 1080);
            DrawGauge(bmp, s, cfg.FoodCenterXRef, cfg.FoodCenterYRef, sweep, FoodHud, thin);
            DrawGauge(bmp, s, cfg.WaterCenterXRef, cfg.WaterCenterYRef, sweep, WaterHud, thin);
            var r = Read(cfg, s, bmp, 1.0);
            double want = sweep / 360.0 * 100.0;
            Check(ref fail, r.FoodValid && Math.Abs(r.FoodPct - want) <= 10.0,
                  $"cam HUD cung {sweep:F0}° → {want:F0}%", $"đọc {r.FoodPct:F1}%");
            Check(ref fail, r.WaterValid && Math.Abs(r.WaterPct - want) <= 10.0,
                  $"xanh HUD cung {sweep:F0}° → {want:F0}%", $"đọc {r.WaterPct:F1}%");
        }

        return fail;
    }

    private static int PolarExtraCases()
    {
        int fail = 0;
        Console.WriteLine("  · polar: lệch tâm, nhiễu rời, học hình học");
        var s = new NavScale(1920, 1080, 0);
        var cfg = new SurvivalSettings();
        cfg.Normalize();

        using (var bmp = Blank(1920, 1080))
        {
            DrawGauge(bmp, s, cfg.FoodCenterXRef, cfg.FoodCenterYRef, 180, Food);
            Sprinkle(bmp, Food, 80, 11);
            var r = Read(cfg, s, bmp, 1.0);
            Check(ref fail, r.FoodValid && Math.Abs(r.FoodPct - 50.0) <= 10.0,
                  "nhiễu màu rời không đội % — chỉ cung liên tục", $"{r.FoodPct:F1}% mảnh={r.FoodFragments}");
        }

        using (var bmp = Blank(1920, 1080))
        {
            DrawGauge(bmp, s, cfg.WaterCenterXRef + 2, cfg.WaterCenterYRef - 1, 270, Water, 5f);
            var shifted = new SurvivalSettings
            {
                FoodCenterXRef = cfg.FoodCenterXRef,
                FoodCenterYRef = cfg.FoodCenterYRef,
                WaterCenterXRef = cfg.WaterCenterXRef,
                WaterCenterYRef = cfg.WaterCenterYRef
            };
            shifted.Normalize();
            var r = Read(shifted, s, bmp, 1.0);
            Check(ref fail, r.WaterValid && Math.Abs(r.WaterPct - 75.0) <= 12.0,
                  "tâm lệch 2px vẫn đọc ~75%", $"{r.WaterPct:F1}%");
        }

        using (var bmp = Blank(1920, 1080))
        {
            DrawGauge(bmp, s, cfg.FoodCenterXRef, cfg.FoodCenterYRef, 180, Food);
            var frame = NavFrame.FromBitmap(bmp, new Rectangle(0, 0, 1920, 1080));
            double cx = cfg.FoodCenterXRef * s.Sx, cy = cfg.FoodCenterYRef * s.Sy;
            var roi = new Rectangle((int)(cx - 32), (int)(cy - 32), 64, 64);
            bool geo = SurvivalCalibrator.TryLearnGeometry(frame, roi, food: true, out var ring);
            Check(ref fail, geo && ring is not null && Math.Abs(ring.Cx - cx) <= 6
                           && ring.Rmin >= 14 && ring.Rmax <= 28 && ring.Rmax > ring.Rmin,
                  "học tâm/radius từ ROI (vành, không phải lõi)",
                  geo ? $"tâm ({ring.Cx:F0},{ring.Cy:F0}) r {ring.Rmin:F0}–{ring.Rmax:F0}" : "thất bại");
        }

        return fail;
    }

    private static int RoiCases()
    {
        int fail = 0;
        Console.WriteLine("  · vùng chụp theo tâm / ROI profile");
        var s = new NavScale(1920, 1080, 0);
        var def = new SurvivalSettings();
        def.Normalize();
        var roi = def.CaptureRoi(s);
        var documented = s.RoiRef(NavTuning.SurvivalRoiRef[0], NavTuning.SurvivalRoiRef[1],
                                  NavTuning.SurvivalRoiRef[2], NavTuning.SurvivalRoiRef[3]);
        Check(ref fail, !roi.IsEmpty, "tâm mặc định → ROI không rỗng", $"{roi.Width}×{roi.Height}");
        Check(ref fail, roi.IntersectsWith(documented),
              "ROI mặc định giao hộp SurvivalRoiRef", $"{roi} vs {documented}");

        double rmax = NavTuning.SurvivalRingRmaxRef * s.Max;
        bool foodIn = 160 - rmax >= roi.X && 160 + rmax <= roi.Right && 1047 - rmax >= roi.Y && 1047 + rmax <= roi.Bottom;
        bool waterIn = 210 - rmax >= roi.X && 210 + rmax <= roi.Right && 1047 - rmax >= roi.Y && 1047 + rmax <= roi.Bottom;
        Check(ref fail, foodIn && waterIn, "ROI mặc định ôm trọn hai vành", $"{roi.X},{roi.Y} {roi.Width}×{roi.Height}");

        var hud = new SurvivalHudProfile
        {
            FoodRoi = new FishingRect { X = 380, Y = 860, W = 70, H = 70 },
            WaterRoi = new FishingRect { X = 450, Y = 860, W = 70, H = 70 }
        };
        var far = def.CaptureRoi(s, hud);
        Check(ref fail, far.Contains(400, 880) && far.Contains(480, 880) && far.X > 300,
              "ROI profile đi theo ô đã khoanh", $"{far.X},{far.Y} {far.Width}×{far.Height}");
        return fail;
    }

    private static int TemporalCases()
    {
        int fail = 0;
        Console.WriteLine("  · median / streak / chống nhảy");
        var s = new NavScale(1920, 1080, 0);
        var cfg = new SurvivalSettings();
        cfg.Normalize();

        using var low = Blank(1920, 1080);
        DrawGauge(low, s, cfg.FoodCenterXRef, cfg.FoodCenterYRef, 90, Food);
        var frameLow = NavFrame.FromBitmap(low, new Rectangle(0, 0, 1920, 1080));

        using var high = Blank(1920, 1080);
        DrawGauge(high, s, cfg.FoodCenterXRef, cfg.FoodCenterYRef, 330, Food);
        var frameHigh = NavFrame.FromBitmap(high, new Rectangle(0, 0, 1920, 1080));

        using var blank = Blank(1920, 1080);
        var frameBlank = NavFrame.FromBitmap(blank, new Rectangle(0, 0, 1920, 1080));

        var gauge = new SurvivalGauge(cfg, s);
        double t = 1.0;
        SurvivalReading last = SurvivalReading.None;
        int need = NavTuning.SurvivalMedianMinValid + NavTuning.SurvivalLowConfirmScans - 1;
        for (int i = 1; i < need; i++)
        {
            last = gauge.Update(frameLow, t);
            Check(ref fail, !last.FoodLow, $"khung {i}/{need} chưa báo THIẾU", $"{last.FoodPct:F1}%");
            t += NavTuning.SurvivalScanIntervalS;
        }
        last = gauge.Update(frameLow, t);
        Check(ref fail, last.FoodLow, $"đủ {need} khung (cửa sổ 7 + 5 thấp liên tiếp) mới báo THIẾU", $"{last.FoodPct:F1}%");

        t += NavTuning.SurvivalScanIntervalS;
        gauge.Update(frameBlank, t);
        t += NavTuning.SurvivalScanIntervalS;
        var afterHide = gauge.Update(frameLow, t);
        Check(ref fail, !afterHide.FoodLow, "HUD ẩn một khung → streak về 0", "");

        var g2 = new SurvivalGauge(cfg, s);
        t = 1.0;
        for (int i = 0; i < NavTuning.SurvivalMedianWindow; i++)
        {
            g2.Update(frameLow, t);
            t += NavTuning.SurvivalScanIntervalS;
        }
        double stable = g2.Last.FoodPct;
        g2.Update(frameHigh, t);
        Check(ref fail, Math.Abs(g2.Last.FoodPct - stable) <= 8.0,
              "một frame 90% không kéo mức ổn định", $"ổn định {g2.Last.FoodPct:F1}% (trước {stable:F1}%)");

        var g3 = new SurvivalGauge(cfg, s);
        Check(ref fail, g3.Due(0.0), "chưa quét lần nào → Due() đúng", "");
        g3.Update(frameLow, 10.0);
        Check(ref fail, !g3.Due(10.0 + NavTuning.SurvivalScanIntervalS / 2), "chưa tới hạn → Due() sai", "");
        Check(ref fail, g3.Due(10.0 + NavTuning.SurvivalScanIntervalS), "tới hạn → Due() đúng", "");
        g3.Reset();
        Check(ref fail, g3.Due(0.0) && !g3.Last.FoodValid, "Reset() xoá cả hạn quét lẫn kết quả", "");
        return fail;
    }

    private static int UseWatchCases()
    {
        int fail = 0;
        Console.WriteLine("  · xác nhận sau dùng");
        var watch = new SurvivalUseWatch();
        watch.Start(41, 0);
        Check(ref fail, watch.Observe(93, 1.0, out _) == SurvivalUseVerdict.Animating,
              "trong animation không kết luận dù thấy 93%", "");

        watch.Start(41, 0);
        var v = SurvivalUseVerdict.Watching;
        double t = 0;
        while (t <= NavTuning.SurvivalUseTimeoutS + 0.3)
        {
            double? pct = t < 1.0 ? 93.0 : 30.0;
            v = watch.Observe(pct, t, out _);
            t += 0.25;
        }
        Check(ref fail, v == SurvivalUseVerdict.Failed,
              "spike 41→93→30 không được công nhận thành công", v.ToString());

        watch.Start(41, 0);
        t = 0;
        v = SurvivalUseVerdict.Watching;
        while (t < NavTuning.SurvivalAnimMinS + NavTuning.SurvivalConfirmS + 0.3)
        {
            double? pct = t < NavTuning.SurvivalAnimMinS ? 45.0 : 72.0;
            v = watch.Observe(pct, t, out _);
            t += 0.25;
        }
        Check(ref fail, v == SurvivalUseVerdict.Success, "tăng ổn định ≥10 điểm trong 2s → thành công", v.ToString());
        return fail;
    }

    private static int GateCases()
    {
        int fail = 0;
        Console.WriteLine("  · cổng state");
        Check(ref fail, SurvivalGate.CanPauseJob("SEEK_LIGHTNING"), "SEEK_LIGHTNING được phép pause", "");
        Check(ref fail, !SurvivalGate.CanPauseJob("WAIT_EMPLOYED_BOARD"), "bảng NPC không bị ngắt", "");
        Check(ref fail, SurvivalGate.Decide("SEEK_LIGHTNING", "WORLD", null, null, false, false) == SurvivalActKind.Start,
              "SEEK_LIGHTNING + thiếu → Start (pause rồi uống)", "");
        Check(ref fail, SurvivalGate.Decide("WAIT_EMPLOYED_BOARD", "WORLD", null, null, false, false) == SurvivalActKind.Pending,
              "bảng NPC → pending meal", "");
        Check(ref fail, SurvivalGate.Decide(null, "WORLD", NavCameraReset.Down, null, false, false) == SurvivalActKind.Blocked,
              "reset camera không bị ngắt", "");
        Check(ref fail, SurvivalGate.Decide(null, "WORLD", null, null, true, false) == SurvivalActKind.Blocked,
              "panel mở → không mở bữa", "");
        Check(ref fail, SurvivalGate.Decide(null, "WAIT_BOARD", null, null, false, false) == SurvivalActKind.Wait,
              "simple flow khác WORLD → chờ", "");
        Check(ref fail, SurvivalGate.Decide(null, "WORLD", null, NavInteraction.Settle, false, false) == SurvivalActKind.Blocked,
              "đang SETTLE E → không mở bữa", "");
        Check(ref fail, SurvivalGate.IsNpcBoard("WAIT_OUTSIDE_PROMPT"), "WAIT_OUTSIDE_PROMPT là bảng NPC", "");

        string reason = SurvivalGate.WaitReason(SurvivalActKind.Pending, false, true, 80, 38);
        Check(ref fail, reason.Contains("38") && reason.Contains("bảng"),
              "log chờ ghi mức ổn định và lý do", reason);
        return fail;
    }

    private static Bitmap Load(string path, int wantW, int wantH, out string why)
    {
        why = null;
        var bmp = StillPicker.Load(path);
        if (bmp is null) { why = $"chưa có ({Path.GetFileName(path)})"; return null; }
        if (wantW > 0 && (bmp.Width != wantW || bmp.Height != wantH))
        {
            why = $"ảnh {bmp.Width}×{bmp.Height} lệch {wantW}×{wantH}";
            bmp.Dispose();
            return null;
        }
        return bmp;
    }

    private static int RealShots(ElectricConfig cfg, ElectricProfile p, NavScale s, Rectangle roi)
    {
        int fail = 0;
        fail += LabeledSamples(cfg, p, s);

        foreach (var name in new[] { "hud-no", "hud-doi" })
        {
            string path = ElectricConfig.ShotPath(p.Key, name);
            using var shot = Load(path, p.Width, p.Height, out string why);
            if (shot is null) { Console.WriteLine($"  [{name}] bỏ qua: {why}"); continue; }

            var r = Read(cfg.Survival, s, shot, 1.0, p.SurvivalHud);
            Console.WriteLine($"  [{name}] bánh={Pct(r.FoodValid, r.FoodPct)} nước={Pct(r.WaterValid, r.WaterPct)} " +
                              $"(không suy nhãn từ tên file)");
            Check(ref fail, r.FoodValid, $"[{name}] thấy icon bánh", "");
            Check(ref fail, r.WaterValid, $"[{name}] thấy icon nước", "");
        }

        if (!roi.IsEmpty && !p.SurvivalHud.HasRois)
        {
            double rmax = NavTuning.SurvivalRingRmaxRef * s.Max;
            foreach (var (cxr, cyr, who) in new[]
                     {
                         (cfg.Survival.FoodCenterXRef, cfg.Survival.FoodCenterYRef, "bánh"),
                         (cfg.Survival.WaterCenterXRef, cfg.Survival.WaterCenterYRef, "nước")
                     })
            {
                double cx = cxr * s.Sx, cy = cyr * s.Sy;
                bool inside = cx - rmax >= roi.X && cx + rmax <= roi.X + roi.Width
                                                 && cy - rmax >= roi.Y && cy + rmax <= roi.Y + roi.Height;
                Check(ref fail, inside, $"vùng chụp bao trọn vành {who}", $"tâm ({cx:F0},{cy:F0}) ± {rmax:F0}");
            }
        }

        return fail;
    }

    private static int LabeledSamples(ElectricConfig cfg, ElectricProfile p, NavScale s)
    {
        int fail = 0;
        string dir = ElectricConfig.SurvivalDir(p.Key);
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("  [survival/] chưa có mẫu LOW/HIGH của wizard — bỏ qua");
            return 0;
        }

        using var foodLow = Load(ElectricConfig.SurvivalSamplePath(p.Key, "food-low"), 0, 0, out _);
        using var foodHigh = Load(ElectricConfig.SurvivalSamplePath(p.Key, "food-high"), 0, 0, out _);
        using var waterLow = Load(ElectricConfig.SurvivalSamplePath(p.Key, "water-low"), 0, 0, out _);
        using var waterHigh = Load(ElectricConfig.SurvivalSamplePath(p.Key, "water-high"), 0, 0, out _);

        if (foodLow is null || foodHigh is null || waterLow is null || waterHigh is null)
        {
            Console.WriteLine("  [survival/] thiếu một trong bốn file food/water-low/high — bỏ qua");
            return 0;
        }

        var gauge = new SurvivalGauge(cfg.Survival, s, p.SurvivalHud);
        var fl = FrameOf(foodLow, p);
        var fh = FrameOf(foodHigh, p);
        var wl = FrameOf(waterLow, p);
        var wh = FrameOf(waterHigh, p);

        var a = gauge.ReadRaw(fl, food: true);
        var b = gauge.ReadRaw(fh, food: true);
        var c = gauge.ReadRaw(wl, food: false);
        var d = gauge.ReadRaw(wh, food: false);
        Console.WriteLine($"  [wizard] bánh LOW={Pct(a.Valid, a.Pct)} HIGH={Pct(b.Valid, b.Pct)}  " +
                          $"nước LOW={Pct(c.Valid, c.Pct)} HIGH={Pct(d.Valid, d.Pct)}");
        Check(ref fail, a.Valid && b.Valid && b.Pct >= a.Pct + 10,
              "mẫu wizard bánh: HIGH lớn hơn LOW", $"LOW {a.Pct:F0} HIGH {b.Pct:F0}");
        Check(ref fail, c.Valid && d.Valid && d.Pct >= c.Pct + 10,
              "mẫu wizard nước: HIGH lớn hơn LOW", $"LOW {c.Pct:F0} HIGH {d.Pct:F0}");
        return fail;
    }

    private static NavFrame FrameOf(Bitmap bmp, ElectricProfile p)
        => NavFrame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));

    private static string Pct(bool valid, double v) => valid ? $"{v:F1}%" : "không đọc được";
}
