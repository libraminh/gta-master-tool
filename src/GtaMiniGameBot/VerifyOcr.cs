using System.Drawing.Imaging;
using System.Drawing.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Tự kiểm tra đường ống đọc chữ số mà KHÔNG cần vào game: tự vẽ chữ ra ảnh, dạy bộ mẫu từ
/// ảnh đó, rồi đọc lại một chuỗi khác. Không chứng minh được độ chính xác trên phông thật của
/// game — phông đó chỉ có ảnh chụp thật mới có — nhưng bắt được mọi lỗi lắp ráp: cắt lệch một
/// pixel, tách nhầm dấu chấm, cổng kiểm tra bắt oan.
///
/// Chạy: GtaMiniGameBot.exe --verify-ocr
/// </summary>
internal static class VerifyOcr
{
    private const string Key = "selftest-ocr";
    private const int Pad = 6;

    public static int Run(string[] args)
    {
        Console.WriteLine("== tự kiểm tra đọc chữ số ==");

        string dir = FishingConfig.DigitDir(Key);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        var cfg = new FishingConfig();
        cfg.Normalize();

        int fail = 0;
        fail += Teach(cfg) ? 0 : 1;

        var atlas = DigitAtlas.Load(Key);
        Console.WriteLine($"bộ mẫu: {atlas.Count} mẫu, thiếu “{atlas.MissingText()}”");
        if (atlas.MissingText().Length > 0)
        {
            Console.WriteLine("  KHÔNG dạy đủ 12 ký tự — dừng");
            return 1;
        }

        fail += Expect(cfg, atlas, "27.4/30 KG", 30, ok: true, value: 27.4) ? 0 : 1;
        fail += Expect(cfg, atlas, "9.7/60 KG", 60, ok: true, value: 9.7) ? 0 : 1;
        fail += Expect(cfg, atlas, "0.0/30 KG", 30, ok: true, value: 0.0) ? 0 : 1;
        fail += Expect(cfg, atlas, "29.9/30 KG", 30, ok: true, value: 29.9) ? 0 : 1;
        // "1" hep hon han cac chu so khac — ca de lam co bo ghep theo be rong nhat.
        fail += Expect(cfg, atlas, "11.1/30 KG", 30, ok: true, value: 11.1) ? 0 : 1;
        fail += Expect(cfg, atlas, "8.8/30 KG", 30, ok: true, value: 8.8) ? 0 : 1;
        fail += Expect(cfg, atlas, "59.9/60 KG", 60, ok: true, value: 59.9) ? 0 : 1;

        // Cac ca PHAI bi tu choi.
        fail += Expect(cfg, atlas, "274/30 KG", 30, ok: false) ? 0 : 1;    // mat dau cham
        fail += Expect(cfg, atlas, "27.4/50 KG", 30, ok: false) ? 0 : 1;   // mau so khac cau hinh
        fail += Expect(cfg, atlas, "", 30, ok: false) ? 0 : 1;             // o trong
        // Chu so lac sau mau so, cach boi mot ky tu khong doc duoc. Khop-phan-dau se nuot
        // truot ca nay va tra ve 27.4 nhu that — day dung la ca da lam hong lan doc that.
        fail += Expect(cfg, atlas, "27.4/30K5", 30, ok: false) ? 0 : 1;

        try { Directory.Delete(Path.Combine(AppPaths.Root, "fishing", Key), recursive: true); } catch { }

        Console.WriteLine(fail == 0 ? "TẤT CẢ ĐẠT (ảnh tự vẽ)" : $"HỎNG {fail} ca (ảnh tự vẽ)");

        RealShots();
        return fail == 0 ? 0 : 2;
    }

    /// <summary>
    /// Đọc thử trên ảnh chụp THẬT nếu đã có. Không tính vào đạt/hỏng — bộ mẫu của người dùng có
    /// thể còn thiếu chữ số — nhưng đây mới là dữ liệu duy nhất có phông thật của game, nên là
    /// chỗ nhanh nhất để đối chiếu sau mỗi lần sửa bộ đọc.
    /// </summary>
    private static void RealShots()
    {
        var cfg = FishingConfig.Load();
        if (cfg.Profiles.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine("== đọc thử trên ảnh chụp thật ==");

        foreach (var (key, p) in cfg.Profiles)
        {
            if (p is null) continue;
            var atlas = DigitAtlas.Load(key);
            if (atlas.Count == 0) { Console.WriteLine($"{key}: chưa có mẫu chữ số"); continue; }

            string missing = atlas.MissingText();
            Console.WriteLine($"{key}: {atlas.Count} mẫu" +
                              (missing.Length > 0 ? $", còn thiếu {missing}" : ", đủ 12 ký tự"));

            One(key, "bag", "ba lô", p.BagWeight, cfg.BagCapKg, atlas, cfg);
            One(key, "trunk", "cốp  ", p.TrunkWeight, cfg.TrunkCapKg, atlas, cfg);
        }
    }

    private static void One(string key, string shot, string label, FishingRect roi,
                            double cap, DigitAtlas atlas, FishingConfig cfg)
    {
        if (!roi.IsSet) { Console.WriteLine($"  {label}: chưa khoanh"); return; }

        using var still = StillPicker.Load(FishingConfig.ShotPath(key, shot));
        if (still is null) { Console.WriteLine($"  {label}: chưa có ảnh"); return; }

        var r = WeightReader.ReadStill(still, roi, atlas, cfg, cap);
        Console.WriteLine($"  {label}: {r}");
        Console.WriteLine($"      {r.Trace}");
    }

    /// <summary>Vẽ đủ 12 ký tự một hàng rồi lưu từng cái làm mẫu.</summary>
    private static bool Teach(FishingConfig cfg)
    {
        const string all = "0123456789./";
        using var bmp = Render(all);
        var gray = GlyphSeg.GrayOf(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), out int w, out int h);
        var bin = GlyphSeg.Binarize(gray, cfg.DigitInkMinGray, out int thr);
        var boxes = GlyphSeg.Segment(bin, w, h, cfg.DigitMinGlyphW, cfg.DigitMinGlyphInk, cfg.DigitMergeGapPx);

        Console.WriteLine($"dạy “{all}”: ngưỡng={thr} tách ra {boxes.Count} khối (cần {all.Length})");
        if (boxes.Count != all.Length)
        {
            foreach (var b in boxes) Console.WriteLine($"   khối {b.Box.Width}×{b.Box.Height} @ {b.Box.X}");
            return false;
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i].Box;
            var crop = GlyphSeg.Crop(gray, w, h, b.X, b.Y, b.Width, b.Height);
            DigitAtlas.SaveGlyph(Key, all[i], crop, b.Width, b.Height, overwrite: true);
        }
        return true;
    }

    private static bool Expect(FishingConfig cfg, DigitAtlas atlas, string text,
                               double cap, bool ok, double value = 0)
    {
        using var bmp = Render(text);
        var roi = new FishingRect { X = 0, Y = 0, W = bmp.Width, H = bmp.Height };
        var r = WeightReader.ReadStill(bmp, roi, atlas, cfg, cap);

        bool pass = r.Ok == ok && (!ok || Math.Abs(r.Value - value) < 0.05);
        Console.WriteLine($"{(pass ? "  ok  " : "  SAI ")} “{text}” cap={cap:0} → {r}");
        if (!pass) Console.WriteLine("        " + r.Trace);
        return pass;
    }

    /// <summary>
    /// Chữ sáng trên nền tối như trong game. Cố ý tắt khử răng cưa: mục tiêu là kiểm tra phần
    /// lắp ráp, không phải giả lập phông của game — nhiễu khử răng cưa chỉ làm test bập bênh.
    /// </summary>
    private static Bitmap Render(string text)
    {
        using var font = new Font("Consolas", 15F, FontStyle.Bold);

        Size size;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
        {
            var m = g.MeasureString(text.Length == 0 ? " " : text, font);
            size = new Size((int)Math.Ceiling(m.Width) + Pad * 2, (int)Math.Ceiling(m.Height) + Pad * 2);
        }

        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(18, 24, 26));
            if (text.Length > 0)
            {
                g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                using var brush = new SolidBrush(Color.FromArgb(240, 240, 235));
                g.DrawString(text, font, brush, Pad, Pad);
            }
        }
        return bmp;
    }
}
