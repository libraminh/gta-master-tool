using System.Drawing.Imaging;

namespace GtaMiniGameBot;

/// <summary>
/// Kiểm bộ điều hướng thợ điện NGOÀI GAME, hai phần:
///   1. Tự vẽ ảnh rồi dò lại, cộng kiểm các hàm thuần (servo, KET1, tích phân chuột, watchdog).
///   2. Dò trên ảnh tĩnh người dùng chụp bằng nút "Chụp ảnh tĩnh…" của tab Thợ điện
///      (<c>%AppData%\GtaMiniGameBot\electric\&lt;WxH&gt;\shots\nav-*.png</c>).
///
/// Phần 1 chứng minh các con số port từ bản Python (config.json CAROT2 V6.7.34) được chép đúng và
/// hình học viết tay (contour, bao lồi, moment) cho ra cùng thang đo với OpenCV. Phần 2 chứng minh
/// chúng bắt được HUD thật ở 2K — đặc biệt gốc mũi tên cố định (163, 980.4)·sx, con số phụ thuộc
/// máy nhất sau độ nhạy chuột.
///
/// Chạy: GtaMiniGameBot.exe --verify-nav
/// </summary>
internal static class VerifyNav
{
    public static int Run(string[] args)
    {
        Console.WriteLine("== kiểm tra bộ điều hướng thợ điện (port CAROT2 V6.7.34) ==");

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
            var t = NavTuning.TargetRoiRef;
            var mini = s.RoiRef(t[0], t[1], t[2], t[3]);
            var w = NavTuning.WorldRoiRef;
            var world = s.RoiRef(w[0], w[1], w[2], w[3]);
            double ox = (cfg.Nav.PlayerOriginXRef > 0 ? cfg.Nav.PlayerOriginXRef : NavTuning.PlayerOriginXRef) * s.Sx;
            double oy = (cfg.Nav.PlayerOriginYRef > 0 ? cfg.Nav.PlayerOriginYRef : NavTuning.PlayerOriginYRef) * s.Sy;
            Console.WriteLine($"  sx={s.Sx:F4} sy={s.Sy:F4} px×{s.Px:F3} chuột×{cfg.Nav.MouseSpeedMultiplier:F1}");
            Console.WriteLine($"  minimap {mini.Width}×{mini.Height} @ {mini.X},{mini.Y}  (target_roi_ref quy đổi)");
            Console.WriteLine($"  world   {world.Width}×{world.Height} @ {world.X},{world.Y}");
            Console.WriteLine($"  gốc mũi tên ({ox:F1},{oy:F1}){(cfg.Nav.PlayerOriginXRef > 0 ? "  (ghi đè)" : "  (mặc định Python)")}");
            fail += RealShots(cfg, profile, s, ox, oy);
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

    // ================================================================ tu kiem tra

    private const int W = 1920, H = 1080;
    private static readonly NavScale S1 = new(W, H, 0);
    private static readonly Color Yellow = Color.FromArgb(255, 220, 40);   // H≈25 (OpenCV), S≈215, V=255
    private static readonly Color Cyan = Color.FromArgb(40, 200, 220);     // H≈93
    private static readonly Color Dark = Color.FromArgb(30, 30, 30);

    private static int SelfTest()
    {
        Console.WriteLine();
        Console.WriteLine("-- tự kiểm tra --");
        int fail = 0;
        fail += GeometryCases();
        fail += DetectorCases();
        fail += TrackerCases();
        fail += ServoCases();
        fail += Ket1Cases();
        fail += MouseCases();
        fail += CameraResetCases();
        fail += WatchdogCases();
        fail += PromptCases();
        fail += InteractionCases();
        fail += PanelInterruptCases();
        fail += WorldCases();
        fail += BoardCases();
        Console.WriteLine(fail == 0 ? "  tự kiểm tra: ĐẠT" : $"  tự kiểm tra: HỎNG {fail} ca");
        return fail;
    }

    private static Bitmap NewFrame()
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Dark);
        return bmp;
    }

    private static NavFrame Frame(Bitmap bmp) => NavFrame.FromBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));

    private static void Disc(Bitmap bmp, double cx, double cy, double r, Color c)
    {
        using var g = Graphics.FromImage(bmp);
        using var br = new SolidBrush(c);
        g.FillEllipse(br, (float)(cx - r), (float)(cy - r), (float)(2 * r), (float)(2 * r));
    }

    private static void Box(Bitmap bmp, int x, int y, int w, int h, Color c)
    {
        using var g = Graphics.FromImage(bmp);
        using var br = new SolidBrush(c);
        g.FillRectangle(br, x, y, w, h);
    }

    private static int GeometryCases()
    {
        int fail = 0;
        var m = new Mask(40, 40);
        ImageOps.FillCircle(m, 20, 20, 6, 1);
        var cs = NavGeometry.FindContours(m);
        Check(ref fail, cs.Count == 1, "contour đĩa r=6: một đường biên", $"{cs.Count}");
        if (cs.Count == 1)
        {
            var c = cs[0];
            Check(ref fail, c.Area > 80 && c.Area < 115 && c.Area < c.PixelCount,
                  "diện tích shoelace < số pixel (như cv2.contourArea)", $"area={c.Area:F1} pixel={c.PixelCount}");
            Check(ref fail, c.Circularity >= 0.80 && c.Circularity <= 1.05, "độ tròn đĩa ≥ 0.80", $"{c.Circularity:F3}");
            Check(ref fail, c.Solidity >= 0.90, "solidity đĩa ≥ 0.90", $"{c.Solidity:F3}");
            // Dia so 13 px: dien tich da giac 96 / bbox 169 = 0.57 — cung so ma cv2 cho ra, va DUOI nguong
            // dot_fill_min 0.58. Cham that trong game co vien khu rang cua nen fill ~0.68 (xem nav-far).
            Check(ref fail, c.Fill > 0.50 && c.Fill < 0.60, "fill đĩa số 13 px ≈ 0.57 (cùng thang cv2)", $"{c.Fill:F3}");
            Check(ref fail, c.HasCentroid && Math.Abs(c.Cx - 20) < 0.6 && Math.Abs(c.Cy - 20) < 0.6, "trọng tâm đa giác", $"({c.Cx:F2},{c.Cy:F2})");
            Check(ref fail, c.RadialCv(c.Cx, c.Cy) <= 0.30, "radial_cv đĩa ≤ 0.30", $"{c.RadialCv(c.Cx, c.Cy):F3}");
        }

        // Tia set: chu Z mong -> khong tron.
        var z = new Mask(40, 40);
        ImageOps.DrawThickLine(z, new Point(12, 8), new Point(26, 8), 2, 1);
        ImageOps.DrawThickLine(z, new Point(26, 8), new Point(14, 22), 2, 1);
        ImageOps.DrawThickLine(z, new Point(14, 22), new Point(28, 22), 2, 1);
        ImageOps.DrawThickLine(z, new Point(28, 22), new Point(16, 34), 2, 1);
        var zc = NavGeometry.FindContours(z);
        Check(ref fail, zc.Count >= 1 && (zc[0].Circularity < NavTuning.DotCircularityMin || zc[0].Fill < NavTuning.DotFillMin || zc[0].Solidity < NavTuning.DotSolidityMin),
              "hình tia sét bị bộ lọc tròn loại", zc.Count >= 1 ? zc[0].ToString() : "không có contour");

        var p = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        Check(ref fail, Math.Abs(NavGeometry.Percentile(p, 50) - 3) < 1e-9 && Math.Abs(NavGeometry.Percentile(p, 10) - 1.4) < 1e-9
                        && Math.Abs(NavGeometry.Percentile(p, 90) - 4.6) < 1e-9, "percentile kiểu numpy", "");
        Check(ref fail, Math.Abs(NavController.Wrap(190) + 170) < 1e-9 && Math.Abs(NavController.Wrap(-190) - 170) < 1e-9, "wrap_deg", "");
        return fail;
    }

    private static int DetectorCases()
    {
        int fail = 0;
        double ox = NavTuning.PlayerOriginXRef, oy = NavTuning.PlayerOriginYRef;

        using (var bmp = NewFrame())
        {
            Disc(bmp, 100, 850, 6, Yellow);
            var f = Frame(bmp);
            var cands = YellowDotDetector.Detect(f, S1, ox, oy);
            Check(ref fail, cands.Count == 1, "chấm vàng r=6 trong minimap → 1 ứng viên", string.Join(" | ", cands));
            if (cands.Count == 1)
            {
                Check(ref fail, cands[0].Score >= NavTuning.BootstrapGeometryMin, "điểm hình học ≥ 0.72 (bootstrap)", $"{cands[0].Score:F3}");
                Check(ref fail, Math.Abs(cands[0].X - 100) < 1.5 && Math.Abs(cands[0].Y - 850) < 1.5, "vị trí đúng", $"({cands[0].X:F1},{cands[0].Y:F1})");
            }
        }

        using (var bmp = NewFrame())
        {
            // Tia set tai moc lightning_anchor_ref (174, 967): zigzag mong.
            using (var g = Graphics.FromImage(bmp))
            using (var pen = new Pen(Yellow, 3))
            {
                g.DrawLines(pen, new[] { new PointF(172, 958), new PointF(178, 958), new PointF(173, 966), new PointF(179, 966), new PointF(171, 976) });
            }
            var f = Frame(bmp);
            var cands = YellowDotDetector.Detect(f, S1, ox, oy);
            Check(ref fail, cands.Count == 0, "icon tia sét ở mốc anchor bị loại", string.Join(" | ", cands));
        }

        using (var bmp = NewFrame())
        {
            // Manh: dia vang lech 8 px bi dia trang (mui ten) che mot phan -> hinh khuyen.
            Disc(bmp, ox + 8, oy, 6, Yellow);
            Disc(bmp, ox, oy, 6, Color.White);
            var f = Frame(bmp);
            var full = YellowDotDetector.Detect(f, S1, ox, oy);
            var frags = YellowDotDetector.DetectNearFragments(f, S1, ox, oy);
            Check(ref fail, full.Count == 0, "khuyên không qua bộ lọc chấm đầy", string.Join(" | ", full));
            Check(ref fail, frags.Count >= 1, "khuyên qua bộ lọc mảnh", string.Join(" | ", frags));
            var tr = new DotTracker(S1);
            var t = tr.Update(full, ox, oy, 10.0, frags);
            Check(ref fail, t.Quality == "NEAR_FRAGMENT" && Math.Abs(t.Confidence - NavTuning.OverlapBootConf) < 1e-9,
                  "tracker bootstrap bằng mảnh → NEAR_FRAGMENT conf 0.86", $"{t.Quality} {t.Confidence:F2}");
        }
        return fail;
    }

    private static int TrackerCases()
    {
        int fail = 0;
        var tr = new DotTracker(S1);
        double ox = NavTuning.PlayerOriginXRef, oy = NavTuning.PlayerOriginYRef;
        var far = new List<NavCandidate> { new() { X = 100, Y = 850, Area = 108, Width = 12, Height = 12, Circularity = 0.9, Fill = 0.7, Solidity = 0.95, Score = 0.9 } };
        var none = new List<NavCandidate>();

        var t1 = tr.Update(far, ox, oy, 0.000, none);
        var t2 = tr.Update(far, ox, oy, 0.025, none);
        Check(ref fail, t1.Quality == "ACQUIRE" && t2.Quality == "FULL_LOCK", "khung 1 ACQUIRE, khung 2 FULL_LOCK", $"{t1.Quality} {t2.Quality}");
        Check(ref fail, t2.Confidence > 0.6 && t2.Confidence <= 1.0, "conf FULL_LOCK", $"{t2.Confidence:F3}");

        string q = "";
        int predicts = 0;
        double now = 0.050;
        TargetOutput last = null;
        for (int i = 0; i < 25; i++)
        {
            last = tr.Update(none, ox, oy, now, none);
            now += 0.025;
            if (last.Quality == "PREDICT_ONLY") predicts++;
            q = last.Quality;
        }
        Check(ref fail, predicts == NavTuning.TrackRebootstrapAfterMisses && q == "NONE",
              "mất xa: 9 khung PREDICT_ONLY rồi NONE", $"predict={predicts} cuối={q}");

        var tr2 = new DotTracker(S1);
        var near = new List<NavCandidate> { new() { X = ox + 6, Y = oy - 6, Area = 108, Width = 12, Height = 12, Circularity = 0.9, Fill = 0.7, Solidity = 0.95, Score = 0.9 } };
        tr2.Update(near, ox, oy, 0.0, none);
        tr2.Update(near, ox, oy, 0.025, none);
        var h1 = tr2.Update(none, ox, oy, 0.100, none);
        var h2 = tr2.Update(none, ox, oy, 0.600, none);
        Check(ref fail, h1.Quality == "HOLD_LAST_ID" && h2.Quality == "PREDICT_ONLY",
              "mất gần (≤15 px): HOLD_LAST_ID trong 0.42 s rồi PREDICT_ONLY", $"{h1.Quality} {h2.Quality}");
        return fail;
    }

    private static int ServoCases()
    {
        int fail = 0;
        double c3 = NavController.ServoCurve(3.0, 2.4), c10 = NavController.ServoCurve(10.0, 2.4);
        double c20 = NavController.ServoCurve(20.0, 2.4), c60 = NavController.ServoCurve(60.0, 2.4);
        Check(ref fail, Math.Abs(c3 - 34.8) < 1e-9 && Math.Abs(c10 - 230) < 1e-9 && Math.Abs(c20 - 594) < 1e-9 && Math.Abs(c60 - 1802) < 1e-9,
              "đường cong servo 3°/10°/20°/60°", $"{c3:F1} {c10:F0} {c20:F0} {c60:F0}");
        Check(ref fail, Math.Abs(c3 * NavTuning.RamPrecisionRateScaleUnder8 - 25.056) < 1e-6, "3° sau precision ×0.72 ≈ 25 cps", $"{c3 * 0.72:F2}");
        Check(ref fail, Math.Min(c60, NavTuning.RamTargetLockNearMouseMaxRateCps) == 520.0, "60° gần: cap 520", "");

        using var input = new NavInput(4.0);
        var ctl = new NavController(S1, input);
        int s1 = ctl.AntiShakeSide(12, 2.4, 0.0);
        int s2 = ctl.AntiShakeSide(-12, 2.4, 0.1);
        int s3 = ctl.AntiShakeSide(-12, 2.4, 0.35);
        int s4 = ctl.AntiShakeSide(30, 2.4, 0.4);
        int s5 = ctl.AntiShakeSide(2, 2.4, 0.5);
        int s6 = ctl.AntiShakeSide(-5, 2.4, 0.6);
        Check(ref fail, s1 == 1 && s2 == 0 && s3 == -1 && s4 == 1 && s5 == 0 && s6 == 0,
              "anti-shake: +12→+1, −12 chờ 220 ms→−1, +30 đảo ngay, ≤3.8° dừng, 5° ngược không đánh", $"{s1} {s2} {s3} {s4} {s5} {s6}");
        return fail;
    }

    private static int Ket1Cases()
    {
        int fail = 0;
        using var input = new NavInput(4.0);
        var ctl = new NavController(S1, input);

        bool started = ctl.StartKet1Recovery(10.0, 30.0, "MINIMAP");
        Check(ref fail, started && ctl.Active is not null && ctl.Active.Side == 1 && ctl.Active.UturnSide == -1,
              "KET1 bắt đầu: rel +30° → bên thoát PHẢI, quay đầu sang TRÁI", ctl.Active is null ? "null" : $"side={ctl.Active.Side}");

        // Mo phong: bearing doi 200°/s theo huong yaw.
        double t = 10.0, rel = 30.0, refRel = 30.0, t1 = -1, t2 = -1, t3 = -1;
        var seen = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            var r = ctl.RecoveryStep(t, 20.0, rel);
            if (r is null) { t3 = t; break; }
            if (seen.Count == 0 || seen[^1] != r.Value.state) seen.Add(r.Value.state);
            t += 0.025;
            if (r.Value.state == "KET1_TURN_AROUND") rel = NavController.Wrap(rel - 200 * 0.025);
            else if (r.Value.state == "KET1_SIDE_TURN") { if (t1 < 0) { t1 = t - 0.025; refRel = rel; } rel = NavController.Wrap(rel + 200 * 0.025); }
            else if (r.Value.state == "KET1_CLEAR_FORWARD" && t2 < 0) t2 = t - 0.025;
        }
        Check(ref fail, string.Join(">", seen) == "KET1_TURN_AROUND>KET1_SIDE_TURN>KET1_CLEAR_FORWARD", "thứ tự pha", string.Join(">", seen));
        Check(ref fail, t1 > 0 && t1 - 10.0 >= 0.80 && t1 - 10.0 <= 0.90, "quay đầu 168° ở 200°/s xong ~0.84 s (< cap 950 ms)", $"{t1 - 10.0:F3}s");
        Check(ref fail, t2 > 0 && t2 - t1 >= 0.18 && t2 - t1 <= 0.26, "bẻ 42° xong ~0.21 s", $"{t2 - t1:F3}s");
        Check(ref fail, t3 > 0 && Math.Abs(t3 - t2 - NavTuning.Ket1ClearForwardS) < 0.03, "W thẳng 650 ms rồi trả quyền", $"{t3 - t2:F3}s");

        // Khong co bearing (nguon WORLD): chi con cap thoi gian.
        ctl.ResetTransient();
        ctl.StartKet1Recovery(30.0, 25.0, "WORLD", force: true);
        t = 30.0; double u = -1, sd = -1;
        for (int i = 0; i < 200; i++)
        {
            var r = ctl.RecoveryStep(t, 20.0, null);
            if (r is null) break;
            if (r.Value.state == "KET1_SIDE_TURN" && u < 0) u = t - 30.0;
            if (r.Value.state == "KET1_CLEAR_FORWARD" && sd < 0) sd = t - 30.0;
            t += 0.025;
        }
        // Dung sai hai tick 25 ms: mo phong cong dan 0.025 nen moc 0.950 co the roi sang tick sau.
        Check(ref fail, Math.Abs(u - 0.950) <= 0.051 && Math.Abs(sd - u - 0.480) <= 0.051, "không bearing: cap 950 ms + 480 ms", $"uturn={u:F3} side={sd - u:F3}");
        return fail;
    }

    private static int MouseCases()
    {
        int fail = 0;
        double rate = 0, frac = 0, dt = 1.0 / 240;
        int total = 0;
        double maxStep = 0, tReach = -1;
        for (int i = 0; i < 240; i++)
        {
            double prev = rate;
            (rate, frac, int outp) = NavInput.AxisStep(6600, rate, frac, dt, 0.050, 36000);
            total += outp;
            maxStep = Math.Max(maxStep, rate - prev);
            if (tReach < 0 && rate >= 6000) tReach = (i + 1) * dt;
        }
        Check(ref fail, maxStep <= 150.0 + 1e-6, "gia tốc X ≤ 150 cps mỗi tick (36000 cps²)", $"{maxStep:F1}");
        Check(ref fail, tReach > 0 && tReach < 0.30, "đạt ~6600 cps trong < 0.3 s", $"{tReach:F3}s");
        Check(ref fail, total > 5000 && total < 6600, "tổng counts 1 s < 6600 do ramp", $"{total}");
        var (r0, _, _) = NavInput.AxisStep(0, 4.0, 0, dt, 0.050, 36000);
        Check(ref fail, r0 == 0.0, "snap về 0 khi |rate| < 5 và target 0", $"{r0}");
        return fail;
    }

    private static int CameraResetCases()
    {
        int fail = 0;
        string phase = NavCameraReset.Reacquire;
        var seen = new List<string> { phase };
        double[] hold =
        {
            NavTuning.CameraResetReacquireSettleS,
            NavTuning.CameraResetSettleS,
            NavTuning.CameraResetDownS,
            NavTuning.CameraResetGroundHoldS,
            NavTuning.CameraResetUpS,
            NavTuning.CameraResetFinalSettleS
        };
        for (int i = 0; i < hold.Length; i++)
        {
            Check(ref fail, NavCameraReset.Advance(phase, hold[i] - 0.001) == phase,
                  $"chưa hết {phase} thì đứng nguyên", "");
            phase = NavCameraReset.Advance(phase, hold[i]);
            seen.Add(phase);
        }
        Check(ref fail, string.Join(">", seen) == string.Join(">", NavCameraReset.Sequence),
              "REACQUIRE → DOWN → UP → W_RECLAIM", string.Join(">", seen));

        using (var input = new NavInput(4.0))
        {
            input.Apply(NavKey.W);
            input.PulseWReacquire(NavTuning.CameraResetReacquireHoldMs);
            Check(ref fail, !input.IsHeld(NavKey.W), "xung W reacquire không để W bị giữ",
                  input.Held.ToString());
        }

        int down = NavInput.SimulateYCounts(NavTuning.CameraResetDownRateCps, NavTuning.CameraResetDownS);
        int upOld = NavInput.SimulateYCounts(-1950.0, NavTuning.CameraResetUpS);
        int up = NavInput.SimulateYCounts(-NavTuning.CameraResetUpRateCps, NavTuning.CameraResetUpS);
        Check(ref fail, Math.Abs(NavTuning.CameraResetUpRateCps - 1950.0 * 0.85) < 1e-9,
              "kéo lên reset giảm 15% (1950 → 1657.5)", $"{NavTuning.CameraResetUpRateCps:F1}");
        Check(ref fail, down >= 1500, "profile Y xuống tạo đủ delta dương", $"{down}");
        Check(ref fail, up <= -800, "profile Y lên tạo đủ delta âm", $"{up}");
        Check(ref fail, Math.Abs(up) < Math.Abs(upOld), "delta lên nhỏ hơn profile cũ 1950 cps", $"{up} vs {upOld}");
        Check(ref fail, NavTuning.CameraResetReacquireSettleS > 0.10
                        && NavTuning.CameraResetReacquireHoldMs >= 30,
              "reacquire có nhấn W rồi settle trước pitch",
              $"hold={NavTuning.CameraResetReacquireHoldMs:F0}ms settle={NavTuning.CameraResetReacquireSettleS:F2}s");
        Check(ref fail, Math.Abs(NavCameraReset.WaitAfterPanelGoneS(3.0) - 2.0) < 1e-9,
              "bảng mất 3 s → chờ thêm 2 s", $"{NavCameraReset.WaitAfterPanelGoneS(3.0):F1}s");
        Check(ref fail, Math.Abs(NavCameraReset.WaitAfterPanelGoneS(1.5) - 3.5) < 1e-9,
              "panel dây mất 1.5 s → chờ thêm 3.5 s", $"{NavCameraReset.WaitAfterPanelGoneS(1.5):F1}s");
        Check(ref fail, NavCameraReset.WaitAfterPanelGoneS(5.0) == 0
                        && NavCameraReset.WaitAfterPanelGoneS(6.0) == 0,
              "panel đã mất ≥ 5 s → reset ngay",
              $"{NavCameraReset.WaitAfterPanelGoneS(5.0):F1}/{NavCameraReset.WaitAfterPanelGoneS(6.0):F1}");
        Check(ref fail, NavTuning.CameraResetAfterPanelGoneS == 5.0,
              "mốc kéo camera = 5 s từ lúc panel mất", $"{NavTuning.CameraResetAfterPanelGoneS:F1}s");
        return fail;
    }

    private static int WatchdogCases()
    {
        int fail = 0;
        var wd = new NavWatchdog(S1);
        double now = 0; bool stuck = false; double tStuck = -1;
        for (int i = 0; i < 60; i++)
        {
            wd.Add(now, 0, -50);
            if (wd.ImpactStuck(now, true, true, 50, 0)) { stuck = true; tStuck = now; break; }
            now += 0.025;
        }
        Check(ref fail, stuck && tStuck >= 0.85 && tStuck <= 1.25, "bán kính phẳng 50 px → kẹt sau ~0.9 s + 180 ms", $"{tStuck:F3}s");

        var wd2 = new NavWatchdog(S1);
        now = 0; bool any = false;
        for (int i = 0; i < 60; i++)
        {
            wd2.Add(now, 0, -(50 - i * 0.5));
            if (wd2.ImpactStuck(now, true, true, 50 - i * 0.5, 0)) any = true;
            now += 0.025;
        }
        Check(ref fail, !any, "đang tiến 0.5 px/khung → không kẹt", "");

        var wd3 = new NavWatchdog(S1);
        now = 0; any = false;
        for (int i = 0; i < 60; i++)
        {
            wd3.Add(now, 0, -50);
            if (wd3.ImpactStuck(now, true, true, 50, 60)) any = true;
            now += 0.025;
        }
        Check(ref fail, !any, "góc 60° > 55° → không tính kẹt", "");
        return fail;
    }

    private static int PromptCases()
    {
        int fail = 0;
        using (var bmp = NewFrame())
        {
            Box(bmp, 900, 560, 30, 30, Color.White);
            for (int i = 0; i < 5; i++) Box(bmp, 900 + 30 + 12 + i * 12, 568, 8, 14, Color.White);
            Check(ref fail, PromptHeuristic.Visible(Frame(bmp), W, H), "ô [E] + 5 chữ bên phải → prompt", "");
        }
        using (var bmp = NewFrame())
        {
            Box(bmp, 900, 560, 30, 30, Color.White);
            Check(ref fail, !PromptHeuristic.Visible(Frame(bmp), W, H), "ô [E] không chữ → không prompt", "");
        }
        using (var bmp = NewFrame())
        {
            Box(bmp, 300, 560, 30, 30, Color.White);
            for (int i = 0; i < 5; i++) Box(bmp, 300 + 30 + 12 + i * 12, 568, 8, 14, Color.White);
            Check(ref fail, !PromptHeuristic.Visible(Frame(bmp), W, H), "prompt ngoài vùng 0.45–0.73 → bỏ qua", "");
        }
        using (var bmp = NewFrame())
        {
            DrawWorkPrompt(bmp, 1000, 560, glyphs: 6);
            var f = Frame(bmp);
            Check(ref fail, PromptHeuristic.Visible(f, W, H) && PromptHeuristic.WorkVisible(f, W, H),
                  "ô [E] + 6 chữ trong ROI chặt → prompt công việc", "");
        }
        using (var bmp = NewFrame())
        {
            DrawWorkPrompt(bmp, 900, 460, glyphs: 6);
            var f = Frame(bmp);
            Check(ref fail, PromptHeuristic.Visible(f, W, H) && !PromptHeuristic.WorkVisible(f, W, H),
                  "prompt lệch lên (y≈0.43) — rộng nhận, ROI chặt bỏ", "");
        }
        using (var bmp = NewFrame())
        {
            Box(bmp, 1000, 560, 30, 30, Color.White);
            for (int i = 0; i < 3; i++) Box(bmp, 1000 + 30 + 12 + i * 12, 568, 8, 14, Color.White);
            Check(ref fail, !PromptHeuristic.WorkVisible(Frame(bmp), W, H),
                  "ROI chặt nhưng thiếu chữ → không phải [E] TƯƠNG TÁC", "");
        }
        return fail;
    }

    private static void DrawWorkPrompt(Bitmap bmp, int x, int y, int glyphs)
    {
        Box(bmp, x, y, 30, 30, Color.White);
        for (int i = 0; i < glyphs; i++) Box(bmp, x + 30 + 12 + i * 12, y + 8, 8, 14, Color.White);
    }

    private static int InteractionCases()
    {
        int fail = 0;
        int lastSeq = -1, streak = 0, absent = 0;
        bool consumed = false;
        bool s1 = NavInteraction.NotePrompt(true, 1, ref lastSeq, ref streak, ref absent, ref consumed, 0, 0);
        bool s1b = NavInteraction.NotePrompt(true, 1, ref lastSeq, ref streak, ref absent, ref consumed, 0.025, 0);
        Check(ref fail, !s1 && !s1b && streak == 1, "cùng snapshot không đủ 2 frame", $"s1={s1} s1b={s1b} streak={streak}");
        bool s2 = NavInteraction.NotePrompt(true, 2, ref lastSeq, ref streak, ref absent, ref consumed, 0.050, 0);
        Check(ref fail, s2 && streak == 2, "hai snapshot khác nhau → prompt ổn định", $"s2={s2} streak={streak}");

        double px = 1.333;
        bool far = NavInteraction.ApproachReady(40, 5, px, "FULL_LOCK", 0.9, false, 0, 0, "RAM_V63_FAST_TARGET_SNAP_W", 0, 10);
        bool near = NavInteraction.ApproachReady(12, 5, px, "FULL_LOCK", 0.9, false, 0, 0, "RAM_V63_AIM_CENTERED_W", 0, 10);
        bool pass = NavInteraction.ApproachReady(40, 20, px, "NEAR_FRAGMENT", 0.86, false, 0, 0, "RAM_V63_PASS_THROUGH_W", 0, 10);
        bool pred = NavInteraction.ApproachReady(12, 5, px, "PREDICT_ONLY", 0.9, false, 0, 0, "RAM_V63_AIM_CENTERED_W", 0, 10);
        Check(ref fail, !far, "prompt xa không arm E", "");
        Check(ref fail, near, "sát điểm + thẳng hướng → arm E", "");
        Check(ref fail, pass, "pass-through hợp lệ → arm E dù dist lớn", "");
        Check(ref fail, !pred, "PREDICT_ONLY gần không arm E", "");

        Check(ref fail, !NavInteraction.LostTargetArm(false, "SEARCH360_MOVING", 2),
              "SEARCH360 nhưng chưa có prompt công việc → không arm E", "");
        Check(ref fail, !NavInteraction.LostTargetArm(true, "RAM_V63_AIM_CENTERED_W", 0),
              "prompt công việc khi còn đích → không arm theo SEARCH360", "");
        Check(ref fail, NavInteraction.LostTargetArm(true, "SEARCH360_MOVING", 2),
              "prompt công việc ổn định + SEARCH360 → được bấm E", "");
        Check(ref fail, NavInteraction.LostTargetArm(true, "RAM_V6_LOST_KEEP_STRAIGHT", 1),
              "đã vào SEARCH360 (round>0) + prompt công việc → được bấm E", "");

        Check(ref fail, !NavInteraction.RetryReady(10.5, 11.0) && NavInteraction.RetryReady(11.0, 11.0),
              "E thất bại: cooldown 1 s trước lần thử sau", "");
        Check(ref fail, NavTuning.InteractionSettleS < 0.5 && NavTuning.InteractionSettleS < NavTuning.SimpleWaitBoardS,
              "settle sau E ngắn hơn cửa sổ chờ panel (không đứng 4 s)",
              $"settle={NavTuning.InteractionSettleS:F2}s wait={NavTuning.SimpleWaitBoardS:F1}s");

        consumed = true;
        bool stillOn = NavInteraction.NotePrompt(true, 3, ref lastSeq, ref streak, ref absent, ref consumed, 2.0, 1.0);
        Check(ref fail, stillOn && consumed, "prompt còn hiện sau E: không re-arm chỉ vì cùng streak", $"consumed={consumed}");
        bool gone1 = NavInteraction.NotePrompt(false, 4, ref lastSeq, ref streak, ref absent, ref consumed, 2.1, 1.0);
        bool gone2 = NavInteraction.NotePrompt(false, 5, ref lastSeq, ref streak, ref absent, ref consumed, 2.2, 1.0);
        bool gone3 = NavInteraction.NotePrompt(false, 6, ref lastSeq, ref streak, ref absent, ref consumed, 2.3, 1.0);
        Check(ref fail, !gone1 && !gone2 && !gone3 && !consumed && absent >= NavTuning.SimplePromptRearmAbsentFrames,
              "prompt tắt đủ frame + hết cooldown → mở lại E",
              $"consumed={consumed} absent={absent}");

        bool lost = NavInteraction.LostTargetArm(true, "SEARCH360_MOVING", 2);
        Check(ref fail, lost && NavInteraction.PostJobBlocksWorldE(true, true, lost, false),
              "khiên ON + LostTargetArm → vẫn chặn E", "");
        Check(ref fail, NavInteraction.PostJobBlocksWorldE(true, true, lost, true),
              "khiên ON dù ApproachReady → vẫn chặn E", "");
        Check(ref fail, NavInteraction.PostJobBlocksWorldE(false, true, lost, false),
              "khiên OFF + needYellow + SEARCH360 + chưa sát điểm → vẫn chặn", "");
        Check(ref fail, !NavInteraction.PostJobBlocksWorldE(false, true, lost, true),
              "sát điểm vàng → cho E dù needYellow", "");
        Check(ref fail, !NavInteraction.PostJobBlocksWorldE(false, false, lost, false),
              "đã khóa vàng rồi → LostTargetArm được bấm", "");

        Check(ref fail, NavInteraction.PostJobPromptHoldsShield(false, true),
              "ROI chặt đơn độc vẫn giữ khiên", "");
        Check(ref fail, NavInteraction.PostJobPromptHoldsShield(true, false),
              "ROI rộng đơn độc vẫn giữ khiên", "");
        Check(ref fail, !NavInteraction.PostJobPromptHoldsShield(false, false),
              "cả hai ROI tắt → không giữ khiên", "");

        int clear = NavTuning.JobPostRehirePromptClearFrames;
        Check(ref fail, !NavInteraction.PostJobClearShield(true, clear - 1, 2.0, clear,
                  NavTuning.JobPostRehireMinGuardS, NavTuning.JobPostRehireNoPromptTimeoutS),
              "đã thấy nhưng chưa đủ frame tắt → giữ khiên", "");
        Check(ref fail, NavInteraction.PostJobClearShield(true, clear, NavTuning.JobPostRehireMinGuardS, clear,
                  NavTuning.JobPostRehireMinGuardS, NavTuning.JobPostRehireNoPromptTimeoutS),
              "đã thấy + minGuard + đủ frame → gỡ khiên", "");
        Check(ref fail, !NavInteraction.PostJobClearShield(false, clear, NavTuning.JobPostRehireNoPromptTimeoutS - 0.1,
                  clear, NavTuning.JobPostRehireMinGuardS, NavTuning.JobPostRehireNoPromptTimeoutS),
              "chưa thấy, chưa đủ timeout → giữ khiên", "");
        Check(ref fail, NavInteraction.PostJobClearShield(false, clear, NavTuning.JobPostRehireNoPromptTimeoutS,
                  clear, NavTuning.JobPostRehireMinGuardS, NavTuning.JobPostRehireNoPromptTimeoutS),
              "chưa thấy + timeout + đủ frame → gỡ khiên", "");

        Check(ref fail, NavInteraction.AfterEEscAccidentalNpc(false, true),
              "sau E: bảng nghề + còn điểm vàng → ESC", "");
        Check(ref fail, !NavInteraction.AfterEEscAccidentalNpc(true, true),
              "sau E: đang reset nghề → không ESC nhầm", "");
        Check(ref fail, NavInteraction.AfterEEnterOpenBoard(false, false),
              "sau E: bảng nghề + mất vàng → vào WaitBoard", "");
        Check(ref fail, !NavInteraction.AfterEEnterOpenBoard(true, false),
              "sau E: đang reset nghề → JobRecovery giữ bảng", "");
        Check(ref fail, !NavInteraction.AfterEEnterOpenBoard(false, true),
              "sau E: còn điểm vàng → không nghỉ việc", "");
        return fail;
    }

    private static int PanelInterruptCases()
    {
        int fail = 0;
        var g = new NavPanelInterrupt();
        g.Note(true, npcBoard: false);
        Check(ref fail, !g.Confirmed(false) && g.Streak == 1, "một hit nền chưa giao panel", $"streak={g.Streak}");
        Check(ref fail, g.Note(true, npcBoard: false) && g.Confirmed(false),
              "hai hit bền vững → ngắt được khi đi thường", $"streak={g.Streak}");

        g.Reset();
        g.Note(true, false);
        Check(ref fail, !g.Note(false, false) && g.Streak == 0, "hit chập chờn → reset streak", $"streak={g.Streak}");

        g.Reset();
        g.Note(true, false);
        g.Note(true, false);
        Check(ref fail, !g.Confirmed(true), "reset nghề: hai hit chưa đủ", $"streak={g.Streak}");
        Check(ref fail, g.Note(true, false) && g.Confirmed(true),
              "reset nghề: ba hit bền vững → ngắt được", $"streak={g.Streak}");

        g.Reset();
        g.Note(true, false);
        Check(ref fail, !g.Note(true, npcBoard: true) && g.Streak == 0,
              "bảng NPC (3 nút cyan) huỷ ứng viên panel", $"streak={g.Streak}");
        Check(ref fail, !g.Note(true, npcBoard: true) && !g.Confirmed(true),
              "bảng NPC không bao giờ giao cho bộ giải minigame", $"streak={g.Streak}");
        return fail;
    }

    private static int WorldCases()
    {
        int fail = 0;
        using (var bmp = NewFrame())
        {
            Box(bmp, 900, 500, 60, 200, Yellow);
            var det = new WorldMarkerDetector(S1);
            var f = Frame(bmp);
            var c = det.Candidate(f);
            Check(ref fail, c is not null && c.Value.score >= NavTuning.WorldAcceptScore, "cột vàng 60×200 → ứng viên ≥ 0.46", det.LastCandidateNote ?? "null");
            if (c is not null)
                Check(ref fail, Math.Abs(c.Value.tx - 930) < 3 && c.Value.ty >= 620 && c.Value.ty <= 700, "điểm tham chiếu ở 40 % dưới (chân cột)", $"({c.Value.tx:F0},{c.Value.ty:F0})");
            var m1 = det.Update(f, 0.0);
            var m2 = det.Update(f, 0.025);
            Check(ref fail, m1.Quality == "WORLD_ACQUIRE" && m2.Quality == "WORLD_LOCK" && m2.Locked && m2.Present, "khung 1 ACQUIRE, khung 2 LOCK", $"{m1.Quality} {m2.Quality}");
            var m3 = det.Update(Frame(NewFrame()), 0.3);
            var m4 = det.Update(Frame(NewFrame()), 1.2);
            Check(ref fail, m3.Quality == "WORLD_HOLD" && m3.Locked && !m3.Present && m4.Quality == "WORLD_NONE", "mất: HOLD 700 ms rồi NONE", $"{m3.Quality} {m4.Quality}");
        }
        using (var bmp = NewFrame())
        {
            Box(bmp, 100, 900, 60, 150, Yellow);          // trong vung HUD goc duoi-trai
            var det = new WorldMarkerDetector(S1);
            Check(ref fail, det.Candidate(Frame(bmp)) is null, "vàng trong vùng HUD dưới-trái bị loại", "");
        }
        using (var bmp = NewFrame())
        {
            Box(bmp, 900, 100, 60, 200, Yellow);          // day bbox 300 < 430 ref
            var det = new WorldMarkerDetector(S1);
            Check(ref fail, det.Candidate(Frame(bmp)) is null, "khối vàng trên cao (đáy < 430) bị loại", "");
        }
        return fail;
    }

    private static int BoardCases()
    {
        int fail = 0;
        using (var bmp = NewFrame())
        {
            Box(bmp, 500, 650, 200, 50, Cyan);
            Box(bmp, 800, 650, 200, 50, Cyan);
            Box(bmp, 1100, 650, 190, 50, Cyan);
            var b = JobBoardReader.Read(Frame(bmp), S1);
            Check(ref fail, b is not null && b.State == "EMPLOYED" && Math.Abs(b.Cx - 1195) <= 2 && Math.Abs(b.Cy - 675) <= 2,
                  "ba nút cyan, nút 3 rộng 0.95 → EMPLOYED, tâm nút 3", b is null ? "null" : $"{b.State} ratio={b.Ratio:F3} ({b.Cx},{b.Cy})");
        }
        using (var bmp = NewFrame())
        {
            Box(bmp, 500, 650, 200, 50, Cyan);
            Box(bmp, 800, 650, 200, 50, Cyan);
            Box(bmp, 1100, 650, 160, 50, Cyan);
            var b = JobBoardReader.Read(Frame(bmp), S1);
            Check(ref fail, b is not null && b.State == "UNEMPLOYED", "nút 3 rộng 0.80 → UNEMPLOYED", b is null ? "null" : $"{b.State} ratio={b.Ratio:F3}");
        }
        using (var bmp = NewFrame())
        {
            Box(bmp, 500, 650, 200, 50, Cyan);
            Box(bmp, 800, 650, 200, 50, Cyan);
            Check(ref fail, JobBoardReader.Read(Frame(bmp), S1) is null, "hai nút → chưa phải bảng", "");
        }
        return fail;
    }

    // ================================================================ anh that

    private static Bitmap Load(ElectricProfile p, string name, out string why)
    {
        why = null;
        string path = ElectricConfig.ShotPath(p.Key, name);
        var bmp = StillPicker.Load(path);
        if (bmp is null) { why = $"chưa chụp ({Path.GetFileName(path)})"; return null; }
        if (bmp.Width != p.Width || bmp.Height != p.Height)
        {
            why = $"ảnh {bmp.Width}×{bmp.Height} lệch profile {p.Width}×{p.Height} — chụp lại";
            bmp.Dispose();
            return null;
        }
        return bmp;
    }

    /// <summary>Thiếu ảnh nào thì bỏ qua ảnh đó — chưa chụp không phải là lỗi.</summary>
    private static int RealShots(ElectricConfig cfg, ElectricProfile p, NavScale s, double ox, double oy)
    {
        int fail = 0;
        fail += ShotFar(p, s, ox, oy);
        fail += ShotMarker(p, s, ox, oy);
        fail += ShotPrompt(p);
        foreach (var name in new[] { "board", "wire3", "wire5" })
        {
            using var shot = Load(p, name, out string why);
            if (shot is null) { Console.WriteLine($"  [{name}] bỏ qua: {why}"); continue; }
            fail += AssertNoCalibratedPrompt(p, shot, name);
        }
        return fail;
    }

    private static int ShotFar(ElectricProfile p, NavScale s, double ox, double oy)
    {
        using var shot = Load(p, "nav-far", out string why);
        if (shot is null) { Console.WriteLine($"  [nav-far] bỏ qua: {why}"); return 0; }
        int fail = 0;
        var f = Frame(shot);
        var cands = YellowDotDetector.Detect(f, s, ox, oy);
        foreach (var c in cands) Console.WriteLine($"      chấm: {c}");
        Check(ref fail, cands.Count >= 1, "[nav-far] có chấm vàng trên minimap", $"{cands.Count} ứng viên");
        if (cands.Count >= 1)
        {
            var tr = new DotTracker(s);
            var frags = YellowDotDetector.DetectNearFragments(f, s, ox, oy);
            tr.Update(cands, ox, oy, 0.0, frags);
            var t = tr.Update(cands, ox, oy, 0.025, frags);
            double dx = t.X.GetValueOrDefault() - ox, dy = t.Y.GetValueOrDefault() - oy;
            Check(ref fail, t.Quality == "FULL_LOCK", "[nav-far] tracker khoá",
                  $"{t.Quality} conf={t.Confidence:F2} dist={Math.Sqrt(dx * dx + dy * dy):F1}px rel={NavController.Wrap(Math.Atan2(dx, -dy) * 180 / Math.PI):+0.0;-0.0}°");
        }
        var det = new WorldMarkerDetector(s);
        var wc = det.Candidate(f);
        Check(ref fail, wc is null || wc.Value.score < NavTuning.WorldAcceptScore, "[nav-far] chưa có đầu nối 3D nhận được", det.LastCandidateNote ?? "không có khối vàng");
        fail += AssertNoCalibratedPrompt(p, shot, "nav-far");
        fail += ArrowCheck("nav-far", f, s, ox, oy);
        return fail;
    }

    private static int ShotMarker(ElectricProfile p, NavScale s, double ox, double oy)
    {
        using var shot = Load(p, "nav-marker", out string why);
        if (shot is null) { Console.WriteLine($"  [nav-marker] bỏ qua: {why}"); return 0; }
        int fail = 0;
        var f = Frame(shot);
        var det = new WorldMarkerDetector(s);
        var wc = det.Candidate(f);
        Check(ref fail, wc is not null && wc.Value.score >= NavTuning.WorldAcceptScore, "[nav-marker] thấy đầu nối vàng 3D", det.LastCandidateNote ?? "không có khối vàng");
        if (wc is not null)
        {
            double err = wc.Value.tx - s.ScreenW / 2.0;
            Console.WriteLine($"      lệch ngang so tâm màn {err:+0;-0} px (deadzone 72·{s.Px:F2} = {72 * s.Px:F0})");
        }
        det.Update(f, 0.0);
        var m = det.Update(f, 0.025);
        Check(ref fail, m.Locked && m.Present, "[nav-marker] khoá sau 2 khung", $"{m.Quality} conf={m.Confidence:F2}");
        fail += AssertNoCalibratedPrompt(p, shot, "nav-marker");
        var cands = YellowDotDetector.Detect(f, s, ox, oy);
        Console.WriteLine($"      minimap: {cands.Count} chấm" + (cands.Count > 0 ? $" — {cands[0]}" : ""));
        fail += ArrowCheck("nav-marker", f, s, ox, oy);
        return fail;
    }

    private static int ShotPrompt(ElectricProfile p)
    {
        using var shot = Load(p, "nav-prompt", out string why);
        if (shot is null) { Console.WriteLine($"  [nav-prompt] bỏ qua: {why}"); return 0; }
        if (!p.IsPromptCalibrated)
        {
            Console.WriteLine("  [nav-prompt] bỏ qua locator: chưa khoanh [E] TƯƠNG TÁC");
            return 0;
        }
        int fail = 0;
        using var loc = ElectricLocator.CreateForBitmap(p, shot, out string problem);
        Check(ref fail, loc is not null, "[nav-prompt] mở được locator", problem ?? "");
        if (loc is not null)
            Check(ref fail, loc.Visible(), "[nav-prompt] mẫu [E] TƯƠNG TÁC khớp trong ô khoanh", "");
        return fail;
    }

    private static int AssertNoCalibratedPrompt(ElectricProfile p, Bitmap shot, string name)
    {
        int fail = 0;
        if (!p.IsPromptCalibrated)
        {
            Console.WriteLine($"  [{name}] bỏ qua locator: chưa khoanh [E] TƯƠNG TÁC");
            return 0;
        }
        using var loc = ElectricLocator.CreateForBitmap(p, shot, out string problem);
        if (loc is null)
        {
            Check(ref fail, false, $"[{name}] mở locator", problem ?? "");
            return fail;
        }
        Check(ref fail, !loc.Visible(), $"[{name}] không có [E] TƯƠNG TÁC", "");
        return fail;
    }

    /// <summary>
    /// Mũi tên trắng trên minimap phải nằm sát gốc cố định (163, 980.4)·sx. Bot không dò mũi tên khi
    /// chạy (vị trí luôn là gốc cố định, đúng bản Python), nên đây là chỗ DUY NHẤT kiểm con số này.
    /// </summary>
    private static int ArrowCheck(string tag, NavFrame f, NavScale s, double ox, double oy)
    {
        int fail = 0;
        var t = NavTuning.TargetRoiRef;
        var local = f.ToLocal(s.RoiRef(t[0], t[1], t[2], t[3]));
        if (local.IsEmpty) return 0;
        var m = new Mask(local.Width, local.Height);
        for (int y = 0; y < local.Height; y++)
        {
            int row = (local.Y + y) * f.Stride;
            for (int x = 0; x < local.Width; x++)
            {
                int i = row + (local.X + x) * 4;
                var (_, sv, vv) = ImageOps.HsvOf(f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2]);
                if (vv >= 168 && sv <= 92) m.Data[y * local.Width + x] = 1;
            }
        }
        Blob? best = null; double bestD = double.MaxValue;
        foreach (var b in ImageOps.Blobs(m))
        {
            double an = b.Area / s.Area;
            if (an < 22 || an > 145) continue;
            if (b.Box.Width / s.Sx < 6 || b.Box.Width / s.Sx > 24 || b.Box.Height / s.Sy < 6 || b.Box.Height / s.Sy > 24) continue;
            double cx = f.OriginX + local.X + b.Cx, cy = f.OriginY + local.Y + b.Cy;
            double d = Math.Sqrt((cx - ox) * (cx - ox) + (cy - oy) * (cy - oy));
            if (d < bestD) { bestD = d; best = b; }
        }
        if (best is null)
        {
            Console.WriteLine($"  [{tag}] không thấy khối trắng cỡ mũi tên trong minimap — bỏ qua kiểm gốc");
            return 0;
        }
        double bx = f.OriginX + local.X + best.Value.Cx, by = f.OriginY + local.Y + best.Value.Cy;
        Check(ref fail, bestD <= 6 * s.Sx, $"[{tag}] mũi tên trắng sát gốc cố định",
              $"mũi tên ({bx:F1},{by:F1}) cách gốc ({ox:F1},{oy:F1}) {bestD:F1}px" +
              (bestD > 6 * s.Sx ? $" → đặt PlayerOriginXRef={bx / s.Sx:F1} PlayerOriginYRef={by / s.Sy:F1} trong electric.json" : ""));
        return fail;
    }
}
