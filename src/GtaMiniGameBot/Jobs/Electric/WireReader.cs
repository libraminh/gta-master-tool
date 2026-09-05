namespace GtaMiniGameBot;

/// <summary>
/// Bảng màu và bố cục của panel đi dây, đo từ ảnh chụp thật trong bản Python
/// (<c>wire/wire_auto_solver_v9.py</c>: <c>COLOR_RGB</c>, <c>PROFILES</c>, <c>SLOT_LAYOUTS</c>).
///
/// Mọi số ở đây ĐỘC LẬP độ phân giải và đó là chủ ý:
///   - Màu là màu, 2K hay FHD đều thế.
///   - Vị trí ổ cắm lưu theo TỈ LỆ hộp bao panel, không phải pixel. Nhờ vậy panel to ra ở 2K vẫn
///     trỏ đúng chỗ mà không phải đo lại — và đây chính là lý do bản C# này bỏ nhánh dò-theo-khối
///     của bản Python: nhánh đó chặn kích thước khối bằng pixel tuyệt đối (<c>w &gt; 28</c>,
///     <c>h &gt; 52</c>) đo ở 1080p, nên ở 2560×1440 khối nào cũng bị loại và nó không trả về đầu
///     dây nào cả. Bản Python gọi nhánh đó là "compatibility fallback for unusual UI scales";
///     đường chính của nó (lấy mẫu theo slot chuẩn hoá) mới là đường chạy thật.
/// </summary>
internal static class WirePalette
{
    /// <summary>Màu viền panel — RGB (146,176,198). So bằng chuẩn L∞.</summary>
    public static readonly (int B, int G, int R) Border = (198, 176, 146);

    /// <summary>Màu nền trong panel — RGB (51,87,105). Dùng để chấm điểm ứng viên.</summary>
    public static readonly (int B, int G, int R) PanelBg = (105, 87, 51);

    /// <summary>
    /// Sai số cho màu NỀN khi chấm điểm ứng viên panel. Bản Python gõ thẳng <c>&lt;= 12</c> ở chỗ
    /// này, không dùng <c>panel_color_tolerance</c> — giữ nguyên chứ không gộp, vì hai con số đo
    /// cho hai việc khác nhau (một cho viền nét mảnh, một cho mảng nền lớn).
    /// </summary>
    public const int PanelBgTolerance = 12;

    private static readonly Dictionary<string, (int B, int G, int R)> Bgr = new()
    {
        // dau day keo duoc
        ["lime"] = (76, 203, 155),
        ["red"] = (37, 28, 237),
        ["yellow"] = (23, 222, 255),
        ["orange"] = (34, 101, 242),
        ["green"] = (75, 161, 0),
        // o cam
        ["pink"] = (140, 0, 236),
        ["beige"] = (108, 154, 196),
        ["purple"] = (175, 105, 124),
        ["cyan"] = (225, 170, 39),
        ["white"] = (232, 231, 230)
    };

    public static (int B, int G, int R) Of(string label) => Bgr[label];

    // ---------------- bo hai loai panel ----------------

    public static readonly string[] Sources3 = { "yellow", "orange", "green" };
    public static readonly string[] Targets3 = { "pink", "beige", "cyan" };

    public static readonly string[] Sources5 = { "lime", "red", "yellow", "orange", "green" };
    public static readonly string[] Targets5 = { "cyan", "purple", "pink", "beige", "white" };

    /// <summary>Tâm slot theo tỉ lệ (x/W, y/H) của hộp bao panel.</summary>
    public static readonly (double X, double Y)[] SourceSlots3 =
        { (0.627, 0.562), (0.725, 0.562), (0.824, 0.564) };

    public static readonly (double X, double Y)[] TargetSlots3 =
        { (0.293, 0.431), (0.731, 0.433), (0.293, 0.577) };

    public static readonly (double X, double Y)[] SourceSlots5 =
        { (0.367, 0.578), (0.437, 0.576), (0.507, 0.578), (0.573, 0.578), (0.643, 0.576) };

    public static readonly (double X, double Y)[] TargetSlots5 =
        { (0.204, 0.433), (0.507, 0.433), (0.807, 0.433), (0.204, 0.578), (0.807, 0.577) };

    public static string[] Sources(int n) => n == 5 ? Sources5 : Sources3;
    public static string[] Targets(int n) => n == 5 ? Targets5 : Targets3;
    public static (double X, double Y)[] SourceSlots(int n) => n == 5 ? SourceSlots5 : SourceSlots3;
    public static (double X, double Y)[] TargetSlots(int n) => n == 5 ? TargetSlots5 : TargetSlots3;
}

/// <summary>
/// Một lượt đã đọc được: panel ở đâu, mấy dây, và mỗi màu nằm ở slot nào.
///
/// Neo lưu theo TỈ LỆ hộp panel (0..1), không phải pixel. Nhờ vậy khi game rung màn và panel nhích
/// đi vài pixel, bot chỉ cần dò lại hộp panel rồi nhân lại — không có bước quy đổi trung gian nào
/// để lệch. Bản Python lưu pixel theo hộp cũ rồi phải rescale sang hộp mới ở
/// <c>_screen_point</c>; ở đây tỉ lệ vốn đã là dạng gốc của bảng <see cref="WirePalette"/> nên bỏ
/// hẳn được bước đó.
/// </summary>
internal sealed class WireRound
{
    /// <summary>Hộp panel lúc ĐỌC lượt này — để log và để biết cỡ panel, không dùng để bấm.</summary>
    public Rectangle Panel { get; init; }

    /// <summary>3 hoặc 5.</summary>
    public int Count { get; init; }

    /// <summary>Nhãn màu đầu dây theo thứ tự chuẩn của bộ panel.</summary>
    public string[] Sources { get; init; }

    public string[] Targets { get; init; }

    /// <summary>Tỉ lệ (x/W, y/H) trong panel của đầu dây thứ i.</summary>
    public PointF[] SourceFrac { get; init; }

    /// <summary>Tỉ lệ (x/W, y/H) trong panel của ổ cắm thứ j.</summary>
    public PointF[] TargetFrac { get; init; }

    /// <summary>Điểm bấm thật, tính theo hộp panel ĐANG thấy.</summary>
    public static Point PointIn(Rectangle panel, PointF frac) =>
        new((int)Math.Round(panel.Left + frac.X * panel.Width),
            (int)Math.Round(panel.Top + frac.Y * panel.Height));

    public string Describe() =>
        $"WIRE_{Count} panel {Panel.Width}×{Panel.Height}@{Panel.X},{Panel.Y}  " +
        $"dây: {string.Join(" ", Sources)}  ổ: {string.Join(" ", Targets)}";
}

/// <summary>Kết quả một lần thăm dò semantic — rỗng <see cref="Round"/> thì chưa được giao quyền.</summary>
internal readonly struct WireProbeHit
{
    public Rectangle Panel { get; init; }
    public WireRound Round { get; init; }
    public string Reject { get; init; }
    public bool Ok => Round is not null;
}

/// <summary>
/// Đọc panel đi dây: tìm panel, phân loại slot, và đo mức "dây đã dính" tại từng ổ cắm.
///
/// Cùng khuôn <see cref="WoodLocator"/>: hai đường vào (<see cref="Open"/> đọc màn hình thật,
/// <see cref="OpenForBitmap"/> đọc ảnh tĩnh) đi qua ĐÚNG MỘT đoạn code, và mọi lý do không đọc
/// được đều trả về thành câu tiếng Việt chứ không ném ra giữa vòng lặp bot.
///
/// Vì sao tách hai vùng đọc: lúc TÌM thì phải quét cả dải giữa màn (2304×1296 ở 2K), lúc GIẢI thì
/// chỉ cần đúng hộp panel (~600×500). Đọc dải to ở nhịp 45 ms là làm game giật, nên bot tìm ở nhịp
/// thưa rồi khoá vào panel và đọc nhanh — xem <see cref="WireSettings.SearchPollMs"/>.
/// </summary>
internal sealed class WireReader : IDisposable
{
    /// <summary>Khối màu nhỏ hơn ngần này là nhiễu. Bản Python: <c>if area &lt; 12: continue</c>.</summary>
    private const int MinColorArea = 12;

    private readonly ElectricConfig _cfg;
    private readonly ElectricProfile _profile;
    private readonly Func<Rectangle, IPixelSource> _open;

    private IPixelSource _band;
    private IPixelSource _panel;
    private Rectangle _panelRect;

    private WireReader(ElectricConfig cfg, ElectricProfile profile,
                       Func<Rectangle, IPixelSource> open, IPixelSource band, string problem)
    {
        _cfg = cfg;
        _profile = profile;
        _open = open;
        _band = band;
        Problem = problem;
    }

    /// <summary>Null khi bình thường; câu tiếng Việt nếu chưa đọc được.</summary>
    public string Problem { get; private set; }

    public bool Configured => _band is not null;

    public Rectangle BandRegion => _band?.Region ?? Rectangle.Empty;

    /// <summary>Hộp panel đang khoá; rỗng khi chưa thấy panel nào.</summary>
    public Rectangle PanelRect => _panelRect;

    // ---------------------------------------------------------------- mo

    public static WireReader Open(ElectricConfig cfg, Screen screen, ElectricProfile profile)
        => Create(cfg, profile,
                  r => new RegionReader(r),
                  band => FishingConfig.ToAbsolute(screen, band));

    /// <summary>
    /// Đọc trên một ảnh chụp cả màn. Toạ độ trong ảnh trùng toạ độ tương đối góc màn (xem
    /// <see cref="StillPicker"/>) nên không phải quy đổi gì.
    /// </summary>
    public static WireReader OpenForBitmap(ElectricConfig cfg, ElectricProfile profile, Bitmap still)
        => Create(cfg, profile,
                  r => new BitmapRegion(still, r),
                  band => band.ToRectangle());

    private static WireReader Create(ElectricConfig cfg, ElectricProfile profile,
                                     Func<Rectangle, IPixelSource> open,
                                     Func<FishingRect, Rectangle> toSource)
    {
        if (profile is null)
            return new WireReader(cfg, null, open, null, "chưa có cấu hình cho màn hình này");

        var band = profile.ScanWireBand();
        if (!band.IsSet)
            return new WireReader(cfg, profile, open, null, "vùng quét panel quá nhỏ");

        try
        {
            var src = open(toSource(band));
            return new WireReader(cfg, profile, open, src, null);
        }
        catch (Exception ex)
        {
            return new WireReader(cfg, profile, open, null, "không mở được vùng quét: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- tim panel

    /// <summary>
    /// Hộp bao panel, toạ độ nguồn pixel. Rỗng nghĩa là không có panel nào trên màn — đó là trạng
    /// thái BÌNH THƯỜNG, không phải lỗi.
    /// </summary>
    public Rectangle FindPanel()
    {
        if (_band is null) return Rectangle.Empty;

        try { _band.Refresh(); }
        catch (Exception ex) { Problem = "không chụp được vùng quét: " + ex.Message; return Rectangle.Empty; }

        int w = _band.Region.Width, h = _band.Region.Height;
        var bgr = _band.BgrBuffer();

        var border = ImageOps.MaskLinf(bgr, w, h,
            WirePalette.Border.B, WirePalette.Border.G, WirePalette.Border.R,
            _cfg.Wire.PanelColorTolerance);

        double scale = _profile.Scale;
        int minW = (int)Math.Round(_cfg.Wire.PanelMinWidth * scale);
        int minH = (int)Math.Round(_cfg.Wire.PanelMinHeight * scale);

        // Thoat som truoc hai buoc dat nhat (dong hinh thai + tach khoi).
        //
        // Luc CHUA co panel — tuc phan lon thoi gian — bot van phai hoi cau nay moi 300 ms tren
        // ca dai 2304×1296 o 2K. Dem mat na thi mien phi (vua quet xong o tren), va mot cai panel
        // that phai co it nhat chu vi cua hop nho nhat voi net vien day 2px. Do duoc tren panel
        // gia: mot panel 560×520 cho ~13000 px vien, con nguong nay chi ~2000.
        int minBorder = (int)Math.Round(2.0 * (minW + minH) * 2.0);
        if (border.Count < minBorder) return Rectangle.Empty;

        // np.ones((5,5)) trong ban Python — noi lien net vien bi rang cua cat khuc.
        border = ImageOps.Close(border, 5);

        Rectangle best = Rectangle.Empty;
        double bestScore = double.NegativeInfinity;

        foreach (var blob in ImageOps.Blobs(border, minArea: 1))
        {
            var box = blob.Box;
            if (box.Width < minW || box.Height < minH) continue;

            double aspect = box.Width / (double)Math.Max(1, box.Height);
            if (aspect is < 0.80 or > 1.85) continue;

            int inset = Math.Max(5, (int)(Math.Min(box.Width, box.Height) * 0.03));
            var inner = Rectangle.Inflate(box, -inset, -inset);
            if (inner.Width < 4 || inner.Height < 4) continue;

            double bgFrac = ImageOps.FracLinf(bgr, w, h, inner,
                WirePalette.PanelBg.B, WirePalette.PanelBg.G, WirePalette.PanelBg.R,
                WirePalette.PanelBgTolerance);

            // Uu tien hop TO va co nen dung mau. Cong 0.25 de mot hop to voi nen hoi lech van
            // thang mot hop be voi nen dung hoan toan — day la cong thuc cua ban Python.
            double score = (double)box.Width * box.Height * (0.25 + bgFrac);
            if (score <= bestScore) continue;

            bestScore = score;
            best = new Rectangle(
                _band.Region.Left + box.X, _band.Region.Top + box.Y, box.Width, box.Height);
        }

        return best;
    }

    /// <summary>
    /// Probe giao quyền: phải có hộp viền VÀ đọc đủ slot màu. Cảnh world (tủ biến áp, giàn sắt)
    /// có thể qua <see cref="FindPanel"/> nhưng fail ở đây.
    /// </summary>
    public WireProbeHit ConfirmPanel()
    {
        var box = FindPanel();
        if (box.IsEmpty)
            return new WireProbeHit { Reject = "không thấy viền" };
        var round = ReadRound(box);
        if (round is null)
            return new WireProbeHit { Panel = box, Reject = "border-only — không đọc được slot" };
        return new WireProbeHit { Panel = box, Round = round };
    }

    // ---------------------------------------------------------------- doc luot

    /// <summary>
    /// Phân loại slot trong hộp panel. Null nghĩa là chưa đọc chắc — panel đang mở dở, hoặc đang
    /// có hoạt ảnh; bot phải thử lại chứ không được đoán.
    ///
    /// <paramref name="panel"/> lấy từ <see cref="FindPanel"/>. Bot gọi lại nó mỗi lần thử lại để
    /// màn rung không làm kéo theo toạ độ cũ — đúng bài học đã ghi trong bản Python.
    /// </summary>
    public WireRound ReadRound(Rectangle panel)
    {
        var bgr = OpenPanel(panel);
        if (bgr is null) return null;

        int w = panel.Width, h = panel.Height;
        double aspect = w / (double)Math.Max(1, h);
        int n = aspect >= _cfg.Wire.ProfileAspectSplit ? 5 : 3;

        var sources = WirePalette.Sources(n);
        var targets = WirePalette.Targets(n);

        var sourceFrac = AssignSlots(bgr, w, h, WirePalette.SourceSlots(n), sources);
        if (sourceFrac is null) return null;

        var targetFrac = AssignSlots(bgr, w, h, WirePalette.TargetSlots(n), targets);
        if (targetFrac is null) return null;

        return new WireRound
        {
            Panel = panel,
            Count = n,
            Sources = sources,
            Targets = targets,
            SourceFrac = sourceFrac,
            TargetFrac = targetFrac
        };
    }

    /// <summary>
    /// Gán mỗi nhãn màu vào đúng MỘT slot.
    ///
    /// Vì sao là bài toán gán chứ không phải "màu nào ở đâu": một dây đã nối đúng biến thành một
    /// khối màu khổng lồ chạy vòng qua cả panel, nên đo bề rộng/chiều cao cả khối là vô nghĩa.
    /// Nhưng cái mấu/nắp màu tại slot thì đứng yên. Nên lấy mẫu từng ô nhỏ quanh slot rồi giải
    /// bài toán gán 1-1 cho đúng (n ≤ 5 nên vét cạn 120 phép là xong), thay vì tin vào hình dạng.
    ///
    /// Trả về mảng theo THỨ TỰ NHÃN: <c>[i]</c> = TỈ LỆ tâm slot của <c>labels[i]</c> trong panel.
    /// </summary>
    private PointF[] AssignSlots(byte[] bgr, int w, int h,
                                 (double X, double Y)[] slotsNorm, string[] labels)
    {
        int n = labels.Length;
        var centres = new PointF[n];
        for (int s = 0; s < n; s++)
            centres[s] = new PointF((float)(slotsNorm[s].X * w), (float)(slotsNorm[s].Y * h));

        int rx = Math.Max(6, (int)Math.Round(w * _cfg.Wire.SlotPatchXFrac));
        int ry = Math.Max(7, (int)Math.Round(h * _cfg.Wire.SlotPatchYFrac));

        var count = new int[n, n];          // [slot, label]
        for (int s = 0; s < n; s++)
        {
            var patch = new Rectangle(
                (int)Math.Round(centres[s].X) - rx, (int)Math.Round(centres[s].Y) - ry,
                rx * 2 + 1, ry * 2 + 1);

            for (int l = 0; l < n; l++)
            {
                var c = WirePalette.Of(labels[l]);
                count[s, l] = ImageOps.CountEuclidIn(bgr, w, h, patch,
                    c.B, c.G, c.R, _cfg.Wire.AnchorColorTolerance);
            }
        }

        int[] best = null;
        int bestSum = -1;
        foreach (var perm in SmallPermutations(n))
        {
            int sum = 0;
            for (int s = 0; s < n; s++) sum += count[s, perm[s]];
            if (sum > bestSum) { bestSum = sum; best = perm; }
        }
        if (best is null) return null;

        // Moi slot phai co DU mau moi tin. Mot slot gan 0 pixel nghia la panel dang ve do,
        // hoac ta dang doc mot cai gi khac khong phai panel.
        for (int s = 0; s < n; s++)
            if (count[s, best[s]] < _cfg.Wire.SlotMinColorPixels) return null;

        // Tra ve TI LE chu khong phai pixel — xem doc cua WireRound.
        var outp = new PointF[n];
        for (int s = 0; s < n; s++)
            outp[best[s]] = new PointF((float)slotsNorm[s].X, (float)slotsNorm[s].Y);
        return outp;
    }

    /// <summary>
    /// Hoán vị của 0..n-1, thứ tự từ điển. Sinh HẾT ra danh sách chứ không trả iterator lười:
    /// n ≤ 5 nên nhiều nhất 120 mảng, mà iterator lười dùng chung mảng đang sửa dở là loại code
    /// đúng-mà-dễ-vỡ khi người sau chỉ thêm một cái <c>.Where(...)</c>.
    /// </summary>
    private static List<int[]> SmallPermutations(int n)
    {
        var outp = new List<int[]>();
        var cur = new int[n];
        var used = new bool[n];

        void Walk(int depth)
        {
            if (depth == n) { outp.Add((int[])cur.Clone()); return; }
            for (int v = 0; v < n; v++)
            {
                if (used[v]) continue;
                used[v] = true;
                cur[depth] = v;
                Walk(depth + 1);
                used[v] = false;
            }
        }

        Walk(0);
        return outp;
    }

    // ---------------------------------------------------------------- do "day da dinh"

    /// <summary>
    /// Khối màu của từng ổ cắm, lấy khối GẦN NHẤT với mấu màu của ổ đó.
    ///
    /// Đây là số liệu thô để so trước/sau lúc game kiểm tra: một ổ còn trống chỉ có cái mấu ngắn,
    /// còn ổ đã nối đúng thì khối cùng màu đó nở ra thành cả sợi cáp uốn. Bản Python nói rõ vì sao
    /// cách này ăn hơn so khung ảnh thô: dây của màu KHÁC bắt chéo qua không làm khối màu NÀY to
    /// lên được.
    ///
    /// <c>null</c> ở một vị trí nghĩa là không tìm thấy khối màu nào — coi như không dính. Trả về
    /// <c>null</c> cho cả mảng nếu không chụp được panel.
    ///
    /// Đo trên hộp panel ĐANG thấy (<paramref name="livePanel"/>). Cỡ panel không đổi giữa các
    /// khung — chỉ vị trí nhích khi màn rung — nên tỉ lệ trước/sau vẫn so được trực tiếp mà không
    /// cần bước resize như bản Python phải làm cho khung DXCam.
    /// </summary>
    public (bool Present, Blob?[] Blobs) ReadTargetBlobs(WireRound round, Rectangle livePanel)
    {
        var bgr = OpenPanel(livePanel);
        if (bgr is null) return (false, null);

        int w = livePanel.Width, h = livePanel.Height;
        if (!Present(bgr, w, h)) return (false, null);

        var outp = new Blob?[round.Count];
        for (int j = 0; j < round.Count; j++)
            outp[j] = TargetBlob(bgr, w, h, round, j);
        return (true, outp);
    }

    /// <summary>
    /// Như <see cref="ReadTargetBlobs"/> nhưng chỉ MỘT ổ cắm.
    ///
    /// Có riêng vì lúc xác nhận một cú kéo, bot đọc liên tiếp vài khung của ĐÚNG một ổ. Đi qua bản
    /// đọc-tất-cả thì mỗi khung tốn n lượt quét panel thay vì một — với 5 dây là gấp năm chi phí
    /// cho phần thông tin bị bỏ đi.
    /// </summary>
    public (bool Present, Blob? Blob) ProbeTarget(WireRound round, Rectangle livePanel, int j)
    {
        var bgr = OpenPanel(livePanel);
        if (bgr is null) return (false, null);

        int w = livePanel.Width, h = livePanel.Height;
        if (!Present(bgr, w, h)) return (false, null);

        return (true, TargetBlob(bgr, w, h, round, j));
    }

    private Blob? TargetBlob(byte[] bgr, int w, int h, WireRound round, int j)
    {
        var c = WirePalette.Of(round.Targets[j]);
        var mask = ImageOps.MaskEuclid(bgr, w, h, c.B, c.G, c.R, _cfg.Wire.AnchorColorTolerance);
        var blobs = ImageOps.Blobs(mask, MinColorArea);

        var anchor = new PointF((float)(round.TargetFrac[j].X * w), (float)(round.TargetFrac[j].Y * h));
        return Nearest(blobs, anchor);
    }

    /// <summary>
    /// Panel còn ở đúng hộp này không, đo NGAY trên đệm vừa đọc.
    ///
    /// Vì sao không gọi lại <see cref="FindPanel"/> để hỏi câu này: dải quét ở 2560×1440 là
    /// 2304×1296 pixel, mà lúc xác nhận một cú kéo bot phải hỏi vài lần mỗi giây. Đọc hộp panel
    /// (~600×500) rồi đếm nền là rẻ hơn hai chục lần. <see cref="FindPanel"/> vẫn là câu trả lời
    /// CHÍNH THỨC về "panel ở đâu" — hàm này chỉ trả lời "còn ở đây không".
    ///
    /// Ngưỡng 0.12: trong panel thì nền chiếm phần lớn diện tích; panel đóng rồi thì ô đó là cảnh
    /// game, và cảnh game khó mà có 12% diện tích trùng đúng RGB (51,87,105).
    /// </summary>
    private static bool Present(byte[] bgr, int w, int h)
    {
        int inset = Math.Max(5, (int)(Math.Min(w, h) * 0.03));
        var inner = Rectangle.Inflate(new Rectangle(0, 0, w, h), -inset, -inset);
        if (inner.Width < 4 || inner.Height < 4) return false;

        double frac = ImageOps.FracLinf(bgr, w, h, inner,
            WirePalette.PanelBg.B, WirePalette.PanelBg.G, WirePalette.PanelBg.R,
            WirePalette.PanelBgTolerance);
        return frac >= 0.12;
    }

    /// <summary>
    /// Mức nở ra của MỘT khối so với chính nó lúc trước. 0 nếu thiếu số liệu ở một trong hai đầu.
    /// Cùng công thức <see cref="GeometryScores"/>.
    /// </summary>
    public static double GeometryScore(Blob? before, Blob? now)
    {
        if (before is not { } b0 || now is not { } b1) return 0.0;

        double wr = b1.Box.Width / (double)Math.Max(1, b0.Box.Width);
        double hr = b1.Box.Height / (double)Math.Max(1, b0.Box.Height);
        double ar = b1.Area / (double)Math.Max(1, b0.Area);
        return Math.Max(wr, Math.Max(hr, ar));
    }

    /// <summary>
    /// Khối gần neo nhất, ưu tiên khối có HỘP BAO trùm/chạm neo trước rồi mới xét trọng tâm.
    ///
    /// Thứ tự đó là có lý do đã trả giá trong bản Python: sợi cáp uốn giữ lại làm trọng tâm khối
    /// chạy rất xa khỏi ổ cắm, nên so trọng tâm không thôi sẽ chọn nhầm cái ô vuông trắng nằm bên
    /// trong ổ thay vì đúng khối cáp trắng.
    /// </summary>
    private static Blob? Nearest(List<Blob> blobs, PointF anchor)
    {
        Blob? best = null;
        double bestRect = double.MaxValue, bestCent = double.MaxValue;

        foreach (var b in blobs)
        {
            double dx = anchor.X < b.Box.Left ? b.Box.Left - anchor.X
                      : anchor.X > b.Box.Right - 1 ? anchor.X - (b.Box.Right - 1) : 0.0;
            double dy = anchor.Y < b.Box.Top ? b.Box.Top - anchor.Y
                      : anchor.Y > b.Box.Bottom - 1 ? anchor.Y - (b.Box.Bottom - 1) : 0.0;

            double rect = dx * dx + dy * dy;
            double cent = (b.Cx - anchor.X) * (b.Cx - anchor.X) + (b.Cy - anchor.Y) * (b.Cy - anchor.Y);

            if (rect < bestRect || (rect == bestRect && cent < bestCent))
            {
                bestRect = rect;
                bestCent = cent;
                best = b;
            }
        }
        return best;
    }

    /// <summary>
    /// Mức NỞ RA của khối màu mỗi ổ cắm so với lúc chưa kiểm tra: <c>max</c> của ba tỉ lệ
    /// rộng / cao / diện tích. 1.0 nghĩa là y như cũ, tức dây đó KHÔNG dính.
    ///
    /// Là TỈ LỆ nên nó không phụ thuộc độ phân giải — đó là lý do các ngưỡng 1.30/1.35/1.65 của
    /// bản Python đem sang đây dùng nguyên được.
    /// </summary>
    public static double[] GeometryScores(Blob?[] baseline, Blob?[] now)
    {
        var outp = new double[baseline.Length];
        for (int j = 0; j < baseline.Length; j++)
        {
            if (baseline[j] is not { } b0 || now is null || j >= now.Length || now[j] is not { } b1)
            {
                outp[j] = 0.0;
                continue;
            }

            double wr = b1.Box.Width / (double)Math.Max(1, b0.Box.Width);
            double hr = b1.Box.Height / (double)Math.Max(1, b0.Box.Height);
            double ar = b1.Area / (double)Math.Max(1, b0.Area);
            outp[j] = Math.Max(wr, Math.Max(hr, ar));
        }
        return outp;
    }

    // ---------------------------------------------------------------- nguon pixel panel

    /// <summary>
    /// Đệm BGR của hộp panel, mở nguồn mới nếu hộp đổi. Null nếu không chụp được — người gọi thử
    /// lại vòng sau.
    /// </summary>
    private byte[] OpenPanel(Rectangle panel)
    {
        if (panel.Width < 8 || panel.Height < 8) return null;

        try
        {
            if (_panel is null || _panelRect != panel)
            {
                _panel?.Dispose();
                _panel = _open(panel);
                _panelRect = panel;
            }
            _panel.Refresh();
            return _panel.BgrBuffer();
        }
        catch (Exception ex)
        {
            Problem = "không đọc được panel: " + ex.Message;
            _panel?.Dispose();
            _panel = null;
            _panelRect = Rectangle.Empty;
            return null;
        }
    }

    public void Dispose()
    {
        _band?.Dispose();
        _band = null;
        _panel?.Dispose();
        _panel = null;
    }
}
