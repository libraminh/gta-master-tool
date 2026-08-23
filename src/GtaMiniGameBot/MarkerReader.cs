namespace GtaMiniGameBot;

/// <summary>Một khối vàng trong khung hình 3D kèm mọi số đo — dùng cho <c>--verify-nav</c>.</summary>
internal sealed class MarkerCandidate
{
    /// <summary>
    /// Tâm khối, toạ độ TRONG KHUNG GAME (0..Width, 0..Height) — KHÔNG phải toạ độ màn hình ảo.
    ///
    /// Phải là toạ độ khung: hộp bóng nhân vật và các ô che HUD đều suy từ
    /// <see cref="ElectricProfile"/> nên cũng tính theo khung. Cộng thêm gốc vùng đọc vào đây thì
    /// trên màn hình THỨ HAI (gốc x = 2560) mọi phép so sẽ lệch đúng bằng 2560 pixel.
    /// </summary>
    public double Cx { get; init; }

    public double Cy { get; init; }

    public Rectangle Box { get; init; }

    /// <summary>Diện tích quy về mốc 1080p.</summary>
    public double AreaRef { get; init; }

    public double Fill { get; init; }

    /// <summary>Trùm lên hộp bóng nhân vật — nghi là logo vàng trên áo.</summary>
    public bool InSilhouette { get; init; }

    public string Reject { get; init; }

    public bool Ok => Reject is null;

    public override string ToString() =>
        $"@{Cx:F0},{Cy:F0} {Box.Width}×{Box.Height} dt={AreaRef:F0} đầy={Fill:F2}" +
        $"{(InSilhouette ? " [TRÙNG BÓNG NHÂN VẬT]" : "")} → {(Ok ? "ứng viên" : Reject)}";
}

/// <summary>Mốc vàng 3D đang bám.</summary>
internal sealed class MarkerFix
{
    /// <summary>Đã qua kiểm thị sai và đủ số khung — được phép lái theo.</summary>
    public bool Locked { get; init; }

    /// <summary>Khung này nhìn thấy thật (khác với đang dùng lại vị trí nhớ).</summary>
    public bool Fresh { get; init; }

    public double Cx { get; init; }

    public double Cy { get; init; }

    public double AreaRef { get; init; }

    /// <summary>Vì sao chưa khoá — để log nói được lý do thay vì im lặng.</summary>
    public string Note { get; init; } = "";

    public override string ToString() =>
        Locked ? $"mốc @{Cx:F0},{Cy:F0} dt={AreaRef:F0}{(Fresh ? "" : " (nhớ)")}" : $"chưa khoá mốc ({Note})";
}

/// <summary>
/// Dò mốc vàng 3D dưới đất — cái vòng tròn phát sáng ở chỗ làm việc.
///
/// Đây là tín hiệu lái CHÍNH lúc tới gần: nó to hàng nghìn pixel, còn chấm minimap chỉ ~10 px.
/// Lệch ngang của nó so với tâm màn là sai số yaw, không cần quy đổi gì thêm.
///
/// HAI CÁI BẪY đã đo được trên ảnh thật của người dùng, và chúng kéo ngược nhau:
///
///   1. Logo "FLASH" VÀNG sau lưng áo nhân vật. Trên tấm ảnh không hề có mốc nào, nó là vật vàng
///      bão hoà duy nhất trong khung: ~77×48 px quy về mốc 1080p, sat/val đều cao — tức lọt hết
///      mọi cửa hình học của bản Python (<c>world_min_area 1200</c>, <c>world_min_height 45</c>,
///      <c>world_min_bbox_bottom_ref 430</c>). Thả bộ dò Python vào là nó khoá vào lưng nhân vật.
///
///   2. Mốc thật thường BỊ CỘT BÊ TÔNG CHE gần hết, chỉ còn một mảng vỡ.
///
/// Nên không thể siết ngưỡng hình học để loại (1) — làm thế là mất (2). Thứ tách được hai cái đó
/// là THỊ SAI: xoay camera thì mốc trôi ngang trên màn, còn logo áo và HUD thì không. Vì vậy
/// <see cref="Update"/> chỉ cấp khoá sau khi thấy ứng viên trôi đúng chiều; không xoay camera thì
/// giữ nguyên khoá cũ chứ không cấp khoá mới.
/// </summary>
internal sealed class MarkerReader : IDisposable
{
    private readonly NavSettings _nav;
    private readonly ElectricProfile _p;
    private readonly IPixelSource _src;
    private readonly Rectangle _silhouette;
    private readonly Rectangle[] _hudMasks;

    /// <summary>Gốc vùng quét trong KHUNG GAME — để trả toạ độ về hệ khung, xem MarkerCandidate.Cx.</summary>
    private readonly Point _frameOrigin;

    private byte[] _bgr;

    private double _prevCx;
    private bool _hasPrev;
    private int _streak;
    private long _lastLockMs;
    private double _lockCx, _lockCy, _lockArea;
    private bool _locked;

    private MarkerReader(NavSettings nav, ElectricProfile p, IPixelSource src, Point frameOrigin,
                         Rectangle silhouette, Rectangle[] hudMasks)
    {
        _nav = nav;
        _p = p;
        _src = src;
        _frameOrigin = frameOrigin;
        _silhouette = silhouette;
        _hudMasks = hudMasks;
    }

    public Rectangle Region => _src.Region;

    public Rectangle SilhouetteBox => _silhouette;

    public List<MarkerCandidate> LastCandidates { get; private set; } = new();

    /// <summary>
    /// Đổi sang một ảnh tĩnh KHÁC mà giữ nguyên trạng thái liên khung — chỉ dùng cho
    /// <see cref="VerifyNav"/>, nơi phải đưa vài khung liên tiếp qua đúng bộ dò này để kiểm phép
    /// thị sai.
    /// </summary>
    public void UseStill(Bitmap still)
    {
        if (_src is not BitmapRegion br)
            throw new InvalidOperationException("UseStill chi dung cho bo do mo tren anh tinh.");
        br.Retarget(still);
    }

    public static MarkerReader Open(ElectricConfig cfg, Screen screen, ElectricProfile p, out string problem)
        => Create(cfg, p, r => new RegionReader(FishingConfig.ToAbsolute(screen, r)), out problem);

    public static MarkerReader ForBitmap(ElectricConfig cfg, ElectricProfile p, Bitmap still, out string problem)
        => Create(cfg, p, r => new BitmapRegion(still, r.ToRectangle()), out problem);

    private static MarkerReader Create(ElectricConfig cfg, ElectricProfile p,
                                       Func<FishingRect, IPixelSource> open, out string problem)
    {
        problem = null;
        if (p is null) { problem = "chưa có cấu hình cho màn hình này"; return null; }
        if (p.Width < 200 || p.Height < 200) { problem = "độ phân giải quá nhỏ"; return null; }

        // Ca BE NGANG man hinh: moc co the vao tu sat mep trai/phai — ban Python phai noi ra het co
        // sau khi thay moc that di vao tu mep trai. Chieu doc thi cat troi va hang HUD.
        int top = (int)Math.Round(p.Height * cfg.Nav.MarkerRoiTopFrac);
        int bottom = (int)Math.Round(p.Height * cfg.Nav.MarkerRoiBottomFrac);
        var band = new FishingRect { X = 0, Y = top, W = p.Width, H = Math.Max(16, bottom - top) };

        var hud = new[]
        {
            // Goc duoi-trai: minimap + hang icon mau (thanh do/vang/xanh cua HUD).
            Inflate(p.ScanMinimap().ToRectangle(), (int)(40 * p.Sx), (int)(40 * p.Sy), p),
            // Goc tren-phai: dong hien thi tien/tuoi + dong ho.
            new Rectangle((int)(p.Width * 0.72), 0, (int)(p.Width * 0.28), (int)(p.Height * 0.14)),
            // Goc tren-trai: khung chat cua server.
            new Rectangle(0, 0, (int)(p.Width * 0.22), (int)(p.Height * 0.10))
        };

        try
        {
            return new MarkerReader(cfg.Nav, p, open(band), new Point(band.X, band.Y),
                                    p.SilhouetteBox(cfg.Nav), hud);
        }
        catch (Exception ex) { problem = "không mở được vùng quét mốc: " + ex.Message; return null; }
    }

    private static Rectangle Inflate(Rectangle r, int dx, int dy, ElectricProfile p)
    {
        var big = new Rectangle(r.X - dx, r.Y - dy, r.Width + dx * 2, r.Height + dy * 2);
        return Rectangle.Intersect(big, new Rectangle(0, 0, p.Width, p.Height));
    }

    // ---------------------------------------------------------------- do

    /// <summary>
    /// Chụp lại khung rồi liệt kê mọi khối vàng, kèm lý do trượt của từng khối.
    ///
    /// Quét theo bước <see cref="NavSettings.MarkerSampleStep"/>: mốc rộng hàng trăm pixel nên bỏ
    /// pixel xen kẽ không mất khối nào, mà mặt nạ nhỏ đi 4 lần — phần đắt nhất của vòng chạy này
    /// là tách khối trên cả khung 2560×1440.
    /// </summary>
    public List<MarkerCandidate> Scan()
    {
        _src.Refresh();
        _bgr = _src.BgrBuffer(_bgr);

        int fw = _src.Region.Width, fh = _src.Region.Height;
        int step = Math.Max(1, _nav.MarkerSampleStep);
        int w = fw / step, h = fh / step;
        if (w < 4 || h < 4) { LastCandidates = new List<MarkerCandidate>(); return LastCandidates; }

        var mask = new Mask(w, h);
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * step * fw;
            int dstRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = (srcRow + x * step) * 3;
                var (hue, s, v) = ImageOps.HsvOf(_bgr[i], _bgr[i + 1], _bgr[i + 2]);
                if (hue < _nav.MarkerHueLo || hue > _nav.MarkerHueHi) continue;
                if (s < _nav.MarkerSatMin || v < _nav.MarkerValMin) continue;
                mask.Data[dstRow + x] = 1;
            }
        }

        // O che HUD do theo KHUNG, con mat na dang o luoi vung quet da giam step lan — phai tru
        // goc vung quet truoc roi moi chia.
        foreach (var r in _hudMasks)
            ImageOps.FillRect(mask, Shrink(Offset(r, -_frameOrigin.X, -_frameOrigin.Y), step), 0);

        mask = ImageOps.Close(mask, 3);
        mask = ImageOps.Open(mask, 2);

        double sx = Math.Max(1e-9, _p.Sx), sy = Math.Max(1e-9, _p.Sy);
        var outp = new List<MarkerCandidate>();

        foreach (var b in ImageOps.Blobs(mask, 4))
        {
            // Ve lai toa do KHUNG: nhan step de bo luoi mau, roi cong goc vung quet. Khong dinh
            // dang gi toi goc man hinh ao — xem ghi chu o MarkerCandidate.Cx.
            var box = new Rectangle(_frameOrigin.X + b.Box.X * step, _frameOrigin.Y + b.Box.Y * step,
                                    b.Box.Width * step, b.Box.Height * step);
            double cx = _frameOrigin.X + b.Cx * step, cy = _frameOrigin.Y + b.Cy * step;

            double areaRef = b.Area * step * step / (sx * sy);
            double fill = b.Area / (double)Math.Max(1, b.Box.Width * b.Box.Height);
            bool inSil = !_silhouette.IsEmpty && _silhouette.IntersectsWith(box) &&
                         _silhouette.Contains(new Point((int)cx, (int)cy));

            string reject = null;
            if (areaRef < _nav.MarkerAreaMinRef) reject = "quá nhỏ";
            else if (areaRef > _nav.MarkerAreaMaxRef) reject = "quá to";
            else if (inSil) reject = "nằm trong bóng nhân vật (logo áo?)";

            outp.Add(new MarkerCandidate
            {
                Cx = cx, Cy = cy, Box = box, AreaRef = areaRef, Fill = fill,
                InSilhouette = inSil, Reject = reject
            });
        }

        LastCandidates = outp;
        return outp;
    }

    private static Rectangle Shrink(Rectangle r, int step) =>
        new(r.X / step, r.Y / step, Math.Max(1, r.Width / step), Math.Max(1, r.Height / step));

    private static Rectangle Offset(Rectangle r, int dx, int dy) =>
        new(r.X + dx, r.Y + dy, r.Width, r.Height);

    /// <summary>
    /// Cập nhật khoá mốc. <paramref name="yawCounts"/> là số count chuột đã bắn ngang KỂ TỪ lần
    /// gọi trước (dương = xoay phải).
    ///
    /// Chiều thị sai: xoay phải thì cảnh trôi sang TRÁI, tức <c>Δcx &lt; 0</c>.
    /// </summary>
    public MarkerFix Update(long nowMs, int yawCounts)
    {
        var best = Scan()
            .Where(c => c.Ok)
            .OrderByDescending(c => c.AreaRef)
            .FirstOrDefault();

        if (best is null)
        {
            _hasPrev = false;
            _streak = 0;
            return Hold(nowMs, "không thấy khối vàng nào hợp lệ");
        }

        // Khoi vang nay co phai chinh cai moc dang khoa khong. Neu dung thi khong bat kiem thi sai
        // lai — nhung neu no NHAY di xa thi phai kiem lai, vi luc moc khuat sau cot thi khoi vang
        // to nhat con lai rat co the la logo tren ao.
        double gate = _nav.MarkerTrackGateRef * Math.Max(_p.Sx, _p.Sy);
        bool continuous = _locked &&
                          Math.Sqrt((best.Cx - _lockCx) * (best.Cx - _lockCx) +
                                    (best.Cy - _lockCy) * (best.Cy - _lockCy)) <= gate;

        // Chua xoay du thi khong ket luan duoc gi moi.
        if (Math.Abs(yawCounts) < _nav.ParallaxMinCounts)
        {
            _prevCx = best.Cx;
            _hasPrev = true;
            if (continuous) return Relock(best, nowMs, fresh: true);
            return Hold(nowMs, "chờ camera xoay để kiểm thị sai");
        }

        if (!_hasPrev)
        {
            _prevCx = best.Cx;
            _hasPrev = true;
            if (continuous) return Relock(best, nowMs, fresh: true);
            return Hold(nowMs, "chưa có khung trước để so thị sai");
        }

        double dx = best.Cx - _prevCx;
        _prevCx = best.Cx;

        double need = _nav.ParallaxMinPxRef * _p.Sx;
        bool movedRightWay = yawCounts > 0 ? dx <= -need : dx >= need;

        if (!movedRightWay)
        {
            _streak = 0;
            if (continuous) return Relock(best, nowMs, fresh: true);
            return Hold(nowMs, $"trượt thị sai (Δx={dx:F0}px khi xoay {yawCounts:+#;-#;0})");
        }

        _streak++;
        if (_streak < _nav.MarkerConfirmFrames && !continuous)
            return Hold(nowMs, $"đạt thị sai {_streak}/{_nav.MarkerConfirmFrames} khung");

        return Relock(best, nowMs, fresh: true);
    }

    private MarkerFix Relock(MarkerCandidate c, long nowMs, bool fresh)
    {
        _locked = true;
        _lockCx = c.Cx;
        _lockCy = c.Cy;
        _lockArea = c.AreaRef;
        _lastLockMs = nowMs;
        return new MarkerFix { Locked = true, Fresh = fresh, Cx = c.Cx, Cy = c.Cy, AreaRef = c.AreaRef };
    }

    private MarkerFix Hold(long nowMs, string note)
    {
        if (_locked && nowMs - _lastLockMs <= _nav.MarkerHoldMs)
            return new MarkerFix
            {
                Locked = true, Fresh = false,
                Cx = _lockCx, Cy = _lockCy, AreaRef = _lockArea,
                Note = note
            };

        _locked = false;
        return new MarkerFix { Locked = false, Note = note };
    }

    /// <summary>Bỏ khoá và mọi lịch sử — gọi khi bắt đầu một lượt tiếp cận mới.</summary>
    public void Forget()
    {
        _hasPrev = false;
        _streak = 0;
        _locked = false;
        _lastLockMs = 0;
    }

    public void Dispose() => _src?.Dispose();
}
