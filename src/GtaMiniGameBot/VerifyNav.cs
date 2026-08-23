using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Kiểm bộ điều hướng của job Thợ điện NGOÀI GAME, hai phần:
///   1. Tự vẽ ảnh rồi dò lại — chạy được ngay, không cần ảnh chụp nào.
///   2. Dò trên ảnh tĩnh người dùng chụp bằng nút "Chụp ảnh tĩnh…" của tab Thợ điện.
///
/// Vì sao đáng viết trước khi viết bộ lái: phần THỊ GIÁC kiểm được offline và lặp lại bao nhiêu
/// lần cũng ra một kết quả, còn bộ lái là vòng điều khiển kín — mỗi lần chỉnh là một lượt thử
/// trong game. Mang một đống ngưỡng chưa đo vào lượt thử là phí lượt thử.
///
/// Ca quan trọng nhất ở đây là LOGO VÀNG TRÊN ÁO nhân vật: đo trên ảnh thật của người dùng, nó lọt
/// hết mọi cửa hình học của bản Python. Bài kiểm "vật vàng đứng im không bao giờ được khoá" chính
/// là hàng rào cho chuyện đó.
///
/// Chạy: GtaMiniGameBot.exe --verify-nav
/// </summary>
internal static class VerifyNav
{
    public static int Run(string[] args)
    {
        Console.WriteLine("== kiểm tra bộ điều hướng thợ điện ==");

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

            var mini = profile.ScanMinimap();
            var band = profile.ScanPromptBand();
            var sil = profile.SilhouetteBox(cfg.Nav);
            Console.WriteLine($"  minimap {mini.W}×{mini.H} @ {mini.X},{mini.Y}" +
                              (profile.Minimap.IsSet ? "" : "  (suy từ mốc 1080p, CHƯA đo lại)"));
            Console.WriteLine($"  băng prompt {band.W}×{band.H} @ {band.X},{band.Y}" +
                              (profile.PromptBand.IsSet ? "" : "  (mặc định giữa màn)"));
            Console.WriteLine($"  hộp bóng nhân vật {sil.Width}×{sil.Height} @ {sil.X},{sil.Y}");

            fail += RealShots(cfg, profile);
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "TẤT CẢ ĐẠT" : $"HỎNG {fail} ca");
        return fail == 0 ? 0 : 1;
    }

    // ================================================================ tu kiem tra

    /// <summary>Độ phân giải giả, cố tình lẻ để không đụng profile thật nào của người dùng.</summary>
    private const int SelfW = 1291;
    private const int SelfH = 727;

    private static int SelfTest()
    {
        Console.WriteLine();
        Console.WriteLine("-- tự kiểm tra (ảnh tự vẽ) --");

        string dir = ElectricConfig.ProfileDir($"{SelfW}x{SelfH}");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }

        var cfg = new ElectricConfig();
        cfg.Normalize();

        var profile = new ElectricProfile { Device = "selftest", Width = SelfW, Height = SelfH };
        profile.Normalize();

        int fail = 0;
        try
        {
            fail += MinimapCases(cfg, profile);
            fail += MarkerCases(cfg, profile);
            fail += PromptCases(cfg, profile);
            fail += EscapeCases(cfg);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "  tự kiểm tra: ĐẠT" : $"  tự kiểm tra: HỎNG {fail} ca");
        return fail;
    }

    // ---------------------------------------------------------------- cham minimap

    private static int MinimapCases(ElectricConfig cfg, ElectricProfile p)
    {
        Console.WriteLine("  [chấm minimap]");
        int fail = 0;

        var mini = p.ScanMinimap().ToRectangle();
        int ox = mini.X + (int)(mini.Width * cfg.Nav.MinimapOriginXFrac);
        int oy = mini.Y + (int)(mini.Height * cfg.Nav.MinimapOriginYFrac);

        // Cham dat BEN PHAI va PHIA TREN goc nguoi choi -> goc phai duong va nho hon 90 do.
        using (var shot = MapShot(p, dot: new Point(ox + 34, oy - 58), bolt: false))
            fail += ExpectDot(cfg, p, shot, "chấm bên phải-trước", want: true,
                              check: f => f.BearingDeg is > 5 and < 85, "góc phải trong (5°,85°)");

        using (var shot = MapShot(p, dot: new Point(ox - 40, oy - 20), bolt: false))
            fail += ExpectDot(cfg, p, shot, "chấm bên trái", want: true,
                              check: f => f.BearingDeg < -5, "góc phải âm");

        // CHI co icon set: cung mau vang, nhung rang cua -> phai bi loai boi cua do tron.
        using (var shot = MapShot(p, dot: null, bolt: true))
            fail += ExpectDot(cfg, p, shot, "chỉ có icon sét", want: false, check: null, null);

        // Ca that: co ca hai, phai bat dung cai cham.
        using (var shot = MapShot(p, dot: new Point(ox + 30, oy - 44), bolt: true))
            fail += ExpectDot(cfg, p, shot, "chấm + icon sét", want: true,
                              check: f => f.BearingDeg is > 5 and < 85, "bắt đúng chấm, không bắt sét");

        return fail;
    }

    private static int ExpectDot(ElectricConfig cfg, ElectricProfile p, Bitmap shot, string label,
                                 bool want, Func<DotFix, bool> check, string checkName)
    {
        using var reader = MinimapReader.ForBitmap(cfg, p, shot, out string problem);
        if (reader is null) { Console.WriteLine($"    [{label}] KHÔNG DÒ ĐƯỢC: {problem}"); return 1; }

        var fix = reader.Read(1000);
        foreach (var c in reader.LastCandidates) Console.WriteLine($"      {c}");

        bool ok = fix.Found == want;
        if (ok && want && check is not null && !check(fix)) ok = false;

        Console.WriteLine($"    [{label}] {fix} — {(ok ? "ĐẠT" : "HỎNG")}" +
                          (checkName is null ? "" : $"  (đòi: {checkName})"));
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- moc vang 3D

    /// <summary>
    /// Ba khung liên tiếp cho mỗi ca, vì phép kiểm thị sai so khung này với khung trước.
    /// Camera xoay PHẢI (+counts) nên vật trong thế giới phải trôi sang TRÁI.
    /// </summary>
    private static int MarkerCases(ElectricConfig cfg, ElectricProfile p)
    {
        Console.WriteLine("  [mốc vàng 3D]");
        int fail = 0;

        var sil = p.SilhouetteBox(cfg.Nav);
        var logo = new Rectangle(sil.X + sil.Width / 2 - 40, sil.Y + 90, 80, 50);

        // 1. Moc that troi sang trai khi xoay phai -> PHAI khoa duoc.
        fail += ExpectMarker(cfg, p, "mốc thật, camera xoay phải", wantLock: true, yaw: +40,
            frames: new[]
            {
                WorldShot(p, marker: new Rectangle(880, 470, 190, 120), logo: logo, staticBlob: Rectangle.Empty),
                WorldShot(p, marker: new Rectangle(800, 470, 190, 120), logo: logo, staticBlob: Rectangle.Empty),
                WorldShot(p, marker: new Rectangle(720, 470, 190, 120), logo: logo, staticBlob: Rectangle.Empty)
            });

        // 2. CHI co logo tren ao: nam trong hop bong nhan vat -> loai ngay tu cua hinh hoc.
        fail += ExpectMarker(cfg, p, "chỉ có logo trên áo", wantLock: false, yaw: +40,
            frames: new[]
            {
                WorldShot(p, marker: Rectangle.Empty, logo: logo, staticBlob: Rectangle.Empty),
                WorldShot(p, marker: Rectangle.Empty, logo: logo, staticBlob: Rectangle.Empty),
                WorldShot(p, marker: Rectangle.Empty, logo: logo, staticBlob: Rectangle.Empty)
            });

        // 3. Vat vang DUNG IM ngoai hop bong (biển báo, đèn HUD lạ): qua duoc cua hinh hoc nhung
        //    khong troi khi camera xoay -> kiem thi sai phai chan. Day la hang rao that su.
        var stat = new Rectangle(1050, 260, 150, 110);
        fail += ExpectMarker(cfg, p, "vật vàng đứng im (phải KHÔNG khoá)", wantLock: false, yaw: +40,
            frames: new[]
            {
                WorldShot(p, marker: Rectangle.Empty, logo: Rectangle.Empty, staticBlob: stat),
                WorldShot(p, marker: Rectangle.Empty, logo: Rectangle.Empty, staticBlob: stat),
                WorldShot(p, marker: Rectangle.Empty, logo: Rectangle.Empty, staticBlob: stat)
            });

        // 4. Khong xoay camera thi khong duoc cap khoa moi, du moc co that.
        fail += ExpectMarker(cfg, p, "mốc thật nhưng camera đứng yên", wantLock: false, yaw: 0,
            frames: new[]
            {
                WorldShot(p, marker: new Rectangle(880, 470, 190, 120), logo: Rectangle.Empty, staticBlob: Rectangle.Empty),
                WorldShot(p, marker: new Rectangle(880, 470, 190, 120), logo: Rectangle.Empty, staticBlob: Rectangle.Empty),
                WorldShot(p, marker: new Rectangle(880, 470, 190, 120), logo: Rectangle.Empty, staticBlob: Rectangle.Empty)
            });

        return fail;
    }

    private static int ExpectMarker(ElectricConfig cfg, ElectricProfile p, string label,
                                    bool wantLock, int yaw, Bitmap[] frames)
    {
        MarkerReader reader = null;
        try
        {
            reader = MarkerReader.ForBitmap(cfg, p, frames[0], out string problem);
            if (reader is null) { Console.WriteLine($"    [{label}] KHÔNG DÒ ĐƯỢC: {problem}"); return 1; }

            MarkerFix fix = null;
            for (int i = 0; i < frames.Length; i++)
            {
                if (i > 0) reader.UseStill(frames[i]);
                fix = reader.Update(1000 + i * 125, yaw);
            }

            foreach (var c in reader.LastCandidates) Console.WriteLine($"      {c}");

            bool ok = fix.Locked == wantLock;
            Console.WriteLine($"    [{label}] {fix} — {(ok ? "ĐẠT" : "HỎNG")}");
            return ok ? 0 : 1;
        }
        finally
        {
            reader?.Dispose();
            foreach (var f in frames) f.Dispose();
        }
    }

    // ---------------------------------------------------------------- prompt E

    private static int PromptCases(ElectricConfig cfg, ElectricProfile p)
    {
        Console.WriteLine("  [prompt E TƯƠNG TÁC]");

        var at = new Point(430, 300);
        if (!CalibratePrompt(cfg, p, at))
        {
            Console.WriteLine("    KHÔNG hiệu chuẩn được từ ảnh tự vẽ");
            return 1;
        }
        Console.WriteLine($"    chữ cao {p.PromptTextH}px, ngưỡng khe {p.PromptGapSplit}px");

        int fail = 0;

        // Prompt TROI sang cho khac — no gan vao tu dien trong khong gian 3D.
        fail += ExpectPrompt(cfg, p, "prompt ở chỗ khác", Prompt(p, new Point(660, 380), "E", "TƯƠNG TÁC"), true);

        // Prompt tuong tac KHAC cua server.
        fail += ExpectPrompt(cfg, p, "prompt khác (mở cốp)", Prompt(p, new Point(600, 330), "E", "MỞ CỐP XE"), false);

        // Tram dien GIUA TRUA: be tong trang nang cung lot cua "muc trang", cac cua KICH CO phai
        // chan. Day la ca sat voi canh that cua job nay nhat.
        fail += ExpectPrompt(cfg, p, "bê tông nắng, không prompt", Yard(p, prompt: false), false);
        fail += ExpectPrompt(cfg, p, "bê tông nắng, có prompt", Yard(p, prompt: true), true);

        return fail;
    }

    /// <summary>Đi đúng đường form hiệu chuẩn sẽ đi: cắt ô → tách chữ → lưu mẫu.</summary>
    private static bool CalibratePrompt(ElectricConfig cfg, ElectricProfile p, Point at)
    {
        using var shot = Prompt(p, at, "E", "TƯƠNG TÁC");

        var box = new Rectangle(at.X - 10, at.Y - 12, 360, 76);
        using var crop = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(shot, new Rectangle(0, 0, box.Width, box.Height), box, GraphicsUnit.Pixel);

        var parts = PromptLocator.ExtractText(crop, cfg.Nav.PromptTuning(p), out string problem);
        if (parts is null) { Console.WriteLine($"    tách chữ HỎNG: {problem}"); return false; }

        var tpl = GrayTemplate.FromBitmapCrop(crop, parts.Text);
        if (tpl.IsFlat) { Console.WriteLine("    mẫu chữ phẳng"); return false; }
        tpl.Save(ElectricConfig.PromptTemplatePath(p.Key));

        p.PromptTextH = parts.Text.Height;
        p.PromptGapSplit = parts.GapSplit;
        return true;
    }

    private static int ExpectPrompt(ElectricConfig cfg, ElectricProfile p, string label, Bitmap shot, bool want)
    {
        using (shot)
        {
            using var reader = PromptReader.ForBitmap(cfg, p, shot, out string problem);
            if (reader is null) { Console.WriteLine($"    [{label}] KHÔNG DÒ ĐƯỢC: {problem}"); return 1; }

            var hit = reader.Read();
            foreach (string r in hit.Rows) Console.WriteLine($"      {r}");
            if (hit.Rows.Count == 0) Console.WriteLine("      không thấy dòng chữ nào trong băng quét");

            bool ok = hit.Visible == want;
            Console.WriteLine($"    [{label}] mong {(want ? "khớp" : "không khớp")}, " +
                              $"ra {(hit.Visible ? "khớp" : "không khớp")} — {(ok ? "ĐẠT" : "HỎNG")}");
            return ok ? 0 : 1;
        }
    }

    // ================================================================ thoat ket

    /// <summary>
    /// Kiểm phần QUYẾT ĐỊNH của cơ chế thoát kẹt, bằng số liệu lấy thẳng từ log trong game 23/08.
    ///
    /// Vì sao phải có: bản đầu tiên sai ở chỗ nhìn từng bước thì bước nào cũng hợp lý, chỉ nhìn
    /// CHUỖI mới thấy nó lặp "kẹt → thoát được → kẹt" 8 lần trong 25 giây mà cự ly đứng nguyên
    /// 12↔13. Đây đúng là loại lỗi mà một ca kiểm ngoài game bắt được còn mắt thường thì không.
    /// </summary>
    private static int EscapeCases(ElectricConfig cfg)
    {
        Console.WriteLine("  [thoát kẹt]");
        int fail = 0;
        var nav = cfg.Nav;

        // --- 1. Cu ly dung yen 3 s -> PHAI bao ket, du sai phan khung dang cao (9.4 do duoc that)
        {
            var pt = new ProgressTracker(nav);
            for (long t = 0; t <= 3200; t += 100) pt.Push(t, 31.0);
            fail += Check("cự ly đứng yên 3 s → kẹt",
                          pt.Ready(3200) && pt.Stalled(3200),
                          $"ready={pt.Ready(3200)} Δ={pt.Delta(3200):F2}");
        }

        // --- 2. Cu ly giam deu -> KHONG duoc bao ket, du flow thap (2.5 do duoc that)
        {
            var pt = new ProgressTracker(nav);
            double d = 42;
            for (long t = 0; t <= 3200; t += 100) { pt.Push(t, d); d -= 0.12; }
            fail += Check("cự ly giảm đều → không kẹt",
                          pt.Ready(3200) && !pt.Stalled(3200),
                          $"Δ={pt.Delta(3200):F2} (đòi ≤ −{nav.MinProgressRef})");
        }

        // --- 3. Chua du lich su thi khong duoc ket luan gi
        {
            var pt = new ProgressTracker(nav);
            for (long t = 0; t <= 900; t += 100) pt.Push(t, 31.0);
            fail += Check("chưa đủ cửa sổ → chưa kết luận", !pt.Ready(900), $"ready={pt.Ready(900)}");
        }

        // --- 4. Ket lien tuc: bac phai LEO va ben KHONG duoc lat
        {
            var lad = new EscapeLadder(nav);
            lad.Open(12.0, preferRight: true);

            var seq = new List<EscapeStep>();
            for (int i = 0; i < 3; i++)
            {
                // Moi vong: van ket (cu ly khong doi) -> Open tra false, giu nguyen ben va bac.
                lad.Open(12.0, preferRight: false);   // dau sai so lat, KHONG duoc doi ben
                seq.Add(lad.Next());
            }

            bool sameSide = seq.All(s => s.Right);
            bool rising = seq[0].DurationMs < seq[1].DurationMs && seq[1].DurationMs < seq[2].DurationMs;
            bool rungs = seq[0].Rung == 1 && seq[1].Rung == 2 && seq[2].Rung == 3;

            fail += Check("3 lần kẹt liên tiếp → cùng bên, bậc dài dần",
                          sameSide && rising && rungs,
                          string.Join(" | ", seq));
        }

        // --- 5. Het bac mot ben -> lui roi DOI BEN, bac dat lai
        {
            var lad = new EscapeLadder(nav);
            lad.Open(12.0, preferRight: true);
            for (int i = 0; i < nav.StrafeRungsMs.Length; i++) lad.Next();

            var flip = lad.Next();
            var after = lad.Next();

            fail += Check("hết bậc → lùi, đổi bên, bậc về 1",
                          flip.Action == EscapeAction.BackupAndFlip && !flip.Right &&
                          after.Action == EscapeAction.Strafe && !after.Right && after.Rung == 1,
                          $"{flip} → {after}");
        }

        // --- 6. Ca hai ben het -> nhay -> het thang
        {
            var lad = new EscapeLadder(nav);
            lad.Open(12.0, preferRight: true);
            int n = nav.StrafeRungsMs.Length;
            for (int i = 0; i < n; i++) lad.Next();
            lad.Next();                                   // doi ben
            for (int i = 0; i < n; i++) lad.Next();

            var jump = lad.Next();
            var done = lad.Next();

            fail += Check("cả hai bên hết → nhảy → hết thang",
                          jump.Action == EscapeAction.Jump && done.Action == EscapeAction.Exhausted,
                          $"{jump} → {done}");
        }

        // --- 7. Thoat that -> dong dot -> lan ket sau bat dau lai tu bac 1, ben chon lai duoc
        {
            var lad = new EscapeLadder(nav);
            lad.Open(12.0, preferRight: true);
            lad.Next();
            lad.Next();
            lad.Close();

            bool opened = lad.Open(9.0, preferRight: false);
            var first = lad.Next();

            fail += Check("thoát được → đợt sau bắt đầu lại từ bậc 1",
                          opened && !first.Right && first.Rung == 1 &&
                          first.DurationMs == nav.StrafeRungsMs[0],
                          $"mở lại={opened} {first}");
        }

        return fail;
    }

    private static int Check(string label, bool ok, string detail)
    {
        Console.WriteLine($"    [{label}] {detail} — {(ok ? "ĐẠT" : "HỎNG")}");
        return ok ? 0 : 1;
    }

    // ================================================================ ve anh gia

    /// <summary>Nền sân trạm điện: bê tông sáng có vân, gieo cố định để chạy lại luôn giống nhau.</summary>
    private static Bitmap Yard(ElectricProfile p, bool prompt)
    {
        var bmp = new Bitmap(p.Width, p.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            using (var brush = new LinearGradientBrush(new Rectangle(0, 0, p.Width, p.Height),
                       Color.FromArgb(118, 122, 124), Color.FromArgb(206, 208, 205), 70f))
                g.FillRectangle(brush, 0, 0, p.Width, p.Height);

            var rnd = new Random(20260823);

            // Cot be tong TRANG NANG: dung thu ma cua "muc trang" bat oan, va cua kich co phai loai.
            using (var lit = new SolidBrush(Color.FromArgb(233, 234, 230)))
                for (int i = 0; i < 6; i++)
                    g.FillRectangle(lit, 90 + i * 190, 40, 62, (int)(p.Height * 0.62));

            using (var shade = new SolidBrush(Color.FromArgb(64, 22, 24, 26)))
                for (int i = 0; i < 140; i++)
                    g.FillRectangle(shade, rnd.Next(p.Width), rnd.Next(p.Height),
                                    rnd.Next(20, 140), rnd.Next(4, 22));
        }

        if (prompt) DrawPrompt(bmp, new Point(520, 340), "E", "TƯƠNG TÁC");
        return bmp;
    }

    private static Bitmap Prompt(ElectricProfile p, Point at, string key, string text)
    {
        var bmp = Yard(p, prompt: false);
        DrawPrompt(bmp, at, key, text);
        return bmp;
    }

    /// <summary>
    /// Ô phím TỐI bo góc với chữ trắng bên trong, rồi chữ trắng bên phải — đúng hình dạng đo được
    /// trên ảnh chụp game của người dùng.
    /// </summary>
    private static void DrawPrompt(Bitmap bmp, Point at, string key, string text)
    {
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        const int badge = 40;
        var box = new Rectangle(at.X, at.Y, badge, badge);

        using (var dark = new SolidBrush(Color.FromArgb(58, 60, 64)))
        using (var path = Rounded(box, 8))
            g.FillPath(dark, path);

        using (var keyFont = new Font("Segoe UI", 14F, FontStyle.Bold))
        using (var white = new SolidBrush(Color.White))
        {
            var size = g.MeasureString(key, keyFont);
            g.DrawString(key, keyFont, white,
                at.X + (badge - size.Width) / 2, at.Y + (badge - size.Height) / 2);
        }

        using (var textFont = new Font("Segoe UI", 15F, FontStyle.Bold))
        using (var white = new SolidBrush(Color.White))
        {
            var size = g.MeasureString(text, textFont);
            g.DrawString(text, textFont, white, at.X + badge + 24, at.Y + (badge - size.Height) / 2);
        }
    }

    /// <summary>Khung 3D: mốc vàng dưới đất, logo vàng trên áo, và/hoặc một vật vàng đứng im.</summary>
    private static Bitmap WorldShot(ElectricProfile p, Rectangle marker, Rectangle logo, Rectangle staticBlob)
    {
        var bmp = Yard(p, prompt: false);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Mau lay xap xi theo anh that: moc la vang chanh bao hoa, hoi trong suot tren nen be tong.
        if (!marker.IsEmpty)
            using (var yellow = new SolidBrush(Color.FromArgb(228, 226, 44)))
                g.FillEllipse(yellow, marker);

        if (!logo.IsEmpty)
            using (var yellow = new SolidBrush(Color.FromArgb(240, 214, 38)))
                g.FillRectangle(yellow, logo);

        if (!staticBlob.IsEmpty)
            using (var yellow = new SolidBrush(Color.FromArgb(236, 220, 40)))
                g.FillRectangle(yellow, staticBlob);

        return bmp;
    }

    /// <summary>Khung có minimap ở góc dưới-trái: nền bản đồ xám, chấm vàng tròn, icon sét răng cưa.</summary>
    private static Bitmap MapShot(ElectricProfile p, Point? dot, bool bolt)
    {
        var bmp = Yard(p, prompt: false);
        var mini = p.ScanMinimap().ToRectangle();

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var map = new SolidBrush(Color.FromArgb(228, 230, 226)))
            g.FillRectangle(map, mini);
        using (var road = new SolidBrush(Color.FromArgb(198, 200, 196)))
        {
            g.FillRectangle(road, mini.X + 30, mini.Y, 26, mini.Height);
            g.FillRectangle(road, mini.X, mini.Y + 70, mini.Width, 22);
        }
        using (var green = new SolidBrush(Color.FromArgb(198, 224, 176)))
            g.FillRectangle(green, mini.X + 70, mini.Y + 8, 90, 54);

        int side = (int)Math.Round(13 * p.Sx);
        if (dot is { } d)
            using (var yellow = new SolidBrush(Color.FromArgb(246, 208, 26)))
                g.FillEllipse(yellow, d.X - side / 2, d.Y - side / 2, side, side);

        if (bolt)
        {
            // Tia set: cung mau vang, cung co, nhung rang cua nen do tron thap.
            int bx = mini.X + 40, by = mini.Y + mini.Height - 70;
            var pts = new[]
            {
                new Point(bx + 9, by), new Point(bx + 2, by + 9), new Point(bx + 7, by + 9),
                new Point(bx, by + 19), new Point(bx + 13, by + 8), new Point(bx + 7, by + 8),
                new Point(bx + 14, by)
            };
            using var yellow = new SolidBrush(Color.FromArgb(246, 208, 26));
            g.FillPolygon(yellow, pts);
        }

        return bmp;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ================================================================ anh that

    /// <summary>
    /// Chạy trên ảnh người dùng chụp. Thiếu ảnh nào thì BỎ QUA ảnh đó chứ không tính hỏng — chưa
    /// chụp không phải là lỗi, và bắt buộc đủ 5 tấm thì lần chạy đầu tiên nào cũng đỏ.
    /// </summary>
    private static int RealShots(ElectricConfig cfg, ElectricProfile p)
    {
        int fail = 0;
        fail += ShotFar(cfg, p);
        fail += ShotMarker(cfg, p);
        fail += ShotPrompt(cfg, p);
        fail += ShotPair(cfg, p);
        return fail;
    }

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

    /// <summary>Xa: phải thấy chấm minimap, và KHÔNG được khoá mốc 3D nào.</summary>
    private static int ShotFar(ElectricConfig cfg, ElectricProfile p)
    {
        using var shot = Load(p, "nav-far", out string why);
        if (shot is null) { Console.WriteLine($"  [nav-far] bỏ qua: {why}"); return 0; }

        int fail = 0;
        using (var reader = MinimapReader.ForBitmap(cfg, p, shot, out string problem))
        {
            if (reader is null) { Console.WriteLine($"  [nav-far] minimap KHÔNG DÒ ĐƯỢC: {problem}"); fail++; }
            else
            {
                var fix = reader.Read(1000);
                foreach (var c in reader.LastCandidates) Console.WriteLine($"      {c}");
                Console.WriteLine($"  [nav-far] {fix} — {(fix.Found ? "ĐẠT" : "HỎNG (phải thấy chấm)")}");
                if (!fix.Found) fail++;
            }
        }

        using (var marker = MarkerReader.ForBitmap(cfg, p, shot, out string problem))
        {
            if (marker is null) { Console.WriteLine($"  [nav-far] mốc KHÔNG DÒ ĐƯỢC: {problem}"); return fail + 1; }

            var cands = marker.Scan().Where(c => c.Ok).ToList();
            foreach (var c in marker.LastCandidates) Console.WriteLine($"      {c}");
            Console.WriteLine($"  [nav-far] ứng viên mốc còn lại: {cands.Count} " +
                              (cands.Count == 0 ? "— ĐẠT" : "— xem kỹ, ảnh này lẽ ra không có mốc"));
        }

        return fail;
    }

    /// <summary>Gần: phải có ứng viên mốc, và không ứng viên nào trùng bóng nhân vật.</summary>
    private static int ShotMarker(ElectricConfig cfg, ElectricProfile p)
    {
        using var shot = Load(p, "nav-marker", out string why);
        if (shot is null) { Console.WriteLine($"  [nav-marker] bỏ qua: {why}"); return 0; }

        using var reader = MarkerReader.ForBitmap(cfg, p, shot, out string problem);
        if (reader is null) { Console.WriteLine($"  [nav-marker] KHÔNG DÒ ĐƯỢC: {problem}"); return 1; }

        var all = reader.Scan();
        foreach (var c in all) Console.WriteLine($"      {c}");

        int ok = all.Count(c => c.Ok);
        int inSil = all.Count(c => c.InSilhouette);
        Console.WriteLine($"  [nav-marker] {ok} ứng viên hợp lệ, {inSil} khối trùng bóng nhân vật " +
                          "(logo áo — đã bị hộp loại trừ chặn)");
        Console.WriteLine($"  [nav-marker] {(ok > 0 ? "ĐẠT" : "HỎNG (không thấy mốc nào)")}");
        return ok > 0 ? 0 : 1;
    }

    private static int ShotPrompt(ElectricConfig cfg, ElectricProfile p)
    {
        using var shot = Load(p, "nav-prompt", out string why);
        if (shot is null) { Console.WriteLine($"  [nav-prompt] bỏ qua: {why}"); return 0; }

        using var reader = PromptReader.ForBitmap(cfg, p, shot, out string problem);
        if (reader is null) { Console.WriteLine($"  [nav-prompt] KHÔNG DÒ ĐƯỢC: {problem}"); return 1; }

        var hit = reader.Read();
        foreach (string r in hit.Rows) Console.WriteLine($"      {r}");
        Console.WriteLine($"  [nav-prompt] {hit} — {(hit.Visible ? "ĐẠT" : "HỎNG (phải thấy prompt)")}");
        return hit.Visible ? 0 : 1;
    }

    /// <summary>
    /// Cặp ảnh chụp trước/sau khi xoay camera lúc ĐỨNG YÊN. Chốt hai thứ mà không ảnh đơn nào nói
    /// được: minimap có xoay theo camera không, và mốc 3D có trôi ngang không.
    /// </summary>
    private static int ShotPair(ElectricConfig cfg, ElectricProfile p)
    {
        using var a = Load(p, "nav-pair-a", out string whyA);
        using var b = Load(p, "nav-pair-b", out string whyB);
        if (a is null || b is null)
        {
            Console.WriteLine($"  [nav-pair] bỏ qua: {whyA ?? whyB}");
            return 0;
        }

        double? bearingA = null, bearingB = null;
        using (var ra = MinimapReader.ForBitmap(cfg, p, a, out _))
        using (var rb = MinimapReader.ForBitmap(cfg, p, b, out _))
        {
            if (ra is not null && rb is not null)
            {
                var fa = ra.Read(1000);
                var fb = rb.Read(1000);
                if (fa.Found) bearingA = fa.BearingDeg;
                if (fb.Found) bearingB = fb.BearingDeg;
            }
        }

        if (bearingA is null || bearingB is null)
        {
            Console.WriteLine("  [nav-pair] không thấy chấm ở cả hai ảnh — không kết luận được");
        }
        else
        {
            double d = Wrap(bearingB.Value - bearingA.Value);
            Console.WriteLine($"  [nav-pair] góc chấm: {bearingA:F1}° → {bearingB:F1}°  (Δ={d:F1}°)");
            Console.WriteLine(Math.Abs(d) >= cfg.Nav.CalibrateMinDeltaDeg
                ? "  [nav-pair] minimap XOAY THEO CAMERA → lái thẳng theo góc chấm"
                : "  [nav-pair] góc gần như không đổi → minimap KHÔNG theo camera, NavBot sẽ dùng chế độ dò dốc");
        }

        double? xa = null, xb = null;
        using (var ma = MarkerReader.ForBitmap(cfg, p, a, out _))
        using (var mb = MarkerReader.ForBitmap(cfg, p, b, out _))
        {
            var ca = ma?.Scan().Where(c => c.Ok).OrderByDescending(c => c.AreaRef).FirstOrDefault();
            var cb = mb?.Scan().Where(c => c.Ok).OrderByDescending(c => c.AreaRef).FirstOrDefault();
            if (ca is not null) xa = ca.Cx;
            if (cb is not null) xb = cb.Cx;
        }

        if (xa is not null && xb is not null)
            Console.WriteLine($"  [nav-pair] mốc trôi ngang {xb - xa:F0}px — " +
                              (Math.Abs(xb.Value - xa.Value) >= cfg.Nav.ParallaxMinPxRef * p.Sx
                                  ? "đủ để kiểm thị sai"
                                  : "KHÔNG đủ, hạ ParallaxMinPxRef hoặc xoay nhiều hơn khi chụp"));
        else
            Console.WriteLine("  [nav-pair] không có mốc ở cả hai ảnh — phần thị sai bỏ qua");

        return 0;
    }

    private static double Wrap(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }
}
