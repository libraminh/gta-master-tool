using System.Drawing.Imaging;
using System.Globalization;

namespace GtaMiniGameBot;

/// <summary>Báo cáo của một lần dựng bản đồ — in ra console (verify) hoặc khung Diễn biến (nút UI).</summary>
internal sealed class YardBuildReport
{
    public List<string> Lines { get; } = new();

    /// <summary>Đạt hết tiêu chí go/no-go chưa.</summary>
    public bool Ok { get; set; }

    /// <summary>Những tiêu chí KHÔNG đạt, viết cho người đọc.</summary>
    public List<string> Blockers { get; } = new();

    public void Add(string line) => Lines.Add(line);
}

/// <summary>
/// Dựng <c>yard-map-v1.json</c> từ các bản ghi đi tay.
///
/// Toàn bộ là HÀM THUẦN trên dữ liệu đã đọc (không chạm file, không chạm màn hình) để
/// <c>--verify-map</c> lùa được bằng một sân tổng hợp. Lý do thực tế: chỉnh ngưỡng xong phải dựng
/// lại được trên bản ghi CŨ, chứ không bắt người dùng đi tay 20–30 chuyến thêm lần nữa.
///
/// Bốn lượt giải, theo đúng thứ tự thông tin có được:
///   1. Tick hai blip → pose chính xác; bỏ tick lệch tỉ lệ so với trung vị phiên.
///   2. Mỗi chuyến → vị trí máy đích T_k (trung vị điểm vàng quy về sân).
///   3. Biết T_k rồi thì tick MỘT blip cũng giải được (blip + máy đích = hai mốc) — hồi tố.
///   4. Nội suy qua những khoảng trống ngắn còn lại.
/// </summary>
internal static class YardMapBuilder
{
    private sealed class Solved
    {
        public YardTickRow Row;
        public double T;
        public bool Ok;
        public string Q = YardQuality.None;
        public Vec2 P;
        public double Theta;
        public double S;
        public Vec2? Dot;
        public bool Interpolated;
    }

    private sealed class Trip
    {
        public YardRecording Rec;
        public double Start, OpenT;
        public List<Solved> Ticks = new();
        public Vec2? T;
        public int MarkerId = -1;
        public int LockTicks;
    }

    // ================================================================ chinh

    public static YardMap Build(IReadOnlyList<YardRecording> recs, NavScale scale, string screenKey,
                                out YardBuildReport rep)
    {
        rep = new YardBuildReport();
        rep.Add($"== dựng bản đồ sân {screenKey} ==");

        if (recs is null || recs.Count == 0)
        {
            rep.Add("không có bản ghi nào (rec-*.csv) — bật “Ghi bản đồ” rồi đi tay vài chuyến trước.");
            rep.Blockers.Add("chưa có bản ghi");
            return null;
        }

        int totalTicks = recs.Sum(r => r.Ticks.Count);
        rep.Add($"{recs.Count} bản ghi, {totalTicks} tick.");

        // ---------------- lượt 1: hai blip ----------------
        var trips = new List<Trip>();
        var allSolved = new List<Solved>();
        var scaleModes = new List<double>();
        int droppedScale = 0;

        foreach (var rec in recs)
        {
            var solved = new List<Solved>();
            foreach (var row in rec.Ticks) solved.Add(SolveTwo(row));

            var ss = solved.Where(x => x.Ok).Select(x => x.S).OrderBy(x => x).ToList();
            double sMed = ss.Count > 0 ? Median(ss) : 0;
            if (sMed > 0) scaleModes.Add(sMed);

            foreach (var x in solved)
            {
                if (!x.Ok || sMed <= 0) continue;
                if (Math.Abs(x.S / sMed - 1.0) > NavTuning.YardScaleGate) { x.Ok = false; x.Q = YardQuality.None; droppedScale++; }
            }

            trips.AddRange(SplitTrips(rec, solved));
            allSolved.AddRange(solved);
        }

        int fix2Count = allSolved.Count(x => x.Ok);
        rep.Add($"lượt 1 (hai blip): {fix2Count} tick giải được, bỏ {droppedScale} tick lệch tỉ lệ > {NavTuning.YardScaleGate:P0}.");
        if (fix2Count == 0)
        {
            rep.Add("không tick nào có đủ ⚡ và ✕ — kiểm lại mẫu blip (nút “Học mốc minimap”) rồi ghi lại.");
            rep.Blockers.Add("không giải được tick nào");
            return null;
        }

        // ---------------- 🍕: ước lượng lại vị trí ----------------
        var pizza = EstimatePizza(allSolved, out int pizzaSamples);
        rep.Add($"🍕 ước lượng ({pizza.X:F1},{pizza.Y:F1}) mu từ {pizzaSamples} tick " +
                $"(mặc định ({NavTuning.YardPizzaXMu:F1},{NavTuning.YardPizzaYMu:F1})).");

        int byPizza = 0;
        foreach (var x in allSolved) if (!x.Ok && SolveWithPizza(x, pizza)) byPizza++;
        rep.Add($"lượt 1b (một blip + 🍕): thêm {byPizza} tick.");

        // ---------------- lượt 2: máy đích mỗi chuyến (theo chấm khoá) ----------------
        double dotMinPx = NavTuning.YardLabelMinDotPx * scale.Px;
        int noDot = 0;
        foreach (var trip in trips)
        {
            var pts = new List<Vec2>();
            foreach (var x in trip.Ticks)
            {
                if (!x.Ok || x.Dot is null) continue;
                if (x.Row.Dotq != "FULL_LOCK" || x.Row.Dotdist < dotMinPx) continue;
                pts.Add(x.Dot.Value);
            }
            trip.LockTicks = pts.Count;
            if (pts.Count >= NavTuning.YardLabelMinTicks) trip.T = MedianPoint(pts);
            else noDot++;
        }
        rep.Add($"lượt 2 (chấm khoá ≥ {dotMinPx:F1}px, ≥ {NavTuning.YardLabelMinTicks} tick): " +
                $"{trips.Count} chuyến, {trips.Count - noDot} chuyến có máy đích, {noDot} chuyến chưa có.");

        // ---------------- lượt 3: hồi tố một blip + máy đích ----------------
        int retro = 0;
        foreach (var trip in trips)
        {
            if (trip.T is null) continue;
            foreach (var x in trip.Ticks) if (!x.Ok && SolveWithTarget(x, trip.T.Value)) retro++;
        }
        rep.Add($"lượt 3 (một blip + máy đích): thêm {retro} tick.");

        // ---------------- lượt 4: nội suy khoảng trống ngắn ----------------
        int filled = trips.Sum(Interpolate);
        rep.Add($"lượt 4: nội suy {filled} tick qua khoảng trống ≤ 0,5 s.");

        // ---------------- lưới ----------------
        var grid = BuildGrid(trips, out int visitedMax);
        if (grid is null)
        {
            rep.Add("không dựng được lưới — quá ít tick giải được.");
            rep.Blockers.Add("không dựng được lưới");
            return null;
        }
        rep.Add($"lưới {grid.W}×{grid.H} ô ({grid.CellMu:F1} mu/ô), {grid.FreeCount()} ô đã đi, ô dày nhất {visitedMax} lượt.");

        // ---------------- máy: gom cụm ----------------
        var markers = Cluster(trips, rep);

        // ---------------- gán chuyến chưa có chấm khoá bằng vị trí đứng lúc mở bảng ----------------
        int byStand = LabelByStand(trips, markers, out int newFromStand, out int stillUnlabeled);
        rep.Add($"gán chuyến: {trips.Count - noDot} theo chấm khoá + {byStand} theo vị trí đứng " +
                $"({newFromStand} máy mới) = {trips.Count - stillUnlabeled}/{trips.Count}; " +
                $"{stillUnlabeled} chuyến vẫn chưa gán (không còn tư thế nào quanh lúc mở bảng).");

        // ---------------- tư thế tiếp cận ----------------
        Approaches(trips, markers, rep);

        // ---------------- khả đạt ----------------
        int npcCell = YardPath.NearestFree(grid, new Vec2(0, 0), 25.0);
        int unreachable = 0, noGoal = 0;
        foreach (var m in markers)
        {
            m.PathLenFromNpcMu = -1;
            // Dich = tu the tiep can; khong co thi lui ve vi tri DUNG — thieu ca hai moi la khong co
            // dich nao de lap duong, ca do KHONG tinh la "khong toi duoc" (khong du du lieu de noi gi).
            Vec2? goalPos = m.Approach is not null ? new Vec2(m.Approach.X, m.Approach.Y)
                : m.Stand is { Length: 2 } st ? new Vec2(st[0], st[1])
                : null;
            if (goalPos is null) { noGoal++; continue; }
            if (npcCell < 0) { unreachable++; continue; }
            int goal = YardPath.NearestFree(grid, goalPos.Value, NavTuning.YardOffGridSearchMu);
            var path = goal < 0 ? null : YardPath.Plan(grid, npcCell, goal);
            if (path is null) { unreachable++; continue; }
            m.PathLenFromNpcMu = Math.Round(YardPath.Length(grid, path) * grid.CellMu, 1);
        }
        rep.Add($"khả đạt: {markers.Count - unreachable - noGoal}/{markers.Count} máy đi tới được từ chỗ ⚡" +
                (npcCell < 0 ? " (chưa đi qua chỗ ⚡ nên không có điểm xuất phát)" : "") +
                (noGoal > 0 ? $"; {noGoal} máy không có tư thế tiếp cận lẫn vị trí đứng — bỏ qua khả đạt" : ""));

        // ---------------- thống kê + go/no-go ----------------
        var stats = Statistics(trips, allSolved, scaleModes, rep);
        stats.TripCount = trips.Count;
        stats.UnlabeledTrips = stillUnlabeled;

        var map = new YardMap
        {
            ScreenKey = screenKey,
            DRef = NavTuning.YardDRef,
            BuiltUtc = DateTime.UtcNow,
            Sources = recs.Select(r => Path.GetFileName(r.Path)).ToList(),
            Landmarks = new Dictionary<string, double[]>
            {
                ["lightning"] = new[] { 0.0, 0.0 },
                ["cross"] = new[] { NavTuning.YardDRef, 0.0 },
                ["pizza"] = new[] { Math.Round(pizza.X, 2), Math.Round(pizza.Y, 2) }
            },
            Grid = grid,
            Markers = markers,
            Stats = stats
        };

        GoNoGo(map, rep, stillUnlabeled, trips.Count, unreachable);
        return map;
    }

    // ================================================================ cac luot giai

    private static Solved SolveTwo(YardTickRow row)
    {
        var x = new Solved { Row = row, T = row.T };
        if (row.Ax is null || row.Bx is null) return x;

        var a = new Vec2(row.Ax.Value, row.Ay.Value);
        var b = new Vec2(row.Bx.Value, row.By.Value);
        var fix = YardPoseSolver.Fix2(a, b);
        if (!fix.Ok) return x;

        x.Ok = true;
        x.Q = YardQuality.Fix2;
        x.P = fix.P;
        x.Theta = fix.ThetaDeg;
        x.S = fix.S;
        SetDot(x, fix.S, fix.Phi, fix.P);
        return x;
    }

    private static bool SolveWithPizza(Solved x, Vec2 pizza)
    {
        var row = x.Row;
        if (row.Zx is null) return false;
        Vec2 m1, q1;
        if (row.Ax is not null) { m1 = YardPoseSolver.MapPos(BlipId.Lightning); q1 = new Vec2(row.Ax.Value, row.Ay.Value); }
        else if (row.Bx is not null) { m1 = YardPoseSolver.MapPos(BlipId.Cross); q1 = new Vec2(row.Bx.Value, row.By.Value); }
        else return false;

        var fix = YardPoseSolver.Solve(new List<(Vec2, Vec2, double)>
        {
            (m1, q1, 1.0),
            (pizza, new Vec2(row.Zx.Value, row.Zy.Value), NavTuning.YardPizzaWeight)
        });
        if (!fix.Ok) return false;

        x.Ok = true;
        x.Q = YardQuality.Fix2Lm;
        x.P = fix.P;
        x.Theta = fix.ThetaDeg;
        x.S = fix.S;
        SetDot(x, fix.S, fix.Phi, fix.P);
        return true;
    }

    /// <summary>Hồi tố: một blip + MÁY ĐÍCH đã biết của chuyến cũng là hai mốc.</summary>
    private static bool SolveWithTarget(Solved x, Vec2 target)
    {
        var row = x.Row;
        if (row.Dotx is null || row.Dotq != "FULL_LOCK") return false;
        Vec2 m1, q1;
        if (row.Ax is not null) { m1 = YardPoseSolver.MapPos(BlipId.Lightning); q1 = new Vec2(row.Ax.Value, row.Ay.Value); }
        else if (row.Bx is not null) { m1 = YardPoseSolver.MapPos(BlipId.Cross); q1 = new Vec2(row.Bx.Value, row.By.Value); }
        else if (row.Zx is not null) { m1 = YardPoseSolver.MapPos(BlipId.Pizza); q1 = new Vec2(row.Zx.Value, row.Zy.Value); }
        else return false;

        var fix = YardPoseSolver.Solve(new List<(Vec2, Vec2, double)>
        {
            (m1, q1, 1.0),
            (target, new Vec2(row.Dotx.Value, row.Doty.Value), 0.5)
        });
        if (!fix.Ok) return false;

        x.Ok = true;
        x.Q = YardQuality.Fix2Lm;
        x.P = fix.P;
        x.Theta = fix.ThetaDeg;
        x.S = fix.S;
        SetDot(x, fix.S, fix.Phi, fix.P);
        return true;
    }

    private static void SetDot(Solved x, double s, double phi, Vec2 p)
    {
        if (x.Row.Dotx is null) { x.Dot = null; return; }
        x.Dot = YardPoseSolver.ToYard(new Vec2(x.Row.Dotx.Value, x.Row.Doty.Value), s, phi, p);
    }

    private static Vec2 EstimatePizza(List<Solved> solved, out int samples)
    {
        var pts = new List<Vec2>();
        foreach (var x in solved)
        {
            if (!x.Ok || x.Q != YardQuality.Fix2 || x.Row.Zx is null) continue;
            double phi = YardPoseSolver.PhiOf(x.Theta);
            pts.Add(YardPoseSolver.ToYard(new Vec2(x.Row.Zx.Value, x.Row.Zy.Value), x.S, phi, x.P));
        }
        samples = pts.Count;
        return pts.Count >= 30 ? MedianPoint(pts) : new Vec2(NavTuning.YardPizzaXMu, NavTuning.YardPizzaYMu);
    }

    /// <summary>Chuyến = đoạn từ đầu phiên (hoặc lần đóng bảng trước) tới lần mở bảng kế tiếp.</summary>
    private static List<Trip> SplitTrips(YardRecording rec, List<Solved> solved)
    {
        var outp = new List<Trip>();
        var opens = rec.Events.Where(e => e.Name == "PANEL_OPEN").OrderBy(e => e.T).ToList();
        var closes = rec.Events.Where(e => e.Name == "PANEL_CLOSED").OrderBy(e => e.T).ToList();
        if (opens.Count == 0) return outp;

        double start = solved.Count > 0 ? solved[0].T : 0;
        foreach (var open in opens)
        {
            var trip = new Trip { Rec = rec, Start = start, OpenT = open.T };
            foreach (var x in solved) if (x.T >= start && x.T <= open.T) trip.Ticks.Add(x);
            if (trip.Ticks.Count > 0) outp.Add(trip);

            var next = closes.FirstOrDefault(c => c.T > open.T);
            start = next?.T ?? open.T;
        }
        return outp;
    }

    /// <summary>Nội suy tuyến tính P và θ qua khoảng trống ≤ 0,5 s giữa hai tick đã giải.</summary>
    private static int Interpolate(Trip trip)
    {
        int filled = 0;
        var t = trip.Ticks;
        for (int i = 0; i < t.Count; i++)
        {
            if (t[i].Ok) continue;
            int lo = i - 1;
            while (lo >= 0 && !t[lo].Ok) lo--;
            int hi = i + 1;
            while (hi < t.Count && !t[hi].Ok) hi++;
            if (lo < 0 || hi >= t.Count) continue;
            double span = t[hi].T - t[lo].T;
            if (span <= 0 || span > 0.5) continue;

            double u = (t[i].T - t[lo].T) / span;
            t[i].P = t[lo].P + u * (t[hi].P - t[lo].P);
            t[i].Theta = YardPoseSolver.Wrap(t[lo].Theta + u * YardPoseSolver.Wrap(t[hi].Theta - t[lo].Theta));
            t[i].S = t[lo].S + u * (t[hi].S - t[lo].S);
            t[i].Ok = true;
            t[i].Interpolated = true;
            t[i].Q = YardQuality.Fix1;
            filled++;
        }
        return filled;
    }

    // ================================================================ luoi

    private static YardGrid BuildGrid(List<Trip> trips, out int visitedMax)
    {
        visitedMax = 0;
        var pts = trips.SelectMany(t => t.Ticks).Where(x => x.Ok).Select(x => x.P).ToList();
        if (pts.Count < 10) return null;

        double minX = pts.Min(p => p.X) - NavTuning.YardGridMarginMu;
        double maxX = pts.Max(p => p.X) + NavTuning.YardGridMarginMu;
        double minY = pts.Min(p => p.Y) - NavTuning.YardGridMarginMu;
        double maxY = pts.Max(p => p.Y) + NavTuning.YardGridMarginMu;

        double cell = NavTuning.YardCellMu;
        int w = Math.Max(4, (int)Math.Ceiling((maxX - minX) / cell));
        int h = Math.Max(4, (int)Math.Ceiling((maxY - minY) / cell));
        var grid = YardGrid.Create(minX, minY, w, h, cell);

        var visited = new int[w * h];
        void Mark(Vec2 p)
        {
            var (x, y) = grid.CellOf(p);
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            visited[y * w + x]++;
        }

        foreach (var trip in trips)
        {
            Solved prev = null;
            foreach (var x in trip.Ticks)
            {
                if (!x.Ok) { prev = null; continue; }
                Mark(x.P);
                // Raster ca doan noi hai P: nhip 25 ms o toc do chay la ~0,35 mu, nhung mot tick bi bo
                // co the tao khoang trong vai o — de thung thi A* sau nay khong di qua duoc.
                if (prev is not null && x.T - prev.T <= 0.5)
                {
                    var d = x.P - prev.P;
                    double len = d.Len;
                    if (len <= 10.0)
                    {
                        int steps = (int)Math.Ceiling(len / (cell * 0.5));
                        for (int k = 1; k < steps; k++) Mark(prev.P + (k / (double)steps) * d);
                    }
                }
                prev = x;
            }
        }

        var mask = new Mask(w, h);
        for (int i = 0; i < visited.Length; i++)
        {
            if (visited[i] > 0) mask.Data[i] = 1;
            visitedMax = Math.Max(visitedMax, visited[i]);
        }

        int k2 = 2 * NavTuning.YardFreeDilateCells + 1;
        var free = ImageOps.Dilate(mask, k2, k2);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                grid.SetFree(x, y, free.Data[y * w + x] != 0);

        grid.VisitedMax = visitedMax;
        grid.Pack();
        return grid;
    }

    // ================================================================ may + tu the tiep can

    /// <summary>DBSCAN đơn giản (eps 3 mu, minPts 1) — số máy nhỏ nên không cần chỉ mục không gian.</summary>
    private static List<YardMarker> Cluster(List<Trip> trips, YardBuildReport rep)
    {
        var labelled = trips.Where(t => t.T is not null).ToList();
        var clusters = new List<List<Trip>>();

        foreach (var trip in labelled)
        {
            List<Trip> hit = null;
            foreach (var c in clusters)
            {
                var center = MedianPoint(c.Select(x => x.T.Value).ToList());
                if ((trip.T.Value - center).Len <= NavTuning.YardClusterEpsMu) { hit = c; break; }
            }
            if (hit is null) clusters.Add(new List<Trip> { trip });
            else hit.Add(trip);
        }

        var outp = new List<YardMarker>();
        for (int i = 0; i < clusters.Count; i++)
        {
            var pts = clusters[i].Select(x => x.T.Value).ToList();
            var center = MedianPoint(pts);
            double rms = Math.Sqrt(pts.Sum(p => (p - center).Len * (p - center).Len) / Math.Max(1, pts.Count));
            foreach (var trip in clusters[i]) trip.MarkerId = i;
            outp.Add(new YardMarker
            {
                Id = i,
                X = Math.Round(center.X, 2),
                Y = Math.Round(center.Y, 2),
                Samples = pts.Count,
                Rms = Math.Round(rms, 2)
            });
        }

        rep.Add($"máy: {outp.Count} cụm (eps {NavTuning.YardClusterEpsMu:F0} mu).");
        foreach (var m in outp)
            rep.Add(($"  máy {m.Id}: ({m.X:F1},{m.Y:F1}) mu, {m.Samples} lần, rms {m.Rms:F2} mu"));
        return outp;
    }

    /// <summary>
    /// Gán chuyến KHÔNG có chấm khoá (chấm nằm dưới mũi tên/dính ⚡ suốt chuyến) bằng vị trí đứng lúc
    /// mở bảng — trạm nào người chơi đứng cạnh lúc bấm E cũng chính là trạm chuyến đó nhắm tới. Gọi
    /// SAU <see cref="Cluster"/>: cần tâm cụm đã có để biết "gần" là gần máy nào.
    /// </summary>
    private static int LabelByStand(List<Trip> trips, List<YardMarker> markers, out int newMarkers, out int stillUnlabeled)
    {
        newMarkers = 0;
        stillUnlabeled = 0;
        int labelled = 0;

        foreach (var trip in trips)
        {
            if (trip.T is not null) continue;              // da gan bang chấm khoá (luot 2)

            var stand = ComputeStand(trip);
            if (stand is null) { stillUnlabeled++; continue; }

            int nearest = -1;
            double nearestD = double.MaxValue;
            foreach (var m in markers)
            {
                double d = (stand.Value - new Vec2(m.X, m.Y)).Len;
                if (d < nearestD) { nearestD = d; nearest = m.Id; }
            }

            if (nearest >= 0 && nearestD <= NavTuning.YardStandLabelRadiusMu)
            {
                trip.MarkerId = nearest;
            }
            else
            {
                var nm = new YardMarker
                {
                    Id = markers.Count,
                    X = Math.Round(stand.Value.X, 2),
                    Y = Math.Round(stand.Value.Y, 2),
                    Samples = 0,
                    Rms = 0,
                    FromStand = true
                };
                markers.Add(nm);
                trip.MarkerId = nm.Id;
                newMarkers++;
            }
            labelled++;
        }

        return labelled;
    }

    /// <summary>
    /// Tư thế ĐỨNG của một chuyến: tick lúc <c>OpenT − 0.1 s</c> (đúng lúc bấm E), không có thì lùi về
    /// tick Ok GẦN NHẤT trong <see cref="NavTuning.YardApproachFallbackWindowS"/> trước đó — chấm đích
    /// có thể mất từ sớm (dính ⚡/mũi tên) nhưng pose người chơi (⚡/✕ trên minimap) thường vẫn còn.
    /// </summary>
    private static Vec2? ComputeStand(Trip trip)
    {
        var atOpen = trip.Ticks.Where(x => x.Ok && x.T <= trip.OpenT - 0.1).LastOrDefault();
        if (atOpen is not null) return atOpen.P;
        return trip.Ticks.Where(x => x.Ok && x.T >= trip.OpenT - NavTuning.YardApproachFallbackWindowS
                                    && x.T <= trip.OpenT).LastOrDefault()?.P;
    }

    private static void Approaches(List<Trip> trips, List<YardMarker> markers, YardBuildReport rep)
    {
        int viaFallback = 0;
        foreach (var m in markers)
        {
            var ps = new List<Vec2>();
            var ths = new List<double>();
            var stands = new List<Vec2>();

            foreach (var trip in trips.Where(t => t.MarkerId == m.Id))
            {
                double lo = trip.OpenT - NavTuning.YardApproachWindowStartS;
                double hi = trip.OpenT - NavTuning.YardApproachWindowEndS;
                var win = trip.Ticks.Where(x => x.Ok && x.T >= lo && x.T <= hi).ToList();

                if (win.Count > 0)
                {
                    ps.Add(MedianPoint(win.Select(x => x.P).ToList()));
                    ths.Add(CircMean(win.Select(x => x.Theta).ToList()));
                }
                else
                {
                    // Cua so binh thuong [t-0.8,t-0.3] rong (chuyen qua ngan hoac dung sat cua roi mo
                    // bang ngay) — lui ve tu the Ok CUOI CUNG truoc do trong cua so du phong, huong lay
                    // luon tu tu the do thay vi bo trang ca chuyen.
                    var fb = trip.Ticks.Where(x => x.Ok && x.T >= trip.OpenT - NavTuning.YardApproachFallbackWindowS
                                                  && x.T <= trip.OpenT).LastOrDefault();
                    if (fb is not null)
                    {
                        ps.Add(fb.P);
                        ths.Add(fb.Theta);
                        viaFallback++;
                    }
                }

                var stand = ComputeStand(trip);
                if (stand is not null) stands.Add(stand.Value);
            }

            if (stands.Count > 0)
            {
                var st = MedianPoint(stands);
                m.Stand = new[] { Math.Round(st.X, 2), Math.Round(st.Y, 2) };
            }

            if (ps.Count == 0) continue;      // khong con tu the nao — giu Stand (neu co) lam dich du phong

            var center = MedianPoint(ps);
            double rms = Math.Sqrt(ps.Sum(p => (p - center).Len * (p - center).Len) / ps.Count);

            m.Approach = new YardApproach
            {
                X = Math.Round(center.X, 2),
                Y = Math.Round(center.Y, 2),
                HeadingDeg = Math.Round(CircMean(ths), 1),
                Events = ps.Count,
                Rms = Math.Round(rms, 2),
                LowConfidence = ps.Count < 2
            };
        }

        int noApproach = markers.Count(x => x.Approach is null);
        int low = markers.Count(x => x.Approach is not null && x.Approach.LowConfidence);
        rep.Add($"tư thế tiếp cận: {markers.Count(x => x.Approach is not null)}/{markers.Count} máy có " +
                $"({viaFallback} chuyến lấy tư thế dự phòng ≤ {NavTuning.YardApproachFallbackWindowS:F1}s), " +
                $"{low} máy chỉ 1 lần (lowConfidence), {noApproach} máy không có tư thế tiếp cận (dùng vị trí đứng nếu có).");
        foreach (var m in markers.Where(x => x.Approach is not null))
            rep.Add($"  máy {m.Id} tiếp cận ({m.Approach.X:F1},{m.Approach.Y:F1}) hướng {m.Approach.HeadingDeg:F0}° " +
                    $"rms {m.Approach.Rms:F2} mu, {m.Approach.Events} lần");
    }

    // ================================================================ thong ke

    private static YardStats Statistics(List<Trip> trips, List<Solved> all, List<double> scaleModes, YardBuildReport rep)
    {
        var stats = new YardStats { ScaleModes = scaleModes.Select(x => Math.Round(x, 3)).Distinct().ToArray() };

        // "Dang di" = tick khong nam trong 1,5 s truoc luc mo bang (luc do nguoi choi dung yen tai may).
        var moving = new List<Solved>();
        var still = new List<Solved>();
        foreach (var trip in trips)
            foreach (var x in trip.Ticks)
                (x.T >= trip.OpenT - 1.5 ? still : moving).Add(x);

        int movingN = Math.Max(1, moving.Count);
        int onlineFix2 = moving.Count(x => x.Row.Q is YardQuality.Fix2 or YardQuality.Fix2Lm);
        int onlineFix1 = moving.Count(x => x.Row.Q == YardQuality.Fix1);
        int retroFix2 = moving.Count(x => x.Ok && !x.Interpolated);
        int retroAny = moving.Count(x => x.Ok);

        stats.Fix2Pct = Math.Round(retroFix2 / (double)movingN, 3);
        rep.Add($"tick đang đi {moving.Count}: trực tuyến 2mốc {100.0 * onlineFix2 / movingN:F0}% " +
                $"1mốc {100.0 * onlineFix1 / movingN:F0}%; hồi tố 2mốc {100.0 * retroFix2 / movingN:F0}% " +
                $"(kể cả nội suy {100.0 * retroAny / movingN:F0}%)");

        // Rung khi dung yen: do tren cua so 1 s truoc moi lan mo bang.
        var jitterP = new List<double>();
        var jitterT = new List<double>();
        foreach (var trip in trips)
        {
            var win = trip.Ticks.Where(x => x.Ok && x.T >= trip.OpenT - 1.0 && x.T <= trip.OpenT).ToList();
            if (win.Count < 5) continue;
            var c = MedianPoint(win.Select(x => x.P).ToList());
            jitterP.Add(Math.Sqrt(win.Sum(x => (x.P - c).Len * (x.P - c).Len) / win.Count));
            double mth = CircMean(win.Select(x => x.Theta).ToList());
            jitterT.Add(Math.Sqrt(win.Sum(x => Math.Pow(YardPoseSolver.Wrap(x.Theta - mth), 2)) / win.Count));
        }
        stats.JitterMu = jitterP.Count > 0 ? Math.Round(Median(jitterP), 2) : 0;
        stats.JitterDeg = jitterT.Count > 0 ? Math.Round(Median(jitterT), 2) : 0;
        rep.Add(($"rung khi đứng yên: {stats.JitterMu:F2} mu / {stats.JitterDeg:F2}° (trung vị {jitterP.Count} lần)"));

        // Toc do chay: de quy doi mu sang met.
        //
        // Do tren BASELINE ~0,5 s chu khong giua hai tick lien tiep. O nhip 25 ms nguoi chay di ~0,35 mu,
        // trong khi nhieu blip 1 px cung cho ~1 mu sai so vi tri — lay hieu hai tick canh nhau thi con
        // so do duoc la NHIEU chu khong phai toc do (do thu: ra 32 mu/s thay vi 13).
        var speeds = new List<double>();
        foreach (var trip in trips)
        {
            var ok = trip.Ticks.Where(x => x.Ok).ToList();
            int j = 0;
            for (int i = 0; i < ok.Count; i++)
            {
                if (j < i) j = i;
                while (j < ok.Count && ok[j].T - ok[i].T < 0.4) j++;
                if (j >= ok.Count) break;
                double dt = ok[j].T - ok[i].T;
                if (dt > 0.6) continue;
                double v = (ok[j].P - ok[i].P).Len / dt;
                if (v > 1.0) speeds.Add(v);
            }
        }
        stats.SpeedMuPerS = speeds.Count > 0 ? Math.Round(Median(speeds), 2) : 0;
        rep.Add($"tốc độ chạy {stats.SpeedMuPerS:F1} mu/s → 1 mu ≈ {(stats.SpeedMuPerS > 0 ? 7.0 / stats.SpeedMuPerS : 0):F2} m " +
                "(lấy tốc độ chạy nước rút GTA ≈ 7 m/s)");

        // Histogram ti le + do hien dien blip theo 8 huong nhin.
        if (scaleModes.Count > 0)
            rep.Add("tỉ lệ s mỗi phiên: " + string.Join(", ", scaleModes.Select(x => x.ToString("F3", CultureInfo.InvariantCulture))));

        var okAll = all.Where(x => x.Ok).ToList();
        if (okAll.Count > 0)
        {
            var sb = new List<string>();
            for (int k = 0; k < 8; k++)
            {
                double lo = -180 + k * 45, hi = lo + 45;
                var bucket = okAll.Where(x => x.Theta >= lo && x.Theta < hi).ToList();
                if (bucket.Count == 0) { sb.Add($"{lo:F0}°:–"); continue; }
                sb.Add(string.Format(CultureInfo.InvariantCulture, "{0:F0}°:⚡{1:F0}%✕{2:F0}%🍕{3:F0}%",
                    lo,
                    100.0 * bucket.Count(x => x.Row.Ax is not null) / bucket.Count,
                    100.0 * bucket.Count(x => x.Row.Bx is not null) / bucket.Count,
                    100.0 * bucket.Count(x => x.Row.Zx is not null) / bucket.Count));
            }
            rep.Add("độ hiện diện blip theo hướng nhìn: " + string.Join(" | ", sb));

            // Phong bi inset: dinh o 0 nghia la blip dang bi ghim mep ROI.
            var insets = all.Select(x => x.Row.Binset).Where(v => v is not null).Select(v => v.Value).ToList();
            if (insets.Count > 0)
            {
                var hist = new int[6];
                foreach (double v in insets) hist[Math.Min(5, (int)(v / 5))]++;
                rep.Add("inset ✕ (px, bước 5): " + string.Join(" ", hist.Select((c, i) => $"{i * 5}+:{100.0 * c / insets.Count:F0}%")));
            }

            var nccs = all.Select(x => x.Row.Bncc).Where(v => v is not null).Select(v => v.Value).OrderBy(v => v).ToList();
            if (nccs.Count > 0)
                rep.Add($"NCC của ✕: nhỏ nhất {nccs[0]:F2}, " +
                        $"phân vị 10 {nccs[Math.Min(nccs.Count - 1, nccs.Count / 10)]:F2}, trung vị {Median(nccs):F2}");
        }

        return stats;
    }

    private static void GoNoGo(YardMap map, YardBuildReport rep, int unlabeled, int tripCount, int unreachable)
    {
        rep.Add("");
        rep.Add("-- go / no-go --");

        void Gate(bool ok, string text)
        {
            rep.Add((ok ? "  [ĐẠT] " : "  [CHƯA] ") + text);
            if (!ok) rep.Blockers.Add(text);
        }

        Gate(map.Stats.Fix2Pct >= 0.70,
             ($"≥ 70 % tick đang đi giải được bằng 2 mốc — đang {map.Stats.Fix2Pct:P0}"));
        Gate(map.Stats.JitterMu <= 1.5 && map.Stats.JitterDeg <= 3.0,
             ($"rung khi đứng yên ≤ 1,5 mu / 3° — đang {map.Stats.JitterMu:F2} mu / {map.Stats.JitterDeg:F2}°"));

        double worstRms = map.Markers.Count == 0 ? 99 : map.Markers.Max(m => m.Rms);
        Gate(map.Markers.Count > 0 && worstRms <= 2.0,
             ($"mọi máy rms ≤ 2 mu — tệ nhất {worstRms:F2} mu trên {map.Markers.Count} máy"));

        Gate(unreachable == 0, $"mọi máy tới được từ chỗ ⚡ (tính cả đi qua vị trí đứng dự phòng) — {unreachable} máy không tới được");

        double unlabelledPct = tripCount == 0 ? 1 : unlabeled / (double)tripCount;
        Gate(unlabelledPct <= 0.10,
             ($"≤ 10 % chuyến chưa gán máy — đang {unlabelledPct:P0} ({unlabeled}/{tripCount})"));

        int noApproach = map.Markers.Count(m => m.Approach is null);
        rep.Add($"  [tin]  {noApproach}/{map.Markers.Count} máy không có tư thế tiếp cận (chỉ có vị trí đứng, " +
                "nếu có) — không tính vào go/no-go, chỉ để biết bước bám waypoint sẽ phải lái thô hơn ở đó.");

        rep.Ok = rep.Blockers.Count == 0;
        rep.Add(rep.Ok
            ? "KẾT LUẬN: ĐẠT — bản đồ dùng được, có thể bật bám waypoint ở bước sau."
            : "KẾT LUẬN: CHƯA ĐẠT — " + string.Join("; ", rep.Blockers));
    }

    // ================================================================ ve

    /// <summary>Ảnh soi bằng mắt: ô đã đi sáng, chưa biết tối, quỹ đạo theo chuyến, máy và tư thế tiếp cận.</summary>
    public static void RenderPng(YardMap map, string path, int cellPx = 3)
    {
        if (map?.Grid is null) return;
        var g0 = map.Grid;
        int pad = 30;
        int w = g0.W * cellPx + pad * 2, h = g0.H * cellPx + pad * 2;

        using var bmp = new Bitmap(Math.Max(64, w), Math.Max(64, h), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(16, 18, 22));
            var clear = g0.Clearance();

            for (int y = 0; y < g0.H; y++)
                for (int x = 0; x < g0.W; x++)
                {
                    if (!g0.IsFree(x, y)) continue;
                    // To theo khoang thoat: giua hanh lang sang hon, sat mep toi hon.
                    int v = (int)Math.Clamp(70 + 24 * clear[y * g0.W + x], 70, 235);
                    using var br = new SolidBrush(Color.FromArgb(v, v, (int)Math.Min(255, v * 1.05)));
                    g.FillRectangle(br, pad + x * cellPx, pad + y * cellPx, cellPx, cellPx);
                }

            PointF Pt(double mx, double my) => new(
                (float)(pad + (mx - g0.OriginX) / g0.CellMu * cellPx),
                (float)(pad + (my - g0.OriginY) / g0.CellMu * cellPx));

            void Glyph(string text, Vec2 p, Color c)
            {
                var q = Pt(p.X, p.Y);
                using var br = new SolidBrush(c);
                g.FillEllipse(br, q.X - 4, q.Y - 4, 8, 8);
                g.DrawString(text, SystemFonts.DefaultFont, br, q.X + 5, q.Y - 7);
            }

            foreach (var m in map.Markers)
            {
                var q = Pt(m.X, m.Y);
                using var pen = new Pen(Color.FromArgb(255, 205, 70), 1.5f);
                float r = (float)Math.Max(3.0, m.Rms / g0.CellMu * cellPx);
                g.DrawEllipse(pen, q.X - r, q.Y - r, r * 2, r * 2);
                g.DrawString(m.Id.ToString(CultureInfo.InvariantCulture), SystemFonts.DefaultFont,
                             new SolidBrush(pen.Color), q.X + r, q.Y - 14);

                if (m.Approach is null) continue;
                var a = Pt(m.Approach.X, m.Approach.Y);
                using var apen = new Pen(m.Approach.LowConfidence ? Color.Orange : Color.LimeGreen, 1.5f)
                {
                    EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor
                };
                double th = m.Approach.HeadingDeg * YardPoseSolver.Deg2Rad;
                g.DrawLine(apen, a.X, a.Y, a.X + (float)(Math.Cos(th) * 14), a.Y + (float)(Math.Sin(th) * 14));
                g.DrawLine(apen, a.X, a.Y, q.X, q.Y);
            }

            Glyph("lightning", new Vec2(0, 0), Color.Gold);
            Glyph("cross", new Vec2(map.DRef, 0), Color.OrangeRed);
            Glyph("pizza", map.Pizza, Color.HotPink);

            string legend = string.Format(CultureInfo.InvariantCulture,
                "{0} máy · {1} chuyến · 2 mốc {2:P0} · rung {3:F2} mu/{4:F2}° · {5:F1} mu/s",
                map.Markers.Count, map.Stats.TripCount, map.Stats.Fix2Pct, map.Stats.JitterMu,
                map.Stats.JitterDeg, map.Stats.SpeedMuPerS);
            g.DrawString(legend, SystemFonts.DefaultFont, Brushes.White, 6, 6);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
    }

    // ================================================================ tien ich

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        var a = v.ToArray();
        Array.Sort(a);
        int n = a.Length;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
    }

    private static Vec2 MedianPoint(List<Vec2> pts) =>
        new(Median(pts.Select(p => p.X).ToList()), Median(pts.Select(p => p.Y).ToList()));

    /// <summary>Trung bình VÒNG của góc — trung bình thường sẽ nhảy bậy quanh ±180°.</summary>
    private static double CircMean(List<double> deg)
    {
        if (deg.Count == 0) return 0;
        double sx = 0, sy = 0;
        foreach (double d in deg)
        {
            sx += Math.Cos(d * YardPoseSolver.Deg2Rad);
            sy += Math.Sin(d * YardPoseSolver.Deg2Rad);
        }
        return YardPoseSolver.Wrap(Math.Atan2(sy, sx) * YardPoseSolver.Rad2Deg);
    }
}
