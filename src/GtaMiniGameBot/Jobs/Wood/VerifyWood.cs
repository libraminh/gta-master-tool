using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Kiểm tra bộ dò prompt thợ mộc, hai phần:
///   1. Tự vẽ prompt ra ảnh rồi dò lại — chạy được ngay, không cần vào game.
///   2. Dò trên chính ảnh tĩnh người dùng đã chụp lúc hiệu chuẩn.
///
/// Đây là hàng rào hồi quy cho mọi lần chỉnh ngưỡng: ảnh "sẵn sàng" PHẢI ra có prompt, còn ảnh
/// "đang chặt" PHẢI ra không — đó chính là điều khiến một mẫu duy nhất là đủ. Cùng vai trò
/// <see cref="VerifyOcr"/> với phần đọc chữ số.
///
/// Chạy: GtaMiniGameBot.exe --verify-wood
/// </summary>
internal static class VerifyWood
{
    public static int Run(string[] args)
    {
        Console.WriteLine("== kiểm tra dò prompt thợ mộc ==");

        int fail = SelfTest();
        int skipped = 0;

        var cfg = WoodConfig.Load();
        if (cfg.Profiles.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("chưa có profile nào trong wood.json — phần kiểm tra trên ảnh thật bỏ qua.");
            return fail == 0 ? 0 : 1;
        }

        foreach (var (key, profile) in cfg.Profiles.OrderBy(kv => kv.Key))
        {
            Console.WriteLine();
            Console.WriteLine($"-- {key} --");
            Console.WriteLine("  " + profile.DescribeGaps());

            if (!profile.IsCalibrated)
            {
                // Chua khoanh KHONG phai hong: mo app mot lan la sinh ra profile rong cho man
                // hinh dang dung. Tinh no la loi thi phep kiem tra bao dong ngay tu dau.
                Console.WriteLine("  BỎ QUA: chưa khoanh đủ");
                skipped++;
                continue;
            }

            var band = profile.ScanBand();
            Console.WriteLine($"  vùng quét {band.W}×{band.H} @ {band.X},{band.Y}" +
                              (profile.Band.IsSet ? "" : "  (mặc định, chưa khoanh)"));

            fail += Expect(cfg, profile, "ready", want: true) ? 0 : 1;

            // Anh "dang chat" la tuy chon: nguoi dung khong bat buoc phai chup no nua. Co thi day
            // la phep thu quan trong nhat — no chung minh mot mau la du.
            if (File.Exists(WoodConfig.ShotPath(profile.Key, "busy")))
                fail += Expect(cfg, profile, "busy", want: false) ? 0 : 1;
        }

        Console.WriteLine();
        if (skipped > 0)
            Console.WriteLine($"{skipped} profile chưa khoanh — mở tab Thợ mộc để khoanh rồi chạy lại.");
        Console.WriteLine(fail == 0 ? "TẤT CẢ ĐẠT" : $"HỎNG {fail} ca");
        return fail == 0 ? 0 : 1;
    }

    // ================================================================ tu kiem tra

    /// <summary>Độ phân giải giả, cố tình lẻ để không đụng profile thật nào của người dùng.</summary>
    private const int SelfW = 1287;
    private const int SelfH = 723;

    /// <summary>
    /// Tự vẽ prompt ra ảnh, hiệu chuẩn từ ảnh đó, rồi dò lại trên ảnh vẽ ở CHỖ KHÁC.
    ///
    /// Không chứng minh được độ chính xác trên phông thật của game — phông đó chỉ ảnh chụp thật
    /// mới có — nhưng bắt được mọi lỗi lắp ráp: tách nhầm ô phím, neo lệch, lọc cỡ bắt oan, và
    /// quan trọng nhất là hai ca sinh ra cả tính năng này: prompt TRÔI sang chỗ khác, và chữ
    /// "ĐANG KHAI THÁC" CHỨA "KHAI THÁC" mà vẫn không được khớp.
    /// </summary>
    private static int SelfTest()
    {
        Console.WriteLine();
        Console.WriteLine("-- tự kiểm tra (ảnh tự vẽ) --");

        string dir = WoodConfig.ProfileDir($"{SelfW}x{SelfH}");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }

        var cfg = new WoodConfig();
        cfg.Normalize();

        var profile = new WoodProfile { Device = "selftest", Width = SelfW, Height = SelfH };
        profile.Normalize();

        int fail = 0;
        try
        {
            if (!Calibrate(cfg, profile, new Point(420, 300)))
            {
                Console.WriteLine("  KHÔNG hiệu chuẩn được từ ảnh tự vẽ — dừng phần tự kiểm tra");
                return fail + 1;
            }
            Console.WriteLine($"  chữ cao {profile.TextH}px, ngưỡng khe {profile.GapSplit}px");

            // Prompt TROI sang cho khac — day la ca ma o co dinh se hut.
            fail += Probe(cfg, profile, "sẵn sàng ở chỗ khác",
                          Draw(new Point(700, 220), "E", "KHAI THÁC"), true) ? 0 : 1;

            // Ca quan trong nhat: "DANG KHAI THAC" CHUA "KHAI THAC" nhung nhom chu bat dau tu
            // "DANG" nen mau neo trai khong khop. Day la thu khien mot mau la du.
            fail += Probe(cfg, profile, "đang chặt (phải KHÔNG khớp)",
                          Draw(new Point(330, 300), "40", "ĐANG KHAI THÁC", ring: true), false) ? 0 : 1;

            // Prompt tuong tac KHAC cua server.
            fail += Probe(cfg, profile, "prompt khác (thang máy)",
                          Draw(new Point(640, 360), "E", "DÙNG THANG MÁY"), false) ? 0 : 1;

            fail += Probe(cfg, profile, "không có prompt", Background(dark: true), false) ? 0 : 1;

            // Ban ngay: troi/da/than xe trang cung thanh muc. Loc theo co chu la thu chan chung.
            fail += Probe(cfg, profile, "nền sáng, không prompt", Background(dark: false), false) ? 0 : 1;
            fail += Probe(cfg, profile, "nền sáng, có prompt",
                          Draw(new Point(500, 250), "E", "KHAI THÁC", bright: true), true) ? 0 : 1;
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "  tự kiểm tra: ĐẠT" : $"  tự kiểm tra: HỎNG {fail} ca");
        return fail;
    }

    /// <summary>Đi đúng đường <see cref="WoodSetupForm"/> đi: cắt ô → tách chữ → lưu mẫu.</summary>
    private static bool Calibrate(WoodConfig cfg, WoodProfile profile, Point at)
    {
        using var shot = Draw(at, "E", "KHAI THÁC");

        // Khoanh rong tay, du ca le nen — dung nhu nguoi dung keo chuot.
        var box = new Rectangle(at.X - 10, at.Y - 12, 420, 80);
        using var crop = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(shot, new Rectangle(0, 0, box.Width, box.Height), box, GraphicsUnit.Pixel);

        var parts = WoodLocator.ExtractText(crop, cfg, out string problem);
        if (parts is null) { Console.WriteLine($"  tách chữ HỎNG: {problem}"); return false; }

        var tpl = GrayTemplate.FromBitmapCrop(crop, parts.Text);
        if (tpl.IsFlat) { Console.WriteLine("  mẫu chữ phẳng"); return false; }
        tpl.Save(WoodConfig.ReadyTemplatePath(profile.Key));

        profile.Ready = FishingRect.FromRelative(box);
        profile.TextH = parts.Text.Height;
        profile.GapSplit = parts.GapSplit;

        Console.WriteLine("  hiệu chuẩn: " + parts.Note);
        return true;
    }

    private static bool Probe(WoodConfig cfg, WoodProfile profile, string label, Bitmap shot, bool want)
    {
        using (shot)
        {
            bool got = WoodProbe.Detect(cfg, profile, shot, out string report, out string problem);
            if (problem is not null)
            {
                Console.WriteLine($"  [{label}] KHÔNG DÒ ĐƯỢC: {problem}");
                return false;
            }

            Console.Write(report);
            bool ok = got == want;
            Console.WriteLine($"  [{label}] mong {(want ? "khớp" : "không khớp")}, " +
                              $"ra {(got ? "khớp" : "không khớp")} — {(ok ? "ĐẠT" : "HỎNG")}");
            return ok;
        }
    }

    // ---------------------------------------------------------------- ve anh gia

    /// <summary>
    /// Nền có vân: mẫu NCC cắt trên nền phẳng tuyệt đối sẽ bị chặn bởi <c>IsFlat</c>, mà nền
    /// phẳng cũng làm phép thử dễ hơn thực tế một cách vô ích.
    /// </summary>
    private static Bitmap Background(bool dark)
    {
        var bmp = new Bitmap(SelfW, SelfH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        // Ban ngay khong co nghia la CA MAN sang hon nguong muc — do tren anh that thi dat/da nam
        // trong khoang 90..160, chi vat the trang (than xe, troi) moi vuot 200. Ve nen sang bang
        // mot dai 168..228 la dung ca bat kha thi (chu trang tren nen trang), khong phai ca thuc.
        var (c1, c2) = dark
            ? (Color.FromArgb(38, 52, 30), Color.FromArgb(96, 84, 62))
            : (Color.FromArgb(92, 96, 88), Color.FromArgb(158, 152, 140));
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, SelfW, SelfH), c1, c2, 25f))
            g.FillRectangle(brush, 0, 0, SelfW, SelfH);

        // Vai vet canh vat cho co cau truc, gieo co dinh de chay lai luon ra cung ket qua.
        var rnd = new Random(20260818);
        using (var blob = new SolidBrush(Color.FromArgb(70, 20, 24, 16)))
            for (int i = 0; i < 260; i++)
                g.FillEllipse(blob, rnd.Next(SelfW), rnd.Next(SelfH), rnd.Next(6, 40), rnd.Next(6, 40));

        // Mang TRANG lon (than xe / troi) — thu ma RowMaxFrac phai chan. Dat o hang KHAC hang cua
        // prompt: nam de len dung hang cua prompt thi khong bo doc nao cuu duoc, va doi hoi the la
        // doi hoi thu bat kha thi.
        if (!dark)
            using (var white = new SolidBrush(Color.FromArgb(246, 247, 250)))
                g.FillRectangle(white, 300, 196, 430, 40);

        return bmp;
    }

    /// <summary>
    /// Vẽ một prompt tương tác: ô phím ĐEN với chữ trắng bên trong, rồi chữ trắng bên phải.
    /// Ô phím đen chứ không trắng — đó là hình dạng thật, đo trên ảnh chụp game.
    /// </summary>
    private static Bitmap Draw(Point at, string key, string text, bool ring = false, bool bright = false)
    {
        var bmp = Background(dark: !bright);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        const int badge = 44;
        var box = new Rectangle(at.X, at.Y, badge, badge);

        using (var dark = new SolidBrush(Color.FromArgb(34, 36, 40)))
        using (var path = Rounded(box, 8))
            g.FillPath(dark, path);

        // Vong tien trinh chay quanh vien o phim — thu lam cum muc cua o phim phinh ra theo phan
        // tram, va la ly do khong duoc neo vao no. Ve sat vien: do tren anh that, vong chi lam cum
        // dau rong them 4 px (296 -> 300), ve day hon la dung tu bay minh mot ca khong co thuc.
        if (ring)
            using (var pen = new Pen(Color.White, 2.5f))
                g.DrawArc(pen, Rectangle.Inflate(box, 1, 1), -90, 145);

        using (var keyFont = new Font("Segoe UI", 15F, FontStyle.Bold))
        using (var white = new SolidBrush(Color.White))
        {
            var size = g.MeasureString(key, keyFont);
            g.DrawString(key, keyFont, white,
                at.X + (badge - size.Width) / 2, at.Y + (badge - size.Height) / 2);
        }

        using (var textFont = new Font("Segoe UI", 17F, FontStyle.Bold))
        using (var white = new SolidBrush(Color.White))
        {
            var size = g.MeasureString(text, textFont);
            g.DrawString(text, textFont, white, at.X + badge + 26, at.Y + (badge - size.Height) / 2);
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

    /// <summary>Dò trên ảnh <paramref name="shot"/> và đòi kết quả đúng bằng <paramref name="want"/>.</summary>
    private static bool Expect(WoodConfig cfg, WoodProfile profile, string shot, bool want)
    {
        string path = WoodConfig.ShotPath(profile.Key, shot);
        using var still = StillPicker.Load(path);
        if (still is null)
        {
            Console.WriteLine($"  [{shot}] KHÔNG CÓ ẢNH: {path}");
            return false;
        }
        if (still.Width != profile.Width || still.Height != profile.Height)
        {
            Console.WriteLine($"  [{shot}] ảnh {still.Width}×{still.Height} lệch profile " +
                              $"{profile.Width}×{profile.Height} — chụp lại");
            return false;
        }

        bool got = WoodProbe.Detect(cfg, profile, still, out string report, out string problem);
        if (problem is not null)
        {
            Console.WriteLine($"  [{shot}] KHÔNG DÒ ĐƯỢC: {problem}");
            return false;
        }

        Console.Write(report);
        bool ok = got == want;
        Console.WriteLine($"  [{shot}] mong {(want ? "khớp" : "không khớp")}, " +
                          $"ra {(got ? "khớp" : "không khớp")} — {(ok ? "ĐẠT" : "HỎNG")}");
        return ok;
    }
}
