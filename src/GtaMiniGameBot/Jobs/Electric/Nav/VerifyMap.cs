using System.Drawing.Imaging;
using System.Globalization;

namespace GtaMiniGameBot;

/// <summary>
/// Kiểm bộ GHI BẢN ĐỒ sân trạm biến áp, ngoài game — anh em với <see cref="VerifyNav"/> và dùng lại
/// đúng khuôn <c>Check(ref fail, …)</c> của nó.
///
/// Ba phần:
///   1. Bộ giải pose và tracker chạy trên số TỔNG HỢP — chứng minh hình học đúng tới 1e-9 và các cổng
///      lọc (tỉ lệ, vị trí, hướng) phản ứng đúng với zoom đổi, blip ghim mép, game nuốt delta chuột.
///   2. Bộ dò blip chạy trên 5 ẢNH THẬT trong <c>electric\&lt;WxH&gt;\shots\</c> — chứng minh nó bắt được
///      ⚡ và ✕ ở cả hai mức zoom (138 px ngày 23/08, 175 px ngày 05/09) và loại đúng blip bị cắt mép.
///   3. Bộ ghi và bộ dựng bản đồ trên một sân tổng hợp — chứng minh CSV đi vòng được và bản đồ dựng ra
///      có cụm máy, tư thế tiếp cận, ô trống và đường đi đúng.
///
/// Chạy: <c>GtaMiniGameBot.exe --verify-map</c> (thêm <c>--learn</c> để lưu mẫu blip học từ
/// <c>nav-far</c>, thêm <c>--build</c> để dựng bản đồ thật từ <c>rec-*.csv</c> đã ghi).
/// </summary>
internal static class VerifyMap
{
    public static int Run(string[] args)
    {
        bool learn = args.Any(a => a.Equals("--learn", StringComparison.OrdinalIgnoreCase));
        bool build = args.Any(a => a.Equals("--build", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("== kiểm bộ ghi bản đồ sân trạm biến áp (job Điện) ==");

        int fail = SelfTest();

        var cfg = ElectricConfig.Load();
        if (cfg.Profiles.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("chưa có profile nào trong electric.json — phần ảnh thật bỏ qua.");
            Console.WriteLine();
            Console.WriteLine(fail == 0 ? "TẤT CẢ ĐẠT" : $"HỎNG {fail} ca");
            return fail == 0 ? 0 : 1;
        }

        foreach (var (key, profile) in cfg.Profiles.OrderBy(kv => kv.Key))
        {
            Console.WriteLine();
            Console.WriteLine($"-- {key} --");
            var s = new NavScale(profile.Width, profile.Height, cfg.Nav.ScreenPxScale);
            double ox = (cfg.Nav.PlayerOriginXRef > 0 ? cfg.Nav.PlayerOriginXRef : NavTuning.PlayerOriginXRef) * s.Sx;
            double oy = (cfg.Nav.PlayerOriginYRef > 0 ? cfg.Nav.PlayerOriginYRef : NavTuning.PlayerOriginYRef) * s.Sy;
            var t = NavTuning.TargetRoiRef;
            var mini = s.RoiRef(t[0], t[1], t[2], t[3]);
            Console.WriteLine($"  minimap {mini.Width}×{mini.Height} @ {mini.X},{mini.Y}; gốc mũi tên ({ox:F1},{oy:F1}); " +
                              $"1 mu = {NavTuning.YardDRef:F0}ᵗʰ khoảng ⚡✕");
            fail += RealShots(profile, s, ox, oy, learn);
            fail += RealMapPlan(profile);
            if (build) fail += BuildReal(profile, s);
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

    private static string F(double v, int d = 2) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    // ================================================================ tu kiem tra

    private static int SelfTest()
    {
        Console.WriteLine();
        Console.WriteLine("-- tự kiểm tra --");
        int fail = 0;
        fail += SolverCases();
        fail += TrackerCases();
        fail += RecorderCases();
        fail += BuilderCases();
        fail += BuilderFallbackCases();
        fail += PathCases();
        fail += FollowerCases();
        Console.WriteLine(fail == 0 ? "  tự kiểm tra: ĐẠT" : $"  tự kiểm tra: HỎNG {fail} ca");
        return fail;
    }

    /// <summary>Dựng toạ độ màn từ một pose biết trước — nghịch đảo của bộ giải, để đóng vòng.</summary>
    private static Vec2 Project(Vec2 m, double s, double phi, Vec2 p) => YardPoseSolver.ToScreen(m, s, phi, p);

    private static int SolverCases()
    {
        int fail = 0;
        var rng = new Random(20260918);

        double worstS = 0, worstPhi = 0, worstP = 0;
        for (int i = 0; i < 400; i++)
        {
            double s = 0.6 + rng.NextDouble() * 1.4;
            double phi = (rng.NextDouble() * 2 - 1) * Math.PI;
            var p = new Vec2((rng.NextDouble() * 2 - 1) * 200, (rng.NextDouble() * 2 - 1) * 200);

            var a = Project(YardPoseSolver.MapPos(BlipId.Lightning), s, phi, p);
            var b = Project(YardPoseSolver.MapPos(BlipId.Cross), s, phi, p);

            var fx = YardPoseSolver.Fix2(a, b);
            worstS = Math.Max(worstS, Math.Abs(fx.S - s));
            worstPhi = Math.Max(worstPhi, Math.Abs(YardPoseSolver.Wrap((fx.Phi - phi) * YardPoseSolver.Rad2Deg)));
            worstP = Math.Max(worstP, (fx.P - p).Len);
        }
        Check(ref fail, worstS < 1e-9 && worstPhi < 1e-9 && worstP < 1e-9,
              "hai mốc: phục hồi s/φ/P tới 1e-9",
              $"lệch tối đa s={worstS:E1} φ={worstPhi:E1}° P={worstP:E1}mu");

        // n mốc = hai mốc khi n = 2.
        {
            double s = 1.27, phi = 0.7;
            var p = new Vec2(-31.5, 88.25);
            var a = Project(YardPoseSolver.MapPos(BlipId.Lightning), s, phi, p);
            var b = Project(YardPoseSolver.MapPos(BlipId.Cross), s, phi, p);
            var two = YardPoseSolver.Fix2(a, b);
            var many = YardPoseSolver.Solve(new List<(Vec2, Vec2, double)>
            {
                (YardPoseSolver.MapPos(BlipId.Lightning), a, 1.0),
                (YardPoseSolver.MapPos(BlipId.Cross), b, 1.0)
            });
            double d = Math.Abs(two.S - many.S) + Math.Abs(two.Phi - many.Phi) + (two.P - many.P).Len;
            Check(ref fail, d < 1e-9, "Umeyama n=2 trùng công thức hai mốc", $"lệch {d:E1}");
        }

        // Ba moc that: residual ~ 0. Moc thu ba PHAN CHIEU: van "giai" duoc nhung residual vot len.
        {
            double s = 1.0, phi = -0.4;
            var p = new Vec2(12.0, -70.0);
            var lm = YardPoseSolver.MapPos(BlipId.Lightning);
            var cr = YardPoseSolver.MapPos(BlipId.Cross);
            var pz = YardPoseSolver.MapPos(BlipId.Pizza);

            var pairs = new List<(Vec2, Vec2, double)>
            {
                (lm, Project(lm, s, phi, p), 1.0),
                (cr, Project(cr, s, phi, p), 1.0),
                (pz, Project(pz, s, phi, p), 1.0)
            };
            var good = YardPoseSolver.Solve(pairs);
            Check(ref fail, good.Ok && good.ResidualMu < 1e-9, "ba mốc khớp: residual ≈ 0", $"residual {good.ResidualMu:E1} mu");

            // Guong: lat moc thu ba qua truc ⚡✕.
            var mirrored = new List<(Vec2, Vec2, double)>(pairs);
            mirrored[2] = (new Vec2(pz.X, -pz.Y), pairs[2].Item2, 1.0);
            var bad = YardPoseSolver.Solve(mirrored);
            Check(ref fail, bad.Ok && bad.ResidualMu > 20.0, "mốc phản chiếu bị lộ qua residual",
                  $"residual {F(bad.ResidualMu, 1)} mu > 20");
        }

        // Goc toi: dung quy uoc rel = atan2(dx, -dy) cua NavBot.
        {
            double s = 1.13, phi = 0.9;
            var p = new Vec2(5, 5);
            double theta = YardPoseSolver.ThetaOf(phi);
            // Diem ngay TRUOC MAT: di theo huong nhin mot doan.
            var ahead = new Vec2(p.X + 10 * Math.Cos(theta * YardPoseSolver.Deg2Rad),
                                 p.Y + 10 * Math.Sin(theta * YardPoseSolver.Deg2Rad));
            var (rel, dist) = YardPoseSolver.Aim(ahead, s, phi, p);
            Check(ref fail, Math.Abs(rel) < 1e-9 && Math.Abs(dist - 10 * s) < 1e-9,
                  "điểm trước mặt → rel = 0", $"rel={rel:E1}° dist={F(dist, 3)}px");

            var right = new Vec2(p.X + 10 * Math.Cos((theta + 90) * YardPoseSolver.Deg2Rad),
                                 p.Y + 10 * Math.Sin((theta + 90) * YardPoseSolver.Deg2Rad));
            var (relR, _) = YardPoseSolver.Aim(right, s, phi, p);
            Check(ref fail, Math.Abs(relR - 90.0) < 1e-9, "điểm bên phải → rel = +90°", $"rel={F(relR, 6)}°");
        }

        // Wrap goc.
        Check(ref fail,
              Math.Abs(YardPoseSolver.Wrap(190) + 170) < 1e-12 &&
              Math.Abs(YardPoseSolver.Wrap(-190) - 170) < 1e-12 &&
              Math.Abs(YardPoseSolver.Wrap(540) - 180) < 1e-12,
              "wrap góc về (−180,180]", "");

        // θ ↔ φ đi vòng.
        {
            double worst = 0;
            for (double th = -180; th <= 180; th += 7.5)
                worst = Math.Max(worst, Math.Abs(YardPoseSolver.Wrap(YardPoseSolver.ThetaOf(YardPoseSolver.PhiOf(th)) - th)));
            Check(ref fail, worst < 1e-9, "θ → φ → θ đi vòng", $"lệch {worst:E1}°");
        }

        return fail;
    }

    // ================================================================ anh that

    private static readonly string[] ShotNames = { "nav-far", "nav-marker", "nav-pair-a", "nav-pair-b", "nav-prompt" };

    private sealed class ShotBlips
    {
        public string Name;
        public Bitmap Bmp;
        public NavFrame Frame;
        public BlipMasks Masks;
        public List<BlipHit> Hits;
        public int BottomRowBlobs;

        public BlipHit Get(BlipId id) => Hits.FirstOrDefault(h => h.Id == id);
    }

    private static int RealShots(ElectricProfile p, NavScale s, double ox, double oy, bool learn)
    {
        int fail = 0;

        using var far = VerifyNav.Load(p, "nav-far", out string why);
        if (far is null) { Console.WriteLine($"  [nav-far] bỏ qua: {why} — phần ảnh thật cần ảnh này để học mẫu"); return 0; }

        var farFrame = VerifyNav.Frame(far);
        var tpl = BlipTemplates.Learn(farFrame, s, "nav-far", out string learnWhy);
        Check(ref fail, tpl is not null, "học mẫu blip từ nav-far", tpl?.Describe() ?? learnWhy ?? "");
        if (tpl is null) return fail;

        if (learn)
        {
            try { tpl.Save(p.Key); Console.WriteLine($"      đã lưu mẫu vào {ElectricConfig.MapDir(p.Key)}"); }
            catch (Exception ex) { Check(ref fail, false, "lưu mẫu blip", ex.Message); }
        }

        var shots = new List<ShotBlips>();
        foreach (var name in ShotNames)
        {
            var bmp = name == "nav-far" ? far : VerifyNav.Load(p, name, out _);
            if (bmp is null) { Console.WriteLine($"  [{name}] bỏ qua: chưa chụp"); continue; }

            var f = name == "nav-far" ? farFrame : VerifyNav.Frame(bmp);
            var masks = BlipMasks.Build(f, s);
            var hits = BlipDetector.Detect(f, s, masks, tpl);

            int bottom = 0;
            foreach (var b in ImageOps.Blobs(masks.Yellow)) if (b.Box.Bottom >= masks.Height) bottom++;
            foreach (var b in ImageOps.Blobs(masks.Red)) if (b.Box.Bottom >= masks.Height) bottom++;

            shots.Add(new ShotBlips { Name = name, Bmp = bmp, Frame = f, Masks = masks, Hits = hits, BottomRowBlobs = bottom });
        }

        foreach (var sh in shots)
        {
            var parts = new List<string>();
            foreach (var id in new[] { BlipId.Lightning, BlipId.Cross, BlipId.Pizza })
            {
                var h = sh.Get(id);
                parts.Add(h is null ? $"{BlipTemplates.Name(id)} —" : h.ToString());
            }
            Console.WriteLine($"      [{sh.Name}] {string.Join(" | ", parts)}  (vụn hàng cuối: {sh.BottomRowBlobs})");
            SaveDebug(p.Key, sh);
        }

        // ---- ⚡ va ✕ o ca 5 anh ----
        var missLm = shots.Where(x => x.Get(BlipId.Lightning) is null).Select(x => x.Name).ToList();
        Check(ref fail, missLm.Count == 0 && shots.Count == ShotNames.Length,
              "⚡ nhận được ở cả 5 ảnh", missLm.Count == 0 ? $"{shots.Count}/5 ảnh" : "thiếu: " + string.Join(", ", missLm));

        var missCr = shots.Where(x => x.Get(BlipId.Cross) is null).Select(x => x.Name).ToList();
        Check(ref fail, missCr.Count == 0, "✕ nhận được ở cả 5 ảnh",
              missCr.Count == 0 ? "" : "thiếu: " + string.Join(", ", missCr));

        bool nccOk = shots.All(x => x.Hits.All(h => h.Ncc >= NavTuning.YardNccAccept && !h.ByShapeOnly));
        double minNcc = shots.SelectMany(x => x.Hits).Select(h => h.Ncc).DefaultIfEmpty(0).Min();
        Check(ref fail, nccOk, $"mọi blip nhận qua NCC ≥ {F(NavTuning.YardNccAccept)}", $"NCC thấp nhất {F(minNcc)}");

        // ---- khoang cach ⚡✕ theo hai muc zoom ----
        double Dist(ShotBlips x)
        {
            var a = x.Get(BlipId.Lightning);
            var b = x.Get(BlipId.Cross);
            return a is null || b is null ? double.NaN : new Vec2(b.X - a.X, b.Y - a.Y).Len;
        }

        var near = new[] { "nav-far", "nav-pair-a", "nav-pair-b" };
        var farZoom = new[] { "nav-marker", "nav-prompt" };
        var d1 = shots.Where(x => near.Contains(x.Name)).Select(Dist).Where(double.IsFinite).ToList();
        var d2 = shots.Where(x => farZoom.Contains(x.Name)).Select(Dist).Where(double.IsFinite).ToList();

        Check(ref fail, d1.Count > 0 && d1.All(v => Math.Abs(v - 138.0) <= 4.0),
              "|⚡✕| = 138±4 px ở ảnh 23/08", string.Join(", ", d1.Select(v => F(v, 1))));
        Check(ref fail, d2.Count > 0 && d2.All(v => Math.Abs(v - 175.0) <= 4.0),
              "|⚡✕| = 175±4 px ở ảnh 05/09", string.Join(", ", d2.Select(v => F(v, 1))));

        double ratio = d1.Count > 0 && d2.Count > 0 ? d2.Average() / d1.Average() : double.NaN;
        Check(ref fail, Math.Abs(ratio - 1.27) <= 0.04, "tỉ lệ zoom 05/09 ÷ 23/08 = 1.27±0.04", F(ratio, 3));

        // ---- ✕ cua nav-pair-a bi ghim mep ----
        var pairA = shots.FirstOrDefault(x => x.Name == "nav-pair-a");
        if (pairA is not null)
        {
            var cr = pairA.Get(BlipId.Cross);
            double insetGate = NavTuning.YardEdgeInsetRef * s.Sx;
            bool rejected = cr is not null && (cr.Clipped || cr.InsetPx < insetGate);
            Check(ref fail, rejected, "nav-pair-a: ✕ bị đánh cắt / inset < 6·sx",
                  cr is null ? "không thấy ✕" : $"clipped={cr.Clipped} inset={F(cr.InsetPx, 1)}px < {F(insetGate, 1)}");
        }

        // ---- 🍕 roi dung cho trong he san ----
        var expectPizza = new Vec2(NavTuning.YardPizzaXMu, NavTuning.YardPizzaYMu);
        foreach (var name in new[] { "nav-far", "nav-marker", "nav-prompt" })
        {
            var sh = shots.FirstOrDefault(x => x.Name == name);
            if (sh is null) continue;
            var lm = sh.Get(BlipId.Lightning);
            var cr = sh.Get(BlipId.Cross);
            var pz = sh.Get(BlipId.Pizza);
            if (lm is null || cr is null || pz is null)
            {
                Check(ref fail, false, $"{name}: 🍕 khớp mốc cố định", "thiếu blip để giải");
                continue;
            }
            var fix = YardPoseSolver.Fix2(new Vec2(lm.X - ox, lm.Y - oy), new Vec2(cr.X - ox, cr.Y - oy));
            var got = YardPoseSolver.ToYard(new Vec2(pz.X - ox, pz.Y - oy), fix.S, fix.Phi, fix.P);
            double err = (got - expectPizza).Len;
            Check(ref fail, err <= 6.0, $"{name}: 🍕 trong 6 mu của (63.5,−154.5)",
                  $"đo ({F(got.X, 1)},{F(got.Y, 1)}) lệch {F(err, 1)} mu");
        }

        // ---- nav-pair-a: giai bang (⚡,🍕) roi du doan ✕ ----
        if (pairA is not null)
        {
            var lm = pairA.Get(BlipId.Lightning);
            var pz = pairA.Get(BlipId.Pizza);
            var cr = pairA.Get(BlipId.Cross);
            if (lm is not null && pz is not null && cr is not null)
            {
                var fix = YardPoseSolver.Solve(new List<(Vec2, Vec2, double)>
                {
                    (YardPoseSolver.MapPos(BlipId.Lightning), new Vec2(lm.X - ox, lm.Y - oy), 1.0),
                    (YardPoseSolver.MapPos(BlipId.Pizza), new Vec2(pz.X - ox, pz.Y - oy), 1.0)
                });
                var q = YardPoseSolver.ToScreen(YardPoseSolver.MapPos(BlipId.Cross), fix.S, fix.Phi, fix.P);
                double err = new Vec2(ox + q.X - cr.X, oy + q.Y - cr.Y).Len;
                Check(ref fail, err <= 8.0, "nav-pair-a: (⚡,🍕) dự đoán ✕ trong 8 px", $"lệch {F(err, 1)} px");
            }
            else Console.WriteLine("      [nav-pair-a] không có 🍕 — bỏ qua ca dự đoán ✕");
        }

        // ---- vun hang cuoi ROI ----
        // Hang cuoi ROI (man y=1367 o 2K) luon co vun 7×1 vang/do cua HUD duoi radar. Ca kiem phai
        // chung minh HAI dieu: vun that su co mat, va khong mieng nao tro thanh mot blip.
        int bottomTotal = shots.Sum(x => x.BottomRowBlobs);
        bool bottomLeaked = shots.Any(x => x.Hits.Any(h => h.InsetPx <= 0));
        Check(ref fail, bottomTotal >= shots.Count && !bottomLeaked, "vụn hàng cuối ROI bị loại",
              $"{bottomTotal} khối chạm hàng cuối trên {shots.Count} ảnh, không khối nào thành blip");

        // ---- goc mui ten: dung lai ca kiem cua --verify-nav ----
        foreach (var sh in shots) fail += VerifyNav.ArrowCheck(sh.Name, sh.Frame, s, ox, oy);

        foreach (var sh in shots) if (!ReferenceEquals(sh.Bmp, far)) sh.Bmp.Dispose();
        return fail;
    }

    /// <summary>Ảnh soi bằng mắt: ROI minimap phóng to 2×, blip nhận được khoanh ô.</summary>
    private static void SaveDebug(string key, ShotBlips sh)
    {
        try
        {
            var m = sh.Masks;
            using var bmp = new Bitmap(m.Width * 2, m.Height * 2, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                var src = new Rectangle(m.Local.X, m.Local.Y, m.Width, m.Height);
                g.DrawImage(sh.Bmp, new Rectangle(0, 0, m.Width * 2, m.Height * 2), src, GraphicsUnit.Pixel);

                foreach (var h in sh.Hits)
                {
                    using var pen = new Pen(h.Clipped ? Color.Orange : Color.Lime, 1f);
                    int cx = (int)Math.Round((h.X - m.OffX) * 2), cy = (int)Math.Round((h.Y - m.OffY) * 2);
                    g.DrawRectangle(pen, cx - h.W, cy - h.H, h.W * 2, h.H * 2);
                    g.DrawString($"{BlipTemplates.Name(h.Id)} {h.Ncc:F2}", SystemFonts.DefaultFont,
                                 new SolidBrush(pen.Color), cx + h.W, cy - h.H - 12);
                }
            }
            string dir = ElectricConfig.DebugMapDir(key);
            Directory.CreateDirectory(dir);
            bmp.Save(Path.Combine(dir, "blip-" + sh.Name + ".png"), ImageFormat.Png);
        }
        catch { /* anh soi mat khong duoc phep lam hong ca kiem */ }
    }

    // ================================================================ tracker

    private static readonly NavScale S2K = new(2560, 1440, 0);
    private const double Ox = 217.3, Oy = 1307.2;

    /// <summary>Dựng một blip giả tại đúng chỗ mà pose này sẽ đặt nó trên màn.</summary>
    private static BlipHit Fake(BlipId id, double s, double phi, Vec2 p, double noiseX = 0, double noiseY = 0,
                               double inset = 50, bool clipped = false, Vec2? mapOverride = null)
    {
        var q = YardPoseSolver.ToScreen(mapOverride ?? YardPoseSolver.MapPos(id), s, phi, p);
        return new BlipHit
        {
            Id = id, X = Ox + q.X + noiseX, Y = Oy + q.Y + noiseY,
            Ncc = 0.9, Clipped = clipped, InsetPx = inset, W = 12, H = 15, Area = 150, Fill = 0.85
        };
    }

    private static int TrackerCases()
    {
        int fail = 0;
        var rng = new Random(4242);

        // ---- nhieu 1,5 px: pose phai on dinh ----
        {
            var tr = new YardPoseTracker(S2K, Ox, Oy);
            double s = 1.0, phi = 0.35;
            var truth = new Vec2(20, -60);
            double worst = 0;
            YardPose pose = null;
            for (int i = 0; i < 40; i++)
            {
                double t = i * 0.025;
                double N() => (rng.NextDouble() * 2 - 1) * 1.5;
                pose = tr.Update(new[]
                {
                    Fake(BlipId.Lightning, s, phi, truth, N(), N()),
                    Fake(BlipId.Cross, s, phi, truth, N(), N())
                }, TargetOutput.Lost, t, null);
                if (i >= 10) worst = Math.Max(worst, (pose.P - truth).Len);
            }
            Check(ref fail, pose.Quality == YardQuality.Fix2 && worst <= 2.0,
                  "blip nhiễu 1,5 px → FIX2 ổn định", $"{pose.Quality}, lệch tối đa {F(worst)} mu");
        }

        // ---- zoom doi 1.00 -> 1.27: loai 5 lan roi nhan lai o lan thu 6 ----
        {
            var tr = new YardPoseTracker(S2K, Ox, Oy);
            var truth = new Vec2(0, -50);
            for (int i = 0; i < 10; i++)
                tr.Update(new[] { Fake(BlipId.Lightning, 1.0, 0.2, truth), Fake(BlipId.Cross, 1.0, 0.2, truth) },
                          TargetOutput.Lost, i * 0.025, null);

            int reinitAt = -1;
            for (int i = 0; i < 8; i++)
            {
                var pose = tr.Update(new[] { Fake(BlipId.Lightning, 1.27, 0.2, truth), Fake(BlipId.Cross, 1.27, 0.2, truth) },
                                     TargetOutput.Lost, 0.25 + i * 0.025, null);
                if (pose.ScaleReinit && reinitAt < 0) reinitAt = i;
            }
            Check(ref fail, reinitAt == NavTuning.YardScaleReinitAfter - 1 && Math.Abs(tr.ScaleEma - 1.27) < 1e-6,
                  $"zoom 1.00→1.27: nhận lại sau {NavTuning.YardScaleReinitAfter} lần lệch",
                  $"nhận lại ở lần {reinitAt + 1}, s={F(tr.ScaleEma, 3)}");
        }

        // ---- ✕ bi che: con mot moc -> FIX1 ----
        {
            var tr = new YardPoseTracker(S2K, Ox, Oy);
            var truth = new Vec2(-10, -30);
            double s = 1.0, phi = -0.6;
            for (int i = 0; i < 10; i++)
                tr.Update(new[] { Fake(BlipId.Lightning, s, phi, truth), Fake(BlipId.Cross, s, phi, truth) },
                          TargetOutput.Lost, i * 0.025, null);

            var only = tr.Update(new[] { Fake(BlipId.Lightning, s, phi, truth) }, TargetOutput.Lost, 0.275, null);
            Check(ref fail, only.Quality == YardQuality.Fix1 && (only.P - truth).Len < 1.0,
                  "✕ bị che → FIX1 bằng ⚡ và θ nhớ", $"{only.Quality}, lệch {F((only.P - truth).Len)} mu");

            // Che do day: qua 0,75 s sau FIX2 thi khong con nhan FIX1 nua.
            var late = tr.Update(new[] { Fake(BlipId.Lightning, s, phi, truth) }, TargetOutput.Lost, 1.4, null);
            Check(ref fail, late.Quality != YardQuality.Fix1,
                  $"chế độ dạy: quá {F(NavTuning.YardTeachFix1MaxS, 2)} s sau FIX2 thì không nhận FIX1", late.Quality);
        }

        // ---- ✕ ghim mep (khoang cach co 30 %) bi cong ti le loai ----
        {
            var tr = new YardPoseTracker(S2K, Ox, Oy);
            var truth = new Vec2(30, -70);
            double s = 1.0, phi = 0.1;
            for (int i = 0; i < 10; i++)
                tr.Update(new[] { Fake(BlipId.Lightning, s, phi, truth), Fake(BlipId.Cross, s, phi, truth) },
                          TargetOutput.Lost, i * 0.025, null);

            // ✕ bao sai vi tri: co ve gan ⚡ 30 % (dung cai xay ra khi blip bi cat o mep ROI).
            var shrunk = Fake(BlipId.Cross, s, phi, truth, mapOverride: new Vec2(NavTuning.YardDRef * 0.7, 0));
            var pose = tr.Update(new[] { Fake(BlipId.Lightning, s, phi, truth), shrunk }, TargetOutput.Lost, 0.275, null);
            Check(ref fail, pose.Quality != YardQuality.Fix2 && (pose.P - truth).Len < 1.0,
                  "✕ ghim mép co 30 % → cổng tỉ lệ loại, lùi về FIX1",
                  $"{pose.Quality}, lệch {F((pose.P - truth).Len)} mu, s giữ {F(tr.ScaleEma, 3)}");
        }

        // ---- game nuot delta chuot 40 do: snap sau 3 fix ----
        {
            var tr = new YardPoseTracker(S2K, Ox, Oy);
            var truth = new Vec2(0, -40);
            double s = 1.0;
            double theta0 = YardPoseSolver.ThetaOf(0.0);
            long counts = 0;
            for (int i = 0; i < 10; i++)
                tr.Update(new[] { Fake(BlipId.Lightning, s, 0.0, truth), Fake(BlipId.Cross, s, 0.0, truth) },
                          TargetOutput.Lost, i * 0.025, counts);

            // Nguoi choi quay 40°, nhung count KHONG doi (game nuot delta) -> DR sai han.
            double theta2 = YardPoseSolver.Wrap(theta0 + 40.0);
            double phi2 = YardPoseSolver.PhiOf(theta2);
            int snapAt = -1;
            for (int i = 0; i < 5; i++)
            {
                var pose = tr.Update(new[] { Fake(BlipId.Lightning, s, phi2, truth), Fake(BlipId.Cross, s, phi2, truth) },
                                     TargetOutput.Lost, 0.25 + i * 0.025, counts);
                if (snapAt < 0 && Math.Abs(YardPoseSolver.Wrap(pose.ThetaDeg - theta2)) < 1e-6) snapAt = i;
            }
            Check(ref fail, snapAt == NavTuning.YardHeadingSnapAfter - 1,
                  $"nuốt delta 40° → snap hướng sau {NavTuning.YardHeadingSnapAfter} fix", $"snap ở fix {snapAt + 1}");
        }

        // ---- moc bi cat / sat mep khong duoc dung ----
        {
            var tr = new YardPoseTracker(S2K, Ox, Oy);
            var truth = new Vec2(0, -20);
            var pose = tr.Update(new[]
            {
                Fake(BlipId.Lightning, 1.0, 0.0, truth, inset: 2.0),
                Fake(BlipId.Cross, 1.0, 0.0, truth, clipped: true)
            }, TargetOutput.Lost, 0.0, null);
            Check(ref fail, pose.Quality == YardQuality.None,
                  "mốc chạm mép / inset nhỏ bị loại khỏi phép giải", pose.Quality);
        }

        return fail;
    }

    // ================================================================ recorder

    private static string TempDir(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gta-verify-map", name);
        if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int RecorderCases()
    {
        int fail = 0;
        string dir = TempDir("rec");
        string path = Path.Combine(dir, "rec-test.csv");

        var hits = new[]
        {
            new BlipHit { Id = BlipId.Lightning, X = Ox + 12.5, Y = Oy - 30.25, Ncc = 0.91, InsetPx = 31.5 },
            new BlipHit { Id = BlipId.Cross, X = Ox + 120.0, Y = Oy - 70.5, Ncc = 0.87, InsetPx = 78.0 }
        };
        var dot = new TargetOutput
        {
            State = "LOCKED", Visible = true, X = Ox + 40.5, Y = Oy - 10.5,
            Confidence = 0.88, Quality = "FULL_LOCK", CandidateCount = 1
        };
        var pose = new YardPose
        {
            Quality = YardQuality.Fix2, Conf = 0.93, S = 1.0125, Phi = 0.3456, ThetaDeg = -50.25,
            P = new Vec2(12.34, -56.78), T = new Vec2(41.2, -88.0), Residual = 0.4
        };

        using (var rec = new YardRecorder("verify", path))
        {
            rec.Event(0.0, "SESSION_START", "2560x1440 sx=1.333");
            rec.Tick(1.250, pose, hits, dot, 3, 2, WorldMarker.None, Ox, Oy, prompt: true, panel: false, tickMs: 6.25);
            rec.Tick(1.275, null, Array.Empty<BlipHit>(), TargetOutput.Lost, 0, 0, WorldMarker.None, Ox, Oy,
                     prompt: false, panel: false, tickMs: 5.0);
            rec.Event(2.0, "PANEL_OPEN", "chuyến 1");
        }

        var back = YardRecorder.Parse(path);
        bool ok = back is not null && back.Ticks.Count == 2 && back.Events.Count == 2;
        Check(ref fail, ok, "CSV bản ghi: ghi rồi đọc lại đúng số dòng",
              back is null ? "không đọc được" : $"{back.Ticks.Count} tick, {back.Events.Count} sự kiện");

        if (ok)
        {
            var r = back.Ticks[0];
            bool same = r.Q == YardQuality.Fix2
                        && Math.Abs(r.T - 1.250) < 1e-6
                        && Math.Abs(r.S - 1.0125) < 1e-3
                        && Math.Abs(r.Px.Value - 12.34) < 1e-6
                        && Math.Abs(r.Py.Value + 56.78) < 1e-6
                        // Toa do blip ghi bang mot chu so thap phan — sai so cho phep dung bang do.
                        && Math.Abs(r.Ax.Value - 12.5) < 0.06
                        && Math.Abs(r.Ay.Value + 30.25) < 0.06
                        && Math.Abs(r.Bncc.Value - 0.87) < 1e-6
                        && r.Dotq == "FULL_LOCK"
                        && Math.Abs(r.Dotdist - 41.84) < 0.1
                        && r.Prompt == 1 && r.Panel == 0
                        && r.Xsent is null;
            Check(ref fail, same, "CSV bản ghi: từng trường đi vòng đúng",
                  $"P=({F(r.Px ?? 0)},{F(r.Py ?? 0)}) a=({F(r.Ax ?? 0)},{F(r.Ay ?? 0)}) s={F(r.S, 4)}");

            var r2 = back.Ticks[1];
            Check(ref fail, r2.Ax is null && r2.Bx is null && r2.Q == YardQuality.None,
                  "tick không có blip → cột trống, không phải 0", $"q={r2.Q}");

            Check(ref fail, back.Events[1].Name == "PANEL_OPEN" && Math.Abs(back.Events[1].T - 2.0) < 1e-6,
                  "sự kiện đọc lại đúng tên và mốc thời gian", back.Events[1].Name);
        }

        // Dau phay thap phan cua locale Viet khong duoc phep lot vao file.
        string raw = File.ReadAllText(path);
        Check(ref fail, !raw.Split('\n').Skip(2).Any(l => l.StartsWith("T,") && l.Split(',').Length != 42),
              "mọi dòng tick có đúng 42 cột (không bị dấu phẩy thập phân chen vào)", "");

        // ---- vong dem khung: chon khung gan nhat theo thoi gian ----
        {
            string dir2 = TempDir("ring");
            string p2 = Path.Combine(dir2, "rec-ring.csv");
            using var rec = new YardRecorder("verify", p2);
            var buf = new byte[4 * 4 * 4];
            for (int i = 0; i < NavTuning.YardFrameRingN + 5; i++)
                rec.PushFrame(new NavFrame { Bgra = buf, Stride = 16, Width = 4, Height = 4 }, i * 0.1);
            rec.DumpRing((NavTuning.YardFrameRingN + 4) * 0.1, "open", 0.5, 0.1);
            int n = Directory.Exists(Path.Combine(dir2, "frames"))
                ? Directory.GetFiles(Path.Combine(dir2, "frames"), "*.png").Length
                : 0;
            Check(ref fail, n == 2, "vòng đệm khung: ghi ra đúng 2 ảnh dù đã quay vòng", $"{n} ảnh");
        }

        // ---- cat cua so tiep can ----
        {
            double openT = 10.0;
            var ts = Enumerable.Range(0, 400).Select(i => i * 0.025).ToList();
            var win = ts.Where(t => t >= openT - NavTuning.YardApproachWindowStartS
                                    && t <= openT - NavTuning.YardApproachWindowEndS).ToList();
            // 0,5 s o nhip 25 ms = 20-21 tick tuy diem roi cua so hoc dau cham dong.
            Check(ref fail, win.Count is 20 or 21 && Math.Abs(win[0] - 9.2) <= 0.026 && win[^1] >= 9.65,
                  "cửa sổ tư thế tiếp cận = [t−0,8 ; t−0,3]", $"{win.Count} tick, {F(win[0], 3)}..{F(win[^1], 3)}");
        }

        return fail;
    }

    // ================================================================ builder

    /// <summary>
    /// Sân tổng hợp: ba máy, chín chuyến đi từ chỗ ⚡ tới từng máy, có nhiễu blip và một phiên đổi zoom.
    /// Đây là cách duy nhất kiểm bộ dựng mà không phải đi tay trong game.
    /// </summary>
    private static YardRecording FakeYard(int seed, Vec2[] machines, double s, out int tripCount)
    {
        var rng = new Random(seed);
        var rec = new YardRecording { Path = $"rec-fake-{seed}.csv" };
        double t = 0;
        tripCount = 0;

        var start = new Vec2(4, -4);
        foreach (var machine in machines)
        {
            // Duong di: ⚡ -> diem gap khuc -> tu the tiep can (cach may 5 mu).
            var approachDir = new Vec2(machine.X - start.X, machine.Y - start.Y);
            double len = approachDir.Len;
            var unit = (1.0 / Math.Max(1e-6, len)) * approachDir;
            var approach = machine - 5.0 * unit;

            int steps = Math.Max(30, (int)(len / 0.35));
            for (int i = 0; i <= steps; i++)
            {
                var p = start + (i / (double)steps) * (approach - start);
                double theta = Math.Atan2(unit.Y, unit.X) * YardPoseSolver.Rad2Deg;
                Emit(rec, rng, t, s, p, theta, machine);
                t += 0.025;
            }
            // Dung yen 1,2 s truoc khi bang mo.
            for (int i = 0; i < 48; i++)
            {
                double theta = Math.Atan2(unit.Y, unit.X) * YardPoseSolver.Rad2Deg;
                Emit(rec, rng, t, s, approach, theta, machine);
                t += 0.025;
            }
            rec.Events.Add(new YardEventRow { T = t, Name = "PANEL_OPEN", Detail = "chuyến" });
            t += 3.0;
            rec.Events.Add(new YardEventRow { T = t, Name = "PANEL_CLOSED", Detail = "" });
            tripCount++;
        }
        return rec;
    }

    private static void Emit(YardRecording rec, Random rng, double t, double s, Vec2 p, double thetaDeg, Vec2 machine)
    {
        double phi = YardPoseSolver.PhiOf(thetaDeg);
        double N() => (rng.NextDouble() * 2 - 1) * 0.8;

        var a = YardPoseSolver.ToScreen(YardPoseSolver.MapPos(BlipId.Lightning), s, phi, p);
        var b = YardPoseSolver.ToScreen(YardPoseSolver.MapPos(BlipId.Cross), s, phi, p);
        var d = YardPoseSolver.ToScreen(machine, s, phi, p);

        var row = new YardTickRow
        {
            T = t, Q = YardQuality.Fix2, Conf = 0.9, S = s, Phi = phi, Theta = thetaDeg,
            Ax = a.X + N(), Ay = a.Y + N(), Ancc = 0.9, Ainset = 40,
            Bx = b.X + N(), By = b.Y + N(), Bncc = 0.9, Binset = 40,
            Dotx = d.X, Doty = d.Y, Dotq = "FULL_LOCK", Dotconf = 0.9, Dotstate = "LOCKED",
            Dotdist = d.Len
        };
        rec.Ticks.Add(row);
    }

    private static YardPose PoseOf(YardTickRow r) => new()
    {
        Quality = r.Q, Conf = r.Conf, S = r.S, Phi = r.Phi, ThetaDeg = r.Theta,
        P = new Vec2(r.Px ?? 0, r.Py ?? 0)
    };

    private static BlipHit[] HitsOf(YardTickRow r)
    {
        var outp = new List<BlipHit>();
        void Add(BlipId id, double? x, double? y, double? ncc, double? inset)
        {
            if (x is null) return;
            outp.Add(new BlipHit { Id = id, X = Ox + x.Value, Y = Oy + y.Value, Ncc = ncc ?? 0, InsetPx = inset ?? 0 });
        }
        Add(BlipId.Lightning, r.Ax, r.Ay, r.Ancc, r.Ainset);
        Add(BlipId.Cross, r.Bx, r.By, r.Bncc, r.Binset);
        Add(BlipId.Pizza, r.Zx, r.Zy, r.Zncc, r.Zinset);
        return outp.ToArray();
    }

    private static TargetOutput DotOf(YardTickRow r) => r.Dotx is null
        ? TargetOutput.Lost
        : new TargetOutput
        {
            State = r.Dotstate ?? "LOCKED", Visible = true, X = Ox + r.Dotx.Value, Y = Oy + r.Doty.Value,
            Confidence = r.Dotconf, Quality = r.Dotq ?? "FULL_LOCK", CandidateCount = 1
        };

    private static int BuilderCases()
    {
        int fail = 0;
        var machines = new[] { new Vec2(40, -80), new Vec2(-20, -120), new Vec2(80, -40) };

        var recs = new List<YardRecording>();
        for (int k = 0; k < 3; k++)
            recs.Add(FakeYard(1000 + k, machines, k == 2 ? 1.27 : 1.0, out _));

        var map = YardMapBuilder.Build(recs, S2K, "2560x1440", out var rep);
        Check(ref fail, map is not null, "sân tổng hợp: dựng được bản đồ", rep.Blockers.Count > 0 ? string.Join("; ", rep.Blockers) : "");
        if (map is null) return fail;

        Check(ref fail, map.Markers.Count == machines.Length,
              $"gom đúng {machines.Length} cụm máy", $"{map.Markers.Count} cụm");

        double worstCluster = 0;
        foreach (var m in map.Markers)
        {
            double best = machines.Min(x => (new Vec2(m.X, m.Y) - x).Len);
            worstCluster = Math.Max(worstCluster, best);
        }
        Check(ref fail, worstCluster <= 1.0, "tâm cụm trong 1 mu của máy thật", $"lệch tối đa {F(worstCluster)} mu");

        double worstApproach = 0, worstHeading = 0;
        foreach (var m in map.Markers)
        {
            if (m.Approach is null) { worstApproach = 99; continue; }
            var machine = machines.OrderBy(x => (new Vec2(m.X, m.Y) - x).Len).First();
            var unit = machine - new Vec2(4, -4);
            unit = (1.0 / Math.Max(1e-6, unit.Len)) * unit;
            var expect = machine - 5.0 * unit;
            worstApproach = Math.Max(worstApproach, (new Vec2(m.Approach.X, m.Approach.Y) - expect).Len);
            double expectTh = Math.Atan2(unit.Y, unit.X) * YardPoseSolver.Rad2Deg;
            worstHeading = Math.Max(worstHeading, Math.Abs(YardPoseSolver.Wrap(m.Approach.HeadingDeg - expectTh)));
        }
        Check(ref fail, worstApproach <= 1.5 && worstHeading <= 5.0,
              "tư thế tiếp cận trong 1,5 mu / 5°", $"lệch {F(worstApproach)} mu, {F(worstHeading, 1)}°");

        Check(ref fail, map.Markers.All(m => m.PathLenFromNpcMu > 0), "mọi máy đi tới được từ chỗ ⚡",
              string.Join(", ", map.Markers.Select(m => F(m.PathLenFromNpcMu, 0) + "mu")));

        // Moi diem tren quy dao phai nam trong o free — neu khong thi A* se di vong qua lo thung.
        int outside = 0;
        foreach (var r in recs.SelectMany(x => x.Ticks))
        {
            var fix = YardPoseSolver.Fix2(new Vec2(r.Ax.Value, r.Ay.Value), new Vec2(r.Bx.Value, r.By.Value));
            var (cx, cy) = map.Grid.CellOf(fix.P);
            if (!map.Grid.IsFree(cx, cy)) outside++;
        }
        Check(ref fail, outside == 0, "mọi điểm quỹ đạo nằm trong ô đã đi", $"{outside} điểm lọt ra ngoài");

        Check(ref fail, rep.Ok, "sân tổng hợp đạt go/no-go",
              rep.Ok ? $"2 mốc {map.Stats.Fix2Pct:P0}, rung {F(map.Stats.JitterMu)} mu" : string.Join("; ", rep.Blockers));

        // ---- duong du lieu THAT: ghi ra file qua YardRecorder roi dung ban do tu file do ----
        // Khong co ca nay thi bo ghi va bo dung chi duoc chung minh rieng le, con cho noi giua chung
        // (42 cot CSV) van co the lech mot cot ma khong ai biet.
        {
            string dir = TempDir("chain");
            string path = Path.Combine(dir, "rec-chain.csv");
            using (var rec = new YardRecorder("verify", path))
            {
                foreach (var row in recs[0].Ticks)
                    rec.Tick(row.T, PoseOf(row), HitsOf(row), DotOf(row), 2, 2, WorldMarker.None, Ox, Oy,
                             prompt: false, panel: false, tickMs: 6.0);
                foreach (var e in recs[0].Events) rec.Event(e.T, e.Name, e.Detail);
            }

            var chain = YardRecorder.Parse(path);
            var map2 = chain is null ? null : YardMapBuilder.Build(new[] { chain }, S2K, "2560x1440", out _);
            Check(ref fail, map2 is not null && map2.Markers.Count == machines.Length,
                  "ghi ra CSV rồi dựng lại từ file: vẫn ra đúng số máy",
                  map2 is null ? "không dựng được" : $"{map2.Markers.Count} máy từ {chain.Ticks.Count} tick");
        }

        // ---- JSON di vong + PNG ----
        {
            string dir = TempDir("map");
            string jsonPath = Path.Combine(dir, "yard-map-v1.json");
            map.SaveTo(jsonPath);
            var back = YardMap.LoadFrom(jsonPath);
            bool same = back is not null
                        && back.Markers.Count == map.Markers.Count
                        && back.Grid.W == map.Grid.W && back.Grid.H == map.Grid.H
                        && back.Grid.FreeCount() == map.Grid.FreeCount()
                        && Math.Abs(back.DRef - map.DRef) < 1e-9
                        && Math.Abs(back.Pizza.X - map.Pizza.X) < 1e-9;
            Check(ref fail, same, "yard-map-v1.json ghi rồi đọc lại đúng",
                  back is null ? "không đọc được" : $"{back.Markers.Count} máy, {back.Grid.FreeCount()} ô free");

            string png = Path.Combine(dir, "yard-map.png");
            try { YardMapBuilder.RenderPng(map, png); } catch (Exception ex) { Check(ref fail, false, "vẽ yard-map.png", ex.Message); }
            Check(ref fail, File.Exists(png) && new FileInfo(png).Length > 1024, "vẽ được yard-map.png",
                  File.Exists(png) ? $"{new FileInfo(png).Length / 1024} KB" : "không có file");
        }

        return fail;
    }

    // ================================================================ builder: gan chuyen khong chấm / tiep can du phong

    /// <summary>
    /// Một chuyến ĐI-VỀ đơn: từ <paramref name="startP"/> tới <paramref name="nearP"/>, cách máy đúng
    /// <c>|nearP − machine|</c> — dùng cho các sân tổng hợp cần khống chế chính xác khoảng cách chấm
    /// đích còn lại lúc dừng, thay vì mặc định 5 mu như <see cref="FakeYard"/>.
    /// </summary>
    private static YardRecording OneTripYard(Vec2 machine, Vec2 startP, Vec2 nearP, double s,
                                              int standTicks, int blackoutTicks, out double openT)
    {
        var rng = new Random(20260918 ^ (int)(machine.X * 1000));
        var rec = new YardRecording { Path = "rec-fake-onetrip.csv" };
        double t = 0;
        var dir = nearP - startP;
        var unit = (1.0 / Math.Max(1e-6, dir.Len)) * dir;
        double theta = Math.Atan2(unit.Y, unit.X) * YardPoseSolver.Rad2Deg;

        int steps = Math.Max(30, (int)(dir.Len / 0.35));
        for (int i = 0; i <= steps; i++)
        {
            var p = startP + (i / (double)steps) * dir;
            Emit(rec, rng, t, s, p, theta, machine);
            t += 0.025;
        }
        for (int i = 0; i < standTicks; i++)
        {
            Emit(rec, rng, t, s, nearP, theta, machine);
            t += 0.025;
        }
        for (int i = 0; i < blackoutTicks; i++)
        {
            EmitBlank(rec, t);
            t += 0.025;
        }
        rec.Events.Add(new YardEventRow { T = t, Name = "PANEL_OPEN", Detail = "chuyến" });
        openT = t;
        t += 3.0;
        rec.Events.Add(new YardEventRow { T = t, Name = "PANEL_CLOSED" });
        return rec;
    }

    /// <summary>Chuyến KHÔNG hề có chấm đích (dính mũi tên/⚡ suốt): mọi cột dot để trống.</summary>
    private static void EmitNoDot(YardRecording rec, Random rng, double t, double s, Vec2 p, double thetaDeg)
    {
        double phi = YardPoseSolver.PhiOf(thetaDeg);
        double N() => (rng.NextDouble() * 2 - 1) * 0.8;
        var a = YardPoseSolver.ToScreen(YardPoseSolver.MapPos(BlipId.Lightning), s, phi, p);
        var b = YardPoseSolver.ToScreen(YardPoseSolver.MapPos(BlipId.Cross), s, phi, p);
        rec.Ticks.Add(new YardTickRow
        {
            T = t, Q = YardQuality.Fix2, Conf = 0.9, S = s, Phi = phi, Theta = thetaDeg,
            Ax = a.X + N(), Ay = a.Y + N(), Ancc = 0.9, Ainset = 40,
            Bx = b.X + N(), By = b.Y + N(), Bncc = 0.9, Binset = 40,
            Dotx = null, Doty = null, Dotq = "NONE", Dotconf = 0, Dotstate = "LOST", Dotdist = 0
        });
    }

    /// <summary>Tick trắng: không mốc, không chấm — mô phỏng mất dấu hoàn toàn (HUD panel che minimap).</summary>
    private static void EmitBlank(YardRecording rec, double t) =>
        rec.Ticks.Add(new YardTickRow { T = t, Q = YardQuality.None, Dotq = "NONE", Dotstate = "LOST" });

    /// <summary>
    /// Ba ca PR4 không dựng được bằng <see cref="BuilderCases"/> (nó khoá chặt 3 máy + tiếp cận đúng
    /// khuôn): (1) chuyến ngắn chấm chỉ ra 12–18 px vẫn phải gán được bằng chấm khoá; (2) chuyến không
    /// hề có chấm FULL_LOCK vẫn phải gán đúng máy bằng vị trí đứng; (3) máy mất dấu ngay trước lúc mở
    /// bảng vẫn phải "tới được" qua vị trí đứng dù không có tư thế tiếp cận.
    /// </summary>
    private static int BuilderFallbackCases()
    {
        int fail = 0;

        // ---- (1) chuyen ngan: chấm khong bao gio qua 18 px (< 26.7 px nguong cu) van gan duoc ----
        {
            var machine = new Vec2(0, -30);
            var farP = new Vec2(machine.X, machine.Y + 18.0);   // cach may 18 mu
            var nearP = new Vec2(machine.X, machine.Y + 12.0);  // dung yen cach may 12 mu — khong bao gio < 12
            var rec = OneTripYard(machine, farP, nearP, 1.0, standTicks: 48, blackoutTicks: 0, out _);

            var map = YardMapBuilder.Build(new[] { rec }, S2K, "2560x1440", out var r1);
            bool ok = map is not null && map.Markers.Count == 1 && !map.Markers[0].FromStand
                      && map.Markers[0].Samples > 0
                      && (new Vec2(map.Markers[0].X, map.Markers[0].Y) - machine).Len <= 1.5;
            Check(ref fail, ok, "chuyến ngắn (chấm chỉ ra 12–18 px, dưới ngưỡng 26,7 px cũ) vẫn gán máy bằng chấm khoá",
                  map is null ? string.Join("; ", r1.Blockers)
                              : $"{map.Markers.Count} máy, fromStand={map.Markers.FirstOrDefault()?.FromStand}, " +
                                $"samples={map.Markers.FirstOrDefault()?.Samples}");
        }

        // ---- (2) chuyen khong FULL_LOCK nao: gan dung may bang vi tri dung ----
        {
            var machines = new[] { new Vec2(40, -80), new Vec2(-20, -120) };
            var rec = FakeYard(3000, machines, 1.0, out _);

            // Chuyen rieng: di tu ⚡ toi gan may A (chi 4 mu, KHONG bao gio thay chấm) roi mo bang.
            var start = new Vec2(4, -4);
            var mA = machines[0];
            var unitA = (1.0 / Math.Max(1e-6, (mA - start).Len)) * (mA - start);
            var approachA = mA - 4.0 * unitA;
            var recNoDot = new YardRecording { Path = "rec-fake-nodot.csv" };
            var rngNoDot = new Random(4242);
            double t = 0;
            var dirA = approachA - start;
            int steps = Math.Max(30, (int)(dirA.Len / 0.35));
            double thetaA = Math.Atan2(unitA.Y, unitA.X) * YardPoseSolver.Rad2Deg;
            for (int i = 0; i <= steps; i++)
            {
                var p = start + (i / (double)steps) * dirA;
                EmitNoDot(recNoDot, rngNoDot, t, 1.0, p, thetaA);
                t += 0.025;
            }
            for (int i = 0; i < 48; i++) { EmitNoDot(recNoDot, rngNoDot, t, 1.0, approachA, thetaA); t += 0.025; }
            recNoDot.Events.Add(new YardEventRow { T = t, Name = "PANEL_OPEN", Detail = "chuyến" });
            t += 3.0;
            recNoDot.Events.Add(new YardEventRow { T = t, Name = "PANEL_CLOSED" });

            var map = YardMapBuilder.Build(new[] { rec, recNoDot }, S2K, "2560x1440", out var r2);
            var mkA = map?.Markers.OrderBy(m => (new Vec2(m.X, m.Y) - mA).Len).FirstOrDefault();
            var mkB = map?.Markers.OrderBy(m => (new Vec2(m.X, m.Y) - machines[1]).Len).FirstOrDefault();
            bool ok = map is not null && map.Markers.Count == machines.Length
                      && mkA is not null && mkA.Approach is not null && mkA.Approach.Events == 2
                      && mkB is not null && mkB.Approach is not null && mkB.Approach.Events == 1;
            Check(ref fail, ok, "chuyến không có chấm FULL_LOCK nào vẫn gán đúng máy gần nhất bằng vị trí đứng",
                  map is null ? string.Join("; ", r2.Blockers)
                              : $"{map.Markers.Count} máy, máy A {mkA?.Approach?.Events} lần, máy B {mkB?.Approach?.Events} lần");
        }

        // ---- (3) mat dau 3 s truoc khi mo bang: khong co tiep can nhung van toi duoc qua vi tri dung ----
        {
            var machine = new Vec2(30, -60);
            var start = new Vec2(4, -4);
            var unit = (1.0 / Math.Max(1e-6, (machine - start).Len)) * (machine - start);
            var approach = machine - 5.0 * unit;
            // dung yen 0,5 s (con thay) roi mat dau 3 s (qua ca cua so binh thuong lan du phong 2 s).
            var rec = OneTripYard(machine, start, approach, 1.0, standTicks: 20, blackoutTicks: 120, out _);

            var map = YardMapBuilder.Build(new[] { rec }, S2K, "2560x1440", out var r3);
            var mk = map?.Markers.FirstOrDefault();
            bool ok = map is not null && map.Markers.Count == 1
                      && mk.Approach is null && mk.Stand is not null && mk.PathLenFromNpcMu > 0;
            Check(ref fail, ok, "máy mất dấu 3 s trước khi mở bảng: không có tư thế tiếp cận nhưng vẫn tới được qua vị trí đứng",
                  map is null ? string.Join("; ", r3.Blockers)
                              : $"{map.Markers.Count} máy, approach={(mk.Approach is null ? "null" : "có")}, " +
                                $"stand={(mk.Stand is null ? "null" : "có")}, pathLen={mk.PathLenFromNpcMu}");
        }

        return fail;
    }

    // ================================================================ duong di (YardPath)

    /// <summary>Lưới thử: ô free trong <paramref name="w"/>×<paramref name="h"/> trừ các hộp bị chặn.</summary>
    private static YardGrid TestGrid(double ox, double oy, int w, int h, params (int x0, int y0, int x1, int y1)[] walls)
    {
        var g = YardGrid.Create(ox, oy, w, h, 1.0);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            g.SetFree(x, y, true);
        foreach (var (x0, y0, x1, y1) in walls)
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                g.SetFree(x, y, false);
        return g;
    }

    private static int PathCases()
    {
        int fail = 0;

        // Hanh lang thang: keo day phai rut ve DUNG hai diem (dau va cuoi).
        {
            var g = TestGrid(0, 0, 40, 20);
            var route = YardPath.PlanRoute(g, new Vec2(2.5, 10.5), new Vec2(36.5, 10.5), 15.0, out string why);
            double len = route is null ? -1 : YardPath.Remaining(route, 0, 0);
            Check(ref fail, route is { Count: 2 } && Math.Abs(len - 34.0) < 0.5,
                  "sân trống: kéo dây rút đường A* về một đoạn thẳng",
                  route is null ? why : $"{route.Count} điểm, dài {F(len, 1)} mu");
        }

        // Tuong chan giua: duong phai vong qua khe, va MOI doan phai nam trong o free.
        {
            var g = TestGrid(0, 0, 40, 20, (20, 0, 21, 14));
            var route = YardPath.PlanRoute(g, new Vec2(2.5, 5.5), new Vec2(36.5, 5.5), 15.0, out string why);
            bool inside = route is not null;
            if (route is not null)
                for (int i = 0; i + 1 < route.Count; i++)
                    if (!YardPath.LineOfSight(g, route[i], route[i + 1])) inside = false;
            Check(ref fail, route is { Count: > 2 } && inside,
                  "tường chắn: đường vòng qua khe và không đoạn nào cắt qua ô bị chặn",
                  route is null ? why : $"{route.Count} điểm, dài {F(YardPath.Remaining(route, 0, 0), 1)} mu");
        }

        // Chieu / cung con lai / diem chay truoc tren mot duong gap khuc biet truoc.
        {
            var poly = new List<Vec2> { new(0, 0), new(10, 0), new(10, 10) };
            var (seg, t, off) = YardPath.Project(poly, new Vec2(4, 3));
            double rem = YardPath.Remaining(poly, seg, t);
            var ahead = YardPath.PointAhead(poly, seg, t, 8.0);
            double turn = YardPath.TurnAheadDeg(poly, seg, t, 8.0);
            bool ok = seg == 0 && Math.Abs(t - 0.4) < 1e-9 && Math.Abs(off - 3.0) < 1e-9
                      && Math.Abs(rem - 16.0) < 1e-9
                      && Math.Abs(ahead.X - 10.0) < 1e-9 && Math.Abs(ahead.Y - 2.0) < 1e-9
                      && Math.Abs(turn - 90.0) < 1e-9;
            Check(ref fail, ok, "chiếu điểm / cung còn lại / điểm chạy trước / góc rẽ tới",
                  $"đoạn {seg} t={F(t)} lệch {F(off)} còn {F(rem, 1)} mu, trước mũi ({F(ahead.X, 1)},{F(ahead.Y, 1)}), rẽ {F(turn, 0)}°");
        }

        // Goc re NGOAI cua so 8 mu khong duoc tinh — neu khong thi bo bam tat nuoc rut ca doan thang dai.
        {
            var poly = new List<Vec2> { new(0, 0), new(30, 0), new(30, 10) };
            double turn = YardPath.TurnAheadDeg(poly, 0, 0, 8.0);
            Check(ref fail, turn < 1e-9, "góc rẽ ngoài cửa sổ 8 mu không tính", $"rẽ {F(turn, 0)}°");
        }

        return fail;
    }

    // ================================================================ mo phong bo bam waypoint

    /// <summary>Sân thử hình chữ U: cốc mở sang phải, máy nằm trong lòng cốc.</summary>
    private static YardMap USharpYard(bool holeAtStart)
    {
        var g = TestGrid(-10, -10, 80, 60,
                         (38, 18, 41, 52),      // canh trai cua chu U
                         (38, 18, 62, 21),      // canh tren
                         (38, 49, 62, 52));     // canh duoi
        if (holeAtStart)
            for (int y = 31; y <= 39; y++)
            for (int x = 11; x <= 19; x++)
                g.SetFree(x, y, false);          // dao "chua ai di" ngay duoi chan nguoi choi

        var map = new YardMap
        {
            ScreenKey = "2560x1440",
            DRef = NavTuning.YardDRef,
            Grid = g,
            Landmarks = new Dictionary<string, double[]>
            {
                ["lightning"] = new[] { 0.0, 0.0 },
                ["cross"] = new[] { NavTuning.YardDRef, 0.0 },
                ["pizza"] = new[] { NavTuning.YardPizzaXMu, NavTuning.YardPizzaYMu }
            }
        };
        map.Markers.Add(new YardMarker
        {
            Id = 0, X = 42, Y = 25, Samples = 3, Rms = 0.5,
            Approach = new YardApproach { X = 40, Y = 25, HeadingDeg = 0, Events = 3, Rms = 0.5 },
            Stand = new[] { 41.0, 25.0 },
            PathLenFromNpcMu = 80
        });
        return map;
    }

    private sealed class SimResult
    {
        public bool Handover;
        public double GapMu = 999;
        public int Ticks;
        public int NoSprintTurnTicks;

        /// <summary>Bán kính đưa cho watchdog tăng nhiều nhất bao nhiêu sau MỘT cửa sổ 0,9 s.</summary>
        public double WorstRadiusJumpPx;

        /// <summary>Số tick mà <see cref="NavWatchdog"/> thật sẽ báo KẸT trên bán kính do bộ bám cấp.</summary>
        public int WatchdogStuckTicks;

        public int Plans;
        public int MarkerId = -1;
        public double MaxOffPathMu;

        /// <summary>Số tick chạm mặt vật cản (đứng im tick đó) — cọ vào tường vài tick khi cua là bình thường.</summary>
        public int WallTicks;

        public string Why = "";
    }

    /// <summary>
    /// Người chơi ĐỘNG HỌC: 14 mu/s, yaw ≤ 200°/s, nhiễu pose 1 mu / 2°, và ĐỨNG IM khi đâm tường —
    /// nếu bộ bám lái vào vật cản thì mô phỏng kẹt cứng và ca kiểm hỏng, đúng như trong game.
    /// </summary>
    /// <param name="walls">
    /// Lưới VẬT CẢN THẬT của mô phỏng. Khác lưới trong <paramref name="map"/>: ô "chưa ai đi" không
    /// phải là tường — người chơi đi qua được, chỉ là bản đồ chưa biết. Trộn hai khái niệm này lại thì
    /// ca "đứng ngoài ô đã đi" tự nhốt nhân vật trong một cái hố không có thật.
    /// </param>
    private static SimResult RunFollowSim(YardMap map, YardGrid walls, Vec2 start, double theta0, int seed,
                                          double teleportAtS = -1, Vec2 teleportTo = default,
                                          double maxS = 60.0)
    {
        var rng = new Random(seed);
        var res = new SimResult();
        var follower = new YardFollower(map, S2K, Ox, Oy);
        var watchdog = new NavWatchdog(S2K);
        var radii = new List<double>();

        var goal = YardFollower.GoalOf(map.Markers[0]);
        var machine = new Vec2(map.Markers[0].X, map.Markers[0].Y);
        var p = start;
        double theta = theta0, t = 0, dt = NavTuning.TickMs / 1000.0;
        bool teleported = false;

        while (t < maxS)
        {
            t += dt;
            bool reinit = false;
            if (teleportAtS > 0 && !teleported && t >= teleportAtS)
            {
                teleported = true;
                p = teleportTo;
                reinit = true;
                radii.Clear();
                watchdog.Reset();
            }

            double N(double amp) => (rng.NextDouble() * 2 - 1) * amp;
            double thetaMeas = YardPoseSolver.Wrap(theta + N(2.0));
            var pose = new YardPose
            {
                Quality = YardQuality.Fix2,
                Conf = 0.9,
                S = 1.0,
                Phi = YardPoseSolver.PhiOf(thetaMeas),
                ThetaDeg = thetaMeas,
                P = new Vec2(p.X + N(1.0), p.Y + N(1.0)),
                T = new Vec2(machine.X + N(0.4), machine.Y + N(0.4)),
                Reinit = reinit,
                HitsUsed = 2
            };

            bool on = follower.Step(t, pose, TargetOutput.Lost, WorldMarker.None, false,
                                    double.NaN, double.NaN, false, out var fo);
            res.Ticks++;
            res.MarkerId = follower.MarkerId;
            res.Plans = follower.PlanCount;

            if (follower.State == YardFollow.Handover)
            {
                res.Handover = true;
                res.GapMu = (p - goal).Len;
                res.Why = follower.State;
                return res;
            }
            if (!on) continue;

            res.MaxOffPathMu = Math.Max(res.MaxOffPathMu, fo.OffPathMu);
            if (fo.NoSprint && fo.RemainingMu > 15.0) res.NoSprintTurnTicks++;

            // Ban kinh do bo bam cap, qua CHINH bo do ket cua ban chinh — cua so 0,9 s, trung vi ba
            // dau so voi ba cuoi. Dang tien ma no bao ket thi bo bam da lam hong luoi an toan cua PR2.
            double radius = Math.Sqrt(fo.Dx * fo.Dx + fo.Dy * fo.Dy);
            watchdog.Add(t, fo.Dx, fo.Dy);
            if (watchdog.ImpactStuck(t, true, true, fo.DistPx, fo.RelDeg)) res.WatchdogStuckTicks++;

            radii.Add(radius);
            int lag = (int)Math.Round(NavTuning.ImpactWindowS / dt);
            if (radii.Count > lag)
                res.WorstRadiusJumpPx = Math.Max(res.WorstRadiusJumpPx, radius - radii[^(lag + 1)]);

            // ---- dong hoc ----
            double yawMax = 200.0 * dt;
            theta = YardPoseSolver.Wrap(theta + Math.Clamp(fo.RelDeg, -yawMax, yawMax));
            double speed = 14.0 * (fo.NoSprint ? 0.5 : 1.0);
            if (Math.Abs(fo.RelDeg) > 60.0) speed *= 0.35;
            var step = (speed * dt) * new Vec2(Math.Cos(theta * YardPoseSolver.Deg2Rad),
                                               Math.Sin(theta * YardPoseSolver.Deg2Rad));
            var next = p + step;
            var (cx, cy) = walls.CellOf(next);
            if (walls.IsFree(cx, cy)) p = next;
            else res.WallTicks++;
        }

        res.GapMu = (p - goal).Len;
        res.Why = follower.State;
        return res;
    }

    /// <summary>
    /// "Tới nơi": vào GIAO_LẠI, đứng trong <paramref name="gapMax"/> mu của đích, và KHÔNG mắc kẹt vào
    /// vật cản. Cọ vào mặt tường vài tick lúc cua là chuyện bình thường (đường kéo dây đi sát góc);
    /// mắc kẹt là khi nó tốn một phần đáng kể chuyến đi để đứng im ăn tường.
    /// </summary>
    private static bool Reached(SimResult r, double gapMax) =>
        r.Handover && r.GapMu <= gapMax && r.WallTicks <= Math.Max(4, r.Ticks / 20);

    private static int FollowerCases()
    {
        int fail = 0;

        // Nguong toi dich: ban giao chot o cung con lai <= YardHandoverMu do tren pose NHIEU (±1 mu),
        // nen khoang cach THAT luc do co the vuot ngan ay chut it — do la ly do co so hang 1,5 mu.
        double gapMax = NavTuning.YardHandoverMu + 1.5;

        // ---- (1) san chu U: phai vong qua cup roi vao trong, khong dam tuong ----
        var map = USharpYard(holeAtStart: false);
        var walls = USharpYard(holeAtStart: false).Grid;
        var r1 = RunFollowSim(map, walls, new Vec2(0, 25), 0.0, 77001);
        Check(ref fail, Reached(r1, gapMax),
              $"vật cản chữ U: tới trong {F(gapMax, 1)} mu của tư thế tiếp cận rồi vào GIAO_LẠI",
              $"{(r1.Handover ? "giao lại" : "KHÔNG giao lại (" + r1.Why + ")")} sau {r1.Ticks} tick " +
              $"({F(r1.Ticks * NavTuning.TickMs / 1000.0, 1)}s), cách đích {F(r1.GapMu)} mu, " +
              $"{r1.Plans} lần A*, lệch đường tối đa {F(r1.MaxOffPathMu)} mu, cọ tường {r1.WallTicks} tick");

        Check(ref fail, r1.MarkerId == 0, "khoá đúng máy đích từ điểm vàng quy về hệ sân", $"máy {r1.MarkerId}");

        Check(ref fail, r1.NoSprintTurnTicks > 0, "tắt nước rút ở khúc cua (không phải chỉ lúc sắp tới nơi)",
              $"{r1.NoSprintTurnTicks} tick NoSprint khi còn > 15 mu");

        // Ban kinh dua cho radial watchdog: do bang chinh cua so 0,9 s ma bo do ket dung.
        Check(ref fail, r1.WatchdogStuckTicks == 0 && r1.WorstRadiusJumpPx <= 2.0,
              "bán kính đưa cho watchdog không tăng khi đang tiến (bộ dò kẹt thật không hề báo)",
              $"{r1.WatchdogStuckTicks} tick báo kẹt, sau 0,9 s bán kính tăng nhiều nhất " +
              $"{F(r1.WorstRadiusJumpPx)} px");

        // A* khong duoc chay qua mot lan moi giay (ngan sach tick).
        double simS = r1.Ticks * NavTuning.TickMs / 1000.0;
        Check(ref fail, r1.Plans <= Math.Ceiling(simS) + 2,
              "A* chạy nhiều nhất một lần mỗi giây", $"{r1.Plans} lần trong {F(simS, 1)}s");

        // ---- (2) dung NGOAI o da di ----
        {
            var mapHole = USharpYard(holeAtStart: true);
            var r = RunFollowSim(mapHole, walls, new Vec2(5, 25), 0.0, 77002);
            Check(ref fail, Reached(r, gapMax),
                  "đứng ngoài ô đã đi: chèn đoạn thẳng vào ô free gần nhất rồi vẫn tới nơi",
                  $"{(r.Handover ? "giao lại" : "KHÔNG giao lại (" + r.Why + ")")} sau {r.Ticks} tick, " +
                  $"cách đích {F(r.GapMu)} mu, cọ tường {r.WallTicks} tick");
        }

        // ---- (3) teleport giua duong -> lap lai duong ----
        {
            var r = RunFollowSim(map, walls, new Vec2(0, 25), 0.0, 77003,
                                 teleportAtS: 2.0, teleportTo: new Vec2(20, 45));
            Check(ref fail, Reached(r, gapMax),
                  "teleport giữa đường: lập lại đường từ chỗ mới và vẫn tới nơi",
                  $"{(r.Handover ? "giao lại" : "KHÔNG giao lại (" + r.Why + ")")} sau {r.Ticks} tick, " +
                  $"cách đích {F(r.GapMu)} mu, {r.Plans} lần A*, cọ tường {r.WallTicks} tick");
        }

        // ---- (4) khong co ban do -> nam im hoan toan ----
        {
            var f = new YardFollower(null, S2K, Ox, Oy);
            var pose = new YardPose
            {
                Quality = YardQuality.Fix2, Conf = 0.9, S = 1.0, Phi = 0, ThetaDeg = 0,
                P = new Vec2(0, 0), T = new Vec2(42, 25)
            };
            bool on = f.Step(1.0, pose, TargetOutput.Lost, WorldMarker.None, false, double.NaN, double.NaN,
                             false, out var fo);
            Check(ref fail, !on && !f.HasMap && !fo.Active && f.MarkerId < 0 && f.State == YardFollow.Off,
                  "không có file bản đồ: bộ bám nằm im vĩnh viễn (hành vi y như trước PR3)",
                  $"trạng thái {f.State}");
        }

        // ---- (5) thang thoat ket dang chay -> nhuong quyen nhung van giu khoa may ----
        {
            var f = new YardFollower(map, S2K, Ox, Oy);
            YardPose Pose(double t) => new()
            {
                Quality = YardQuality.Fix2, Conf = 0.9, S = 1.0,
                Phi = YardPoseSolver.PhiOf(0), ThetaDeg = 0,
                P = new Vec2(0, 25), T = new Vec2(42, 25)
            };
            for (int i = 0; i < 12; i++) f.Step(i * 0.025, Pose(i * 0.025), TargetOutput.Lost, WorldMarker.None,
                                                false, double.NaN, double.NaN, false, out _);
            int locked = f.MarkerId;
            bool on = f.Step(1.0, Pose(1.0), TargetOutput.Lost, WorldMarker.None, false, double.NaN, double.NaN,
                             escapeActive: true, out var fo);
            Check(ref fail, locked == 0 && !on && !fo.Active && f.State == YardFollow.Escape,
                  "thang thoát kẹt cầm lái: bộ bám nhường quyền nhưng giữ khoá máy",
                  $"máy {f.MarkerId}, trạng thái {f.State}");
        }

        // ---- (6) cham that da khoa va du gan -> giao lai ngay ----
        {
            var f = new YardFollower(map, S2K, Ox, Oy);
            var pose = new YardPose
            {
                Quality = YardQuality.Fix2, Conf = 0.9, S = 1.0,
                Phi = YardPoseSolver.PhiOf(0), ThetaDeg = 0,
                P = new Vec2(0, 25), T = new Vec2(42, 25)
            };
            for (int i = 0; i < 12; i++)
                f.Step(i * 0.025, pose, TargetOutput.Lost, WorldMarker.None, false, double.NaN, double.NaN, false, out _);
            var dot = new TargetOutput { State = "LOCK", Visible = true, X = Ox + 4, Y = Oy - 12, Confidence = 0.9, Quality = "FULL_LOCK" };
            bool on = f.Step(1.0, pose, dot, WorldMarker.None, false, 20.0 * S2K.Px, 10.0, false, out _);
            bool stays = !f.Step(1.1, pose, TargetOutput.Lost, WorldMarker.None, false, double.NaN, double.NaN, false, out _);
            Check(ref fail, !on && stays && f.State == YardFollow.Handover,
                  "chấm vàng thật khoá ở 20 px / 10°: giao lại ngay và tắt tới hết chuyến",
                  $"trạng thái {f.State}");
        }

        return fail;
    }

    // ================================================================ lap duong tren ban do THAT

    /// <summary>
    /// Bản đồ đã dựng được từ 51 chuyến đi tay: từ ô gần ⚡ (chỗ NPC) phải lập được đường tới đích của
    /// MỌI máy. Đây là ca kiểm gần thực tế nhất mà không cần vào game — nó chạy đúng bộ lập đường mà
    /// <see cref="YardFollower"/> gọi mỗi giây.
    ///
    /// Chưa dạy bản đồ thì KHÔNG phải lỗi — chỉ in một dòng nhắc.
    /// </summary>
    private static int RealMapPlan(ElectricProfile p)
    {
        var map = YardMap.LoadFrom(ElectricConfig.YardMapPath(p.Key));
        Console.WriteLine();
        if (map is null)
        {
            Console.WriteLine($"  [bám waypoint] chưa có yard-map-v1.json trong {ElectricConfig.MapDir(p.Key)} — bỏ qua");
            return 0;
        }

        int fail = 0;
        var npc = YardPoseSolver.MapPos(BlipId.Lightning);
        int ok = 0, weak = 0;
        double worstVsAstar = 0;
        var lines = new List<string>();

        foreach (var m in map.Markers.OrderBy(x => x.Id))
        {
            var goal = YardFollower.GoalOf(m);
            var route = YardPath.PlanRoute(map.Grid, npc, goal, NavTuning.YardOffGridSearchMu, out string why);
            if (route is null || route.Count < 2)
            {
                lines.Add($"máy {m.Id}: KHÔNG lập được đường ({why})");
                continue;
            }

            ok++;
            if (m.FromStand || m.Approach is null || m.Approach.LowConfidence) weak++;
            double len = YardPath.Remaining(route, 0, 0);

            // Keo day chi duoc lam duong NGAN hon duong theo tam o cua A* (cung hai dau mut) — dai hon
            // la loi hinh hoc. So voi m.PathLenFromNpcMu thi khong so sanh duoc: so do bo dung ghi chi
            // tinh phan A* trong luoi, khong tinh doan tu ⚡ vao o free dau tien.
            int c0 = YardPath.NearestFree(map.Grid, npc, NavTuning.YardOffGridSearchMu);
            int c1 = YardPath.NearestFree(map.Grid, goal, NavTuning.YardOffGridSearchMu);
            var cells = YardPath.Plan(map.Grid, c0, c1);
            double cellLen = YardPath.Length(map.Grid, cells)
                             + (map.Grid.CenterOf(c0) - npc).Len + (goal - map.Grid.CenterOf(c1)).Len;
            worstVsAstar = Math.Max(worstVsAstar, len - cellLen);

            lines.Add($"máy {m.Id,2}: {F(len, 1),6} mu, {route.Count,2} khúc " +
                      $"(A* theo ô {F(cellLen, 1)}; bộ dựng ghi {F(Math.Max(0.0, m.PathLenFromNpcMu), 1)}), đích " +
                      $"{(m.Approach is null ? "vị trí đứng" : m.Approach.LowConfidence ? "tiếp cận (1 lần)" : "tiếp cận")} " +
                      $"({F(goal.X, 1)},{F(goal.Y, 1)})");
        }

        Console.WriteLine($"  [bám waypoint] bản đồ thật {p.Key}: {map.Markers.Count} máy, " +
                          $"lưới {map.Grid.W}×{map.Grid.H} ô ({map.Grid.FreeCount()} free), " +
                          $"{map.Stats.TripCount} chuyến, dựng {map.BuiltUtc:yyyy-MM-dd HH:mm} UTC");
        foreach (var line in lines) Console.WriteLine("    " + line);

        Check(ref fail, ok == map.Markers.Count, "bản đồ thật: lập được đường tới tư thế tiếp cận của MỌI máy",
              $"{ok}/{map.Markers.Count} máy ({weak} máy tư thế yếu → giao lại ở " +
              $"{NavTuning.YardHandoverLowConfMu:F0} mu thay vì {NavTuning.YardHandoverMu:F0})");
        Check(ref fail, worstVsAstar <= 1.5, "bản đồ thật: kéo dây không làm đường dài ra",
              $"dài hơn A* theo ô nhiều nhất {F(worstVsAstar, 2)} mu");
        return fail;
    }

    // ================================================================ dung ban do that

    /// <summary>
    /// <c>--verify-map --build</c>: dựng bản đồ từ <c>rec-*.csv</c> thật đã ghi được. Chưa ghi buổi nào
    /// thì KHÔNG phải lỗi — chỉ in một dòng nhắc.
    /// </summary>
    private static int BuildReal(ElectricProfile p, NavScale s)
    {
        var recs = YardRecorder.LoadAll(p.Key, out int broken);
        Console.WriteLine();
        if (recs.Count == 0)
        {
            Console.WriteLine($"  [dựng bản đồ] chưa có rec-*.csv trong {ElectricConfig.MapDir(p.Key)}" +
                              (broken > 0 ? $" ({broken} file hỏng/khác phiên bản)" : ""));
            return 0;
        }

        var map = YardMapBuilder.Build(recs, s, p.Key, out var rep);
        foreach (var line in rep.Lines) Console.WriteLine("  " + line);
        if (map is null) return 0;

        map.Save(p.Key);
        string png = Path.Combine(ElectricConfig.DebugMapDir(p.Key), "yard-map.png");
        YardMapBuilder.RenderPng(map, png);
        YardMap.ClearCache();
        Console.WriteLine($"  đã ghi {ElectricConfig.YardMapPath(p.Key)} và {png}");
        return 0;
    }
}
