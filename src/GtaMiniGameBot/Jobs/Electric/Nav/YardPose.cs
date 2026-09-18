using System.Globalization;
using System.Text.Json;

namespace GtaMiniGameBot;

/// <summary>
/// Vector 2D bằng <c>double</c>. Không dùng <see cref="PointF"/>: nó là <c>float</c>, mà bộ giải pose
/// phải phục hồi <c>s/φ/P</c> tới 1e-9 trong ca kiểm tổng hợp — độ chính xác của float (~1e-7 tương
/// đối) không đủ, và sai số đó lan sang mọi phép quy đổi mu ↔ pixel về sau.
/// </summary>
internal readonly struct Vec2
{
    public readonly double X, Y;

    public Vec2(double x, double y) { X = x; Y = y; }

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(double k, Vec2 a) => new(k * a.X, k * a.Y);

    public double Len => Math.Sqrt(X * X + Y * Y);

    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>Tích có hướng 2D (z của tích chéo) — dấu của nó cho góc quay trong Umeyama.</summary>
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:F1},{1:F1})", X, Y);
}

/// <summary>Ba blip cố định của sân trạm biến áp trên minimap.</summary>
internal enum BlipId
{
    /// <summary>⚡ NPC xin/nghỉ việc — gốc hệ toạ độ sân.</summary>
    Lightning = 0,

    /// <summary>✕ đỏ — mốc thứ hai, định nghĩa trục x và đơn vị mu.</summary>
    Cross = 1,

    /// <summary>🍕 nghề khác — mốc PHỤ, chỉ hiện khi tới gần.</summary>
    Pizza = 2
}

/// <summary>Một blip đã nhận trên khung minimap. Toạ độ là toạ độ MÀN (tương đối góc màn).</summary>
internal sealed class BlipHit
{
    public BlipId Id { get; init; }
    public double X { get; init; }
    public double Y { get; init; }

    /// <summary>NCC tại ô được chọn; 0.5 khi chưa có mẫu (nhận theo hình dạng để buổi ghi đầu vẫn chạy).</summary>
    public double Ncc { get; init; }

    /// <summary>bbox chạm mép ROI, hoặc nhỏ hơn 0,8 mẫu — blip đang bị cắt, toạ độ không tin được.</summary>
    public bool Clipped { get; init; }

    /// <summary>Khoảng cách (px) từ bbox tới mép ROI gần nhất.</summary>
    public double InsetPx { get; init; }

    public int W { get; init; }
    public int H { get; init; }
    public int Area { get; init; }
    public double Fill { get; init; }

    /// <summary>Nhận bằng hình dạng chứ không bằng NCC (chưa học mẫu).</summary>
    public bool ByShapeOnly { get; init; }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture,
            "{0} ({1:F1},{2:F1}) {3}x{4} area={5} fill={6:F2} ncc={7:F2} inset={8:F1}{9}",
            Id, X, Y, W, H, Area, Fill, Ncc, InsetPx, Clipped ? " CẮT" : "");
}

/// <summary>Khuôn hình dạng của một blip, đo trên 5 ảnh 2K rồi quy về mốc 1080p (xem <see cref="NavTuning"/>).</summary>
internal readonly struct BlipShape
{
    public BlipId Id { get; init; }

    /// <summary>true = lấy từ mặt nạ đỏ, false = mặt nạ vàng.</summary>
    public bool Red { get; init; }

    public double AreaMin { get; init; }
    public double AreaMax { get; init; }
    public double WMin { get; init; }
    public double WMax { get; init; }
    public double HMin { get; init; }
    public double HMax { get; init; }
    public double FillMin { get; init; }
    public double FillMax { get; init; }

    public static readonly BlipShape[] All =
    {
        new()
        {
            Id = BlipId.Lightning, Red = false,
            AreaMin = NavTuning.YardLightningAreaRefMin, AreaMax = NavTuning.YardLightningAreaRefMax,
            WMin = NavTuning.YardLightningWRefMin, WMax = NavTuning.YardLightningWRefMax,
            HMin = NavTuning.YardLightningHRefMin, HMax = NavTuning.YardLightningHRefMax,
            FillMin = NavTuning.YardLightningFillMin, FillMax = NavTuning.YardLightningFillMax
        },
        new()
        {
            Id = BlipId.Cross, Red = true,
            AreaMin = NavTuning.YardCrossAreaRefMin, AreaMax = NavTuning.YardCrossAreaRefMax,
            WMin = NavTuning.YardCrossWRefMin, WMax = NavTuning.YardCrossWRefMax,
            HMin = NavTuning.YardCrossHRefMin, HMax = NavTuning.YardCrossHRefMax,
            FillMin = NavTuning.YardCrossFillMin, FillMax = 1.01
        },
        new()
        {
            Id = BlipId.Pizza, Red = true,
            AreaMin = NavTuning.YardPizzaAreaRefMin, AreaMax = NavTuning.YardPizzaAreaRefMax,
            WMin = NavTuning.YardPizzaWRefMin, WMax = NavTuning.YardPizzaWRefMax,
            HMin = NavTuning.YardPizzaHRefMin, HMax = NavTuning.YardPizzaHRefMax,
            FillMin = NavTuning.YardPizzaFillMin, FillMax = NavTuning.YardPizzaFillMax
        }
    };

    public static BlipShape Of(BlipId id) => All[(int)id];

    /// <summary>Diện tích quy về mốc 1080p — cùng thang mà <c>YellowDotDetector.Detect</c> dùng.</summary>
    public static double AreaRef(Blob b, NavScale s) => b.Area / (s.Sx * s.Sy + 1e-9);

    public static double WRef(Blob b, NavScale s) => b.Box.Width / Math.Max(s.Sx, 1e-9);

    public static double HRef(Blob b, NavScale s) => b.Box.Height / Math.Max(s.Sy, 1e-9);

    public static double FillOf(Blob b) => b.Area / Math.Max(1.0, (double)b.Box.Width * b.Box.Height);

    public bool Fits(Blob b, NavScale s)
    {
        double a = AreaRef(b, s);
        if (a < AreaMin || a > AreaMax) return false;
        double w = WRef(b, s), h = HRef(b, s);
        if (w < WMin || w > WMax || h < HMin || h > HMax) return false;
        double f = FillOf(b);
        return f >= FillMin && f <= FillMax;
    }

    /// <summary>Lệch chuẩn hoá so với GIỮA mọi dải — dùng chọn blob tốt nhất khi có nhiều ứng viên lúc học mẫu.</summary>
    public double Distance(Blob b, NavScale s)
    {
        double Mid(double lo, double hi) => (lo + hi) / 2.0;
        double Norm(double v, double lo, double hi) => Math.Abs(v - Mid(lo, hi)) / Math.Max(1e-6, (hi - lo) / 2.0);
        return Norm(AreaRef(b, s), AreaMin, AreaMax)
               + Norm(WRef(b, s), WMin, WMax)
               + Norm(HRef(b, s), HMin, HMax)
               + Norm(FillOf(b), FillMin, FillMax);
    }
}

/// <summary>
/// Hai mặt nạ màu của ROI minimap, tính trong MỘT lượt quét pixel.
///
/// Vì sao gộp: bộ ghi bản đồ cần mặt nạ vàng cho chấm đích lẫn ⚡, và mặt nạ đỏ cho ✕/🍕, trong cùng
/// một tick 25 ms. Quét ROI 402×341 ba lần là ba lần trả giá cache cho cùng một dữ liệu.
///
/// Mặt nạ vàng ở đây là bản THÔ (chưa <c>Close</c>) — xem chú thích của
/// <see cref="YellowDotDetector.Detect(NavFrame, NavScale, double, double, Mask, Rectangle)"/>.
/// </summary>
internal sealed class BlipMasks
{
    public Rectangle Local { get; init; }
    public Mask Yellow { get; init; }
    public Mask Red { get; init; }

    /// <summary>Cộng vào toạ độ cục bộ để ra toạ độ màn.</summary>
    public int OffX { get; init; }

    public int OffY { get; init; }

    public int Width => Local.Width;
    public int Height => Local.Height;

    public Mask For(bool red) => red ? Red : Yellow;

    public static BlipMasks Build(NavFrame f, NavScale s)
    {
        var local = YellowDotDetector.TargetRoiLocal(f, s);
        if (local.IsEmpty) return null;

        var yellow = new Mask(local.Width, local.Height);
        var red = new Mask(local.Width, local.Height);

        for (int y = 0; y < local.Height; y++)
        {
            int row = (local.Y + y) * f.Stride;
            int orow = y * local.Width;
            for (int x = 0; x < local.Width; x++)
            {
                int i = row + (local.X + x) * 4;
                int b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];

                // Vang va do roi nhau tren vong H, nen "else" khong bo sot gi — va giu duoc dung
                // ham IsYellow cua bo do cham vang thay vi chep lai nguong.
                if (YellowDotDetector.IsYellow(b, g, r)) { yellow.Data[orow + x] = 1; continue; }

                var (h, sv, vv) = ImageOps.HsvOf(b, g, r);
                if (sv >= NavTuning.YardRedSMin && vv >= NavTuning.YardRedVMin
                    && (h <= NavTuning.YardRedHHi || h >= NavTuning.YardRedHLo))
                    red.Data[orow + x] = 1;
            }
        }

        return new BlipMasks
        {
            Local = local, Yellow = yellow, Red = red,
            OffX = f.OriginX + local.X, OffY = f.OriginY + local.Y
        };
    }
}

/// <summary>
/// Mẫu hình dạng của ba blip, học MỘT LẦN cho mỗi độ phân giải rồi lưu xuống
/// <c>electric\&lt;WxH&gt;\map\</c>.
///
/// Vì sao phải học chứ không hard-code: glyph blip do game vẽ theo cỡ HUD của từng máy, và bộ lọc
/// hình dạng thuần (diện tích/bbox/fill) không tách nổi ⚡ khỏi một mảnh vàng cùng cỡ trên bản đồ.
/// NCC trên MẶT NẠ thì tách được, vì nó so cấu trúc chứ không so màu — và mặt nạ đã lọc màu xong.
/// </summary>
internal sealed class BlipTemplates
{
    private const int SchemaVersion = 1;

    private readonly GrayTemplate[] _tpl = new GrayTemplate[3];
    private readonly double[] _areaRef = new double[3];
    private readonly double[] _wRef = new double[3];
    private readonly double[] _hRef = new double[3];

    public string ScreenKey { get; private set; }
    public string Source { get; private set; }
    public DateTime BuiltUtc { get; private set; }

    public GrayTemplate Of(BlipId id) => _tpl[(int)id];

    public bool Has(BlipId id) => _tpl[(int)id] is not null;

    public double AreaRefOf(BlipId id) => _areaRef[(int)id];

    public double WRefOf(BlipId id) => _wRef[(int)id];

    public double HRefOf(BlipId id) => _hRef[(int)id];

    /// <summary>Dung sai quanh số đo của mẫu khi lọc ứng viên: ±35 %.</summary>
    public const double Tolerance = 0.35;

    private void Set(BlipId id, GrayTemplate t, Blob b, NavScale s)
    {
        _tpl[(int)id] = t;
        _areaRef[(int)id] = BlipShape.AreaRef(b, s);
        _wRef[(int)id] = BlipShape.WRef(b, s);
        _hRef[(int)id] = BlipShape.HRef(b, s);
    }

    /// <summary>
    /// Học mẫu từ một khung minimap. ⚡ và ✕ là BẮT BUỘC (hai mốc định nghĩa hệ toạ độ); 🍕 chỉ hiện
    /// khi tới gần nên thiếu cũng nhận.
    /// </summary>
    public static BlipTemplates Learn(NavFrame f, NavScale s, string source, out string why)
    {
        why = null;
        var masks = BlipMasks.Build(f, s);
        if (masks is null) { why = "không suy được ô minimap trên ảnh này"; return null; }

        var outp = new BlipTemplates { ScreenKey = $"{s.ScreenW}x{s.ScreenH}", Source = source, BuiltUtc = DateTime.UtcNow };
        var yellowBlobs = ImageOps.Blobs(masks.Yellow);
        var redBlobs = ImageOps.Blobs(masks.Red);

        foreach (var spec in BlipShape.All)
        {
            var blobs = spec.Red ? redBlobs : yellowBlobs;
            Blob? best = null;
            double bestD = double.MaxValue;
            foreach (var b in blobs)
            {
                if (TouchesEdge(b, masks)) continue;
                if (!spec.Fits(b, s)) continue;
                double d = spec.Distance(b, s);
                if (d < bestD) { bestD = d; best = b; }
            }
            if (best is null) continue;

            var crop = Crop(masks.For(spec.Red), best.Value.Box, NavTuning.YardTemplateMarginPx);
            var tpl = GrayTemplate.FromMask(crop);
            if (tpl.IsFlat) continue;
            outp.Set(spec.Id, tpl, best.Value, s);
        }

        if (!outp.Has(BlipId.Lightning))
        {
            // Cham vang dinh ⚡ thi hai khoi gop lam mot va dien tich vot len — bao dung ly do thay vi
            // "khong thay", de nguoi dung biet phai doi cho dung roi chup lai.
            foreach (var b in yellowBlobs)
                if (BlipShape.AreaRef(b, s) > 160.0)
                {
                    why = $"chấm vàng đang dính ⚡ (khối gộp {BlipShape.AreaRef(b, s):F0} ref) — đứng chỗ khác rồi học lại";
                    return null;
                }
            why = "không thấy ⚡ trong ô minimap";
            return null;
        }
        if (!outp.Has(BlipId.Cross)) { why = "không thấy ✕ đỏ trong ô minimap"; return null; }
        return outp;
    }

    public static bool TouchesEdge(Blob b, BlipMasks m) =>
        b.Box.X <= 0 || b.Box.Y <= 0 || b.Box.Right >= m.Width || b.Box.Bottom >= m.Height;

    /// <summary>Cắt bbox + lề ra một mặt nạ con (đã kẹp trong mặt nạ gốc).</summary>
    public static Mask Crop(Mask m, Rectangle box, int margin)
    {
        var r = Rectangle.Intersect(
            new Rectangle(box.X - margin, box.Y - margin, box.Width + 2 * margin, box.Height + 2 * margin),
            new Rectangle(0, 0, m.Width, m.Height));
        var outp = new Mask(Math.Max(1, r.Width), Math.Max(1, r.Height));
        for (int y = 0; y < r.Height; y++)
        {
            int src = (r.Y + y) * m.Width + r.X;
            int dst = y * outp.Width;
            for (int x = 0; x < r.Width; x++) outp.Data[dst + x] = m.Data[src + x];
        }
        return outp;
    }

    public string Describe()
    {
        var parts = new List<string>();
        foreach (var id in new[] { BlipId.Lightning, BlipId.Cross, BlipId.Pizza })
            parts.Add(Has(id) ? $"{Name(id)} {Of(id).Width}×{Of(id).Height}" : $"{Name(id)} —");
        return string.Join(", ", parts);
    }

    public static string Name(BlipId id) => id switch
    {
        BlipId.Lightning => "⚡",
        BlipId.Cross => "✕",
        _ => "🍕"
    };

    public static string FileName(BlipId id) => id switch
    {
        BlipId.Lightning => "lightning",
        BlipId.Cross => "cross",
        _ => "pizza"
    };

    // ---------------------------------------------------------------- luu / doc

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Ghi temp rồi <c>File.Move(…, true)</c> như <see cref="BoardRouteCache"/> — không để lại file nửa vời.</summary>
    public void Save(string key)
    {
        string dir = ElectricConfig.MapDir(key);
        Directory.CreateDirectory(dir);

        var meta = new MetaFile
        {
            Version = SchemaVersion,
            ScreenKey = ScreenKey,
            Source = Source,
            BuiltUtc = BuiltUtc,
            Items = new List<MetaItem>()
        };

        foreach (var id in new[] { BlipId.Lightning, BlipId.Cross, BlipId.Pizza })
        {
            if (!Has(id)) continue;
            Of(id).Save(ElectricConfig.BlipTemplatePath(key, FileName(id)));
            meta.Items.Add(new MetaItem
            {
                Name = FileName(id),
                W = Of(id).Width,
                H = Of(id).Height,
                AreaRef = AreaRefOf(id),
                BoxWRef = WRefOf(id),
                BoxHRef = HRefOf(id)
            });
        }

        string path = ElectricConfig.BlipTemplateMetaPath(key);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(meta, JsonOpts));
        File.Move(temp, path, true);
    }

    /// <summary>Thiếu file hay file hỏng thì trả null — buổi ghi đầu tiên vẫn chạy bằng hình dạng.</summary>
    public static BlipTemplates Load(string key)
    {
        try
        {
            string path = ElectricConfig.BlipTemplateMetaPath(key);
            if (!File.Exists(path)) return null;
            var meta = JsonSerializer.Deserialize<MetaFile>(File.ReadAllText(path), JsonOpts);
            if (meta is null || meta.Version != SchemaVersion || meta.Items is null) return null;

            var outp = new BlipTemplates { ScreenKey = meta.ScreenKey, Source = meta.Source, BuiltUtc = meta.BuiltUtc };
            foreach (var item in meta.Items)
            {
                BlipId id;
                if (item.Name == "lightning") id = BlipId.Lightning;
                else if (item.Name == "cross") id = BlipId.Cross;
                else if (item.Name == "pizza") id = BlipId.Pizza;
                else continue;

                string png = ElectricConfig.BlipTemplatePath(key, item.Name);
                if (!File.Exists(png)) continue;
                outp._tpl[(int)id] = GrayTemplate.FromFile(png);
                outp._areaRef[(int)id] = item.AreaRef;
                outp._wRef[(int)id] = item.BoxWRef;
                outp._hRef[(int)id] = item.BoxHRef;
            }
            return outp.Has(BlipId.Lightning) && outp.Has(BlipId.Cross) ? outp : null;
        }
        catch { return null; }
    }

    private sealed class MetaFile
    {
        public MetaFile() { }
        public int Version { get; set; }
        public string ScreenKey { get; set; }
        public string Source { get; set; }
        public DateTime BuiltUtc { get; set; }
        public List<MetaItem> Items { get; set; }
    }

    private sealed class MetaItem
    {
        public MetaItem() { }
        public string Name { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public double AreaRef { get; set; }
        public double BoxWRef { get; set; }
        public double BoxHRef { get; set; }
    }
}

/// <summary>
/// Dò ba blip trên một khung minimap.
///
/// Hai quyết định đáng ghi lại:
///   1. <b>Tâm = tâm cửa sổ mẫu</b>, không phải trọng tâm màu. Glyph ⚡ lệch tâm và xoay theo bản đồ,
///      nên trọng tâm màu nhảy 2–4 px giữa hai khung đứng yên — đủ để pose rung 2 mu.
///   2. <b>NCC chạy thẳng trên mặt nạ 0/1</b>. NCC bất biến với phép <c>a·s + b</c> nên mặt nạ 0/1 và
///      mẫu 0/255 cho cùng một số; nhờ vậy không phải dựng thêm mảng 0/255 mỗi tick.
/// </summary>
internal static class BlipDetector
{
    public static List<BlipHit> Detect(NavFrame f, NavScale s, BlipMasks masks, BlipTemplates tpl,
                                       IReadOnlyDictionary<BlipId, Vec2> predicted = null,
                                       List<string> rejected = null)
    {
        var outp = new List<BlipHit>();
        if (masks is null) return outp;

        var yellowBlobs = ImageOps.Blobs(masks.Yellow);
        var redBlobs = ImageOps.Blobs(masks.Red);

        foreach (var spec in BlipShape.All)
        {
            bool hasTpl = tpl is not null && tpl.Has(spec.Id);
            var blobs = spec.Red ? redBlobs : yellowBlobs;
            var mask = masks.For(spec.Red);

            BlipHit best = null;
            double bestKey = double.NegativeInfinity;

            foreach (var b in blobs)
            {
                if (!Gate(b, s, spec, tpl, hasTpl)) continue;

                double cx = b.Box.X + b.Box.Width / 2.0;
                double cy = b.Box.Y + b.Box.Height / 2.0;
                double ncc = NavTuning.YardNccAccept;
                bool byShape = true;
                int tw = b.Box.Width, th = b.Box.Height;

                if (hasTpl)
                {
                    var t = tpl.Of(spec.Id);
                    tw = t.Width; th = t.Height;
                    int bx0 = (int)Math.Round(cx - tw / 2.0);
                    int by0 = (int)Math.Round(cy - th / 2.0);
                    double bestNcc = -2.0;
                    int bestX = bx0, bestY = by0;
                    for (int dy = -NavTuning.YardNccSearchPx; dy <= NavTuning.YardNccSearchPx; dy++)
                        for (int dx = -NavTuning.YardNccSearchPx; dx <= NavTuning.YardNccSearchPx; dx++)
                        {
                            double v = t.ScoreAt(mask.Data, mask.Width, mask.Height, mask.Width, bx0 + dx, by0 + dy);
                            if (v > bestNcc) { bestNcc = v; bestX = bx0 + dx; bestY = by0 + dy; }
                        }
                    if (bestNcc < NavTuning.YardNccAccept)
                    {
                        rejected?.Add($"{BlipTemplates.Name(spec.Id)} ncc={bestNcc:F2} < {NavTuning.YardNccAccept:F2} tại ({cx:F0},{cy:F0})");
                        continue;
                    }
                    ncc = bestNcc;
                    byShape = false;
                    cx = bestX + tw / 2.0;
                    cy = bestY + th / 2.0;
                }

                bool clipped = BlipTemplates.TouchesEdge(b, masks);
                if (hasTpl && (b.Box.Width < 0.8 * tpl.Of(spec.Id).Width - 2 * NavTuning.YardTemplateMarginPx
                               || b.Box.Height < 0.8 * tpl.Of(spec.Id).Height - 2 * NavTuning.YardTemplateMarginPx))
                    clipped = true;

                double inset = Math.Min(
                    Math.Min(b.Box.X, b.Box.Y),
                    Math.Min(masks.Width - b.Box.Right, masks.Height - b.Box.Bottom));

                var hit = new BlipHit
                {
                    Id = spec.Id,
                    X = masks.OffX + cx,
                    Y = masks.OffY + cy,
                    Ncc = ncc,
                    Clipped = clipped,
                    InsetPx = Math.Max(0.0, inset),
                    W = b.Box.Width, H = b.Box.Height, Area = b.Area,
                    Fill = BlipShape.FillOf(b),
                    ByShapeOnly = byShape
                };

                // Nhieu ung vien cung Id: gan du doan nhat, khong co du doan thi ncc cao nhat.
                double key;
                if (predicted is not null && predicted.TryGetValue(spec.Id, out var p))
                {
                    double d = new Vec2(hit.X - p.X, hit.Y - p.Y).Len;
                    key = -d;
                }
                else key = ncc;

                if (key > bestKey) { bestKey = key; best = hit; }
            }

            if (best is not null) outp.Add(best);
        }
        return outp;
    }

    /// <summary>
    /// Cổng hình dạng. Có mẫu thì bám số đo của MẪU ±35 %; chưa có mẫu thì dùng bảng đo sẵn trong
    /// <see cref="NavTuning"/> — buổi ghi đầu tiên vẫn phải chạy được.
    ///
    /// Vụn 7×1 ở hàng cuối ROI (HUD dưới radar) rớt ở đây vì diện tích quá nhỏ, và nếu có lọt thì
    /// <see cref="BlipTemplates.TouchesEdge"/> đánh dấu <c>Clipped</c> nên tracker không dùng.
    /// </summary>
    private static bool Gate(Blob b, NavScale s, BlipShape spec, BlipTemplates tpl, bool hasTpl)
    {
        if (!hasTpl) return spec.Fits(b, s);

        bool Near(double v, double refv) =>
            refv <= 1e-6 || Math.Abs(v - refv) <= BlipTemplates.Tolerance * refv;

        return Near(BlipShape.AreaRef(b, s), tpl.AreaRefOf(spec.Id))
               && Near(BlipShape.WRef(b, s), tpl.WRefOf(spec.Id))
               && Near(BlipShape.HRef(b, s), tpl.HRefOf(spec.Id));
    }
}

/// <summary>Chất lượng pose, theo thứ tự tin cậy giảm dần.</summary>
internal static class YardQuality
{
    /// <summary>Hai mốc thật (⚡ + ✕) — đo được cả tỉ lệ lẫn hướng.</summary>
    public const string Fix2 = "FIX2";

    /// <summary>Hai mốc nhưng một trong đó là mốc phụ (🍕, hoặc máy đích ở PR3).</summary>
    public const string Fix2Lm = "FIX2_LM";

    /// <summary>Một mốc + hướng/tỉ lệ nhớ từ fix trước.</summary>
    public const string Fix1 = "FIX1";

    /// <summary>Không mốc nào; chỉ còn suy theo count chuột.</summary>
    public const string DrOnly = "DR_ONLY";

    public const string Lost = "LOST";

    /// <summary>Chưa từng có fix nào.</summary>
    public const string None = "NONE";
}

/// <summary>Kết quả một lần giải pose.</summary>
internal sealed class YardPose
{
    public string Quality { get; init; } = YardQuality.None;
    public double Conf { get; init; }

    /// <summary>px màn trên một mu (≈1.00 ngày 23/08, ≈1.27 ngày 05/09 — zoom minimap đổi theo ngày).</summary>
    public double S { get; init; }

    /// <summary>Góc xoay bản đồ (radian) trong công thức <c>q = s·R(φ)·(m − P)</c>.</summary>
    public double Phi { get; init; }

    public double ThetaDeg { get; init; }
    public Vec2 P { get; init; }

    /// <summary>Vị trí điểm vàng đích trong hệ sân, khi chấm đang khoá.</summary>
    public Vec2? T { get; init; }

    /// <summary>RMS sai số mốc, đơn vị MU (đã chia s) — không phụ thuộc zoom.</summary>
    public double Residual { get; init; }

    public double Fix2Age { get; init; }
    public double FixAge { get; init; }
    public int HitsUsed { get; init; }

    /// <summary>Vừa nhảy vị trí (respawn/teleport) — người dùng của pose phải bỏ lịch sử.</summary>
    public bool Reinit { get; init; }

    /// <summary>Vừa nhận lại tỉ lệ vì zoom minimap đổi.</summary>
    public bool ScaleReinit { get; init; }

    public bool Usable => Quality is YardQuality.Fix2 or YardQuality.Fix2Lm or YardQuality.Fix1;
}

/// <summary>
/// Bộ giải pose THUẦN — không trạng thái, không chạm ảnh, để <c>--verify-map</c> lùa bằng số tổng hợp.
///
/// Mô hình duy nhất, với <c>q</c> là toạ độ màn ĐÃ TRỪ gốc mũi tên và <c>m</c> là toạ độ sân (mu):
/// <code>
///   q = s · R(φ) · (m − P)        R(φ) = [[cosφ, −sinφ], [sinφ, cosφ]]
/// </code>
/// Hệ sân: <c>L⚡ = (0,0)</c>, <c>L✕ = (D_ref, 0)</c>, y quay +90° cùng chiều y-xuống (không phản chiếu).
/// Trên minimap "lên" là hướng nhìn, nên vector nhìn trong hệ sân là <c>(−sinφ, −cosφ)</c>, tức
/// <c>θ = wrap(−φ − 90°)</c>.
/// </summary>
internal static class YardPoseSolver
{
    public const double Rad2Deg = 180.0 / Math.PI;
    public const double Deg2Rad = Math.PI / 180.0;

    public static double Wrap(double deg)
    {
        deg %= 360.0;
        if (deg > 180.0) deg -= 360.0;
        if (deg < -180.0) deg += 360.0;
        return deg;
    }

    public static Vec2 Rot(double phi, Vec2 v)
    {
        double c = Math.Cos(phi), s = Math.Sin(phi);
        return new Vec2(c * v.X - s * v.Y, s * v.X + c * v.Y);
    }

    public static double ThetaOf(double phi) => Wrap(-phi * Rad2Deg - 90.0);

    public static double PhiOf(double thetaDeg) => Wrap(-thetaDeg - 90.0) * Deg2Rad;

    /// <summary>Toạ độ sân của ba blip. 🍕 mặc định theo <see cref="NavTuning"/>; bản đồ dựng xong ghi đè.</summary>
    public static Vec2 MapPos(BlipId id, Vec2? pizza = null) => id switch
    {
        BlipId.Lightning => new Vec2(0, 0),
        BlipId.Cross => new Vec2(NavTuning.YardDRef, 0),
        _ => pizza ?? new Vec2(NavTuning.YardPizzaXMu, NavTuning.YardPizzaYMu)
    };

    public static double WeightOf(BlipId id) =>
        id == BlipId.Pizza ? NavTuning.YardPizzaWeight : 1.0;

    public readonly struct Fix
    {
        public bool Ok { get; init; }
        public double S { get; init; }
        public double Phi { get; init; }
        public Vec2 P { get; init; }
        public double ResidualMu { get; init; }
        public double ThetaDeg => ThetaOf(Phi);

        public static readonly Fix Fail = new() { Ok = false };
    }

    /// <summary>Hai mốc định nghĩa: <c>s = |d|/D_ref</c>, <c>φ = atan2(d.y, d.x)</c>, <c>P = −(1/s)·R(−φ)·a</c>.</summary>
    public static Fix Fix2(Vec2 a, Vec2 b, double dRef = NavTuning.YardDRef)
    {
        var d = b - a;
        double len = d.Len;
        if (len < 1e-9 || dRef < 1e-9) return Fix.Fail;
        double s = len / dRef;
        double phi = Math.Atan2(d.Y, d.X);
        var p = (-1.0 / s) * Rot(-phi, a);
        return new Fix { Ok = true, S = s, Phi = phi, P = p, ResidualMu = 0.0 };
    }

    /// <summary>Một mốc + hướng và tỉ lệ đã biết: <c>φ = −θ − 90°</c>, <c>P = L_i − (1/s)·R(−φ)·q_i</c>.</summary>
    public static Fix Fix1(Vec2 q, Vec2 landmark, double thetaDeg, double s)
    {
        if (s < 1e-9) return Fix.Fail;
        double phi = PhiOf(thetaDeg);
        var p = landmark - (1.0 / s) * Rot(-phi, q);
        return new Fix { Ok = true, S = s, Phi = phi, P = p, ResidualMu = 0.0 };
    }

    /// <summary>
    /// Umeyama 2D có trọng số, không phản chiếu — đóng, không lặp. n = 2 rút về đúng
    /// <see cref="Fix2"/> (có ca kiểm).
    ///
    /// <b>Điểm mở rộng</b>: hàm nhận danh sách cặp (mốc trong sân, mốc trên màn, trọng số) bất kỳ, nên
    /// thêm mốc mới — ví dụ máy đích đã nhận diện ở PR3 — chỉ là thêm một phần tử, không sửa bộ giải.
    ///
    /// Residual trả về bằng MU (RMS pixel chia s): cùng thang với cổng vị trí và với ngưỡng phát hiện
    /// nghiệm gương, và không đổi nghĩa khi zoom minimap nhảy 1,27×.
    /// </summary>
    public static Fix Solve(IReadOnlyList<(Vec2 m, Vec2 q, double w)> pairs)
    {
        if (pairs is null || pairs.Count < 2) return Fix.Fail;

        double sw = 0;
        Vec2 mBar = new(0, 0), qBar = new(0, 0);
        foreach (var (m, q, w) in pairs)
        {
            if (w <= 0) continue;
            sw += w;
            mBar += w * m;
            qBar += w * q;
        }
        if (sw <= 1e-12) return Fix.Fail;
        mBar = (1.0 / sw) * mBar;
        qBar = (1.0 / sw) * qBar;

        double alpha = 0, beta = 0, varM = 0, varQ = 0;
        foreach (var (m, q, w) in pairs)
        {
            if (w <= 0) continue;
            var dm = m - mBar;
            var dq = q - qBar;
            alpha += w * Vec2.Dot(dm, dq);
            beta += w * Vec2.Cross(dm, dq);
            varM += w * Vec2.Dot(dm, dm);
            varQ += w * Vec2.Dot(dq, dq);
        }
        if (varM < 1e-12 || varQ < 1e-12) return Fix.Fail;

        double phi = Math.Atan2(beta, alpha);
        double s = Math.Sqrt(varQ / varM);
        if (s < 1e-9 || double.IsNaN(s)) return Fix.Fail;

        var t = qBar - s * Rot(phi, mBar);
        var p = (-1.0 / s) * Rot(-phi, t);

        double err = 0;
        foreach (var (m, q, w) in pairs)
        {
            if (w <= 0) continue;
            var fit = s * Rot(phi, m) + t;
            var e = q - fit;
            err += w * Vec2.Dot(e, e);
        }
        double residualPx = Math.Sqrt(err / sw);

        return new Fix { Ok = true, S = s, Phi = phi, P = p, ResidualMu = residualPx / s };
    }

    /// <summary>Đưa một điểm màn (đã trừ gốc) về hệ sân.</summary>
    public static Vec2 ToYard(Vec2 q, double s, double phi, Vec2 p) => p + (1.0 / s) * Rot(-phi, q);

    /// <summary>Đưa một điểm sân ra màn (đã trừ gốc).</summary>
    public static Vec2 ToScreen(Vec2 m, double s, double phi, Vec2 p) => s * Rot(phi, m - p);

    /// <summary>Góc tới và khoảng cách (px màn) của một điểm sân — đúng quy ước <c>rel</c> của NavBot.</summary>
    public static (double relDeg, double distPx) Aim(Vec2 w, double s, double phi, Vec2 p)
    {
        var v = ToScreen(w, s, phi, p);
        return (Wrap(Math.Atan2(v.X, -v.Y) * Rad2Deg), v.Len);
    }
}

/// <summary>
/// Bám pose qua các khung: chọn mốc dùng được, giải, rồi lọc tỉ lệ / vị trí / hướng.
///
/// Vì sao phải lọc chứ không lấy thẳng nghiệm mỗi khung:
///   - blip người chơi khác cùng màu cùng cỡ đi ngang qua (cổng dự đoán + cổng tỉ lệ loại);
///   - blip sắp khuất bị CẮT ở mép ROI làm khoảng cách ⚡✕ co lại → s giả, pose nhảy (cổng tỉ lệ);
///   - zoom minimap đổi thật giữa hai ngày → phải phân biệt "s sai" với "s đổi" bằng số lần lệch liên tiếp;
///   - game nuốt delta chuột sau NUI → hướng suy theo count sai HẲN chứ không trôi dần (snap sau 3 lần).
/// </summary>
internal sealed class YardPoseTracker
{
    private readonly NavScale _s;
    private readonly double _ox, _oy;

    private double _sEma;
    private int _scaleBad;

    private bool _hasPose;
    private Vec2 _p, _v;
    private int _posBad;

    private double _theta, _thetaAnchor;
    private long _xAnchor;
    private bool _hasTheta;
    private int _headBad;

    private double _lastT, _lastFixT, _lastFix2T;
    private Vec2? _t;
    private string _lastQuality = YardQuality.None;

    public YardPoseTracker(NavScale s, double originX, double originY)
    {
        _s = s;
        _ox = originX;
        _oy = originY;
    }

    public string LastQuality => _lastQuality;

    public double ScaleEma => _sEma;

    public void Reset()
    {
        _sEma = 0; _scaleBad = 0;
        _hasPose = false; _p = new Vec2(0, 0); _v = new Vec2(0, 0); _posBad = 0;
        _theta = 0; _thetaAnchor = 0; _xAnchor = 0; _hasTheta = false; _headBad = 0;
        _lastT = _lastFixT = _lastFix2T = 0;
        _t = null;
        _lastQuality = YardQuality.None;
    }

    /// <summary>Vị trí màn DỰ ĐOÁN của một mốc, để cổng ứng viên. Chưa có pose thì không dự đoán.</summary>
    public Dictionary<BlipId, Vec2> Predict(double now)
    {
        var outp = new Dictionary<BlipId, Vec2>();
        if (!_hasPose || !_hasTheta || _sEma <= 1e-9) return outp;

        double dt = Math.Max(0.0, Math.Min(0.5, now - _lastT));
        var p = _p + dt * _v;
        double phi = YardPoseSolver.PhiOf(_theta);
        foreach (var id in new[] { BlipId.Lightning, BlipId.Cross, BlipId.Pizza })
        {
            var q = YardPoseSolver.ToScreen(YardPoseSolver.MapPos(id), _sEma, phi, p);
            outp[id] = new Vec2(_ox + q.X, _oy + q.Y);
        }
        return outp;
    }

    /// <summary>
    /// Một lần cập nhật. <paramref name="xSentCounts"/> null = CHẾ ĐỘ DẠY (bot không cầm chuột nên
    /// không có count để suy hướng): giữ θ của FIX2 cuối, và FIX1 chỉ nhận trong
    /// <see cref="NavTuning.YardTeachFix1MaxS"/> sau FIX2 — ngoài ra ghi NONE để bộ dựng bản đồ giải
    /// lại bằng hồi tố.
    /// </summary>
    public YardPose Update(IReadOnlyList<BlipHit> hits, TargetOutput dot, double now, long? xSentCounts)
    {
        double dt = _lastT > 0 ? Math.Max(1e-3, now - _lastT) : 0.025;
        var predicted = Predict(now);
        bool teach = xSentCounts is null;

        // ---------------- 1. moc dung duoc ----------------
        double inset = NavTuning.YardEdgeInsetRef * _s.Sx;
        double predGate = NavTuning.YardPredGatePx * _s.Sx;
        var gated = new List<(Vec2 m, Vec2 q, double w)>();
        var gatedIds = new List<BlipId>();
        var loose = new List<(Vec2 m, Vec2 q, double w)>();
        var looseIds = new List<BlipId>();

        foreach (var h in hits ?? Array.Empty<BlipHit>())
        {
            // Mep ROI la cong CUNG: blip bi cat co bbox co lai va tam troi vao trong, khong cach nao
            // biet duoc no le bao nhieu.
            if (h.Clipped || h.InsetPx < inset) continue;

            var q = new Vec2(h.X - _ox, h.Y - _oy);
            var pair = (YardPoseSolver.MapPos(h.Id), q, YardPoseSolver.WeightOf(h.Id));
            loose.Add(pair);
            looseIds.Add(h.Id);

            if (predicted.TryGetValue(h.Id, out var pq)
                && new Vec2(h.X - pq.X, h.Y - pq.Y).Len > predGate) continue;

            gated.Add(pair);
            gatedIds.Add(h.Id);
        }

        // Cong du doan dung de CHON, khong dung de VUT. Neu vut thi khi zoom minimap doi (mốc xa lệch
        // 27 %) hay khi game nuot delta chuot (ca hai mốc quay 40° trong một tick), moi moc xa deu roi
        // ra ngoai cong — dung luc can phat hien chuyen do nhat thi tracker lai ket o FIX1 vinh vien.
        // Bo cong roi de cong TI LE va cong VI TRI phan: chung bat blip la ma khong khoa duong phuc hoi.
        bool loosened = gated.Count < 2 && loose.Count >= 2;
        var pairs = loosened ? loose : gated;
        var usedIds = loosened ? looseIds : gatedIds;

        // ---------------- 2-4. >= 2 moc ----------------
        bool scaleReinit = false;
        if (pairs.Count >= 2)
        {
            var fix = YardPoseSolver.Solve(pairs);
            if (fix.Ok)
            {
                bool scaleOk = true;
                if (_sEma > 1e-9)
                {
                    if (Math.Abs(fix.S / _sEma - 1.0) > NavTuning.YardScaleGate)
                    {
                        _scaleBad++;
                        scaleOk = false;
                        if (_scaleBad >= NavTuning.YardScaleReinitAfter)
                        {
                            _sEma = fix.S;
                            _scaleBad = 0;
                            scaleReinit = true;
                            scaleOk = true;
                        }
                    }
                    else
                    {
                        _scaleBad = 0;
                        _sEma += NavTuning.YardScaleEmaAlpha * (fix.S - _sEma);
                    }
                }
                else { _sEma = fix.S; _scaleBad = 0; }

                if (scaleOk)
                {
                    bool lm = usedIds.Contains(BlipId.Pizza)
                              && !(usedIds.Contains(BlipId.Lightning) && usedIds.Contains(BlipId.Cross));
                    return Accept(fix, lm ? YardQuality.Fix2Lm : YardQuality.Fix2,
                                  usedIds.Count, now, dt, xSentCounts, dot, scaleReinit, teach);
                }
            }
        }

        // ---------------- 5. FIX1 ----------------
        // Toi day nghia la: hoac chi con MOT moc, hoac cap hai moc vua bi cong ti le loai (mot blip
        // bi cat lam khoang cach co lai). Ca hai truong hop deu con giai duoc bang MOT moc voi θ va s
        // nho tu fix truoc — bo luon thi mat pose oan trong dung luc blip kia sap khuat.
        if (pairs.Count >= 1 && _sEma > 1e-9 && _hasTheta)
        {
            double maxAge = teach ? NavTuning.YardTeachFix1MaxS : NavTuning.YardFix1MaxHeadingAgeS;
            if (_lastFix2T > 0 && now - _lastFix2T <= maxAge)
            {
                // Moc trong so cao nhat (⚡/✕ truoc 🍕); danh sach da theo thu tu uu tien cua BlipShape.All.
                int best = 0;
                for (int i = 1; i < pairs.Count; i++) if (pairs[i].w > pairs[best].w) best = i;

                double thetaDr = HeadingDr(xSentCounts);
                var fix = YardPoseSolver.Fix1(pairs[best].q, pairs[best].m, thetaDr, _sEma);
                if (fix.Ok)
                    return Accept(fix, YardQuality.Fix1, 1, now, dt, xSentCounts, dot, scaleReinit, teach);
            }
        }

        // ---------------- 6. khong moc nao ----------------
        _lastT = now;
        if (!_hasPose)
        {
            _lastQuality = YardQuality.None;
            return new YardPose { Quality = YardQuality.None, S = _sEma, ThetaDeg = _theta };
        }

        double age = now - _lastFixT;
        _p += dt * _v;
        if (age > NavTuning.YardLostAfterS)
        {
            _lastQuality = YardQuality.Lost;
            return new YardPose
            {
                Quality = YardQuality.Lost, Conf = 0, S = _sEma, Phi = YardPoseSolver.PhiOf(_theta),
                ThetaDeg = _theta, P = _p, T = _t, FixAge = age, Fix2Age = now - _lastFix2T
            };
        }

        _lastQuality = YardQuality.DrOnly;
        return new YardPose
        {
            Quality = YardQuality.DrOnly,
            Conf = Math.Max(0.0, 0.4 * (1.0 - age / 2.0)),
            S = _sEma,
            Phi = YardPoseSolver.PhiOf(teach ? _theta : HeadingDr(xSentCounts)),
            ThetaDeg = teach ? _theta : HeadingDr(xSentCounts),
            P = _p, T = _t, FixAge = age, Fix2Age = now - _lastFix2T
        };
    }

    /// <summary>Hướng suy theo count chuột kể từ lần neo gần nhất. Chế độ dạy không có count → giữ θ cũ.</summary>
    private double HeadingDr(long? xSentCounts)
    {
        if (!_hasTheta) return _theta;
        if (xSentCounts is null) return _theta;
        double delta = (xSentCounts.Value - _xAnchor) / NavTuning.MouseCountsPerDegree;
        return YardPoseSolver.Wrap(_thetaAnchor + NavTuning.YardHeadingSign * delta);
    }

    private YardPose Accept(YardPoseSolver.Fix fix, string quality, int hitsUsed, double now, double dt,
                            long? xSentCounts, TargetOutput dot, bool scaleReinit, bool teach)
    {
        bool reinit = false;

        // ---------------- 3. cong vi tri ----------------
        if (_hasPose)
        {
            var pred = _p + dt * _v;
            var innov = fix.P - pred;
            double sigma = NavTuning.YardSigmaBaseMu + NavTuning.YardSigmaRateMuS * dt;
            double gate = Math.Max(NavTuning.YardPosGateMu, 3.0 * sigma);

            if (innov.Len <= gate)
            {
                _posBad = 0;
                _p = pred + NavTuning.YardPosAlpha * innov;
                _v += (NavTuning.YardPosBeta / dt) * innov;
            }
            else if (++_posBad >= 4)
            {
                _p = fix.P; _v = new Vec2(0, 0); _posBad = 0; reinit = true;
            }
            else
            {
                // Loai phep do nay nhung van giu pose truot theo van toc.
                _p = pred;
            }
        }
        else { _p = fix.P; _v = new Vec2(0, 0); _hasPose = true; }

        // ---------------- 4. huong ----------------
        double thetaMeas = fix.ThetaDeg;
        if (!_hasTheta) { _theta = thetaMeas; _hasTheta = true; _headBad = 0; }
        else
        {
            double thetaDr = HeadingDr(xSentCounts);
            double d = YardPoseSolver.Wrap(thetaMeas - thetaDr);
            if (Math.Abs(d) <= NavTuning.YardHeadingSnapDeg)
            {
                _theta = YardPoseSolver.Wrap(thetaDr + NavTuning.YardHeadingBlend * d);
                _headBad = 0;
            }
            else if (++_headBad >= NavTuning.YardHeadingSnapAfter)
            {
                _theta = thetaMeas;
                _headBad = 0;
            }
            else _theta = thetaDr;
        }

        // Moi fix nhan duoc deu neo lai goc + count: do la cach duy nhat de DR khong cong don sai so.
        _thetaAnchor = _theta;
        _xAnchor = xSentCounts ?? 0;

        _lastT = now;
        _lastFixT = now;
        if (quality is YardQuality.Fix2 or YardQuality.Fix2Lm) _lastFix2T = now;

        // ---------------- 7. diem vang trong he san ----------------
        double phi = YardPoseSolver.PhiOf(_theta);
        if (dot is not null && dot.HasPos && dot.Quality == "FULL_LOCK"
            && quality is YardQuality.Fix2 or YardQuality.Fix2Lm)
        {
            var q = new Vec2(dot.X.Value - _ox, dot.Y.Value - _oy);
            var meas = YardPoseSolver.ToYard(q, _sEma > 1e-9 ? _sEma : fix.S, phi, _p);
            _t = _t is null ? meas : _t.Value + 0.3 * (meas - _t.Value);
        }

        double conf = quality switch
        {
            YardQuality.Fix2 => Math.Clamp(0.95 - fix.ResidualMu / 6.0, 0.05, 0.95),
            YardQuality.Fix2Lm => 0.75,
            _ => 0.6 * Math.Exp(-(now - _lastFix2T) / 4.0)
        };

        _lastQuality = quality;
        return new YardPose
        {
            Quality = quality,
            Conf = conf,
            S = _sEma > 1e-9 ? _sEma : fix.S,
            Phi = phi,
            ThetaDeg = _theta,
            P = _p,
            T = _t,
            Residual = fix.ResidualMu,
            Fix2Age = _lastFix2T > 0 ? now - _lastFix2T : 0,
            FixAge = 0,
            HitsUsed = hitsUsed,
            Reinit = reinit,
            ScaleReinit = scaleReinit
        };
    }
}
