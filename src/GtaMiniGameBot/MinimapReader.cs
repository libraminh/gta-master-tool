namespace GtaMiniGameBot;

/// <summary>Một khối vàng trên minimap kèm mọi số đo đã tính — để chỉnh ngưỡng bằng số thật.</summary>
internal sealed class DotCandidate
{
    /// <summary>Tâm khối, toạ độ TRONG vùng minimap.</summary>
    public double Cx { get; init; }

    public double Cy { get; init; }

    public Rectangle Box { get; init; }

    /// <summary>Diện tích đã quy về mốc 1080p, để so thẳng với ngưỡng trong config.</summary>
    public double AreaRef { get; init; }

    public double WidthRef { get; init; }

    public double HeightRef { get; init; }

    public double Aspect { get; init; }

    public double Circularity { get; init; }

    public double Fill { get; init; }

    /// <summary>Null nếu đạt hết các cửa; ngược lại là cửa đầu tiên trượt.</summary>
    public string Reject { get; init; }

    public bool Ok => Reject is null;

    public override string ToString() =>
        $"@{Cx:F0},{Cy:F0} {Box.Width}×{Box.Height} dt={AreaRef:F0} tròn={Circularity:F2} " +
        $"đầy={Fill:F2} tỉlệ={Aspect:F2} → {(Ok ? "CHẤM" : Reject)}";
}

/// <summary>Chấm vàng đang bám, đã quy ra góc và cự ly.</summary>
internal sealed class DotFix
{
    public bool Found { get; init; }

    /// <summary>
    /// Góc từ mũi tên người chơi tới chấm: 0° = thẳng trước mặt, dương = bên phải.
    /// Cùng quy ước <c>atan2(dx, −dy)</c> của bản Python.
    /// </summary>
    public double BearingDeg { get; init; }

    /// <summary>Cự ly trên minimap, quy về mốc 1080p.</summary>
    public double DistRef { get; init; }

    /// <summary>Toạ độ tâm chấm trong vùng minimap.</summary>
    public PointF At { get; init; }

    /// <summary>Khung này không thấy chấm, đang dùng lại vị trí cũ trong hạn nhớ.</summary>
    public bool Held { get; init; }

    public override string ToString() =>
        Found ? $"góc={BearingDeg:F1}° xa={DistRef:F0}{(Held ? " (nhớ)" : "")}" : "không thấy chấm";
}

/// <summary>
/// Dò chấm vàng đích trên minimap và quy nó thành GÓC cần xoay camera.
///
/// Vì sao góc từ minimap là đủ để lái, không cần biết nhân vật đang quay mặt đâu: trong game này
/// chuột chỉ xoay CAMERA, nhân vật không xoay theo, và giữ W thì nhân vật đi theo hướng camera.
/// Nên "xoay camera cho chấm về 12 giờ rồi giữ W" là đúng, bất kể thân nhân vật đang hướng nào.
/// Bản Python phải đo hướng mũi tên trắng bằng <c>minEnclosingTriangle</c> vì nó muốn chạy được
/// với cả minimap north-up lẫn player-up; ở đây <see cref="NavBot"/> tự đo quy ước một lần lúc
/// khởi động nên không cần lớp đó.
///
/// Cửa chính để loại ICON SÉT (điểm giao việc — cũng vàng, cũng trên minimap) là ĐỘ TRÒN: chấm
/// đích là đĩa đặc, tia sét là hình răng cưa nên chu vi dài mà diện tích nhỏ.
/// </summary>
internal sealed class MinimapReader : IDisposable
{
    private readonly NavSettings _nav;
    private readonly ElectricProfile _p;
    private readonly IPixelSource _src;

    private PointF _last;
    private bool _hasLast;
    private long _lastSeenMs;

    private MinimapReader(NavSettings nav, ElectricProfile p, IPixelSource src)
    {
        _nav = nav;
        _p = p;
        _src = src;
    }

    public Rectangle Region => _src.Region;

    /// <summary>Mọi ứng viên của lần <see cref="Scan"/> gần nhất — dùng cho <c>--verify-nav</c>.</summary>
    public List<DotCandidate> LastCandidates { get; private set; } = new();

    public static MinimapReader Open(ElectricConfig cfg, Screen screen, ElectricProfile p, out string problem)
        => Create(cfg, p, r => new RegionReader(FishingConfig.ToAbsolute(screen, r)), out problem);

    public static MinimapReader ForBitmap(ElectricConfig cfg, ElectricProfile p, Bitmap still, out string problem)
        => Create(cfg, p, r => new BitmapRegion(still, r.ToRectangle()), out problem);

    private static MinimapReader Create(ElectricConfig cfg, ElectricProfile p,
                                        Func<FishingRect, IPixelSource> open, out string problem)
    {
        problem = null;
        if (p is null) { problem = "chưa có cấu hình cho màn hình này"; return null; }

        var rect = p.ScanMinimap();
        if (!rect.IsSet) { problem = "vùng minimap quá nhỏ"; return null; }

        try { return new MinimapReader(cfg.Nav, p, open(rect)); }
        catch (Exception ex) { problem = "không mở được vùng minimap: " + ex.Message; return null; }
    }

    // ---------------------------------------------------------------- do

    /// <summary>Chụp lại minimap rồi chấm điểm mọi khối vàng trong đó.</summary>
    public List<DotCandidate> Scan()
    {
        _src.Refresh();
        int w = _src.Region.Width, h = _src.Region.Height;
        var hsv = ImageOps.BgrToHsv(_src.BgrBuffer(), w, h);

        var mask = new Mask(w, h);
        for (int i = 0; i < mask.Data.Length; i++)
        {
            int hue = hsv.H[i];
            if (hue < _nav.DotHueLo || hue > _nav.DotHueHi) continue;
            if (hsv.S[i] < _nav.DotSatMin || hsv.V[i] < _nav.DotValMin) continue;
            mask.Data[i] = 1;
        }

        // Khep khe ho 1px cua vien chấm; ban Python cung dung MORPH_CLOSE 2x2 o day.
        mask = ImageOps.Close(mask, 2);

        var lab = ImageOps.Label(mask);
        double sx = Math.Max(1e-9, _p.Sx), sy = Math.Max(1e-9, _p.Sy);

        var outp = new List<DotCandidate>();
        for (int i = 0; i < lab.Blobs.Count; i++)
        {
            var b = lab.Blobs[i];
            double areaRef = b.Area / (sx * sy);
            double wRef = b.Box.Width / sx, hRef = b.Box.Height / sy;
            double aspect = wRef / Math.Max(1e-6, hRef);
            double fill = b.Area / (double)Math.Max(1, b.Box.Width * b.Box.Height);
            double circ = Circularity(lab, i + 1, b);

            string reject = null;
            if (areaRef < _nav.DotAreaMinRef || areaRef > _nav.DotAreaMaxRef) reject = "diện tích";
            else if (wRef < _nav.DotSideMinRef || wRef > _nav.DotSideMaxRef) reject = "bề rộng";
            else if (hRef < _nav.DotSideMinRef || hRef > _nav.DotSideMaxRef) reject = "bề cao";
            else if (aspect < _nav.DotAspectMin || aspect > _nav.DotAspectMax) reject = "tỉ lệ";
            else if (circ < _nav.DotCircularityMin) reject = "không tròn (icon sét?)";
            else if (fill < _nav.DotFillMin) reject = "rỗng ruột";

            outp.Add(new DotCandidate
            {
                Cx = b.Cx, Cy = b.Cy, Box = b.Box,
                AreaRef = areaRef, WidthRef = wRef, HeightRef = hRef,
                Aspect = aspect, Circularity = circ, Fill = fill,
                Reject = reject
            });
        }

        LastCandidates = outp;
        return outp;
    }

    /// <summary>
    /// Độ tròn <c>4πA/P²</c>, chu vi đếm bằng số pixel biên (có ít nhất một hàng xóm 4-hướng nằm
    /// ngoài khối).
    ///
    /// Khác <c>cv2.arcLength</c> của bản Python: nó đo chu vi đa giác xấp xỉ, luôn NGẮN hơn số
    /// pixel biên, nên cùng một chấm thì công thức ở đây ra số THẤP hơn. Vì vậy ngưỡng mặc định là
    /// 0.70 chứ không bê nguyên 0.8 của Python — và <c>--verify-nav</c> in giá trị thật để chỉnh.
    /// </summary>
    private static double Circularity(Labeled lab, int label, Blob b)
    {
        int w = lab.Width, h = lab.Height;
        int perim = 0;

        for (int y = b.Box.Top; y < b.Box.Bottom; y++)
        {
            int row = y * w;
            for (int x = b.Box.Left; x < b.Box.Right; x++)
            {
                if (lab.Label[row + x] != label) continue;

                bool edge =
                    x == 0 || y == 0 || x == w - 1 || y == h - 1 ||
                    lab.Label[row + x - 1] != label ||
                    lab.Label[row + x + 1] != label ||
                    lab.Label[row - w + x] != label ||
                    lab.Label[row + w + x] != label;

                if (edge) perim++;
            }
        }

        if (perim <= 0) return 0;
        return Math.Min(1.0, 4.0 * Math.PI * b.Area / ((double)perim * perim));
    }

    /// <summary>
    /// Chấm đang bám, kèm nhớ ngắn khi mất dấu.
    ///
    /// Nhiều ứng viên đạt cửa thì lấy cái GẦN vị trí khung trước nhất (trong cổng
    /// <see cref="NavSettings.DotTrackGateRef"/>), chưa có vị trí cũ thì lấy cái tròn nhất — chấm
    /// đích tròn hơn mọi thứ vàng khác lọt lưới.
    /// </summary>
    public DotFix Read(long nowMs)
    {
        var cands = Scan().Where(c => c.Ok).ToList();

        DotCandidate pick = null;
        if (cands.Count > 0)
        {
            if (_hasLast)
            {
                double gate = _nav.DotTrackGateRef * Math.Max(_p.Sx, _p.Sy);
                pick = cands
                    .Select(c => (c, d: Dist(c.Cx, c.Cy, _last.X, _last.Y)))
                    .Where(t => t.d <= gate)
                    .OrderBy(t => t.d)
                    .Select(t => t.c)
                    .FirstOrDefault();
            }
            pick ??= cands.OrderByDescending(c => c.Circularity).First();
        }

        if (pick is not null)
        {
            _last = new PointF((float)pick.Cx, (float)pick.Cy);
            _hasLast = true;
            _lastSeenMs = nowMs;
            return Fix(_last, held: false);
        }

        if (_hasLast && nowMs - _lastSeenMs <= _nav.DotHoldMs) return Fix(_last, held: true);

        _hasLast = false;
        return new DotFix { Found = false };
    }

    /// <summary>Quên chấm đang bám — gọi khi bắt đầu một lượt tiếp cận mới.</summary>
    public void Forget()
    {
        _hasLast = false;
        _lastSeenMs = 0;
    }

    private DotFix Fix(PointF at, bool held)
    {
        // Hoi profile chu khong doc thang NavSettings: goc nay CHI co nghia khi gan voi mot o
        // minimap cu the, ma o do la cua tung profile. Xem ElectricProfile.MinimapOrigin.
        var (fx, fy) = _p.MinimapOrigin(_nav);
        double ox = _src.Region.Width * fx;
        double oy = _src.Region.Height * fy;

        double dx = at.X - ox, dy = at.Y - oy;
        double bearing = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        double dist = Math.Sqrt(dx * dx + dy * dy) / Math.Max(1e-9, (_p.Sx + _p.Sy) / 2.0);

        return new DotFix { Found = true, BearingDeg = bearing, DistRef = dist, At = at, Held = held };
    }

    private static double Dist(double ax, double ay, double bx, double by)
        => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    public void Dispose() => _src?.Dispose();
}
