namespace GtaMiniGameBot;

/// <summary>
/// Bốn hướng đi của đầu dây trên bảng, gọi theo đúng phím bấm.
///
/// Y hướng XUỐNG (toạ độ ảnh), nên "S" là +Y. Nhầm dấu ở đây là cả tuyến đường bị lộn ngược, mà
/// bảng thì đối xứng đủ để một tuyến lộn ngược vẫn "trông hợp lý" trong log.
/// </summary>
internal static class BoardKeys
{
    public const string Right = "D";
    public const string Down = "S";
    public const string Left = "A";
    public const string Up = "W";

    /// <summary>Thứ tự này là CHỈ SỐ hướng trong A*; đừng đổi mà không đổi cả nơi dùng.</summary>
    public static readonly string[] All = { Right, Down, Left, Up };

    public static readonly ushort[] Vk = { 0x44, 0x53, 0x41, 0x57 };   // D, S, A, W

    public static int Index(string key) => Array.IndexOf(All, key);

    public static Point Vec(string key) => key switch
    {
        Right => new Point(1, 0),
        Down => new Point(0, 1),
        Left => new Point(-1, 0),
        _ => new Point(0, -1)
    };

    public static string Opposite(string key) => key switch
    {
        Right => Left,
        Left => Right,
        Up => Down,
        _ => Up
    };

    /// <summary>Mặt của đầu nối → phím đi ra khỏi mặt đó.</summary>
    public static string FromSide(string side) => side switch
    {
        "right" => Right,
        "bottom" => Down,
        "left" => Left,
        _ => Up
    };

    public static Point SideVector(string side) => side switch
    {
        "right" => new Point(1, 0),
        "bottom" => new Point(0, 1),
        "left" => new Point(-1, 0),
        _ => new Point(0, -1)
    };

    public static string OppositeSide(string side) => side switch
    {
        "right" => "left",
        "left" => "right",
        "top" => "bottom",
        _ => "top"
    };

    /// <summary>Trục mà phím này chạy dọc theo: "x" hoặc "y".</summary>
    public static bool IsHorizontal(string key) => key is Right or Left;
}

/// <summary>Một đầu nối trên bảng: thân xám hình chữ nhật, một mặt có dải chân cắm sáng.</summary>
internal sealed class BoardTerminal
{
    public Rectangle Box { get; init; }
    public int Area { get; init; }
    public double Cx { get; init; }
    public double Cy { get; init; }

    /// <summary>Mặt có dải chân cắm trắng. Cổng ra nằm ở mặt ĐỐI DIỆN.</summary>
    public string PinSide { get; init; }

    public double PinConfidence { get; init; }

    public override string ToString() =>
        $"{Box.Width}×{Box.Height}@{Box.X},{Box.Y} area={Area} chân={PinSide}({PinConfidence:F2})";
}

/// <summary>
/// Một khung bảng đã đọc: ROI, kênh màu, tỉ lệ, và hai đầu nối. Tương ứng cái mà bản Python gọi
/// là <c>info</c>.
/// </summary>
internal sealed class BoardFrame
{
    /// <summary>Vùng ROI trong toạ độ NGUỒN (màn hình thật, hoặc ảnh tĩnh).</summary>
    public Rectangle RoiRect { get; init; }

    public int Width => RoiRect.Width;
    public int Height => RoiRect.Height;

    public byte[] Bgr { get; init; }
    public Hsv Hsv { get; init; }

    public double Sx { get; init; }
    public double Sy { get; init; }

    /// <summary>
    /// <c>(sx + sy) / 2</c> — đúng công thức <c>core_v13</c> dòng 611. Mọi hằng số <c>*_1080P</c>
    /// nhân với số này (hoặc bình phương nó, với diện tích).
    /// </summary>
    public double Scale { get; init; }

    public BoardTerminal[] Terminals { get; init; }

    public int TitleCount { get; init; }

    /// <summary>Đổi điểm trong ROI sang toạ độ nguồn.</summary>
    public Point ToSource(Point roiPoint) =>
        new(RoiRect.Left + roiPoint.X, RoiRect.Top + roiPoint.Y);
}

/// <summary>START/GOAL đã chốt, kèm mặt vật lý và điểm vào/ra của từng đầu.</summary>
internal sealed class BoardRole
{
    public int StartIdx { get; init; }
    public int GoalIdx { get; init; }

    /// <summary>Phím của đoạn ĐẦU TIÊN — hướng dây đi ra khỏi đầu nối START.</summary>
    public string StartKey { get; init; }

    /// <summary>Phím của đoạn CUỐI — hướng dây phải đi để CẮM VÀO đầu nối GOAL.</summary>
    public string GoalFinalKey { get; init; }

    public string StartPortSide { get; init; }
    public string GoalPortSide { get; init; }

    /// <summary>Điểm bắt đầu, nằm ngay ngoài mặt cổng của START.</summary>
    public Point StartPoint { get; init; }

    /// <summary>Điểm đích, nằm hơi VÀO TRONG thân GOAL — dây hợp lệ đi vào thân, không dừng ngoài.</summary>
    public Point GoalHit { get; init; }

    public Point GoalApproach { get; init; }

    public string Describe() =>
        $"START=t{StartIdx} mặt {StartPortSide} phím {StartKey} @{StartPoint.X},{StartPoint.Y} → " +
        $"GOAL=t{GoalIdx} mặt {GoalPortSide} phím cuối {GoalFinalKey} @{GoalHit.X},{GoalHit.Y}";
}

/// <summary>
/// Đọc bảng Water &amp; Power: bảng có đang mở không, ROI ở đâu, hai đầu nối ở đâu, và đầu nào là
/// START.
///
/// Cùng khuôn <see cref="WireReader"/>: hai đường vào (màn hình thật / ảnh tĩnh) chạy chung một
/// đoạn code, và không hàm nào ném ra giữa vòng lặp bot.
///
/// Toàn bộ hằng số lấy từ <c>water_power_solver_core_v13.Config</c> và
/// <c>water_power_solver_v75_planner</c>. Cái nào là pixel thì nhân
/// <see cref="BoardFrame.Scale"/> — đó là cả cơ chế hỗ trợ 2K của phần này, không có bộ số thứ hai.
/// </summary>
internal sealed class BoardReader : IDisposable
{
    // ---------------- dau noi: than xam ----------------
    private const int TermSatMax = 65;      // CFG.TERM_S_MAX_V4
    private const int TermValMin = 52;      // CFG.TERM_V_MIN_V4
    private const int TermValMax = 248;     // CFG.TERM_V_MAX_V4
    private const double TermShortMin = 18; // CFG.TERM_SHORT_MIN_1080P
    private const double TermShortMax = 48;
    private const double TermLongMin = 42;
    private const double TermLongMax = 88;
    private const double TermAreaMin = 500;
    private const double TermAreaMax = 4500;
    private const double TermPinConfMinSoft = 1.12;
    private const double TermPairMinDistance = 240;  // CFG.TERM_PAIR_MIN_DISTANCE_1080P

    // ---------------- dai chan cam trang ----------------
    private const int PinWhiteSatMax = 55;   // CFG.PIN_WHITE_S_MAX
    private const int PinWhiteValMin = 115;  // CFG.PIN_WHITE_V_MIN
    private const double PinEdgeFraction = 0.28;

    // ---------------- den bao ----------------
    private const int LampMinGreen = 28;      // planner.LAMP_MIN_GREEN
    private const double LampDominance = 1.65;
    private const int LampSideMinPixels = 20;

    // ---------------- hinh hoc dau noi ----------------
    private const int PortGapPx = 7;              // CFG.PORT_GAP_PX_1080P
    private const int GoalApproachPx = 26;        // CFG.GOAL_APPROACH_PX_1080P
    private const double GoalHitDepthFraction = 0.32;

    private readonly ElectricConfig _cfg;
    private readonly ElectricProfile _profile;

    private IPixelSource _title;
    private IPixelSource _roi;

    /// <summary>Cửa sổ nhỏ đi theo đầu dây trong vòng chạy. Xem <see cref="OpenPatch"/>.</summary>
    private RegionReader _patch;

    private byte[] _patchBuf;
    private int _patchSide;

    private BoardReader(ElectricConfig cfg, ElectricProfile profile,
                        IPixelSource title, IPixelSource roi, string problem)
    {
        _cfg = cfg;
        _profile = profile;
        _title = title;
        _roi = roi;
        Problem = problem;
    }

    public string Problem { get; private set; }

    public bool Configured => _roi is not null && _title is not null;

    public Rectangle RoiRegion => _roi?.Region ?? Rectangle.Empty;

    // ---------------------------------------------------------------- mo

    public static BoardReader Open(ElectricConfig cfg, Screen screen, ElectricProfile profile)
        => Create(cfg, profile, r => new RegionReader(r), b => FishingConfig.ToAbsolute(screen, b));

    public static BoardReader OpenForBitmap(ElectricConfig cfg, ElectricProfile profile, Bitmap still)
        => Create(cfg, profile, r => new BitmapRegion(still, r), b => b.ToRectangle());

    private static BoardReader Create(ElectricConfig cfg, ElectricProfile profile,
                                      Func<Rectangle, IPixelSource> open,
                                      Func<FishingRect, Rectangle> toSource)
    {
        if (profile is null)
            return new BoardReader(cfg, null, null, null, "chưa có cấu hình cho màn hình này");

        var roi = profile.ScanBoardRoi();
        var title = profile.ScanTitleBand();
        if (!roi.IsSet || !title.IsSet)
            return new BoardReader(cfg, profile, null, null, "độ phân giải quá nhỏ, không suy được ROI bảng");

        try
        {
            var t = open(toSource(title));
            var r = open(toSource(roi));
            return new BoardReader(cfg, profile, t, r, null);
        }
        catch (Exception ex)
        {
            return new BoardReader(cfg, profile, null, null, "không mở được ROI bảng: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- doc khung

    /// <summary>
    /// Đọc một khung. Null nghĩa là bảng KHÔNG đang mở (hoặc chưa vẽ xong) — trạng thái bình
    /// thường, không phải lỗi; <paramref name="why"/> nói rõ vướng ở đâu để log lên được.
    /// </summary>
    public BoardFrame TryRead(out string why)
    {
        why = null;
        if (!Configured) { why = Problem; return null; }

        int titleCount;
        try
        {
            _title.Refresh();
            titleCount = CountTitle(_title);
        }
        catch (Exception ex) { why = "không chụp được dải tiêu đề: " + ex.Message; return null; }

        // Nguong la DIEN TICH nen nhan sx*sy, khong phai nhan ti le dai. Ban Python:
        // count >= TITLE_MIN_PIXELS_1080P * sx * sy.
        int need = (int)(_cfg.Board.TitleMinPixels * _profile.Sx * _profile.Sy);
        if (titleCount < need)
        {
            why = $"bảng chưa mở (chữ tiêu đề {titleCount}/{need} px)";
            return null;
        }

        byte[] bgr;
        try { _roi.Refresh(); bgr = _roi.BgrBuffer(); }
        catch (Exception ex) { why = "không chụp được ROI bảng: " + ex.Message; return null; }

        int w = _roi.Region.Width, h = _roi.Region.Height;
        var hsv = ImageOps.BgrToHsv(bgr, w, h);

        var terms = DetectTerminals(hsv, _profile.Scale);
        if (terms.Length != 2)
        {
            why = $"thấy {terms.Length} đầu nối, cần đúng 2";
            return null;
        }

        return new BoardFrame
        {
            RoiRect = _roi.Region,
            Bgr = bgr,
            Hsv = hsv,
            Sx = _profile.Sx,
            Sy = _profile.Sy,
            Scale = _profile.Scale,
            Terminals = terms,
            TitleCount = titleCount
        };
    }

    /// <summary>
    /// Chụp lại ROI bảng và trả đệm BGR, KHÔNG dò lại tiêu đề hay đầu nối.
    ///
    /// Dùng trong vòng closed-loop lúc đang chạy tuyến: ở đó bot cần khung mới ~500 lần/giây-quy-đổi
    /// và chỉ quan tâm tới đầu dây đang di chuyển; dò lại đầu nối mỗi khung là trả tiền cho thông
    /// tin đã đóng băng từ trước. Null nếu không chụp được.
    /// </summary>
    public byte[] GrabRoi(byte[] into = null)
    {
        if (_roi is null) return null;
        try { _roi.Refresh(); return _roi.BgrBuffer(into); }
        catch (Exception ex) { Problem = "không chụp được ROI bảng: " + ex.Message; return null; }
    }

    /// <summary>
    /// Bảng còn đang mở không — chỉ đếm chữ tiêu đề, KHÔNG dò đầu nối.
    ///
    /// Tách riêng khỏi <see cref="TryRead"/> vì vòng chạy cần đúng một câu hỏi này ở đúng một chỗ:
    /// đầu dây vừa tới sát đầu nối đích rồi bộ theo dõi mất dấu. Hai lý do có thể — game đã đóng
    /// bảng vì THẮNG, hay bộ theo dõi hỏng — và trả lời sai thì một lượt thắng bị ghi thành lỗi rồi
    /// tắt cả job. <c>TryRead</c> không dùng được ở đây: nó trả null cho cả trường hợp bảng vẫn mở
    /// mà chưa dò ra hai đầu nối.
    /// </summary>
    public bool BoardOpen()
    {
        if (_title is null) return false;
        try
        {
            _title.Refresh();
            int need = (int)(_cfg.Board.TitleMinPixels * _profile.Sx * _profile.Sy);
            return CountTitle(_title) >= need;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- cua so nho di theo dau day

    /// <summary>Đệm của cửa sổ nhỏ, BGR row-major. Có giá trị sau mỗi <see cref="GrabPatch"/>.</summary>
    public byte[] PatchBuffer => _patchBuf;

    /// <summary>Cạnh cửa sổ nhỏ (vuông), tính bằng pixel.</summary>
    public int PatchSide => _patchSide;

    /// <summary>
    /// Mở cửa sổ chụp NHỎ dùng cho vòng chạy, cạnh <c>2·half+1</c>.
    ///
    /// Vì sao phải có đường này thay vì cứ gọi <see cref="GrabRoi"/>: đo được trên máy 2560×1440,
    /// một lượt chụp cả ROI 1814×1053 tốn 16.08 ms (chụp 11.59 + đổi BGRA→BGR 4.03), còn chụp cửa
    /// sổ 320×320 với góc di động tốn 3.35 ms — và con số đó gần như KHÔNG đổi từ 288×288 tới
    /// 384×384, vì phần lớn là độ trễ cố định của GDI/DWM chứ không phải dữ liệu. Tức 62 lượt/giây
    /// so với 300 lượt/giây. Bộ điều khiển checkpoint cần nhìn đầu dây vài chục lần trên mỗi đoạn
    /// để bắn phím rẽ đúng góc; ở nhịp 16 ms thì mỗi lần nhìn dây đã đi 6–9 px, tức cú rẽ luôn trễ
    /// gần bằng cả khoảng thoát của tuyến.
    ///
    /// CHỈ chạy trên đường màn hình thật. Đường ảnh tĩnh (<see cref="OpenForBitmap"/>) không có
    /// vòng chạy — <c>--verify-board</c> chỉ kiểm phần đọc bảng và dựng tuyến — nên ở đó hàm này
    /// trả về false thay vì giả lập một đường chụp thứ hai.
    /// </summary>
    public bool OpenPatch(int half, out string why)
    {
        why = null;
        if (_roi is null) { why = Problem ?? "chưa mở được ROI bảng"; return false; }

        if (_roi is not RegionReader)
        {
            why = "cửa sổ chụp nhỏ chỉ có trên đường màn hình thật";
            return false;
        }

        int side = Math.Max(16, half * 2 + 1);
        if (side > _roi.Region.Width || side > _roi.Region.Height)
        {
            why = $"cửa sổ {side}×{side} lớn hơn ROI {_roi.Region.Width}×{_roi.Region.Height}";
            return false;
        }

        try
        {
            _patch?.Dispose();
            _patch = new RegionReader(new Rectangle(_roi.Region.Left, _roi.Region.Top, side, side));
            _patchSide = side;
            _patchBuf = new byte[side * side * 3];
            return true;
        }
        catch (Exception ex)
        {
            _patch = null;
            why = "không mở được cửa sổ chụp nhỏ: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Chụp cửa sổ nhỏ sao cho <paramref name="centerRoi"/> nằm giữa, rồi ghi vào
    /// <see cref="PatchBuffer"/>.
    ///
    /// Cửa sổ được DỊCH vào trong ROI chứ không bị cắt nhỏ khi tới sát bìa: cỡ đệm phải cố định để
    /// bộ theo dõi khỏi phải cấp lại mảng giữa vòng chạy.
    /// </summary>
    /// <returns>Vùng đã chụp, toạ độ TRONG ROI. Rỗng nghĩa là chụp lỗi.</returns>
    public Rectangle GrabPatch(Point centerRoi)
    {
        if (_patch is null || _roi is null) return Rectangle.Empty;

        int w = _roi.Region.Width, h = _roi.Region.Height, side = _patchSide;
        int x0 = Math.Clamp(centerRoi.X - side / 2, 0, w - side);
        int y0 = Math.Clamp(centerRoi.Y - side / 2, 0, h - side);

        try
        {
            _patch.MoveWindow(_roi.Region.Left + x0, _roi.Region.Top + y0);
            _patch.Refresh();
            _patch.BgrBuffer(_patchBuf);
            return new Rectangle(x0, y0, side, side);
        }
        catch (Exception ex)
        {
            Problem = "không chụp được cửa sổ nhỏ: " + ex.Message;
            return Rectangle.Empty;
        }
    }

    /// <summary>
    /// Số pixel chữ tiêu đề trong dải trên. Đây là chữ ký "bảng đang mở" — dùng dải chữ chứ không
    /// dùng đầu nối vì đầu nối chỉ hiện sau khi bảng vẽ xong, còn tiêu đề hiện ngay.
    /// </summary>
    private int CountTitle(IPixelSource src)
    {
        var b = _cfg.Board;
        int w = src.Region.Width, h = src.Region.Height;
        var hsv = ImageOps.BgrToHsv(src.BgrBuffer(), w, h);

        int n = 0;
        for (int i = 0; i < hsv.H.Length; i++)
        {
            if (hsv.H[i] >= b.TitleHueMin && hsv.H[i] <= b.TitleHueMax &&
                hsv.S[i] >= b.TitleSatMin && hsv.V[i] >= b.TitleValMin) n++;
        }
        return n;
    }

    // ---------------------------------------------------------------- dau noi

    /// <summary>
    /// Hai đầu nối. Dò thân XÁM (bão hoà thấp, độ sáng trung bình), lọc theo hình dạng đã đo, rồi
    /// chọn CẶP điểm cao nhất mà còn cách nhau đủ xa.
    ///
    /// Bản Python còn một bộ dò phụ theo dải chân cắm sáng; ở đây bỏ vì bản Python ghi rõ "during
    /// normal play the gray-body detector generally wins" và bộ phụ tồn tại chủ yếu để chẩn đoán
    /// trên màn báo lỗi. Thiếu đầu nối thì bot BÁO và chờ khung sau, chứ không đoán.
    /// </summary>
    private static BoardTerminal[] DetectTerminals(Hsv hsv, double scale)
    {
        int w = hsv.Width, h = hsv.Height;
        var gray = new Mask(w, h);
        for (int i = 0; i < gray.Data.Length; i++)
        {
            if (hsv.S[i] < TermSatMax && hsv.V[i] > TermValMin && hsv.V[i] < TermValMax)
                gray.Data[i] = 1;
        }
        gray = ImageOps.Close(gray, 3);

        var scored = new List<(double Score, BoardTerminal Term)>();
        foreach (var blob in ImageOps.Blobs(gray))
        {
            double shape = ShapeScore(blob, scale);
            if (shape < -100) continue;

            var pin = PinSide(hsv, blob.Box);
            if (pin is null) continue;
            var (side, conf) = pin.Value;
            if (conf < TermPinConfMinSoft) continue;

            scored.Add((shape + Math.Min(conf, 3.5), new BoardTerminal
            {
                Box = blob.Box,
                Area = blob.Area,
                Cx = blob.Cx,
                Cy = blob.Cy,
                PinSide = side,
                PinConfidence = conf
            }));
        }

        if (scored.Count < 2) return Array.Empty<BoardTerminal>();

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        double minSep = TermPairMinDistance * scale;

        BoardTerminal bestA = null, bestB = null;
        double bestScore = double.NegativeInfinity;

        for (int i = 0; i < scored.Count; i++)
        for (int j = i + 1; j < scored.Count; j++)
        {
            var a = scored[i];
            var b = scored[j];
            double d = Dist(a.Term, b.Term);
            if (d < minSep) continue;

            // Hai dau noi thuong o rat xa nhau, nen khoang cach la thong tin tot — nhung chan
            // tren 2.2 de no khong lat duoc hai ung vien hinh dang xuat sac.
            double pair = a.Score + b.Score + Math.Min(2.2, d / Math.Max(1.0, 500 * scale));
            if (pair <= bestScore) continue;

            bestScore = pair;
            bestA = a.Term;
            bestB = b.Term;
        }

        return bestA is null ? Array.Empty<BoardTerminal>() : new[] { bestA, bestB };
    }

    private static double Dist(BoardTerminal a, BoardTerminal b)
    {
        double dx = a.Cx - b.Cx, dy = a.Cy - b.Cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Điểm hình dạng của một khối xám. Hai dáng chuẩn đã đo trong game: ngang ~62×32 và dọc
    /// ~39×59, diện tích quanh 1900 px² ở mốc 1080p.
    /// </summary>
    private static double ShapeScore(Blob blob, double scale)
    {
        double ws = blob.Box.Width / Math.Max(scale, 1e-6);
        double hs = blob.Box.Height / Math.Max(scale, 1e-6);
        double shortSide = Math.Min(ws, hs), longSide = Math.Max(ws, hs);
        double areaRef = blob.Area / Math.Max(scale * scale, 1e-6);

        if (shortSide < TermShortMin || shortSide > TermShortMax) return -1e9;
        if (longSide < TermLongMin || longSide > TermLongMax) return -1e9;
        if (areaRef < TermAreaMin || areaRef > TermAreaMax) return -1e9;

        double d1 = Math.Abs(shortSide - 32.0) + Math.Abs(longSide - 62.0);
        double d2 = Math.Abs(shortSide - 39.0) + Math.Abs(longSide - 59.0);
        double dimError = Math.Min(d1, d2);
        double areaError = Math.Abs(areaRef - 1900.0) / 900.0;
        return 5.0 - 0.045 * dimError - 0.35 * areaError;
    }

    /// <summary>
    /// Mặt nào của thân đầu nối có dải chân cắm TRẮNG. Đếm pixel trắng trong bốn dải mép rồi so
    /// mặt cao nhất với mặt nhì — cổng ra nằm ở mặt ĐỐI DIỆN dải chân cắm.
    /// </summary>
    private static (string Side, double Confidence)? PinSide(Hsv hsv, Rectangle box)
    {
        var white = EdgeCounts(hsv, box, PinEdgeFraction,
            (h, s, v) => s < PinWhiteSatMax && v > PinWhiteValMin, out int total);
        if (total <= 0) return null;

        return Dominant(white);
    }

    /// <summary>
    /// Đếm pixel thoả điều kiện trong bốn dải mép của một hộp. Bề rộng dải là
    /// <paramref name="fraction"/> của cạnh NGẮN — nên nó tự co giãn theo độ phân giải.
    ///
    /// Một pixel ở góc được tính cho HAI dải, đúng như bản Python (bốn phép đếm độc lập trên bốn
    /// lát cắt chồng nhau ở góc). Đó là chủ ý: đèn báo vẽ thành hai khối góc thì cả hai dải của
    /// mặt thật đều được cộng.
    /// </summary>
    private static Dictionary<string, int> EdgeCounts(Hsv hsv, Rectangle box, double fraction,
                                                      Func<int, int, int, bool> match, out int total)
    {
        var clip = Rectangle.Intersect(box, new Rectangle(0, 0, hsv.Width, hsv.Height));
        var outp = new Dictionary<string, int>
        {
            ["top"] = 0, ["bottom"] = 0, ["left"] = 0, ["right"] = 0
        };
        total = 0;
        if (clip.Width <= 0 || clip.Height <= 0) return outp;

        int band = Math.Max(3, (int)Math.Round(Math.Min(clip.Width, clip.Height) * fraction));

        for (int y = clip.Top; y < clip.Bottom; y++)
        {
            int row = y * hsv.Width;
            for (int x = clip.Left; x < clip.Right; x++)
            {
                int i = row + x;
                if (!match(hsv.H[i], hsv.S[i], hsv.V[i])) continue;

                total++;
                if (y - clip.Top < band) outp["top"]++;
                if (clip.Bottom - 1 - y < band) outp["bottom"]++;
                if (x - clip.Left < band) outp["left"]++;
                if (clip.Right - 1 - x < band) outp["right"]++;
            }
        }
        return outp;
    }

    /// <summary>
    /// Mặt trội nhất trong bốn dải, kèm mức tin cậy = (nhất+1)/(nhì+1).
    ///
    /// Vì sao đếm theo DẢI chứ không lấy trọng tâm: đèn báo hay được vẽ thành hai khối nhỏ ở hai
    /// GÓC của cùng một mặt, nên trọng tâm của chúng rơi vào giữa thân và lệch sang trái/phải.
    /// Mặt thật thì trội ở CẢ HAI dải góc.
    /// </summary>
    private static (string Side, double Confidence)? Dominant(Dictionary<string, int> scores)
    {
        string side = null;
        int best = -1, second = -1;
        foreach (var (k, v) in scores)
        {
            if (v > best) { second = best; best = v; side = k; }
            else if (v > second) second = v;
        }
        if (side is null) return null;

        return (side, (best + 1.0) / (Math.Max(0, second) + 1.0));
    }

    // ---------------------------------------------------------------- vai tro START/GOAL

    /// <summary>
    /// Chốt đầu nào là START, đầu nào là GOAL, và mặt vật lý của từng đầu.
    ///
    /// START nhận theo ĐÈN XANH: một đầu phải xanh trội hẳn (gấp 1.65 lần đầu kia). Hướng đi ra
    /// lấy từ chính MẶT có đèn xanh. GOAL lấy theo đèn ĐỎ nếu thấy; không thấy thì mới lùi về suy
    /// từ dải chân cắm trắng.
    ///
    /// Null nghĩa là chưa chốt chắc — bot chờ khung sau. Đoán bừa ở đây là đi ngược cả tuyến.
    /// </summary>
    public static BoardRole DetectRole(BoardFrame frame, out string why)
    {
        why = null;
        var terms = frame.Terminals;
        var hsv = frame.Hsv;

        int[] greens = new int[2];
        for (int i = 0; i < 2; i++) greens[i] = LampCount(hsv, terms[i].Box, green: true);

        int si = greens[0] >= greens[1] ? 0 : 1;
        int gi = 1 - si;

        if (greens[si] < LampMinGreen)
        {
            why = $"đèn xanh quá yếu ({greens[si]} px < {LampMinGreen})";
            return null;
        }
        if (greens[si] < Math.Max(LampMinGreen, greens[gi] * LampDominance))
        {
            why = $"đèn xanh không trội rõ ({greens[si]} vs {greens[gi]})";
            return null;
        }

        var startFace = LampEdgeSide(hsv, terms[si].Box, green: true);
        if (startFace is null)
        {
            why = "không xác định được mặt có đèn xanh của START";
            return null;
        }

        string startPortSide = startFace.Value.Side;
        string startKey = BoardKeys.FromSide(startPortSide);
        var startPoint = OutsideFace(terms[si].Box, startPortSide, frame.Scale);

        string goalPortSide;
        var goalFace = LampEdgeSide(hsv, terms[gi].Box, green: false);
        if (goalFace is not null) goalPortSide = goalFace.Value.Side;
        else
        {
            // Khong thay den do: suy tu dai chan cam trang, nhung doi tin cay cao hon han
            // nguong mem cua buoc do dau noi (1.35 so voi 1.12).
            if (terms[gi].PinConfidence < 1.35)
            {
                why = $"GOAL không có đèn đỏ và dải chân cắm cũng không rõ ({terms[gi].PinConfidence:F2})";
                return null;
            }
            goalPortSide = BoardKeys.OppositeSide(terms[gi].PinSide);
        }

        string goalOutKey = BoardKeys.FromSide(goalPortSide);
        string goalFinalKey = BoardKeys.Opposite(goalOutKey);
        var (approach, hit) = EdgeAndHit(terms[gi].Box, goalPortSide, frame.Scale);

        return new BoardRole
        {
            StartIdx = si,
            GoalIdx = gi,
            StartKey = startKey,
            GoalFinalKey = goalFinalKey,
            StartPortSide = startPortSide,
            GoalPortSide = goalPortSide,
            StartPoint = startPoint,
            GoalHit = hit,
            GoalApproach = approach
        };
    }

    private static bool IsLamp(int hue, int sat, int val, bool green) => green
        ? hue >= 48 && hue <= 112 && sat >= 55 && val >= 60
        : (hue <= 18 || hue >= 168) && sat >= 65 && val >= 55;

    /// <summary>Số pixel đèn màu quanh thân đầu nối (nới 2 px mỗi cạnh như bản Python).</summary>
    private static int LampCount(Hsv hsv, Rectangle box, bool green)
    {
        var r = Rectangle.Intersect(Rectangle.Inflate(box, 2, 2),
                                    new Rectangle(0, 0, hsv.Width, hsv.Height));
        int n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
        {
            int row = y * hsv.Width;
            for (int x = r.Left; x < r.Right; x++)
            {
                int i = row + x;
                if (IsLamp(hsv.H[i], hsv.S[i], hsv.V[i], green)) n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Mặt có đèn màu, đo bằng bốn dải mép rộng 25% cạnh ngắn. Đòi mặt nhất phải chiếm ít nhất
    /// 48% tổng pixel đèn (hoặc ≥8 px) và trội hơn mặt nhì ≥1.45 lần — dưới ngưỡng đó là không rõ,
    /// và không rõ thì trả null chứ không chọn bừa.
    /// </summary>
    private static (string Side, double Confidence)? LampEdgeSide(Hsv hsv, Rectangle box, bool green)
    {
        var counts = EdgeCounts(hsv, box, 0.25, (h, s, v) => IsLamp(h, s, v, green), out int total);
        if (total < LampSideMinPixels) return null;

        var pick = Dominant(counts);
        if (pick is null) return null;

        int best = counts[pick.Value.Side];
        if (best < Math.Max(8.0, 0.48 * total)) return null;
        if (pick.Value.Confidence < 1.45) return null;
        return pick;
    }

    /// <summary>Điểm nằm ngay NGOÀI một mặt của đầu nối, cách ra một khe nhỏ.</summary>
    private static Point OutsideFace(Rectangle box, string side, double scale)
    {
        int gap = Math.Max(4, (int)Math.Round(PortGapPx * scale));
        int cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
        return side switch
        {
            "top" => new Point(cx, box.Y - gap),
            "bottom" => new Point(cx, box.Bottom + gap),
            "left" => new Point(box.X - gap, cy),
            _ => new Point(box.Right + gap, cy)
        };
    }

    /// <summary>
    /// Điểm tiếp cận (ngoài thân) và điểm ĐÍCH của đầu nối GOAL.
    ///
    /// Đích nằm hơi VÀO TRONG thân — sâu 32% cạnh tương ứng. Các khung mẫu cho thấy dây hợp lệ đi
    /// vào thân đầu nối chứ không dừng lại vài pixel bên ngoài, nên đặt đích ngoài thân là tự
    /// nhận thất bại ở đúng bước cuối.
    /// </summary>
    private static (Point Approach, Point Hit) EdgeAndHit(Rectangle box, string portSide, double scale)
    {
        int cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;

        Point edge;
        int depthBase;
        switch (portSide)
        {
            case "top": edge = new Point(cx, box.Y); depthBase = box.Height; break;
            case "bottom": edge = new Point(cx, box.Bottom); depthBase = box.Height; break;
            case "left": edge = new Point(box.X, cy); depthBase = box.Width; break;
            default: edge = new Point(box.Right, cy); depthBase = box.Width; break;
        }

        var outVec = BoardKeys.SideVector(portSide);
        int approachDist = Math.Max(14, (int)Math.Round(GoalApproachPx * scale));
        int depth = Math.Max(6, (int)Math.Round(depthBase * GoalHitDepthFraction));

        return (new Point(edge.X + outVec.X * approachDist, edge.Y + outVec.Y * approachDist),
                new Point(edge.X - outVec.X * depth, edge.Y - outVec.Y * depth));
    }

    public void Dispose()
    {
        _title?.Dispose();
        _title = null;
        _roi?.Dispose();
        _roi = null;
        _patch?.Dispose();
        _patch = null;
        _patchBuf = null;
    }
}
