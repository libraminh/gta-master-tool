using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// MOC 1 - kiem chung offline tren ban ghi demo, khong chay game, khong gui input.
/// Chay:  GtaMiniGameBot.exe --verify [duong-dan-thu-muc-recordings]
/// </summary>
internal static class Verify
{
    public static int Run(string[] args)
    {
        string dir = args.Length > 1
            ? args[1]
            : Path.Combine(FindRepoRoot(), "recordings", "demo-01");

        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"Không thấy thư mục: {dir}");
            return 2;
        }

        var cfg = new BotConfig();
        int fails = 0;

        Console.WriteLine($"Thư mục   : {dir}");
        Console.WriteLine($"Cấu hình  : thân thanh y = {cfg.BarYTop}…{cfg.BarYBottom}, " +
                          $"ngưỡng đầy ≥ {cfg.FullThreshold}, coi là đã reset < {cfg.ResetThreshold}");
        Console.WriteLine();

        // ---------------- KIEM CHUNG A : tin hieu "panel dang mo" ----------------
        Console.WriteLine("=== A. Tín hiệu “panel đang mở” = thấy được 4 thanh cách đều ===");
        Console.WriteLine($"    ngưỡng: cả 4 thanh phải nổi lên ≥ {cfg.PanelBarProminenceMin} so với median vùng");
        Console.WriteLine("    (cách cũ — đếm pixel trắng chuỗi “/50” — đã BỎ: vùng đó chồng với minimap,");
        Console.WriteLine("     minimap vẽ vạch kẻ đường màu trắng thì đọc ra 544 và bot tưởng panel còn mở)");
        Console.WriteLine();
        Console.WriteLine("    frame           nổi lên từng thanh                 min      kết luận");

        var expectOpen = new Dictionary<string, bool>
        {
            ["095.png"] = true, ["099.png"] = true, ["103.png"] = true,
            ["107.png"] = true, ["111.png"] = false, ["112.png"] = false,
        };

        foreach (var (file, shouldBeOpen) in expectOpen)
        {
            string p = Path.Combine(dir, file);
            if (!File.Exists(p)) { Console.WriteLine($"    {file}    (không có file)"); continue; }

            var proms = Prominences(p, cfg);
            double min = proms.Min();
            bool isOpen = min >= cfg.PanelBarProminenceMin;
            bool pass = isOpen == shouldBeOpen;
            if (!pass) fails++;
            Console.WriteLine($"    {file}   [{string.Join(", ", proms.Select(v => $"{v,6:F1}"))}] {min,7:F1}   " +
                              $"{(isOpen ? "MỞ  " : "ĐÓNG")}  {(pass ? "đúng" : "<<< SAI")}");
        }

        // Phep thu manh nhat: 34 frame dung ngay o gian khoan, panel DONG toan bo,
        // minimap dang hien duong - chinh hoan canh da pha phep do cu.
        string carDirA = Path.Combine(Path.GetDirectoryName(dir.TrimEnd('\\', '/')) ?? "", "car-states");
        if (Directory.Exists(carDirA))
        {
            var mins = new List<double>();
            var oldProbe = new List<int>();
            foreach (var f in Directory.GetFiles(carDirA, "*.png"))
            {
                mins.Add(Prominences(f, cfg).Min());
                oldProbe.Add(CountWhite(f, 154, 204, 1288, 1330));
            }
            bool ok = mins.Count > 0 && mins.Max() < cfg.PanelBarProminenceMin;
            if (!ok) fails++;
            Console.WriteLine();
            Console.WriteLine($"    car-states ({mins.Count} frame, panel ĐÓNG, đứng ở giàn khoan):");
            Console.WriteLine($"       tín hiệu mới : {mins.Min(),7:F1} … {mins.Max(),6:F1}   " +
                              $"{(ok ? "đúng — đều dưới ngưỡng" : "<<< SAI")}");
            Console.WriteLine($"       tín hiệu cũ  : {oldProbe.Min(),7} … {oldProbe.Max(),6}   " +
                              $"(ngưỡng cũ 200 → đây là chỗ nó báo động sai)");
        }

        // ---------------- KIEM CHUNG B : calibrate ----------------
        Console.WriteLine();
        Console.WriteLine("=== B. Hiệu chỉnh: tìm 4 thanh bằng ràng buộc cách đều ===");
        Console.WriteLine($"    kỳ vọng: ~{string.Join(", ", cfg.BarX)}  (±4 px)");

        foreach (var file in new[] { "095.png", "099.png", "103.png", "107.png" })
        {
            string p = Path.Combine(dir, file);
            if (!File.Exists(p)) continue;

            var prof = Calibrator.ProfileFromFile(p, 280, 880, cfg.BarYTop, cfg.BarYBottom);
            var r = Calibrator.Find(prof, 280);

            Console.WriteLine();
            Console.WriteLine($"    --- {file}   median={r.Median:F1}  max={r.Max:F1}  ngưỡng={r.Threshold:F1}");
            foreach (var c in r.Clusters)
                Console.WriteLine($"        cụm x {c.Lo,4}…{c.Hi,4}  tâm {c.Center,4}  đỉnh {c.Peak,6:F1}  nổi lên {c.Prominence,5:F1}");

            if (!r.Ok) { Console.WriteLine($"        ==> {r.Note}"); fails++; continue; }

            var diffs = r.Centers.Zip(cfg.BarX, (a, b) => Math.Abs(a - b)).ToArray();
            bool near = diffs.All(v => v <= 4);
            if (!near) fails++;
            Console.WriteLine($"        ==> {string.Join(", ", r.Centers)}   " +
                              $"khoảng cách={r.Spacing:F1}  lệch nội bộ={r.Deviation:F2}");
            Console.WriteLine($"        ==> so với kỳ vọng: lệch {string.Join("/", diffs)} px  {(near ? "đúng" : "<<< SAI")}");
        }

        // ---------------- KIEM CHUNG C : nguong day/rong tren tung thanh ----------------
        Console.WriteLine();
        Console.WriteLine("=== C. Phân loại thanh (ĐẦY / chưa) ===");
        Console.WriteLine("    kỳ vọng: 095 = chưa thanh nào;  107 = thanh 1,2,3 xong, thanh 4 chưa");
        foreach (var file in new[] { "095.png", "099.png", "103.png", "107.png" })
        {
            string p = Path.Combine(dir, file);
            if (!File.Exists(p)) continue;
            var states = ClassifyFile(p, cfg);
            Console.WriteLine($"    {file}  →  " + string.Join("  ",
                states.Select((s, i) => $"[{i + 1}] {(s.full ? "ĐẦY" : "chưa")} (min={s.min,3})")));
        }

        // ---------------- KIEM CHUNG D : tin hieu "dang trong xe" bang NCC ----------------
        Console.WriteLine();
        Console.WriteLine("=== D. NCC đồng hồ xe — CHỈ LÀ THÔNG TIN, không còn tính đạt/không đạt ===");
        Console.WriteLine("    Phép đo này ĐÃ BỎ khỏi đường quyết định. Đồng hồ tốc độ bán trong suốt");
        Console.WriteLine("    nên nền phía sau lọt vào mẫu: hiệu chuẩn (nền đất tối) ra 0.958…1.000,");
        Console.WriteLine("    đến lúc có nhà tôn xám sáng đứng sau thì tụt xuống 0.71…0.81.");
        Console.WriteLine("    Chuỗi reset xe giờ chờ theo thời gian đo được thay vì đợi tín hiệu này.");
        Console.WriteLine("    Vẫn in ra để nếu sau này muốn quay lại vòng kín thì đã có dữ liệu.");
        var r0 = cfg.CarProbe;
        Console.WriteLine($"    ô x {r0.Left}…{r0.Right - 1}  y {r0.Top}…{r0.Bottom - 1}");

        string root = Path.GetDirectoryName(dir.TrimEnd('\\', '/')) ?? "";
        if (!File.Exists(cfg.CarTemplateFullPath))
        {
            Console.WriteLine($"    (chưa có mẫu {cfg.CarTemplateFullPath} — bỏ qua mục D/E)");
        }
        else
        {
            var tpl = GrayTemplate.FromFile(cfg.CarTemplateFullPath);
            Console.WriteLine($"    mẫu: {tpl.Width}×{tpl.Height} từ {cfg.CarTemplateFullPath}");
            if (tpl.Width != r0.Width || tpl.Height != r0.Height)
                Console.WriteLine($"    (mẫu không khớp kích thước ô đó {r0.Width}×{r0.Height} — bỏ qua)");

            // Moi bo frame duoc kiem bang CUNG MOT NGUONG. Do moi la phep thu thuc su:
            // bo car-states-2 duoc chup o anh sang khac, neu cung nguong van tach duoc
            // thi tin hieu that su bat bien voi anh sang.
            foreach (var (label, folder) in new[]
                     {
                         ("D. car-states   (ánh sáng lúc hiệu chuẩn)", Path.Combine(root, "car-states")),
                         ("E. car-states-2 (ánh sáng KHÁC)",           Path.Combine(root, "car-states-2")),
                     })
            {
                Console.WriteLine();
                Console.WriteLine($"    --- {label}");
                if (!Directory.Exists(folder)) { Console.WriteLine($"        (không có {folder})"); continue; }

                var hi = new List<double>();
                var lo = new List<double>();
                var mid = new List<double>();
                var oldHi = new List<int>();
                var oldLo = new List<int>();

                foreach (var f in Directory.GetFiles(folder, "*.png").OrderBy(p => p))
                {
                    double s = tpl.Score(GrayCrop(f, r0));
                    int w = CountWhite(f, r0.Left, r0.Right - 1, r0.Top, r0.Bottom - 1,
                                       cfg.CarWhiteMinBright, cfg.CarWhiteSpread);
                    if (s >= cfg.CarNccIn) { hi.Add(s); oldHi.Add(w); }
                    else if (s <= cfg.CarNccOut) { lo.Add(s); oldLo.Add(w); }
                    else mid.Add(s);
                }

                Console.WriteLine($"        TRONG XE  : {hi.Count,3} frame" + (hi.Count > 0 ? $"   ncc {hi.Min():F3} … {hi.Max():F3}" : ""));
                Console.WriteLine($"        DƯỚI ĐẤT  : {lo.Count,3} frame" + (lo.Count > 0 ? $"   ncc {lo.Min():F3} … {lo.Max():F3}" : ""));
                Console.WriteLine($"        KHÔNG RÕ  : {mid.Count,3} frame" + (mid.Count > 0 ? $"   ncc {mid.Min():F3} … {mid.Max():F3}" : ""));

                if (hi.Count > 0 && lo.Count > 0)
                    Console.WriteLine($"        biên độ ncc: {lo.Max():F3} … {hi.Min():F3}");

                // Doi chieu: cach dem do sang cu lan o dau.
                if (oldHi.Count > 0 && oldLo.Count > 0)
                {
                    bool oldWorks = oldLo.Max() < 1000 && oldHi.Min() > 1000;
                    Console.WriteLine($"        cách đếm CŨ: trong xe {oldHi.Min()}…{oldHi.Max()}, " +
                                      $"dưới đất {oldLo.Min()}…{oldLo.Max()}  " +
                                      $"→ {(oldWorks ? "tình cờ vẫn đúng ở bộ này" : "LẪN NHAU (đây là chỗ nó sập)")}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("    panel minigame đang mở:");
            foreach (var file in new[] { "095.png", "107.png", "111.png" })
            {
                string p = Path.Combine(dir, file);
                if (!File.Exists(p)) continue;
                Console.WriteLine($"        {file}  ncc={tpl.Score(GrayCrop(p, r0)),7:F3}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? ">>> ĐẠT: mọi kiểm chứng đều đúng."
            : $">>> THẤT BẠI: {fails} kiểm chứng sai — phải sửa thiết kế trước khi viết tiếp.");
        return fails == 0 ? 0 : 1;
    }

    // ---------------- helpers ----------------

    private static (bool full, int min)[] ClassifyFile(string path, BotConfig cfg)
    {
        using var bmp = new Bitmap(path);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                              ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var buf = new byte[bd.Stride * bmp.Height];
        Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(bd);

        int Gray(int x, int y)
        {
            int i = y * bd.Stride + x * 4;
            return (buf[i + 2] * 30 + buf[i + 1] * 59 + buf[i] * 11) / 100;
        }

        var ys = cfg.SampleYs().ToArray();
        var outp = new (bool, int)[cfg.BarX.Length];
        for (int b = 0; b < cfg.BarX.Length; b++)
        {
            int min = int.MaxValue;
            foreach (int y in ys)
            {
                int sum = 0, n = 0;
                for (int x = cfg.BarX[b] - cfg.BarHalfWidth; x <= cfg.BarX[b] + cfg.BarHalfWidth; x++)
                {
                    if (x < 0 || x >= bmp.Width) continue;
                    sum += Gray(x, y); n++;
                }
                if (n > 0) min = Math.Min(min, sum / n);
            }
            outp[b] = (min >= cfg.FullThreshold, min);
        }
        return outp;
    }

    /// <summary>Cat mot o tu file PNG ra mang thang xam row-major (de so khop NCC).</summary>
    private static byte[] GrayCrop(string path, Rectangle rect)
    {
        using var bmp = new Bitmap(path);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                              ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new byte[bd.Stride * bmp.Height];
            Marshal.Copy(bd.Scan0, raw, 0, raw.Length);

            var outp = new byte[rect.Width * rect.Height];
            int k = 0;
            for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) { outp[k++] = 0; continue; }
                int i = y * bd.Stride + x * 4;
                outp[k++] = (byte)((raw[i + 2] * 30 + raw[i + 1] * 59 + raw[i] * 11) / 100);
            }
            return outp;
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>Do "noi len" cua 4 thanh so voi median vung - tin hieu "panel dang mo".</summary>
    private static double[] Prominences(string path, BotConfig cfg)
    {
        var prof = Calibrator.ProfileFromFile(path, cfg.BarRegionX0, cfg.BarRegionX1,
                                              cfg.BarYTop, cfg.BarYBottom);
        var sorted = (double[])prof.Clone();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];

        var outp = new double[cfg.BarX.Length];
        for (int b = 0; b < cfg.BarX.Length; b++)
        {
            double sum = 0; int cnt = 0;
            for (int x = cfg.BarX[b] - cfg.BarHalfWidth; x <= cfg.BarX[b] + cfg.BarHalfWidth; x++)
            {
                if (x < cfg.BarRegionX0 || x > cfg.BarRegionX1) continue;
                sum += prof[x - cfg.BarRegionX0]; cnt++;
            }
            outp[b] = cnt == 0 ? double.MinValue : sum / cnt - median;
        }
        return outp;
    }

    private static int CountWhite(string path, int x0, int x1, int y0, int y1,
                                  int minBright = 150, int spread = 30)
    {
        using var bmp = new Bitmap(path);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                              ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[bd.Stride * bmp.Height];
            Marshal.Copy(bd.Scan0, buf, 0, buf.Length);

            int n = 0;
            for (int y = y0; y <= Math.Min(y1, bmp.Height - 1); y++)
            for (int x = x0; x <= Math.Min(x1, bmp.Width - 1); x++)
            {
                int i = y * bd.Stride + x * 4;
                int b = buf[i], g = buf[i + 1], r = buf[i + 2];
                if (r > minBright && Math.Abs(r - g) < spread && Math.Abs(g - b) < spread) n++;
            }
            return n;
        }
        finally { bmp.UnlockBits(bd); }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "recordings")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
