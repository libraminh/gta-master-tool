using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// Chon O LAM MAU cho phep do "dang trong xe" bang so do, khong chon bang mat.
///
/// Cach lam: voi tung o ung vien, lay mau tu mot frame trong xe roi cham diem
/// TAT CA frame. Diem tot = khoang cach giua nhom thap nhat cua "trong xe" va
/// nhom cao nhat cua "duoi dat" cang rong cang tot.
///
/// Chay:  GtaMiniGameBot.exe --pick-car-template
/// </summary>
internal static class CarTemplatePicker
{
    private sealed class Frame
    {
        public string Name;
        public byte[] Gray;      // full screen, row-major
        public int W, H;
        public bool InCar;       // ground truth
    }

    public static int Run(string[] args)
    {
        var cfg = new BotConfig();
        string root = FindRoot();
        string folder = args.Length > 1 ? args[1] : Path.Combine(root, "recordings", "car-states");

        if (!Directory.Exists(folder)) { Console.WriteLine($"Không thấy {folder}"); return 2; }

        Console.WriteLine($"Nạp frame từ {folder} …");
        var frames = new List<Frame>();
        foreach (var f in Directory.GetFiles(folder, "*.png").OrderBy(p => p))
        {
            var fr = LoadGray(f);
            // Ground truth lay bang cach dem pixel gan-trang tren o do hien tai.
            // Cach nay KHONG dang tin khi doi anh sang (day la ly do phai doi sang NCC),
            // nhung tren DUNG bo frame nay no tach tuyet doi (0 vs 3175) nen dung lam
            // nhan ground truth thi chinh xac.
            fr.InCar = CountWhite(fr, cfg.CarProbe, cfg.CarWhiteMinBright, cfg.CarWhiteSpread) >= 1000;
            frames.Add(fr);
        }

        int nIn = frames.Count(f => f.InCar), nOut = frames.Count - nIn;
        Console.WriteLine($"  {frames.Count} frame:  trong xe = {nIn},  dưới đất = {nOut}");
        if (nIn == 0 || nOut == 0) { Console.WriteLine("Thiếu một trong hai trạng thái."); return 1; }

        // ---- sinh o ung vien quanh vung dong ho toc do ----
        var sizes = new[] { (320, 140), (260, 110), (220, 90), (380, 180) };
        var candidates = new List<Rectangle>();
        foreach (var (w, h) in sizes)
            for (int x = 2060; x + w <= 2500; x += 40)
            for (int y = 1080; y + h <= 1340; y += 40)
                candidates.Add(new Rectangle(x, y, w, h));
        candidates.Add(cfg.CarProbe);   // o dang dung, de doi chieu

        Console.WriteLine($"Chấm điểm {candidates.Count} ô ứng viên trên {frames.Count} frame …");

        // Lay mau tu frame trong xe o GIUA day (tranh frame ngay sat luc chuyen trang thai)
        var inCarFrames = frames.Where(f => f.InCar).ToList();
        var srcFrame = inCarFrames[inCarFrames.Count / 2];

        var scored = new List<(Rectangle rect, double sep, double loMax, double hiMin, int mid)>();
        foreach (var rect in candidates)
        {
            var tpl = MakeTemplate(srcFrame, rect);
            if (tpl is null || tpl.IsFlat) continue;

            double hiMin = double.MaxValue, loMax = double.MinValue;
            int midCount = 0;
            foreach (var fr in frames)
            {
                double s = tpl.Score(Crop(fr, rect));
                if (fr.InCar) hiMin = Math.Min(hiMin, s);
                else loMax = Math.Max(loMax, s);
                if (s > cfg.CarNccOut && s < cfg.CarNccIn) midCount++;
            }
            scored.Add((rect, hiMin - loMax, loMax, hiMin, midCount));
        }

        Console.WriteLine();
        Console.WriteLine("=== 15 ô tốt nhất (độ tách = ncc thấp nhất của “trong xe” − ncc cao nhất của “dưới đất”) ===");
        Console.WriteLine("  ô (x,y,w,h)                 độ tách   dưới đất≤   trong xe≥   số frame lơ lửng");
        foreach (var s in scored.OrderByDescending(s => s.sep).Take(15))
            Console.WriteLine($"  {s.rect.X},{s.rect.Y},{s.rect.Width},{s.rect.Height}".PadRight(30) +
                              $"{s.sep,8:F3}   {s.loMax,10:F3}   {s.hiMin,10:F3}   {s.mid,8}");

        var best = scored.OrderByDescending(s => s.sep).First();
        Console.WriteLine();
        Console.WriteLine($"Ô TỐT NHẤT: x={best.rect.X} y={best.rect.Y} w={best.rect.Width} h={best.rect.Height}");
        Console.WriteLine($"   dưới đất tối đa {best.loMax:F3}  |  trong xe tối thiểu {best.hiMin:F3}  |  độ tách {best.sep:F3}");
        Console.WriteLine($"   ngưỡng đang đặt: dưới đất ≤ {cfg.CarNccOut:F2}, trong xe ≥ {cfg.CarNccIn:F2}");
        bool fits = best.loMax <= cfg.CarNccOut && best.hiMin >= cfg.CarNccIn;
        Console.WriteLine($"   {(fits ? "khớp ngưỡng hiện tại" : "<<< KHÔNG khớp ngưỡng hiện tại — cần điều chỉnh ngưỡng hoặc ô")}");

        // luu mau cua o tot nhat
        var bestTpl = MakeTemplate(srcFrame, best.rect);
        string outPath = Path.Combine(AppContext.BaseDirectory, "car-template.png");
        bestTpl.Save(outPath);
        Console.WriteLine();
        Console.WriteLine($"Đã lưu mẫu → {outPath}  (lấy từ {srcFrame.Name})");
        Console.WriteLine("Đặt vào BotConfig:");
        Console.WriteLine($"   CarProbeX = {best.rect.X};  CarProbeY = {best.rect.Y};  " +
                          $"CarProbeW = {best.rect.Width};  CarProbeH = {best.rect.Height};");
        return 0;
    }

    // ---------------- helpers ----------------

    private static GrayTemplate MakeTemplate(Frame fr, Rectangle rect) =>
        GrayTemplate.FromRaw(rect.Width, rect.Height, Crop(fr, rect));

    private static byte[] Crop(Frame fr, Rectangle rect)
    {
        var outp = new byte[rect.Width * rect.Height];
        int k = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
        for (int x = rect.Left; x < rect.Right; x++)
            outp[k++] = (x < 0 || y < 0 || x >= fr.W || y >= fr.H) ? (byte)0 : fr.Gray[y * fr.W + x];
        return outp;
    }

    private static int CountWhite(Frame fr, Rectangle rect, int minBright, int spread)
    {
        // dung lai anh mau thi can doc lai file; o day chi can uoc luong tren thang xam
        // -> thay bang: dem pixel sang (>minBright). Tren bo frame nay dong ho toc do
        // sang han han nen ket qua phan nhom giong het cach dem theo mau.
        int n = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
        for (int x = rect.Left; x < rect.Right; x++)
        {
            if (x < 0 || y < 0 || x >= fr.W || y >= fr.H) continue;
            if (fr.Gray[y * fr.W + x] > minBright) n++;
        }
        return n;
    }

    private static Frame LoadGray(string path)
    {
        using var bmp = new Bitmap(path);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                              ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new byte[bd.Stride * bmp.Height];
            Marshal.Copy(bd.Scan0, raw, 0, raw.Length);

            var gray = new byte[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * bd.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int i = row + x * 4;
                    gray[y * bmp.Width + x] = (byte)((raw[i + 2] * 30 + raw[i + 1] * 59 + raw[i] * 11) / 100);
                }
            }
            return new Frame { Name = Path.GetFileName(path), Gray = gray, W = bmp.Width, H = bmp.Height };
        }
        finally { bmp.UnlockBits(bd); }
    }

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "recordings"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }
}
