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

        int fail = SyntheticTests() + StabilityAndMotionTests() + FailureAndProfileTests()
                 + AfterSolveWaitTests() + StillTests();

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

    private static int StabilityAndMotionTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- chữ ký nhanh và dự báo chuyển động --");
        int fail = 0;

        var profile = new ElectricProfile { Width = 1920, Height = 1080 };
        profile.Normalize();
        using (var still = DrawBoard(profile))
        using (var pixels = new BitmapRegion(still, profile.ScanBoardRoi().ToRectangle()))
        {
            var a = BoardWallSignature.Create(
                pixels.Raw, pixels.Region.Width, pixels.Region.Height, pixels.Stride, 1, 100);
            var b = BoardWallSignature.Create(
                pixels.Raw, pixels.Region.Width, pixels.Region.Height, pixels.Stride, 2, 200);

            if (!BoardWallSignature.Stable(a, b, out string stableWhy))
            {
                Console.WriteLine("  HỎNG — bảng tĩnh không ổn định: " + stableWhy);
                fail++;
            }
            else Console.WriteLine("  chữ ký bảng tĩnh: đạt — " + stableWhy);

            if (BoardWallSignature.Stable(a, a, out _))
            {
                Console.WriteLine("  HỎNG — nhận lại cùng FrameId là hai frame ổn định");
                fail++;
            }
        }

        foreach (int hz in new[] { 60, 120, 144 })
        {
            var estimator = new BoardMotionEstimator();
            long t = 1_000_000;
            double speed = 0.42;
            double position = 0;
            estimator.Reset(position, t, speed);

            double dtMs = 1000.0 / hz;
            long dtTicks = (long)Math.Round(dtMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
            for (int i = 0; i < 40; i++)
            {
                // Bỏ một frame để chứng minh timestamp, không phải số vòng lặp, điều khiển dự báo.
                int frames = i == 15 ? 2 : 1;
                t += dtTicks * frames;
                position += speed * dtMs * frames;
                estimator.Update(position, t);
            }

            double predicted = estimator.Predict(t + dtTicks);
            double expected = position + speed * dtMs;
            if (Math.Abs(predicted - expected) > 1.5)
            {
                Console.WriteLine($"  HỎNG — dự báo {hz}Hz lệch {Math.Abs(predicted - expected):F2}px");
                fail++;
            }
            else Console.WriteLine($"  dự báo {hz}Hz: đạt, lệch {Math.Abs(predicted - expected):F2}px");
        }

        var onsetSamples = new List<(double Ms, double New, double Old)>
        {
            (3, 0.2, 4.0),
            (6, 2.4, 6.0),
            (18, 8.0, 7.0),
        };
        double onset = BoardBot.EstimateInputOnsetMs(onsetSamples, 1.0, 18);
        if (onset < 5.5 || onset > 6.5)
        {
            Console.WriteLine($"  HỎNG — onset học {onset:F1}ms, mong ~6ms chứ không phải 18ms xác nhận");
            fail++;
        }
        else Console.WriteLine($"  onset phím: đạt, {onset:F1}ms");

        return fail;
    }

    private static int FailureAndProfileTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- ROI tùy chọn, frame sạch và overlay fail --");
        int fail = 0;

        var profile = new ElectricProfile { Width = 1920, Height = 1080 };
        profile.Normalize();
        var defaultBoard = profile.ScanBoardRoi().ToRectangle();
        var customBoard = new Rectangle(310, 150, 1280, 760);
        var customTitle = new Rectangle(430, 20, 900, 100);
        profile.BoardRoi = FishingRect.FromRelative(customBoard);
        profile.TitleBand = FishingRect.FromRelative(customTitle);
        if (profile.ScanBoardRoi().ToRectangle() != customBoard ||
            profile.ScanTitleBand().ToRectangle() != customTitle)
        {
            Console.WriteLine("  HỎNG — profile không dùng ROI bảng/tiêu đề đã khoanh");
            fail++;
        }
        profile.BoardRoi = new FishingRect();
        profile.TitleBand = new FishingRect();
        if (profile.ScanBoardRoi().ToRectangle() != defaultBoard)
        {
            Console.WriteLine("  HỎNG — xóa ROI không trở về vùng suy theo độ phân giải");
            fail++;
        }
        else Console.WriteLine("  ROI tùy chọn/mặc định: đạt");

        var cfg = new ElectricConfig();
        cfg.Normalize();
        using var still = DrawBoard(profile);
        using (var reader = BoardReader.OpenForBitmap(cfg, profile, still))
        {
            var frame = reader.TryRead(out string why);
            if (frame is null)
            {
                Console.WriteLine("  HỎNG — không tạo được frame sạch: " + why);
                fail++;
            }
            else
            {
                int before = 17;
                foreach (byte b in frame.Bgr) before = unchecked(before * 31 + b);
                using (var g = Graphics.FromImage(still))
                {
                    var r = profile.ScanBoardRoi().ToRectangle();
                    g.FillRectangle(Brushes.Magenta, r.Left + 2, r.Top + 2, 8, 8);
                }
                int after = 17;
                foreach (byte b in frame.Bgr) after = unchecked(after * 31 + b);
                if (after != before)
                {
                    Console.WriteLine("  HỎNG — frame sạch bị ảnh nguồn sửa ngược");
                    fail++;
                }
                else Console.WriteLine("  frame sạch độc lập buffer nguồn: đạt");

                var scan = BoardPlanner.ScanWalls(frame);
                var primary = BoardPlanner.ScanWalls(frame, includeSecondary: false);
                if (primary.Wall.Count > scan.Wall.Count)
                {
                    Console.WriteLine("  HỎNG — bỏ lớp phụ lại làm tăng số pixel tường");
                    fail++;
                }
                var role = BoardReader.DetectRole(frame, out _);
                var tight = role is null ? null : BoardPlanner.PlanTight(frame, role, primary, out _);
                if (role is null || tight is null || !RouteAvoidsWalls(tight, frame, out _))
                {
                    Console.WriteLine("  HỎNG — planner lề chặt không tạo được tuyến được chứng nhận");
                    fail++;
                }
                else Console.WriteLine("  fallback lề chặt/tường chính: đạt");
            }
        }

        // Vẽ banner fail sau khi đã kiểm frame sạch; detector chỉ được bắt banner giữa bảng,
        // không được nhầm đèn đỏ GOAL của bảng bình thường.
        using var normalRegion = new BitmapRegion(still, profile.ScanBoardRoi().ToRectangle());
        if (BoardReader.FailOverlayPresent(normalRegion.Raw, normalRegion.Region.Width,
                                           normalRegion.Region.Height, normalRegion.Stride))
        {
            Console.WriteLine("  HỎNG — bảng thường bị nhận nhầm là overlay fail");
            fail++;
        }

        using (var g = Graphics.FromImage(still))
        {
            var r = profile.ScanBoardRoi().ToRectangle();
            int y = r.Top + (int)(r.Height * 0.46);
            using var red = new SolidBrush(Color.FromArgb(220, 42, 35));
            for (int x = r.Left + r.Width / 5; x < r.Right - r.Width / 5; x += 70)
                g.FillRectangle(red, x, y, 42, Math.Max(45, r.Height / 8));
            g.FillRectangle(red, r.Left + r.Width / 3, y + 20, r.Width / 3, 18);
        }
        using var failRegion = new BitmapRegion(still, profile.ScanBoardRoi().ToRectangle());
        if (!BoardReader.FailOverlayPresent(failRegion.Raw, failRegion.Region.Width,
                                            failRegion.Region.Height, failRegion.Stride))
        {
            Console.WriteLine("  HỎNG — không nhận banner fail đỏ giữa bảng");
            fail++;
        }
        else Console.WriteLine("  overlay fail đỏ: đạt");

        if (BoardAfterSolvePolicy.UnsolvedCloseReason(everSeen: true) != BoardStopReason.Failed ||
            BoardAfterSolvePolicy.UnsolvedCloseReason(everSeen: false) != BoardStopReason.NoBoard)
        {
            Console.WriteLine("  HỎNG — bảng đóng chưa thắng vẫn bị phân loại sai");
            fail++;
        }
        else Console.WriteLine("  bảng đóng 0 lượt = Failed: đạt");

        return fail;
    }

    /// <summary>
    /// Sau khi thắng, bảng còn hiện thì không được dựng tuyến lần hai; chỉ Solved khi tiêu đề
    /// vắng đủ <see cref="BoardAfterSolvePolicy.BoardGoneMs"/>.
    /// </summary>
    private static int AfterSolveWaitTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- sau khi giải, chờ bảng đóng --");
        int fail = 0;

        var policy = new BoardAfterSolvePolicy();
        int plans = 0;
        if (policy.AllowPlan) plans++;
        policy.OnRouteSuccess();

        long now = 0;
        for (int i = 0; i < 20; i++)
        {
            now += 80;
            if (policy.AllowPlan)
            {
                Console.WriteLine("  HỎNG — bảng vừa thắng vẫn được phép dựng tuyến lần hai");
                fail++;
                break;
            }
            if (policy.Tick(boardOpen: true, now) is not null)
            {
                Console.WriteLine("  HỎNG — trả Solved khi bảng còn hiện");
                fail++;
                break;
            }
        }

        if (policy.Tick(boardOpen: false, now + 1_000) is not null)
        {
            Console.WriteLine("  HỎNG — Solved quá sớm, tiêu đề mới vắng 1s");
            fail++;
        }

        var done = policy.Tick(boardOpen: false, now + 1_000 + BoardAfterSolvePolicy.BoardGoneMs);
        if (done != BoardStopReason.Solved)
        {
            Console.WriteLine("  HỎNG — tiêu đề vắng đủ lâu mà không trả Solved");
            fail++;
        }
        else if (plans != 1 || policy.AllowPlan)
        {
            Console.WriteLine($"  HỎNG — số lần được phép lập tuyến = {plans}, AllowPlan={policy.AllowPlan}");
            fail++;
        }
        else Console.WriteLine("  đạt — không lập tuyến lần hai, Solved sau khi bảng đóng đủ lâu");

        var reopen = new BoardAfterSolvePolicy();
        reopen.OnRouteSuccess();
        reopen.Tick(boardOpen: false, 0);
        if (reopen.Tick(boardOpen: true, BoardAfterSolvePolicy.BoardGoneMs) is not null)
        {
            Console.WriteLine("  HỎNG — bảng hiện lại giữa lúc chờ vẫn bị coi là đã đóng");
            fail++;
        }
        else if (reopen.Tick(boardOpen: false, BoardAfterSolvePolicy.BoardGoneMs + 500) is not null)
        {
            Console.WriteLine("  HỎNG — đồng hồ vắng không reset khi bảng hiện lại");
            fail++;
        }
        else if (reopen.Tick(boardOpen: false, BoardAfterSolvePolicy.BoardGoneMs + 500 + BoardAfterSolvePolicy.BoardGoneMs)
                 != BoardStopReason.Solved)
        {
            Console.WriteLine("  HỎNG — không Solved sau lần vắng đủ lâu thứ hai");
            fail++;
        }
        else Console.WriteLine("  đạt — hiện lại giữa chừng reset đồng hồ đóng bảng");

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
        var fullScan = BoardPlanner.ScanWallsFullResolution(frame);
        if (!FastMaskCoversFull(scan.Wall, fullScan.Wall, out string coverWhy))
        {
            Console.WriteLine("    HỎNG — mask nhanh bỏ sót tường full-res: " + coverWhy);
            return false;
        }
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

        var cache = BoardRouteCache.CreateEmpty();
        string cacheKey = BoardRouteCache.MakeKey(frame, role, scan.Wall);
        cache.Put(cacheKey, plan);
        if (!cache.TryGet(cacheKey, role, frame.Width, frame.Height, out var cached) ||
            cached.Length != plan.Segments.Length ||
            !cached.Select(s => s.Key).SequenceEqual(plan.Segments.Select(s => s.Key)))
        {
            Console.WriteLine("    HỎNG — cache không phục hồi đúng chuỗi đoạn");
            return false;
        }
        var cachedPlan = BoardPlanner.ValidateCached(frame, role, scan, cached, out string cacheWhy);
        if (cachedPlan is null || !RouteAvoidsWalls(cachedPlan, frame, out _))
        {
            Console.WriteLine("    HỎNG — cache không được chứng nhận lại: " + cacheWhy);
            return false;
        }
        Console.WriteLine($"    cache chứng nhận lại {cachedPlan.BuildMs:F0}ms");

        string tempCache = Path.Combine(Path.GetTempPath(), $"gta-board-cache-{Guid.NewGuid():N}.json");
        try
        {
            var persisted = BoardRouteCache.CreateEmpty(tempCache);
            persisted.Put(cacheKey, plan);
            persisted.SaveIfDirty();
            var loaded = BoardRouteCache.Load(tempCache);
            if (!loaded.TryGet(cacheKey, role, frame.Width, frame.Height, out _))
            {
                Console.WriteLine("    HỎNG — cache JSON không đọc lại được");
                return false;
            }
            File.WriteAllText(tempCache, "{not-json");
            if (BoardRouteCache.Load(tempCache).Count != 0)
            {
                Console.WriteLine("    HỎNG — cache JSON hỏng không bị bỏ qua");
                return false;
            }
        }
        finally
        {
            try { File.Delete(tempCache); } catch { }
            try { File.Delete(tempCache + ".tmp"); } catch { }
        }

        Console.WriteLine($"    đạt — {plan.Segments.Length} đoạn, không xuyên tường, cache khớp");
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

                // Đo thời gian xử lý đầy đủ sau stability gate. Live path chỉ dựng chữ ký nhỏ ở
                // các frame chờ; đọc HSV + quét tường đầy đủ chạy đúng một lần.
                sw.Restart();
                var scan = BoardPlanner.ScanWalls(frame);
                double scanMs = sw.Elapsed.TotalMilliseconds;
                var fullScan = BoardPlanner.ScanWallsFullResolution(frame);
                if (!FastMaskCoversFull(scan.Wall, fullScan.Wall, out string coverWhy))
                {
                    Console.WriteLine("    HỎNG — mask nhanh bỏ sót tường full-res: " + coverWhy);
                    fail++;
                    continue;
                }
                Console.WriteLine($"    thời gian full: đọc khung {readMs:F0}ms, " +
                                  $"quét tường {scanMs:F0}ms, tổng {readMs + scanMs:F0}ms");
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
                    Console.WriteLine("    planner chính: " + planWhy);
                    plan = BoardPlanner.PlanTight(frame, role, scan, out planWhy);
                }
                if (plan is null)
                {
                    var primary = BoardPlanner.ScanWalls(frame, includeSecondary: false);
                    SaveMask(primary.Wall, Path.Combine(dir, "01b-tuong-chinh.png"));
                    plan = BoardPlanner.PlanTight(frame, role, primary, out planWhy);
                }
                if (plan is null)
                {
                    Console.WriteLine("    HỎNG — mọi tầng planner đều thất bại: " + planWhy);
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

    private static bool FastMaskCoversFull(Mask fast, Mask full, out string why)
    {
        if (fast.Width != full.Width || fast.Height != full.Height)
        {
            why = "khác kích thước";
            return false;
        }

        // Cho sai số biên 1px do co 2x rồi phóng lại; lõi tường và từng khối không được biến mất
        // — ngân sách 0.1% toàn cục có thể nuốt một bức chắn mảnh (~1000px trên mặt nạ 2M).
        var covered = ImageOps.Dilate(fast, 3, 3);
        var labeled = ImageOps.Label(full);
        var core = ImageOps.Erode(full, 3, 3);
        var missedPer = new int[labeled.Blobs.Count + 1];

        int fullCount = 0, missed = 0, coreCount = 0, coreMissed = 0;
        for (int i = 0; i < full.Data.Length; i++)
        {
            if (full.Data[i] == 0) continue;
            fullCount++;
            int id = labeled.Label[i];
            bool hit = covered.Data[i] != 0;
            if (!hit)
            {
                missed++;
                if (id > 0) missedPer[id]++;
            }
            if (core.Data[i] == 0) continue;
            coreCount++;
            if (!hit) coreMissed++;
        }

        // Downsample chọn pixel xanh mạnh nhất trong ô 2×2 nên mép cong có thể lệch quá 1px ở
        // vài điểm rời rạc. Vẫn cấm mất cả mảng lõi: 0.02% nhỏ hơn rất nhiều một mấu tường thật,
        // và cửa kiểm theo từng component ngay dưới tiếp tục bắt trường hợp mất khối cục bộ.
        if (coreMissed / Math.Max(1.0, coreCount) > 0.0002)
        {
            why = $"lõi tường mất {coreMissed}/{Math.Max(1, coreCount)} px";
            return false;
        }

        const int minComponent = 80;
        for (int i = 0; i < labeled.Blobs.Count; i++)
        {
            var blob = labeled.Blobs[i];
            if (blob.Area < minComponent) continue;
            if (missedPer[i + 1] / (double)blob.Area <= 0.25) continue;
            why = $"khối {blob.Box.Width}×{blob.Box.Height} mất {missedPer[i + 1]}/{blob.Area}";
            return false;
        }

        double ratio = missed / Math.Max(1.0, fullCount);
        why = $"{missed}/{fullCount} pixel ({ratio:P3})";
        return ratio <= 0.001;
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
