using System.Drawing.Imaging;

namespace GtaMiniGameBot;

/// <summary>
/// Kiểm bộ đọc đồng hồ đói/khát NGOÀI GAME, hai phần:
///   1. Tự vẽ vòng cung ở cả 1920×1080 và 2560×1440 rồi đọc lại — chứng minh phép quy đổi tỉ lệ và
///      cách đo độ phủ góc cho ra đúng phần trăm đã vẽ.
///   2. Đọc trên ảnh tĩnh người dùng chụp bằng nút "Chụp ảnh tĩnh…" của tab Thợ điện
///      (<c>%AppData%\GtaMiniGameBot\electric\&lt;WxH&gt;\shots\hud-*.png</c>).
///
/// Phần 1 KHÔNG chứng minh được ngưỡng màu: ảnh vẽ tay dùng đúng màu mình chọn nên nó chỉ kiểm hình
/// học và tỉ lệ. Phần 2 mới là phần chịu lực — nó trả lời câu duy nhất đáng hỏi: ở HUD THẬT của
/// server này, bộ đọc có ra đúng cái phần trăm mắt người nhìn thấy không. Nó in phần trăm theo cách
/// TỰ DÒ VÀNH đặt cạnh phần trăm theo dải cố định 17–23 của bản Python, kèm bán kính và độ lệch tâm
/// dò được — chênh nhau nhiều nghĩa là dải cố định đang bắn trượt vành, đúng gốc của lỗi "ăn sớm".
/// Không thấy icon nào cả thì mới là chuyện tâm sai hẳn: sửa
/// <c>Survival.FoodCenterXRef</c>/<c>WaterCenterXRef</c> trong electric.json.
///
/// Chạy: GtaMiniGameBot.exe --verify-survival
/// </summary>
internal static class VerifySurvival
{
    public static int Run(string[] args)
    {
        Console.WriteLine("== kiểm tra bộ đọc đồng hồ đói/khát (port SURVIVAL V6.7.34) ==");

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
            var v = NavTuning.SurvivalRoiRef;
            var roi = s.RoiRef(v[0], v[1], v[2], v[3]);
            Console.WriteLine($"  vùng chụp {roi.Width}×{roi.Height} @ {roi.X},{roi.Y}");
            Console.WriteLine($"  tâm bánh ({cfg.Survival.FoodCenterXRef * s.Sx:F1},{cfg.Survival.FoodCenterYRef * s.Sy:F1})  " +
                              $"tâm nước ({cfg.Survival.WaterCenterXRef * s.Sx:F1},{cfg.Survival.WaterCenterYRef * s.Sy:F1})");
            Console.WriteLine($"  dò vành trong {NavTuning.SurvivalRingSearchRminRef * s.Max:F1}→" +
                              $"{NavTuning.SurvivalRingSearchRmaxRef * s.Max:F1} px " +
                              $"(dự phòng {NavTuning.SurvivalRingRminRef * s.Max:F1}→{NavTuning.SurvivalRingRmaxRef * s.Max:F1}), " +
                              $"lệch tâm ±{NavTuning.SurvivalCenterSearchRef * s.Max:F0} px, " +
                              $"lõi cần {Math.Max(3, (int)(NavTuning.SurvivalCoreMinPixels * s.Area))} px, " +
                              $"ngưỡng {cfg.Survival.LowThresholdPct:F0}%");
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

    // ================================================================ tu kiem tra

    private static readonly Color Food = Color.FromArgb(255, 220, 40);    // H≈25 (OpenCV) — trong dai 14..35
    private static readonly Color Water = Color.FromArgb(40, 200, 220);   // H≈93          — trong dai 88..110
    private static readonly Color Dark = Color.FromArgb(30, 30, 30);      // V=30 < 70 — bi loai o buoc re nhat

    private static int SelfTest()
    {
        Console.WriteLine();
        Console.WriteLine("-- tự kiểm tra --");
        int fail = 0;
        fail += SlotCases();
        fail += NormalizeCases();
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440) })
            fail += GaugeCases(w, h);
        fail += StreakCases();
        Console.WriteLine(fail == 0 ? "  tự kiểm tra: ĐẠT" : $"  tự kiểm tra: HỎNG {fail} ca");
        return fail;
    }

    private static int SlotCases()
    {
        int fail = 0;
        Console.WriteLine("  · ô hotbar");
        Check(ref fail, SurvivalSettings.SlotKeys("5,7").SequenceEqual(new ushort[] { 0x35, 0x37 }),
              "\"5,7\" → mã phím 0x35,0x37", "");
        Check(ref fail, SurvivalSettings.SlotKeys("4,6").SequenceEqual(new ushort[] { 0x34, 0x36 }),
              "\"4,6\" → mã phím 0x34,0x36", "");
        Check(ref fail, SurvivalSettings.SlotKeys("").Length == 0, "chuỗi rỗng → không có phím nào", "");

        // Hotbar chi bam duoc 4..8; 3 va 9 phai bi loai ngay o buoc doi ma phim.
        Check(ref fail, SurvivalSettings.SlotKeys("3,9").Length == 0, "ngoài dải 4–8 → không có phím nào", "");
        return fail;
    }

    private static int NormalizeCases()
    {
        int fail = 0;
        Console.WriteLine("  · kẹp cấu hình");

        var c = new SurvivalSettings { FoodSlots = "x5!!7y9", WaterSlots = "4,4,6" };
        c.Normalize();
        Check(ref fail, c.FoodSlots == "5,7", "lọc rác + cắt còn 2 phím", c.FoodSlots);
        Check(ref fail, c.WaterSlots == "4,6", "bỏ phím trùng", c.WaterSlots);

        var d = new SurvivalSettings { FoodSlots = "abc", FoodCenterXRef = -1, WaterCenterYRef = 99999 };
        d.Normalize();
        Check(ref fail, d.FoodSlots == "5,7", "không còn phím hợp lệ → về mặc định", d.FoodSlots);
        Check(ref fail, Math.Abs(d.FoodCenterXRef - 160.0) < 1e-9, "tâm âm → về mặc định", $"{d.FoodCenterXRef}");
        Check(ref fail, Math.Abs(d.WaterCenterYRef - 1047.0) < 1e-9, "tâm vượt màn → về mặc định", $"{d.WaterCenterYRef}");

        // Ngoai dai hotbar: bo het roi bu lai tu mac dinh, chu KHONG duoc tra ve mot o.
        var e = new SurvivalSettings { FoodSlots = "3,9", WaterSlots = "8" };
        e.Normalize();
        Check(ref fail, e.FoodSlots == "5,7", "ô 3/9 ngoài hotbar → về mặc định", e.FoodSlots);
        Check(ref fail, e.WaterSlots == "8,4", "chỉ khai một ô → bù cho đủ hai", e.WaterSlots);

        // Nguong nguoi dung go bay: kep vao dai chu khong lam ca tinh nang dai ra.
        var t = new SurvivalSettings { LowThresholdPct = 400 };
        t.Normalize();
        Check(ref fail, Math.Abs(t.LowThresholdPct - NavTuning.SurvivalThresholdMaxPct) < 1e-9,
              "ngưỡng vượt trần → kẹp", $"{t.LowThresholdPct:F0}%");

        var t2 = new SurvivalSettings { LowThresholdPct = 0 };
        t2.Normalize();
        Check(ref fail, Math.Abs(t2.LowThresholdPct - NavTuning.SurvivalThresholdMinPct) < 1e-9,
              "ngưỡng 0 → kẹp về sàn", $"{t2.LowThresholdPct:F0}%");

        // Chi kep, KHONG nem: ElectricConfig.Load nuot moi exception va tra config moi.
        bool threw = false;
        try { new SurvivalSettings { FoodSlots = null, WaterSlots = null }.Normalize(); }
        catch { threw = true; }
        Check(ref fail, !threw, "Normalize() với chuỗi null không ném", "");
        return fail;
    }

    /// <summary>Vẽ một đồng hồ: đĩa lõi + vòng cung quét <paramref name="sweepDeg"/> độ từ 3 giờ.</summary>
    private static void DrawGauge(Bitmap bmp, NavScale s, double cxRef, double cyRef, double sweepDeg, Color c)
        => DrawGaugeAt(bmp, s, cxRef, cyRef, sweepDeg, c,
            0.5 * (NavTuning.SurvivalRingRminRef + NavTuning.SurvivalRingRmaxRef), 9.0);

    /// <summary>
    /// Như trên nhưng nói rõ bán kính và bề dày vành (mốc 1080p) — để dựng được cái HUD mà dải cố
    /// định của bản Python bắn trượt.
    /// </summary>
    private static void DrawGaugeAt(Bitmap bmp, NavScale s, double cxRef, double cyRef, double sweepDeg,
        Color c, double ringRRef, double penRef)
    {
        double cx = cxRef * s.Sx, cy = cyRef * s.Sy;
        double coreR = NavTuning.SurvivalCoreRadiusRef * s.Max;
        double ringR = ringRRef * s.Max;
        float pen = (float)(penRef * s.Max);

        using var g = Graphics.FromImage(bmp);
        using var br = new SolidBrush(c);
        g.FillEllipse(br, (float)(cx - coreR - 2), (float)(cy - coreR - 2),
            (float)(2 * coreR + 4), (float)(2 * coreR + 4));
        if (sweepDeg <= 0) return;

        using var p = new Pen(c, pen);
        g.DrawArc(p, (float)(cx - ringR), (float)(cy - ringR), (float)(2 * ringR), (float)(2 * ringR),
            0f, (float)sweepDeg);
    }

    private static SurvivalReading Read(SurvivalSettings cfg, NavScale s, Bitmap bmp, double now)
        => Read(cfg, s, bmp, now, new SurvivalState());

    private static SurvivalReading Read(SurvivalSettings cfg, NavScale s, Bitmap bmp, double now,
        SurvivalState state)
    {
        var gauge = new SurvivalGauge(cfg, s, state);
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
            Check(ref fail, r.FoodValid && Math.Abs(r.FoodPct - want) <= 6.0,
                  $"bánh cung {sweep:F0}° → {want:F0}%", $"đọc {r.FoodPct:F1}%");
            Check(ref fail, r.WaterValid && Math.Abs(r.WaterPct - want) <= 6.0,
                  $"nước cung {sweep:F0}° → {want:F0}%", $"đọc {r.WaterPct:F1}%");
        }

        // Chi co dia loi, khong co vanh: doc duoc nhung 0 %. Day la trang thai "can an ngay".
        using (var bmp = Blank(w, h))
        {
            DrawGauge(bmp, s, cfg.FoodCenterXRef, cfg.FoodCenterYRef, 0, Food);
            var r = Read(cfg, s, bmp, 1.0);
            Check(ref fail, r.FoodValid && r.FoodPct <= 3.0, "chỉ có lõi, không vành → 0%", $"{r.FoodPct:F1}%");
        }

        // CHOT CHAN QUAN TRONG NHAT: khong ve gi thi phai bao "khong doc duoc", KHONG duoc tra 0 %.
        // Tra 0 % la bot tu mo mot bua an giua luc HUD an hoac dang o menu.
        using (var bmp = Blank(w, h))
        {
            var r = Read(cfg, s, bmp, 1.0);
            Check(ref fail, !r.FoodValid && !r.WaterValid && double.IsNaN(r.FoodPct),
                  "màn trống → KHÔNG đọc được (không phải 0%)", $"food={r.FoodPct}");
            Check(ref fail, !r.FoodLow && !r.WaterLow, "màn trống → không kích hoạt ăn uống", "");
        }

        // Sai mau o dung cho: vang o o nuoc thi bo doc nuoc phai tu choi.
        using (var bmp = Blank(w, h))
        {
            DrawGauge(bmp, s, cfg.WaterCenterXRef, cfg.WaterCenterYRef, 360, Food);
            var r = Read(cfg, s, bmp, 1.0);
            Check(ref fail, !r.WaterValid, "vàng ở ô nước → không nhận nhầm", "");
        }

        fail += OffCentreCases(w, h);
        return fail;
    }

    /// <summary>
    /// CA CHỊU LỰC CỦA BẢN SỬA "ĂN SỚM". Vành mảnh, nằm ở bán kính 28 (ngoài hẳn dải cố định 17–23
    /// của bản Python) và cả icon lệch tâm so với config — đúng kiểu HUD của một server khác.
    ///
    /// Cách đo cũ bắn 7 tia trong dải 17–23 rồi bắt mỗi nan quạt trúng ≥2 tia: ở đây nó trúng 0 tia
    /// nên đọc ra ~0 % và bot mở bữa ăn ngay lúc vạch còn đầy. Cách đo mới tự dò tâm + bán kính nên
    /// phải ra đúng phần cung đã vẽ.
    /// </summary>
    private static int OffCentreCases(int w, int h)
    {
        int fail = 0;
        Console.WriteLine($"  · vành lệch tâm & lệch bán kính {w}×{h}");
        var s = new NavScale(w, h, 0);
        var cfg = new SurvivalSettings();
        cfg.Normalize();

        const double ringRRef = 28.0, penRef = 4.0, offXRef = 3.0, offYRef = -2.0;

        foreach (double sweep in new double[] { 360, 180, 90 })
        {
            using var bmp = Blank(w, h);
            DrawGaugeAt(bmp, s, cfg.FoodCenterXRef + offXRef, cfg.FoodCenterYRef + offYRef,
                sweep, Food, ringRRef, penRef);
            DrawGaugeAt(bmp, s, cfg.WaterCenterXRef + offXRef, cfg.WaterCenterYRef + offYRef,
                sweep, Water, ringRRef, penRef);

            var state = new SurvivalState();
            var r = Read(cfg, s, bmp, 1.0, state);

            double want = sweep / 360.0 * 100.0;
            Check(ref fail, r.FoodValid && Math.Abs(r.FoodPct - want) <= 6.0,
                  $"bánh cung {sweep:F0}° ở bán kính {ringRRef:F0} lệch tâm ({offXRef:F0},{offYRef:F0}) → {want:F0}%",
                  $"đọc {r.FoodPct:F1}%");
            Check(ref fail, r.WaterValid && Math.Abs(r.WaterPct - want) <= 6.0,
                  $"nước cung {sweep:F0}° tương tự → {want:F0}%", $"đọc {r.WaterPct:F1}%");

            // Ban kinh phai ra dung cai da ve chu khong phai dai du phong 17-23. Chi doi hoi o cung
            // 360: cung ngan van ra ban kinh dung nhung nhieu hon, va tri so do khong phai ket luan
            // cua bo do — phan tram o tren moi la.
            if (sweep >= 360)
                Check(ref fail, state.FoodRing.ROk && Math.Abs(state.FoodRing.R / s.Max - ringRRef) <= 2.0,
                      $"dò được bán kính vành bánh ≈ {ringRRef:F0}",
                      state.FoodRing.ROk ? $"{state.FoodRing.R / s.Max:F1} mốc 1080p" : "chưa dò được");
        }

        return fail;
    }

    private static int StreakCases()
    {
        int fail = 0;
        Console.WriteLine("  · streak & EMA");
        var s = new NavScale(1920, 1080, 0);
        var cfg = new SurvivalSettings();
        cfg.Normalize();

        // 25 % — duoi nguong 50. Phai doi du SurvivalLowConfirmScans luot moi duoc bao THIEU.
        using var low = Blank(1920, 1080);
        DrawGauge(low, s, cfg.FoodCenterXRef, cfg.FoodCenterYRef, 90, Food);
        var frameLow = NavFrame.FromBitmap(low, new Rectangle(0, 0, 1920, 1080));

        var gauge = new SurvivalGauge(cfg, s);
        double t = 1.0;
        for (int i = 1; i < NavTuning.SurvivalLowConfirmScans; i++)
        {
            var r = gauge.Update(frameLow, t);
            Check(ref fail, !r.FoodLow, $"lượt {i}/{NavTuning.SurvivalLowConfirmScans} chưa báo THIẾU", $"{r.FoodPct:F1}%");
            t += NavTuning.SurvivalScanIntervalS;
        }
        var last = gauge.Update(frameLow, t);
        Check(ref fail, last.FoodLow, $"lượt {NavTuning.SurvivalLowConfirmScans} mới báo THIẾU", $"{last.FoodPct:F1}%");

        // Mat icon giua chung la XOA streak: mot khung HUD an khong duoc phep gop vao chuoi.
        using var blank = Blank(1920, 1080);
        var frameBlank = NavFrame.FromBitmap(blank, new Rectangle(0, 0, 1920, 1080));
        t += NavTuning.SurvivalScanIntervalS;
        gauge.Update(frameBlank, t);
        t += NavTuning.SurvivalScanIntervalS;
        var after = gauge.Update(frameLow, t);
        Check(ref fail, !after.FoodLow, "mất icon một lượt → streak về 0, phải đếm lại từ đầu", "");

        // Tiet luu: hoi truoc khi chup, 0.25 s mot lan chu khong moi tick 25 ms.
        var g2 = new SurvivalGauge(cfg, s);
        Check(ref fail, g2.Due(0.0), "chưa quét lần nào → Due() đúng", "");
        g2.Update(frameLow, 10.0);
        Check(ref fail, !g2.Due(10.0 + NavTuning.SurvivalScanIntervalS / 2), "chưa tới hạn → Due() sai", "");
        Check(ref fail, g2.Due(10.0 + NavTuning.SurvivalScanIntervalS), "tới hạn → Due() đúng", "");

        g2.Reset();
        Check(ref fail, g2.Due(0.0) && !g2.Last.FoodValid, "Reset() xoá cả hạn quét lẫn kết quả", "");
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
    private static int RealShots(ElectricConfig cfg, ElectricProfile p, NavScale s, Rectangle roi)
    {
        int fail = 0;

        foreach (var (name, label) in new[] { ("hud-no", "no đủ"), ("hud-doi", "đói/khát") })
        {
            using var shot = Load(p, name, out string why);
            if (shot is null) { Console.WriteLine($"  [{name}] bỏ qua: {why}"); continue; }

            // Doc HAI lan de tach bach: lan hai ep dung dai co dinh 17-23 va cam do lech tam, moi
            // thu khac giu nguyen. Chenh nhau nhieu nghia la dai co dinh dang ban truot vanh cua
            // HUD nay. (Day KHONG phai con so cua ban cu: ban cu con lay mau bang 7 tia nen con hut
            // them nua — xem chu thich dau SurvivalGauge.)
            var state = new SurvivalState();
            var r = Read(cfg.Survival, s, shot, 1.0, state);
            var fixedBand = ReadFixedBand(cfg.Survival, s, shot);

            Console.WriteLine($"  [{name}] ({label}) bánh={Pct(r.FoodValid, r.FoodPct)} nước={Pct(r.WaterValid, r.WaterPct)}");
            Console.WriteLine($"      nếu ép dải cố định 17–23 và không dò tâm: " +
                              $"bánh={Pct(fixedBand.FoodValid, fixedBand.FoodPct)} " +
                              $"nước={Pct(fixedBand.WaterValid, fixedBand.WaterPct)}");
            Ring("bánh", state.FoodRing, s);
            Ring("nước", state.WaterRing, s);

            // Doc duoc HAI icon la ca kiem duy nhat dat cung o day: phan tram bao nhieu thi chi mat
            // nguoi moi xac nhan duoc, nhung "khong thay icon" thi chac chan la tam trong config
            // tro sai cho — va do la loi lam ca tinh nang chet cam.
            Check(ref fail, r.FoodValid, $"[{name}] thấy icon bánh ở tâm đã cấu hình", "");
            Check(ref fail, r.WaterValid, $"[{name}] thấy icon nước ở tâm đã cấu hình", "");

            double thr = cfg.Survival.LowThresholdPct;
            if (r.FoodValid && r.WaterValid)
                Console.WriteLine($"      → ngưỡng {thr:F0}%: bánh {(r.FoodPct < thr ? "DƯỚI" : "trên")}, " +
                                  $"nước {(r.WaterPct < thr ? "DƯỚI" : "trên")}" +
                                  $"{(r.FoodPct < thr || r.WaterPct < thr ? " → sẽ mở bữa ăn" : "")}");
        }

        // Vung chup phai om tron ca hai vanh, ke ca dai TU DO rong hon dai co dinh — khong thi phan
        // tram bi cat oan o ria khung dung cai kieu sinh ra loi "an som".
        double rmax = NavTuning.SurvivalRingSearchRmaxRef * s.Max;
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

        return fail;
    }

    private static string Pct(bool valid, double v) => valid ? $"{v:F1}%" : "không đọc được";

    /// <summary>
    /// Đọc bằng ĐÚNG dải cố định 17–23 và tâm y nguyên trong config — tức là ép bộ đọc bỏ bước tự
    /// dò, để đối chiếu. Ép bằng cách chốt sẵn kết quả "hiệu chuẩn" vào state: bán kính giữa dải,
    /// lệch tâm 0. Bề dày dải trùng khớp vì <c>SurvivalRingHalfWidthRef</c> đúng bằng nửa dải cũ.
    /// </summary>
    private static SurvivalReading ReadFixedBand(SurvivalSettings cfg, NavScale s, Bitmap bmp)
    {
        double rc = 0.5 * (NavTuning.SurvivalRingRminRef + NavTuning.SurvivalRingRmaxRef) * s.Max;
        var state = new SurvivalState();
        foreach (var ring in new[] { state.FoodRing, state.WaterRing })
        {
            ring.ROk = true;
            ring.CentreOk = true;
            ring.OffX = 0;
            ring.OffY = 0;
            ring.R = rc;
        }
        return Read(cfg, s, bmp, 1.0, state);
    }

    private static void Ring(string who, SurvivalRing ring, NavScale s)
    {
        if (!ring.ROk)
        {
            Console.WriteLine($"      vành {who}: CHƯA dò được (vạch quá ngắn hoặc HUD mờ) → dùng dải dự phòng");
            return;
        }

        Console.WriteLine($"      vành {who}: bán kính {ring.R:F1}px = {ring.R / s.Max:F1} mốc 1080p, " +
                          (ring.CentreOk
                              ? $"tâm lệch ({ring.OffX:+0;-0;0},{ring.OffY:+0;-0;0})px, "
                              : "chưa chốt tâm (vành chưa đủ đầy) → dải nới rộng, ") +
                          $"{ring.Strength} điểm ảnh");
    }
}
