using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GtaMiniGameBot;

/// <summary>
/// Kiểm tra bộ giải bảng Water &amp; Power. Hai phần:
///   1. Tự vẽ một bảng giả ở CẢ 1920×1080 và 2560×1440 rồi chạy trọn chuỗi: đọc khung → chốt
///      START/GOAL → dò tường → dựng tuyến. Cuối cùng kiểm ĐỘC LẬP rằng tuyến không xuyên qua
///      pixel tường nào.
///   2. Chạy trên ảnh tĩnh người dùng đã chụp, nếu có, và đổ ảnh trung gian ra để soi mắt.
///
/// Bảng giả KHÔNG chứng minh được các ngưỡng màu đúng với hoạ tiết mạch thật trong game — chỉ ảnh
/// chụp thật mới nói được điều đó. Nó chứng minh phần hình học và phần dựng tuyến: tỉ lệ 2K, chốt
/// vai trò theo đèn báo, A* qua khe, tinh chỉnh ngã rẽ, và bất biến "tuyến không đi qua tường".
///
/// Chạy: GtaMiniGameBot.exe --verify-board
/// </summary>
internal static class VerifyBoard
{
    public static int Run(string[] args)
    {
        Console.WriteLine("== kiểm tra bộ giải bảng nước/điện ==");

        int fail = SyntheticTests() + StillTests();

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "TẤT CẢ ĐẠT" : $"HỎNG {fail} ca");
        return fail == 0 ? 0 : 1;
    }

    // ================================================================ bang gia

    private static int SyntheticTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- tự kiểm tra (bảng tự vẽ) --");

        int fail = 0;
        foreach (var (sw, sh) in new[] { (1920, 1080), (2560, 1440) })
            fail += CheckSynthetic(sw, sh) ? 0 : 1;
        return fail;
    }

    private static bool CheckSynthetic(int screenW, int screenH)
    {
        var profile = new ElectricProfile { Width = screenW, Height = screenH };
        profile.Normalize();

        var cfg = new ElectricConfig();
        cfg.Normalize();

        double scale = profile.Scale;
        Console.WriteLine($"  màn {screenW}×{screenH} (tỉ lệ {scale:F3})");

        using var still = DrawBoard(profile);
        using var reader = BoardReader.OpenForBitmap(cfg, profile, still);

        if (!reader.Configured)
        {
            Console.WriteLine($"    HỎNG — không mở được ROI: {reader.Problem}");
            return false;
        }

        var frame = reader.TryRead(out string why);
        if (frame is null)
        {
            Console.WriteLine($"    HỎNG — không đọc được khung: {why}");
            return false;
        }

        Console.WriteLine($"    ROI {frame.Width}×{frame.Height}, chữ tiêu đề {frame.TitleCount} px");
        foreach (var t in frame.Terminals) Console.WriteLine("    đầu nối " + t);

        var role = BoardReader.DetectRole(frame, out string roleWhy);
        if (role is null)
        {
            Console.WriteLine($"    HỎNG — không chốt được vai trò: {roleWhy}");
            return false;
        }
        Console.WriteLine("    " + role.Describe());

        // Ban ve dat den xanh o mat PHAI cua START va den do o mat TRAI cua GOAL, nen huong phai
        // ra dung nhu the. Sai o day la sai ca tuyen.
        if (role.StartPortSide != "right" || role.StartKey != BoardKeys.Right)
        {
            Console.WriteLine($"    HỎNG — mong START mặt right/phím D, đọc ra " +
                              $"{role.StartPortSide}/{role.StartKey}");
            return false;
        }
        if (role.GoalPortSide != "left" || role.GoalFinalKey != BoardKeys.Right)
        {
            Console.WriteLine($"    HỎNG — mong GOAL mặt left/phím cuối D, đọc ra " +
                              $"{role.GoalPortSide}/{role.GoalFinalKey}");
            return false;
        }

        // Quet lai nhieu lan tren cung anh — dung nhu bo canh on dinh doi truoc khi dong bang tuyen.
        var history = new List<BoardPlanner.WallScan>();
        for (int i = 0; i < cfg.Board.WallStableFrames; i++) history.Add(BoardPlanner.ScanWalls(frame));

        var scan = history[^1];
        Console.WriteLine($"    tường: che {scan.Coverage:P1}, V thân bảng {scan.PanelV:F1}, " +
                          $"ngưỡng V {scan.ValueThreshold}, {scan.LargeWalls} khối lớn / " +
                          $"{scan.MicroWalls} khối nhỏ / {scan.SecondaryWalls} lớp bảo");

        if (!BoardPlanner.SignatureStable(history, cfg.Board.WallStableFrames, out string stableWhy))
        {
            Console.WriteLine($"    HỎNG — bộ canh ổn định từ chối bảng tĩnh: {stableWhy}");
            return false;
        }

        var plan = BoardPlanner.Plan(frame, role, scan, out string planWhy);
        if (plan is null)
        {
            Console.WriteLine($"    HỎNG — không dựng được tuyến: {planWhy}");
            return false;
        }

        Console.WriteLine("    " + plan.Describe());
        foreach (var s in plan.Segments) Console.WriteLine("      " + s);

        if (plan.Segments[0].Key != role.StartKey)
        {
            Console.WriteLine($"    HỎNG — đoạn đầu {plan.Segments[0].Key}, mong {role.StartKey}");
            return false;
        }
        if (plan.Segments[^1].Key != role.GoalFinalKey)
        {
            Console.WriteLine($"    HỎNG — đoạn cuối {plan.Segments[^1].Key}, mong {role.GoalFinalKey}");
            return false;
        }

        var end = plan.Segments[^1].End;
        if (Math.Abs(end.X - role.GoalHit.X) > 2 || Math.Abs(end.Y - role.GoalHit.Y) > 2)
        {
            Console.WriteLine($"    HỎNG — tuyến kết thúc ở ({end.X:F0},{end.Y:F0}), " +
                              $"đích là ({role.GoalHit.X},{role.GoalHit.Y})");
            return false;
        }

        if (!RouteAvoidsWalls(plan, frame, out string crossWhy))
        {
            Console.WriteLine("    HỎNG — " + crossWhy);
            return false;
        }

        Console.WriteLine($"    đạt — {plan.Segments.Length} đoạn, không đoạn nào xuyên tường");
        return true;
    }

    /// <summary>
    /// Kiểm ĐỘC LẬP: không mẫu nào trên tuyến rơi vào pixel tường VẬT LÝ.
    ///
    /// Không dùng lại số liệu của bộ dựng tuyến (khoảng thoát, chứng chỉ) — nếu dùng thì phép thử
    /// chỉ đang xác nhận chính nó. Hai đầu tuyến được miễn một đoạn ngắn vì chúng nằm trong thân
    /// đầu nối, mà thân đầu nối luôn được đánh dấu là tường.
    /// </summary>
    private static bool RouteAvoidsWalls(BoardPlan plan, BoardFrame frame, out string why)
    {
        var wall = plan.Obstacles;
        double exempt = Math.Max(16.0, 62.0 * frame.Scale);

        for (int i = 0; i < plan.Segments.Length; i++)
        {
            var s = plan.Segments[i];
            double dx = s.End.X - s.Start.X, dy = s.End.Y - s.Start.Y;
            double d = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
            int n = (int)d + 1;

            for (int k = 0; k <= n; k++)
            {
                double u = k / (double)n;
                double along = d * u;
                if (i == 0 && along <= exempt) continue;
                if (i == plan.Segments.Length - 1 && d - along <= exempt) continue;

                int x = (int)Math.Round(s.Start.X + dx * u);
                int y = (int)Math.Round(s.Start.Y + dy * u);
                if (x < 0 || y < 0 || x >= wall.Width || y >= wall.Height)
                {
                    why = $"tuyến ra ngoài ROI: đoạn {i} tại ({x},{y})";
                    return false;
                }
                if (wall[x, y] != 0)
                {
                    why = $"tuyến xuyên tường: đoạn {i} ({s.Key}) tại ({x},{y})";
                    return false;
                }
            }
        }

        why = null;
        return true;
    }

    // ---------------------------------------------------------------- ve bang

    /// <summary>
    /// Vẽ một bảng đủ đúng để cả chuỗi nhận được: dải chữ tiêu đề, nền tối, các thân bảng xanh
    /// làm tường có khe so le, và hai đầu nối có đèn báo.
    ///
    /// Mọi kích thước cho ở mốc 1920×1080 rồi nhân tỉ lệ — chính là cách UI của game co giãn, và
    /// là điều mà phép thử này cần chứng minh.
    /// </summary>
    private static Bitmap DrawBoard(ElectricProfile profile)
    {
        double k = profile.Scale;
        int R(double v) => (int)Math.Round(v * k);

        var bmp = new Bitmap(profile.Width, profile.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;

        // Nen ca man: toi, V=16 nen khong bao gio bi nham la than dau noi xam.
        g.Clear(Color.FromArgb(12, 16, 14));

        // Chu tieu de: hue 90 (xanh lo) — dung dai H 70..95 ma bo doc dung lam chu ky.
        var title = profile.ScanTitleBand();
        using (var b = new SolidBrush(Color.FromArgb(0, 200, 200)))
            g.FillRectangle(b, title.X + R(20), title.Y + R(20), R(260), R(70));

        var roi = profile.ScanBoardRoi();
        int ox = roi.X, oy = roi.Y;

        var wallColour = Color.FromArgb(40, 160, 40);     // H=60 S=191 V=160
        var bodyColour = Color.FromArgb(90, 90, 90);      // S=0  V=90
        var pinColour = Color.FromArgb(240, 240, 240);    // S=0  V=240
        var greenLamp = Color.FromArgb(0, 220, 0);
        var redLamp = Color.FromArgb(230, 0, 0);

        void Fill(Color c, double x, double y, double w, double h)
        {
            using var b = new SolidBrush(c);
            g.FillRectangle(b, ox + R(x), oy + R(y), R(w), R(h));
        }

        // Khung tren/duoi cua bang.
        Fill(wallColour, 200, 45, 900, 50);
        Fill(wallColour, 200, 680, 900, 45);

        // Bon cot tuong, moi cot mot khe, khe so le nen tuyen buoc phai zigzag.
        var bars = new (double X, double GapY0, double GapY1)[]
        {
            (300, 330, 460),
            (520, 95, 220),
            (740, 330, 460),
            (960, 500, 630)
        };

        foreach (var (x, gy0, gy1) in bars)
        {
            Fill(wallColour, x, 60, 120, gy0 - 60);
            Fill(wallColour, x, gy1, 120, 700 - gy1);
        }

        // Dau noi START: than xam, dai chan cam trang ben TRAI, den xanh ben PHAI.
        //
        // Den bao chi cao 12/32 chu khong tran het chieu cao, va do la chi tiet QUAN TRONG: bo do
        // than dau noi bat mau xam (S thap), nen mot cai den tran het chieu cao se CAT doi than va
        // hop bao thu duoc khong con chua den. Trong game den la khoi nho nam tren mat, than xam
        // vay quanh no — ve dung nhu vay thi hop bao moi trum ca den, va do la dieu ma
        // BoardReader.LampEdgeSide dua vao.
        Fill(bodyColour, 140, 375, 62, 32);
        Fill(pinColour, 140, 375, 8, 32);
        Fill(greenLamp, 194, 385, 8, 12);

        // Dau noi GOAL: den do ben TRAI (day la mat day se cam vao), chan cam ben PHAI.
        Fill(bodyColour, 1120, 375, 62, 32);
        Fill(redLamp, 1120, 385, 8, 12);
        Fill(pinColour, 1174, 375, 8, 32);

        return bmp;
    }

    // ================================================================ anh that

    private static int StillTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- ảnh tĩnh đã chụp --");

        var cfg = ElectricConfig.Load();
        if (cfg.Profiles.Count == 0)
        {
            Console.WriteLine("  chưa có profile nào trong electric.json — bỏ qua.");
            return 0;
        }

        int fail = 0, found = 0;
        foreach (var (key, profile) in cfg.Profiles.OrderBy(kv => kv.Key))
        {
            string path = ElectricConfig.ShotPath(key, "board");
            if (!File.Exists(path)) continue;

            found++;
            Console.WriteLine($"  {key}/board.png");
            try
            {
                using var still = new Bitmap(path);
                using var reader = BoardReader.OpenForBitmap(cfg, profile, still);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var frame = reader.TryRead(out string why);
                double readMs = sw.Elapsed.TotalMilliseconds;
                if (frame is null)
                {
                    Console.WriteLine("    HỎNG — " + why);
                    fail++;
                    continue;
                }

                Console.WriteLine($"    ROI {frame.Width}×{frame.Height}, tiêu đề {frame.TitleCount} px");
                foreach (var t in frame.Terminals) Console.WriteLine("    đầu nối " + t);

                // Do THOI GIAN, khong chi doc ket qua: day dung la ngan sach quyet dinh bot co kip
                // bam phim dau tien hay khong. Doc khung + quet tuong phai chay 3 lan (cong on
                // dinh) truoc khi dung tuyen.
                sw.Restart();
                var scan = BoardPlanner.ScanWalls(frame);
                double scanMs = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"    thời gian: đọc khung {readMs:F0}ms, quét tường {scanMs:F0}ms " +
                                  $"→ 3 khung ổn định ≈ {(readMs + scanMs) * 3:F0}ms");
                Console.WriteLine("    chi tiết quét tường: " + scan.Timing);
                Console.WriteLine($"    tường: che {scan.Coverage:P1}, V thân {scan.PanelV:F1}, " +
                                  $"ngưỡng {scan.ValueThreshold}, {scan.LargeWalls}/{scan.MicroWalls}/{scan.SecondaryWalls}");

                string dir = ElectricConfig.DebugDir(key);
                Directory.CreateDirectory(dir);
                SaveMask(scan.Wall, Path.Combine(dir, "01-tuong.png"));

                var role = BoardReader.DetectRole(frame, out string roleWhy);
                if (role is null)
                {
                    Console.WriteLine("    HỎNG — không chốt được vai trò: " + roleWhy);
                    Console.WriteLine("    (đã ghi 01-tuong.png để soi)");
                    fail++;
                    continue;
                }
                Console.WriteLine("    " + role.Describe());

                // Chan doan: hai diem dau/cuoi BUOC PHAI nam trong vung ban hop le, khong thi moi
                // ban kinh no deu that bai va thong bao chi noi "khong dung duoc tuyen" — dung mot
                // cau cho ca chuc nguyen nhan khac nhau.
                var legal = BoardPlanner.LegalBounds(scan.Wall, frame);
                Console.WriteLine($"    vùng hợp lệ ({legal.Left},{legal.Top})-({legal.Right},{legal.Bottom})" +
                                  $"  ROI 0,0-{frame.Width - 1},{frame.Height - 1}");
                Console.WriteLine($"    START @{role.StartPoint.X},{role.StartPoint.Y} trong vùng: " +
                                  $"{(legal.Contains(role.StartPoint) ? "CÓ" : "KHÔNG")}   " +
                                  $"GOAL @{role.GoalHit.X},{role.GoalHit.Y} trong vùng: " +
                                  $"{(legal.Contains(role.GoalHit) ? "CÓ" : "KHÔNG")}");

                var plan = BoardPlanner.Plan(frame, role, scan, out string planWhy);
                if (plan is null)
                {
                    Console.WriteLine("    HỎNG — không dựng được tuyến: " + planWhy);
                    fail++;
                    continue;
                }

                Console.WriteLine("    " + plan.Describe());
                foreach (string n in plan.RefineNotes) Console.WriteLine("      ngã rẽ " + n);
                foreach (var s in plan.Segments) Console.WriteLine("      " + s);

                SaveMask(plan.Inflated, Path.Combine(dir, "02-tuong-da-no.png"));
                SaveRoute(plan, Path.Combine(dir, "03-tuyen.png"));
                Console.WriteLine("    đã ghi ảnh trung gian vào " + dir);

                if (!RouteAvoidsWalls(plan, frame, out string crossWhy))
                {
                    Console.WriteLine("    HỎNG — " + crossWhy);
                    fail++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    HỎNG — " + ex.Message);
                fail++;
            }
        }

        if (found == 0)
            Console.WriteLine("  chưa có ảnh nào. Chụp bằng tab Điện → “Chụp ảnh tĩnh…”, " +
                              "lưu tên board.png.");
        return fail;
    }

    private static void SaveMask(Mask m, string path)
    {
        using var bmp = new Bitmap(m.Width, m.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < m.Height; y++)
        for (int x = 0; x < m.Width; x++)
            bmp.SetPixel(x, y, m[x, y] != 0 ? Color.White : Color.Black);
        bmp.Save(path, ImageFormat.Png);
    }

    private static void SaveRoute(BoardPlan plan, string path)
    {
        var m = plan.Obstacles;
        using var bmp = new Bitmap(m.Width, m.Height, PixelFormat.Format32bppArgb);

        for (int y = 0; y < m.Height; y++)
        for (int x = 0; x < m.Width; x++)
            bmp.SetPixel(x, y, m[x, y] != 0 ? Color.FromArgb(60, 90, 60) : Color.FromArgb(12, 16, 14));

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        using var pen = new Pen(Color.Cyan, 3f);
        foreach (var s in plan.Segments)
            g.DrawLine(pen, s.Start, s.End);

        using var startBrush = new SolidBrush(Color.Lime);
        using var goalBrush = new SolidBrush(Color.Red);
        g.FillEllipse(startBrush, plan.Role.StartPoint.X - 6, plan.Role.StartPoint.Y - 6, 12, 12);
        g.FillEllipse(goalBrush, plan.Role.GoalHit.X - 6, plan.Role.GoalHit.Y - 6, 12, 12);

        bmp.Save(path, ImageFormat.Png);
    }
}
