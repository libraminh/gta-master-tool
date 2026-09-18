using System.Text.Json;
using System.Text.Json.Serialization;

namespace GtaMiniGameBot;

/// <summary>
/// Lưới ô vuông của sân, đơn vị mu. Ô "free" = ô người chơi đã từng đi qua (nở 1 ô).
///
/// Vì sao lưu bằng BIT chứ không mảng byte: 260×300 ô là 78 000 giá trị 0/1; dạng bit là ~9,8 KB,
/// base64 ~13 KB — vừa đủ nhỏ để nằm gọn trong một file JSON đọc được bằng mắt.
/// </summary>
internal sealed class YardGrid
{
    public YardGrid() { }

    /// <summary>Toạ độ sân (mu) của góc ô (0,0).</summary>
    public double OriginX { get; set; }

    public double OriginY { get; set; }

    public double CellMu { get; set; } = NavTuning.YardCellMu;

    public int W { get; set; }

    public int H { get; set; }

    /// <summary>Bitmap 1 bit/ô, hàng trước cột sau, bit thấp trước.</summary>
    public string FreeBase64 { get; set; }

    /// <summary>Số lần đi qua ô dày nhất — để biết bản ghi có đủ dày không.</summary>
    public int VisitedMax { get; set; }

    [JsonIgnore]
    private byte[] _bits;

    [JsonIgnore]
    private float[] _clear;

    public int Count => W * H;

    public static YardGrid Create(double originX, double originY, int w, int h, double cellMu)
    {
        var g = new YardGrid { OriginX = originX, OriginY = originY, W = w, H = h, CellMu = cellMu };
        g._bits = new byte[(w * h + 7) / 8];
        return g;
    }

    private byte[] Bits
    {
        get
        {
            if (_bits is not null) return _bits;
            int need = (W * H + 7) / 8;
            try { _bits = string.IsNullOrEmpty(FreeBase64) ? new byte[need] : Convert.FromBase64String(FreeBase64); }
            catch { _bits = new byte[need]; }
            if (_bits.Length < need) Array.Resize(ref _bits, need);
            return _bits;
        }
    }

    public bool IsFree(int i) => i >= 0 && i < W * H && (Bits[i >> 3] & (1 << (i & 7))) != 0;

    public bool IsFree(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && IsFree(y * W + x);

    public void SetFree(int x, int y, bool value)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        int i = y * W + x;
        if (value) Bits[i >> 3] |= (byte)(1 << (i & 7));
        else Bits[i >> 3] &= (byte)~(1 << (i & 7));
        _clear = null;
    }

    public void Pack() => FreeBase64 = Convert.ToBase64String(Bits);

    public int FreeCount()
    {
        int n = 0;
        for (int i = 0; i < W * H; i++) if (IsFree(i)) n++;
        return n;
    }

    public (int x, int y) CellOf(Vec2 p) => (
        (int)Math.Floor((p.X - OriginX) / CellMu),
        (int)Math.Floor((p.Y - OriginY) / CellMu));

    public Vec2 CenterOf(int x, int y) => new(OriginX + (x + 0.5) * CellMu, OriginY + (y + 0.5) * CellMu);

    public Vec2 CenterOf(int i) => CenterOf(i % W, i / W);

    /// <summary>
    /// Khoảng thoát (số Ô) tới ô không-free gần nhất — dùng lại <see cref="ImageOps.Clearance"/>, đúng
    /// bộ EDT mà bộ lập tuyến bảng Water &amp; Power dùng. Tính một lần rồi giữ.
    /// </summary>
    public float[] Clearance()
    {
        if (_clear is not null) return _clear;
        var blocked = new Mask(Math.Max(1, W), Math.Max(1, H));
        for (int i = 0; i < W * H; i++) blocked.Data[i] = IsFree(i) ? (byte)0 : (byte)1;
        _clear = ImageOps.Clearance(blocked);
        return _clear;
    }
}

internal sealed class YardApproach
{
    public YardApproach() { }
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Hướng nhìn của người chơi lúc tiếp cận (độ, hệ sân).</summary>
    public double HeadingDeg { get; set; }

    /// <summary>Số lần đã quan sát tư thế này.</summary>
    public int Events { get; set; }

    public double Rms { get; set; }

    /// <summary>Chỉ có một lần quan sát — 3 m cuối nên lái như cũ.</summary>
    public bool LowConfidence { get; set; }
}

internal sealed class YardMarker
{
    public YardMarker() { }
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Samples { get; set; }
    public double Rms { get; set; }
    public YardApproach Approach { get; set; }

    /// <summary>Chỗ người chơi ĐỨNG lúc bảng mở (t−0.1 s).</summary>
    public double[] Stand { get; set; }

    /// <summary>Độ dài đường A* từ ô gần ⚡ tới tư thế tiếp cận; âm = không tới được.</summary>
    public double PathLenFromNpcMu { get; set; }
}

internal sealed class YardStats
{
    public YardStats() { }
    public double Fix2Pct { get; set; }
    public double JitterMu { get; set; }
    public double JitterDeg { get; set; }
    public double[] ScaleModes { get; set; }
    public int TripCount { get; set; }
    public int UnlabeledTrips { get; set; }
    public double SpeedMuPerS { get; set; }
}

/// <summary>
/// Bản đồ sân đã dựng. Ghi temp rồi <c>File.Move(…, true)</c> như <see cref="BoardRouteCache"/>:
/// nửa file JSON còn tệ hơn không có file, vì nó nuốt mất 20–30 chuyến đi tay.
///
/// <b>Không có file map thì mọi thứ vẫn chạy y như hôm nay</b> — đó là hợp đồng mà PR3 dựa vào để
/// bật bộ bám waypoint một cách an toàn.
/// </summary>
internal sealed class YardMap
{
    public const int SchemaVersion = 1;

    public YardMap() { }

    public int Version { get; set; } = SchemaVersion;
    public string ScreenKey { get; set; }
    public double DRef { get; set; } = NavTuning.YardDRef;
    public DateTime BuiltUtc { get; set; }
    public List<string> Sources { get; set; } = new();

    /// <summary>Toạ độ sân của ba blip: <c>lightning</c>, <c>cross</c>, <c>pizza</c>.</summary>
    public Dictionary<string, double[]> Landmarks { get; set; } = new();

    public YardGrid Grid { get; set; }
    public List<YardMarker> Markers { get; set; } = new();
    public YardStats Stats { get; set; } = new();

    [JsonIgnore]
    public Vec2 Pizza =>
        Landmarks is not null && Landmarks.TryGetValue("pizza", out var v) && v is { Length: 2 }
            ? new Vec2(v[0], v[1])
            : new Vec2(NavTuning.YardPizzaXMu, NavTuning.YardPizzaYMu);

    /// <summary>
    /// camelCase để khớp đúng khuôn <c>yard-map-v1.json</c> đã chốt trong kế hoạch — đây là hợp đồng
    /// mà bộ bám waypoint sẽ đọc. Đọc thì không phân biệt hoa thường nên file cũ vẫn vào được.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void SaveTo(string path)
    {
        Grid?.Pack();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(temp, path, true);
    }

    public void Save(string key) => SaveTo(ElectricConfig.YardMapPath(key));

    public static YardMap LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var map = JsonSerializer.Deserialize<YardMap>(File.ReadAllText(path), JsonOpts);
            return map is null || map.Version != SchemaVersion || map.Grid is null ? null : map;
        }
        catch { return null; }
    }

    private static readonly Dictionary<string, YardMap> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Đọc bản đồ của một độ phân giải, nhớ lại kết quả. Thiếu file → null (không phải lỗi).</summary>
    public static YardMap Load(string key)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var map = LoadFrom(ElectricConfig.YardMapPath(key));
            Cache[key] = map;
            return map;
        }
    }

    public static void ClearCache()
    {
        lock (Cache) Cache.Clear();
    }
}

/// <summary>
/// Lập đường trên lưới sân — A* 8 hướng, cùng dạng chi phí rủi ro <c>c/(clear+0.6)²</c> mà
/// <see cref="BoardPlanner"/> dùng cho tuyến dây: đi giữa hành lang chứ không men mép, vì mép hành
/// lang trong sân chính là đế cột và tủ điện.
///
/// PR1 chỉ dùng nó để trả lời "mọi máy có tới được không" trong báo cáo go/no-go.
/// </summary>
internal static class YardPath
{
    private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] Dy = { 0, 0, 1, -1, 1, -1, 1, -1 };

    /// <summary>Ô free gần nhất trong bán kính <paramref name="maxR"/> ô — khuôn <c>NearestFree</c> của BoardPlanner.</summary>
    public static int NearestFree(YardGrid g, int x, int y, int maxR)
    {
        if (g.IsFree(x, y)) return y * g.W + x;
        for (int r = 1; r <= maxR; r++)
        {
            for (int yy = y - r; yy <= y + r; yy++)
            {
                if (g.IsFree(x - r, yy)) return yy * g.W + (x - r);
                if (g.IsFree(x + r, yy)) return yy * g.W + (x + r);
            }
            for (int xx = x - r + 1; xx < x + r; xx++)
            {
                if (g.IsFree(xx, y - r)) return (y - r) * g.W + xx;
                if (g.IsFree(xx, y + r)) return (y + r) * g.W + xx;
            }
        }
        return -1;
    }

    public static int NearestFree(YardGrid g, Vec2 p, double searchMu)
    {
        var (x, y) = g.CellOf(p);
        return NearestFree(g, x, y, Math.Max(1, (int)Math.Ceiling(searchMu / Math.Max(0.01, g.CellMu))));
    }

    /// <summary>Đường từ ô <paramref name="start"/> tới ô <paramref name="goal"/>, hoặc null nếu không tới được.</summary>
    public static List<int> Plan(YardGrid g, int start, int goal)
    {
        if (g is null || start < 0 || goal < 0 || !g.IsFree(start) || !g.IsFree(goal)) return null;
        if (start == goal) return new List<int> { start };

        int w = g.W, h = g.H, n = w * h;
        var clear = g.Clearance();
        var gs = new double[n];
        Array.Fill(gs, double.PositiveInfinity);
        var came = new int[n];
        Array.Fill(came, -1);

        double H(int i)
        {
            int ax = Math.Abs(i % w - goal % w), ay = Math.Abs(i / w - goal / w);
            int lo = Math.Min(ax, ay), hi = Math.Max(ax, ay);
            return hi - lo + lo * Math.Sqrt(2.0);      // octile
        }

        gs[start] = 0;
        var heap = new PriorityQueue<int, double>();
        heap.Enqueue(start, H(start));

        while (heap.TryDequeue(out int cur, out double f))
        {
            if (cur == goal) break;

            // Bo ban ghi CU con sot: PriorityQueue khong co decrease-key nen mot o co the nam trong
            // hang nhieu lan, chi ban co f nho nhat con dung (cung khuon BoardPlanner).
            if (f > gs[cur] + H(cur) + 1e-9) continue;

            int cx = cur % w, cy = cur / w;
            for (int k = 0; k < 8; k++)
            {
                int nx = cx + Dx[k], ny = cy + Dy[k];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int ni = ny * w + nx;
                if (!g.IsFree(ni)) continue;

                // Cam cat goc qua hai o bi chan.
                if (Dx[k] != 0 && Dy[k] != 0 && (!g.IsFree(cx + Dx[k], cy) || !g.IsFree(cx, cy + Dy[k]))) continue;

                double step = Dx[k] != 0 && Dy[k] != 0 ? Math.Sqrt(2.0) : 1.0;
                double c = Math.Max(0.25, clear[ni]);
                double risk = NavTuning.YardEdgeCost / ((c + 0.60) * (c + 0.60));
                double ng = gs[cur] + step + risk;
                if (ng >= gs[ni]) continue;

                gs[ni] = ng;
                came[ni] = cur;
                heap.Enqueue(ni, ng + H(ni));
            }
        }

        if (double.IsPositiveInfinity(gs[goal])) return null;

        var path = new List<int>();
        for (int i = goal; i != -1; i = came[i]) path.Add(i);
        path.Reverse();
        return path;
    }

    /// <summary>Độ dài hình học của một đường (mu).</summary>
    public static double Length(YardGrid g, List<int> path)
    {
        if (path is null || path.Count < 2) return 0;
        double d = 0;
        for (int i = 1; i < path.Count; i++) d += (g.CenterOf(path[i]) - g.CenterOf(path[i - 1])).Len;
        return d;
    }
}
