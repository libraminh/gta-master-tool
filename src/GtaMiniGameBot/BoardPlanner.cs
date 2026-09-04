using System.Diagnostics;

namespace GtaMiniGameBot;

/// <summary>Một đoạn đường thẳng của tuyến: giữ một phím cho tới khi tới <see cref="End"/>.</summary>
internal sealed class BoardSegment
{
    public string Key { get; init; }
    public PointF Start { get; init; }
    public PointF End { get; init; }
    public double Distance { get; init; }

    /// <summary>Đoạn cuối — đoạn cắm vào thân đầu nối GOAL.</summary>
    public bool IsGoalEntry { get; init; }

    public override string ToString() =>
        $"{Key} ({Start.X:F0},{Start.Y:F0})→({End.X:F0},{End.Y:F0}) {Distance:F0}px";
}

/// <summary>Tuyến đã ĐÓNG BĂNG, cùng mọi số liệu chứng minh nó an toàn.</summary>
internal sealed class BoardPlan
{
    public BoardSegment[] Segments { get; init; }
    public BoardRole Role { get; init; }

    /// <summary>Tường vật lý (chưa nở). Dùng để đo lại khoảng thoát lúc kiểm chứng.</summary>
    public Mask Obstacles { get; init; }

    /// <summary>Tường đã nở thêm lề an toàn — bản đồ mà A* thật sự đi trên đó.</summary>
    public Mask Inflated { get; init; }

    /// <summary>
    /// Bản đồ khoảng thoát của mặt nạ CHỨNG NHẬN (tường vật lý + biên hợp lệ, đã khoét hai đường
    /// hầm cổng) — đúng bản đồ mà <see cref="BoardPlanner.Certificate"/> đo trên đó.
    ///
    /// Mang theo vào lúc chạy để <c>BoardBot.Acquire</c> quyết được một câu hỏi bằng SỐ ĐO thay vì
    /// cảm tính: dây đã vượt ngã rẽ đầu tiên mất rồi, dịch cả đoạn kế đi đúng ngần ấy px thì còn
    /// đủ thoáng không. Tính sẵn ở đây nên không tốn thêm gì.
    /// </summary>
    public float[] CertClearance { get; init; }

    public Rectangle LegalBounds { get; init; }
    public int ValueThreshold { get; init; }
    public double InflationRadiusRef { get; init; }
    public int GridCell { get; init; }
    public double MinClearance { get; init; }
    public double TotalLength { get; init; }
    public int Turns => Math.Max(0, (Segments?.Length ?? 1) - 1);
    public double BuildMs { get; init; }
    public int LargeWalls { get; init; }
    public int MicroWalls { get; init; }
    public int SecondaryWalls { get; init; }
    public List<string> RefineNotes { get; init; } = new();

    /// <summary>"lan-nhanh" hay "tinh-chinh" — đường nào đã sinh ra tuyến này.</summary>
    public string Mode { get; init; } = "tinh-chinh";

    /// <summary>
    /// Đoạn ngắn nhất của tuyến. Đây là con số DỰ BÁO thắng/thua tốt nhất đo được: ngày 22/08, mọi
    /// lượt có số này ≥ 53px đều thắng, và hai lượt duy nhất thua là hai lượt có nó dưới 17px.
    /// </summary>
    public double ShortestSegment =>
        Segments is null || Segments.Length == 0 ? 0.0 : Segments.Min(s => s.Distance);

    public string Describe() =>
        $"tuyến [{string.Join(" ", Segments.Select(s => s.Key))}] {Turns} lần rẽ, " +
        $"dài {TotalLength:F0}px, đoạn ngắn nhất {ShortestSegment:F0}px, " +
        $"khoảng thoát nhỏ nhất {MinClearance:F1}px, " +
        $"lưới {GridCell}px, nở {InflationRadiusRef:F0}, dựng {BuildMs:F0}ms [{Mode}]";
}

/// <summary>
/// Dựng tuyến đi cho bảng Water &amp; Power: đọc tường, chốt vùng bàn hợp lệ, chạy A* rồi tinh
/// chỉnh từng ngã rẽ về tâm khe an toàn nhất.
///
/// Nguyên tắc bất di bất dịch, giữ nguyên từ bản Python: tuyến được ĐÓNG BĂNG trước khi bấm phím
/// đầu tiên và lúc chạy KHÔNG bao giờ đổi. Nếu không dựng nổi tuyến đủ an toàn thì GIỮ, không
/// bấm — thà đứng im còn hơn lao vào tường.
///
/// Đã bỏ có ý thức so với bản Python (và lý do):
///   - Nhánh "urgent fast lane" (A* thô, lưới 32→16px, nhận nếu đoạn đầu ngắn): đó là tối ưu ĐỘ
///     TRỄ cho các bản đồ có góc rẽ đầu chỉ cách START 60–100px. Nó chạy thêm một bộ A* nữa với
///     tiêu chí nhận riêng, tức thêm một đường code chỉ chạy trong ca hiếm — loại code khó tin
///     nhất khi không dựng lại được ca đó. Bỏ đi thì tuyến vẫn đúng, chỉ chậm hơn ~50–80ms ở
///     bước dựng.
///   - Bộ dò đầu nối phụ theo dải chân cắm: xem <see cref="BoardReader"/>.
///   - Bản vá "v38.3 diagonal corner pair": một trường hợp riêng cho vài bản đồ nhất định.
/// </summary>
internal static class BoardPlanner
{
    // ---------------- tuong ----------------
    private const int WallHueMin = 35;
    private const int WallHueMax = 105;
    private const int WallSatMin = 120;
    private const double WallDensityWindowRef = 15.0;
    private const double WallDensityMin = 0.38;
    private const double WallCloseRef = 5.0;

    private const double LargeAreaRef = 1500.0;
    private const double MicroAreaRef = 240.0;
    private const double MicroShortRef = 8.0;
    private const double MicroLongRef = 14.0;
    private const double MicroAspectMax = 7.5;
    private const double MicroFillMin = 0.34;

    // ---------------- lop bao thu hai (bias ve phia BAO TUONG) ----------------
    private const int SoftHueMin = 30;
    private const int SoftHueMax = 115;
    private const int SoftSatMin = 78;
    private const double SecondaryDensityRef = 25.0;
    private const double SecondaryDensityMin = 0.55;
    private const double SecondaryMinAreaRef = 150.0;
    private const double SecondaryShortMinRef = 6.0;
    private const double SecondaryNearRef = 11.0;

    // ---------------- vung ban ----------------
    private const double EnvelopeComponentAreaRef = 1800.0;
    private const double EnvelopePadRef = 18.0;
    private const double BoardExtentInsetRef = 4.0;
    private const double BoardExtentExemptRef = 62.0;

    // ---------------- cong dau noi ----------------
    private const double PortCarveLenRef = 54.0;
    private const double PortCarveHalfRef = 9.0;
    private const double BoundaryMarginRef = 14.0;

    // ---------------- A* ----------------
    private const double TurnCost = 22.0;
    private const double ClearanceCost = 18.0;
    private const int MaxAStarStates = 180_000;

    /// <summary>
    /// Ngân sách cho vòng quét dự phòng, ms. Hết ngân sách thì dùng bản dự phòng tốt nhất đang có
    /// thay vì quét nốt.
    ///
    /// Vì sao cần: khi MỌI ứng viên đều có đoạn ngắn hơn <see cref="MinSegmentRef"/>, vòng lặp
    /// không bao giờ <c>break</c> nên nó quét đủ 7 bán kính × 3 lưới = 21 lượt A*, mỗi lượt duyệt
    /// tới ~80k trạng thái — đo được 708–727ms trên bảng thật ngày 04/09, so với 158–232ms của
    /// bảng bình thường. Sợi dây TỰ CHẠY, nên 727ms là đủ để nó vượt qua ngã rẽ đầu tiên và cả
    /// lượt thành LateStart: ba lượt 17:16–17:24 chết đúng như vậy.
    ///
    /// Và quét thừa là chắc chắn — trace cho thấy kết quả LẶP Y HỆT qua mọi bán kính (22/16/13px
    /// ở nở 18, 16, 14, 12, 10, 8, 6). Bản dự phòng tốt nhất luôn xuất hiện trong một hai bán kính
    /// đầu; phần còn lại chỉ tiêu thời gian.
    ///
    /// 300ms vì một bán kính đo được ≈ 100–200ms: đủ cho hai bán kính, tức đủ để bắt được bản dự
    /// phòng tốt nhất trên CẢ HAI bảng thua đã ghi log, mà vẫn cắt được ~430ms phần đuôi.
    /// </summary>
    private const double FallbackBudgetMs = 300.0;
    private static readonly double[] InflationRadiiRef = { 18, 16, 14, 12, 10, 8, 6 };
    private static readonly double[] GridFallbackRefs = { 12.0, 10.0, 8.0 };
    private const double MinAcceptClearRef = 6.0;

    /// <summary>
    /// Đoạn ngắn nhất mà bộ điều khiển CÒN BẮN ĐƯỢC, mốc 1080p. Đây là một con số VẬT LÝ, không
    /// phải khẩu vị.
    ///
    /// Bấm phím rẽ không làm dây rẽ ngay: đo được trong game ở 2K, dây phải chạy khoảng 17px trên
    /// trục mới thì cú rẽ mới nhìn thấy được. Nên khi bộ điều khiển vừa xác nhận xong cú rẽ ở góc
    /// i, đầu dây đã ở quá góc đó ~17px. Nếu góc i+1 nằm gần hơn thế thì nó ĐÃ BỊ VƯỢT ngay tại
    /// thời điểm ta bắt đầu canh nó, và cửa sổ bắn (−7…+3px) không chạm tới được.
    ///
    /// Ngày 22/08 chuyện này đo được rất rõ: hai lượt fail duy nhất còn lại là hai lượt duy nhất có
    /// đoạn ngắn hơn 17px (4px và 14px); mọi lượt có đoạn ngắn nhất ≥ 53px đều thắng. Lượt 4px:
    /// bot bấm góc #6 tại (981,607), 16ms sau xác nhận xong thì dây đã ở x=964, mà góc #7 ở x=972 —
    /// vượt 8px, ngoài cửa sổ đúng 1px. Bot đứng nhìn 200ms rồi dây đâm tường.
    ///
    /// 20px ở mốc 1080p = 27px ở 2K, dư trên 17px đo được, đủ để góc kế tiếp còn nằm TRƯỚC đầu dây
    /// lúc bắt đầu canh — tức về đúng đường bắn bình thường.
    /// </summary>
    private const double MinSegmentRef = 20.0;

    // ---------------- lan nhanh cho cu re dau o gan ----------------
    private static readonly double[] UrgentGridRefs = { 32.0, 28.0, 24.0, 20.0, 16.0 };
    private const double UrgentRadiusRef = 18.0;
    private const double UrgentFirstSegMinRef = 16.0;
    private const double UrgentFirstSegMaxRef = 180.0;
    private const double UrgentMinClearRef = 10.0;

    // ---------------- on dinh khung ve ----------------
    //
    // BA LAN, sao dung ba lan cua ban Python. Truoc day C# chi co MOT lan hai khung voi nguong
    // long nhat trong ba cai (iou 0.975 / troi 0.020), tuc no nhan ca nhung khung ma ban Python
    // se tu choi. Log that 04/09 co luot thua o dung iou=0.9953: ban Python doi lan hai khung
    // phai >= 0.988 nen se cho them, con C# nhan roi dung tuyen tren mat na tuong chua ve xong.
    //
    // Vi sao khong don gian doi 3 khung: mot khung o day ton ~175 ms (ROI 2K, ban Release), ba
    // khung la ~525 ms, trong khi cu re dau tien co the chi cach START 0.2-0.5 giay. Ba lan giu
    // duoc CA HAI: khung nao ro rang da ve xong thi di ngay, khung nao con dong thi cho.
    private const double SignaturePanelVMin = 64.0;
    private const double SignatureCoverageMin = 0.28;
    private const double SignatureCoverageMax = 0.72;
    private const double SignatureCoverageDrift = 0.020;
    private const double SignatureIouMin = 0.975;

    // Lan HAI khung: chat hon han lan ba khung, vi it bang chung hon thi phai chac hon.
    private const double FastPanelVMin = 68.0;
    private const double FastCoverageMin = 0.30;
    private const double FastCoverageMax = 0.70;
    private const double FastCoverageDrift = 0.010;
    private const double FastIouMin = 0.988;

    // Lan MOT khung: chi mo duoc nhanh urgent, khong bao gio mo duoc bo dung tuyen day du.
    private const double SoloPanelVMin = 74.0;

    // ================================================================ mat na tuong

    /// <summary>Kết quả một lượt phân đoạn tường, kèm số liệu để log và để canh ổn định.</summary>
    internal sealed class WallScan
    {
        public Mask Wall { get; init; }
        public int ValueThreshold { get; init; }

        /// <summary>Trung vị V trong vùng tường — khung chưa vẽ xong thì số này thấp.</summary>
        public double PanelV { get; init; }

        /// <summary>Tỉ lệ che của ảnh thu nhỏ 128×72, dùng so hai khung liền nhau.</summary>
        public double Coverage { get; init; }

        public Mask Thumb { get; init; }
        public int LargeWalls { get; init; }
        public int MicroWalls { get; init; }
        public int SecondaryWalls { get; init; }

        /// <summary>
        /// Thời gian từng bước, ms. Có mặt vì bước này là chỗ quyết định bot có kịp bấm phím đầu
        /// tiên hay không — đo được thì mới biết tối ưu chỗ nào, thay vì đoán.
        /// </summary>
        public string Timing { get; init; }
    }

    /// <summary>
    /// Dò THÂN BẢNG đặc, không dò nét mạch trang trí.
    ///
    /// Ba bước: (1) pixel xanh bão hoà và sáng hơn ngưỡng Otsu; (2) đòi mật độ CỤC BỘ cao trong
    /// cửa sổ 15px — đây là bước loại nét mạch mảnh và chữ ma trận, vì chúng không bao giờ đủ dày
    /// trong một cửa sổ ngần đó; (3) giữ khối lớn HOẶC khối nhỏ mà đặc và vuông vắn.
    ///
    /// Bước (3) có hai tiêu chí là vì bản Python đã sửa đúng chỗ này: bản cũ chỉ giữ khối ≥1500px²
    /// nên xoá mất những mấu chữ nhật nhỏ vốn là tường thật, và A* coi chúng là đường trống rồi
    /// lao thẳng vào.
    /// </summary>
    public static WallScan ScanWalls(BoardFrame f)
    {
        int w = f.Width, h = f.Height;
        var hsv = f.Hsv;
        double scale = f.Scale;

        var sw = Stopwatch.StartNew();
        var laps = new List<string>();
        void Lap(string name) { laps.Add($"{name} {sw.Elapsed.TotalMilliseconds:F0}"); sw.Restart(); }

        int vt = GreenOtsu(hsv);
        Lap("otsu");

        var green = new Mask(w, h);
        for (int i = 0; i < green.Data.Length; i++)
        {
            if (hsv.H[i] >= WallHueMin && hsv.H[i] <= WallHueMax
                && hsv.S[i] >= WallSatMin && hsv.V[i] >= vt) green.Data[i] = 1;
        }

        int k = Odd(Math.Max(9, (int)Math.Round(WallDensityWindowRef * scale)));
        var solid = ImageOps.BoxAtLeast(green, k, WallDensityMin);
        Lap("mat-do");

        solid = ImageOps.Close(solid, Odd(Math.Max(3, (int)Math.Round(WallCloseRef * scale))));
        Lap("close");

        int largeMinArea = Math.Max(350, (int)Math.Round(LargeAreaRef * scale * scale));
        int microMinArea = Math.Max(90, (int)Math.Round(MicroAreaRef * scale * scale));
        int microShort = Math.Max(6, (int)Math.Round(MicroShortRef * scale));
        int microLong = Math.Max(10, (int)Math.Round(MicroLongRef * scale));
        int sideMin = Math.Max(8, (int)(18 * scale));

        int large = 0, micro = 0;
        var labeled = ImageOps.Label(solid);
        var clean = ImageOps.Keep(labeled, b =>
        {
            int shortSide = Math.Min(b.Box.Width, b.Box.Height);
            int longSide = Math.Max(b.Box.Width, b.Box.Height);
            double fill = b.Area / Math.Max(1.0, (double)b.Box.Width * b.Box.Height);

            bool largeOk = b.Area >= largeMinArea && b.Box.Width >= sideMin && b.Box.Height >= sideMin;
            bool microOk = b.Area >= microMinArea
                           && shortSide >= microShort
                           && longSide >= microLong
                           && longSide / Math.Max(1.0, (double)shortSide) <= MicroAspectMax
                           && fill >= MicroFillMin;

            if (largeOk) large++;
            else if (microOk) micro++;
            return largeOk || microOk;
        });
        Lap("nhan-khoi");

        // Lop bao thu hai: nguong xanh mem hon + cua so mat do LON hon. No co the MO RONG mot
        // buc tuong da duoc chung minh, nhung khong duoc tu tao ra tuong moi — day la gioi han
        // ban Python dat ra de tranh nhan tranh tri mach dam thanh tuong.
        var (secondary, secondaryKept) = SecondaryWalls(hsv, vt, scale);
        Lap("lop-bao");

        int nearK = Odd(Math.Max(3, (int)Math.Round(SecondaryNearRef * scale)));
        var near = ImageOps.Dilate(clean, nearK, nearK);
        clean = ImageOps.Or(clean, ImageOps.And(secondary, near));
        Lap("gop-bao");

        // Than dau noi LUON la tuong. Duong ham o hai cong duoc khoet lai sau, va chi o hai
        // mat da chung minh bang den bao.
        int pad = Math.Max(2, (int)Math.Round(2 * scale));
        foreach (var t in f.Terminals)
            ImageOps.FillRect(clean, Rectangle.Inflate(t.Box, pad, pad), 1);

        double panelV = ImageOps.MedianIn(hsv.V, clean);
        var thumb = ImageOps.ResizeNearest(clean, 128, 72);
        Lap("trung-vi+thumb");

        return new WallScan
        {
            Timing = string.Join("  ", laps),
            Wall = clean,
            ValueThreshold = vt,
            PanelV = panelV,
            Coverage = thumb.Count / (double)thumb.Data.Length,
            Thumb = thumb,
            LargeWalls = large,
            MicroWalls = micro,
            SecondaryWalls = secondaryKept
        };
    }

    private static (Mask Mask, int Kept) SecondaryWalls(Hsv hsv, int vt, double scale)
    {
        int w = hsv.Width, h = hsv.Height;
        double softV = Math.Max(34.0, vt - 10.0);

        var soft = new Mask(w, h);
        for (int i = 0; i < soft.Data.Length; i++)
        {
            if (hsv.H[i] >= SoftHueMin && hsv.H[i] <= SoftHueMax
                && hsv.S[i] >= SoftSatMin && hsv.V[i] >= softV) soft.Data[i] = 1;
        }

        int sk = Odd(Math.Max(11, (int)Math.Round(SecondaryDensityRef * scale)));
        var mask = ImageOps.BoxAtLeast(soft, sk, SecondaryDensityMin);

        mask = ImageOps.Close(mask, Odd(Math.Max(3, (int)Math.Round(5 * scale))));

        int minArea = Math.Max(60, (int)Math.Round(SecondaryMinAreaRef * scale * scale));
        int minShort = Math.Max(4, (int)Math.Round(SecondaryShortMinRef * scale));

        int kept = 0;
        var labeled = ImageOps.Label(mask);
        var outp = ImageOps.Keep(labeled, b =>
        {
            int shortSide = Math.Min(b.Box.Width, b.Box.Height);
            int longSide = Math.Max(b.Box.Width, b.Box.Height);
            if (b.Area < minArea || shortSide < minShort) return false;

            double fill = b.Area / Math.Max(1.0, (double)b.Box.Width * b.Box.Height);

            // Vung dac va gon la than bang thuc. Vung rat dai va thua thi van la net mach / chu.
            bool ok = fill >= 0.26 && longSide / Math.Max(1.0, (double)shortSide) <= 11.0;
            if (ok) kept++;
            return ok;
        });

        return (outp, kept);
    }

    /// <summary>
    /// Ngưỡng Otsu trên V của những pixel xanh bão hoà — chỗ trũng giữa nền xanh mờ và thân bảng
    /// xanh sáng. Bị kẹp trong 52..64 chỉ để loại khung đang mờ dần, KHÔNG để nhắm một tỉ lệ che
    /// nào; xem <see cref="ImageOps.Otsu"/>.
    /// </summary>
    private static int GreenOtsu(Hsv hsv)
    {
        var hist = new long[256];
        long n = 0;
        for (int i = 0; i < hsv.H.Length; i++)
        {
            if (hsv.H[i] < 35 || hsv.H[i] > 105 || hsv.S[i] < 100) continue;
            hist[hsv.V[i]]++;
            n++;
        }
        if (n < 1500) return 58;
        return Math.Clamp(ImageOps.Otsu(hist), 52, 64);
    }

    private static int Odd(int k) => k % 2 == 0 ? k + 1 : k;

    // ================================================================ on dinh khung ve

    /// <summary>Khung vẽ đã đủ tin để làm gì.</summary>
    public enum StabilityLane
    {
        /// <summary>Chưa đủ tin — GIỮ, không bấm phím nào.</summary>
        None,

        /// <summary>Một khung nhưng vẽ rất đậm: chỉ được dùng nhánh urgent.</summary>
        UrgentOnly,

        /// <summary>Đủ tin cho bộ dựng tuyến đầy đủ.</summary>
        Full
    }

    /// <summary>
    /// Khung tường đã ổn định tới mức nào — ba làn, sao đúng bản Python.
    ///
    /// Vì sao phải canh: bảng có hoạt ảnh hiện dần, và một khung vẽ dở cho ra tường THIẾU — tức
    /// đường trống ở chỗ có tường thật. Đóng băng tuyến trên khung đó là lao vào tường mà mọi
    /// chứng chỉ an toàn đều báo "ổn".
    ///
    /// Ba làn, xét từ mạnh nhất xuống:
    ///   - BA khung, ngưỡng thường (V≥64, che 0.28–0.72, trôi ≤0.020, iou ≥0.975) → đầy đủ;
    ///   - HAI khung, ngưỡng chặt (V≥68, che 0.30–0.70, trôi ≤0.010, iou ≥0.988) → đầy đủ;
    ///   - MỘT khung, vẽ rất đậm (V≥74, che trong dải) → CHỈ nhánh urgent.
    /// </summary>
    public static StabilityLane Stability(IReadOnlyList<WallScan> history, out string reason)
    {
        if (history is null || history.Count == 0) { reason = "chưa có khung nào"; return StabilityLane.None; }

        if (Agrees(history, 3, SignaturePanelVMin, SignatureCoverageMin, SignatureCoverageMax,
                   SignatureCoverageDrift, SignatureIouMin, out reason))
            return StabilityLane.Full;

        if (Agrees(history, 2, FastPanelVMin, FastCoverageMin, FastCoverageMax,
                   FastCoverageDrift, FastIouMin, out string fastWhy))
        {
            reason = fastWhy;
            return StabilityLane.Full;
        }

        var last = history[^1];
        if (last.PanelV >= SoloPanelVMin
            && last.Coverage >= SignatureCoverageMin && last.Coverage <= SignatureCoverageMax)
        {
            reason = $"một khung vẽ đậm (V={last.PanelV:F1} ≥ {SoloPanelVMin}, " +
                     $"che={last.Coverage:F3}) — chỉ mở lối nhanh";
            return StabilityLane.UrgentOnly;
        }

        // Bao lai ly do cua lan RONG nhat: no la cai sat nhat voi "sap dat".
        return StabilityLane.None;
    }

    /// <summary>Một làn: <paramref name="need"/> khung cuối có khớp nhau theo bộ ngưỡng cho trước không.</summary>
    private static bool Agrees(IReadOnlyList<WallScan> history, int need, double panelVMin,
                               double covMin, double covMax, double drift, double iouMin,
                               out string reason)
    {
        if (history.Count < need) { reason = $"cần {need} khung"; return false; }

        var hs = history.Skip(history.Count - need).ToArray();

        double minPanelV = hs.Min(x => x.PanelV);
        if (minPanelV < panelVMin)
        {
            reason = $"bảng còn mờ (V={minPanelV:F1} < {panelVMin})";
            return false;
        }

        double minCov = hs.Min(x => x.Coverage), maxCov = hs.Max(x => x.Coverage);
        if (minCov < covMin || maxCov > covMax)
        {
            reason = $"tỉ lệ che ngoài dải ({minCov:F3}–{maxCov:F3})";
            return false;
        }
        if (maxCov - minCov > drift)
        {
            reason = $"tỉ lệ che còn đổi ({minCov:F3}–{maxCov:F3})";
            return false;
        }

        double minIou = double.MaxValue;
        for (int i = 0; i + 1 < hs.Length; i++)
            minIou = Math.Min(minIou, Iou(hs[i].Thumb, hs[i + 1].Thumb));

        if (minIou < iouMin)
        {
            reason = $"mặt nạ tường còn dịch (iou={minIou:F4} < {iouMin})";
            return false;
        }

        reason = $"ổn định {hs.Length} khung (iou={(minIou is double.MaxValue ? 1.0 : minIou):F4}, " +
                 $"che={(minCov + maxCov) / 2:F3}, V={minPanelV:F1})";
        return true;
    }

    private static double Iou(Mask a, Mask b)
    {
        int inter = 0, union = 0;
        for (int i = 0; i < a.Data.Length; i++)
        {
            bool x = a.Data[i] != 0, y = b.Data[i] != 0;
            if (x && y) inter++;
            if (x || y) union++;
        }
        return inter / Math.Max(1.0, union);
    }

    // ================================================================ vung ban hop le

    /// <summary>
    /// Vùng bàn hợp lệ: bao lồi ĐO ĐƯỢC của các thân bảng lớn cộng hai đầu nối, cắt về trong ROI.
    /// Mọi thứ ngoài vùng này bị coi là tường cứng, nên đây là một quyết định nặng.
    ///
    /// Bản trước còn GIAO thêm với một khung "canvas" suy từ tỉ lệ ROI
    /// (0.060–0.915 × 0.055–0.920). Con số đó là giá trị DỰ PHÒNG của bản Python — nó chỉ rơi vào
    /// đó khi bộ dò biên bằng ảnh của nó không kết luận nổi — mà tôi lại dùng như một phép đo.
    /// Trên bảng thật đo được: ROI 1814×1053, hai đầu nối ở y=22 và y=1001..1044, tức NGOÀI khung
    /// đoán đó. Phép giao cắt mất cả điểm đầu lẫn điểm đích, chúng nằm trong tường đặc, và A* báo
    /// "không có tuyến nào an toàn" — một thông báo đúng cho một bản đồ đã bị làm sai.
    ///
    /// Nên bỏ hẳn khung đoán. Bao lồi vẫn giữ được mục đích ban đầu (không cho A* chạy vòng ra
    /// khoảng trang trí ngoài mạch) vì nó bám theo hình học thật của bản đồ, và nó LUÔN chứa hai
    /// đầu nối theo cách dựng — tức điểm đầu/đích không bao giờ có thể bị chính bước này khoá lại.
    /// </summary>
    public static Rectangle LegalBounds(Mask wall, BoardFrame f)
    {
        var legal = Envelope(wall, f);
        legal.Intersect(new Rectangle(0, 0, f.Width, f.Height));
        return legal;
    }

    private static Rectangle Envelope(Mask wall, BoardFrame f)
    {
        int minArea = Math.Max(200, (int)Math.Round(EnvelopeComponentAreaRef * f.Scale * f.Scale));
        var boxes = ImageOps.Blobs(wall, minArea);

        int x0, y0, x1, y1;
        if (boxes.Count == 0)
        {
            x0 = 0; y0 = 0; x1 = f.Width - 1; y1 = f.Height - 1;
        }
        else
        {
            x0 = boxes.Min(b => b.Box.Left);
            y0 = boxes.Min(b => b.Box.Top);
            x1 = boxes.Max(b => b.Box.Right - 1);
            y1 = boxes.Max(b => b.Box.Bottom - 1);
        }

        // Gom ca than dau noi de bao chum toi diem dau/cuoi thuc — nhung dau noi khong duoc keo
        // bao di sau vao vung logo/trang tri, nen khong noi them gi ngoai pad chung.
        foreach (var t in f.Terminals)
        {
            x0 = Math.Min(x0, t.Box.Left);
            y0 = Math.Min(y0, t.Box.Top);
            x1 = Math.Max(x1, t.Box.Right - 1);
            y1 = Math.Max(y1, t.Box.Bottom - 1);
        }

        int pad = Math.Max(8, (int)Math.Round(EnvelopePadRef * f.Scale));
        return Rectangle.FromLTRB(
            Math.Max(0, x0 - pad), Math.Max(0, y0 - pad),
            Math.Min(f.Width - 1, x1 + pad), Math.Min(f.Height - 1, y1 + pad));
    }

    /// <summary>
    /// Đóng cứng mọi thứ ngoài vùng bàn hợp lệ thành TƯỜNG, rồi khoét lại đúng hai đường hầm của
    /// hai cổng đầu nối. Không lỗ nào khác trên biên có thể thành đường đi.
    /// </summary>
    private static Mask ApplyEnvelope(Mask wall, BoardFrame f, BoardRole role, Rectangle legal)
    {
        var outp = ImageOps.Clone(wall);
        int w = f.Width, h = f.Height;

        ImageOps.FillRect(outp, new Rectangle(0, 0, w, legal.Top), 1);
        ImageOps.FillRect(outp, new Rectangle(0, legal.Bottom + 1, w, h - legal.Bottom - 1), 1);
        ImageOps.FillRect(outp, new Rectangle(0, 0, legal.Left, h), 1);
        ImageOps.FillRect(outp, new Rectangle(legal.Right + 1, 0, w - legal.Right - 1, h), 1);

        int inset = Math.Max(1, (int)Math.Round(BoardExtentInsetRef * f.Scale));
        ImageOps.FillRect(outp, new Rectangle(0, legal.Top, w, inset), 1);
        ImageOps.FillRect(outp, new Rectangle(0, legal.Bottom - inset + 1, w, inset), 1);
        ImageOps.FillRect(outp, new Rectangle(legal.Left, 0, inset, h), 1);
        ImageOps.FillRect(outp, new Rectangle(legal.Right - inset + 1, 0, inset, h), 1);

        return CarvePorts(outp, role, f.Scale, PortCarveHalfRef * f.Scale);
    }

    /// <summary>Khoét đường hầm ở hai cổng đã chứng minh, để START/GOAL không bị chính thân đầu nối bịt.</summary>
    private static Mask CarvePorts(Mask mask, BoardRole role, double scale, double halfWidth)
    {
        var outp = ImageOps.Clone(mask);

        var sv = BoardKeys.Vec(role.StartKey);
        var gv = BoardKeys.Vec(role.GoalFinalKey);
        double len = PortCarveLenRef * scale;
        int width = Math.Max(5, (int)Math.Round(2 * halfWidth + 1));
        int r = Math.Max(8, (int)Math.Round(15 * scale));

        ImageOps.FillCircle(outp, role.StartPoint.X, role.StartPoint.Y, r, 0);
        ImageOps.DrawThickLine(outp, role.StartPoint,
            new Point((int)Math.Round(role.StartPoint.X + sv.X * len),
                      (int)Math.Round(role.StartPoint.Y + sv.Y * len)), width, 0);

        ImageOps.FillCircle(outp, role.GoalHit.X, role.GoalHit.Y, r, 0);
        ImageOps.DrawThickLine(outp,
            new Point((int)Math.Round(role.GoalHit.X - gv.X * len),
                      (int)Math.Round(role.GoalHit.Y - gv.Y * len)),
            role.GoalHit, width, 0);

        return outp;
    }

    /// <summary>
    /// Nở tường thêm lề an toàn, đóng cứng mép ROI, rồi mở lại hai đường hầm cổng (hẹp hơn lúc
    /// chứng nhận, để A* không men sát vào thân đầu nối).
    ///
    /// Nhận vào bản đồ khoảng thoát ĐÃ TÍNH của mặt nạ tường + biên hợp lệ, chứ không tự tính:
    /// bộ dựng tuyến thử bảy bán kính trên cùng một mặt nạ, mà distance transform là phần đắt
    /// nhất. Tính một lần rồi ngưỡng bảy lần.
    /// </summary>
    private static Mask InflateFrom(float[] certClear, BoardFrame f, BoardRole role, double radiusRef)
    {
        int r = Math.Max(2, (int)Math.Round(radiusRef * f.Scale));
        var inf = ImageOps.Within(certClear, f.Width, f.Height, r);

        int b = Math.Max(3, (int)Math.Round(BoundaryMarginRef * f.Scale));
        ImageOps.FillRect(inf, new Rectangle(0, 0, f.Width, b), 1);
        ImageOps.FillRect(inf, new Rectangle(0, f.Height - b, f.Width, b), 1);
        ImageOps.FillRect(inf, new Rectangle(0, 0, b, f.Height), 1);
        ImageOps.FillRect(inf, new Rectangle(f.Width - b, 0, b, f.Height), 1);

        return CarvePorts(inf, role, f.Scale, Math.Max(3, PortCarveHalfRef * f.Scale * 0.55));
    }

    // ================================================================ A*

    /// <summary>
    /// A* trên lưới thô, ba chiều trạng thái (x, y, hướng).
    ///
    /// Ba thành phần giá, và cả ba đều cần:
    ///   - 1.0 mỗi ô: đường ngắn.
    ///   - <see cref="TurnCost"/> = 22 mỗi lần rẽ: mỗi lần rẽ trong game là một lần phải bấm đúng
    ///     thời điểm, tức một cơ hội thất bại. Tuyến ít rẽ đáng giá hơn tuyến ngắn.
    ///   - <see cref="ClearanceCost"/>/(c+0.6)²: men sát tường bị phạt nặng dần. Đây là thứ giữ
    ///     cho tuyến đi giữa khe thay vì cọ vào mép.
    ///
    /// Cấm quay đầu (đi ngược hướng hiện tại): dây trong game không lùi lại được.
    /// </summary>
    private static List<(int X, int Y, int D)> AStar(Mask blockedPx, float[] clearPx,
                                                     BoardRole role, int cell)
    {
        int w = blockedPx.Width, h = blockedPx.Height;
        int W = Math.Max(2, (int)Math.Ceiling(w / (double)cell));
        int H = Math.Max(2, (int)Math.Ceiling(h / (double)cell));

        var blocked = ImageOps.ResizeNearest(blockedPx, W, H);
        var clear = ImageOps.ResizeArea(clearPx, w, h, W, H);
        for (int i = 0; i < clear.Length; i++) clear[i] /= cell;

        int sx = (int)Math.Round(role.StartPoint.X / (double)cell);
        int sy = (int)Math.Round(role.StartPoint.Y / (double)cell);
        int gx = (int)Math.Round(role.GoalHit.X / (double)cell);
        int gy = (int)Math.Round(role.GoalHit.Y / (double)cell);

        var sf = NearestFree(blocked, sx, sy, 8);
        var gf = NearestFree(blocked, gx, gy, 8);
        if (sf is null || gf is null) return null;
        (sx, sy) = sf.Value;
        (gx, gy) = gf.Value;

        int sd = BoardKeys.Index(role.StartKey);
        int gd = BoardKeys.Index(role.GoalFinalKey);

        int Encode(int x, int y, int d) => (y * W + x) * 4 + d;

        var g = new double[W * H * 4];
        Array.Fill(g, double.PositiveInfinity);
        var came = new int[W * H * 4];
        Array.Fill(came, -1);

        int startState = Encode(sx, sy, sd);
        int goalState = Encode(gx, gy, gd);
        g[startState] = 0;

        var heap = new PriorityQueue<int, double>();
        heap.Enqueue(startState, 0);

        int explored = 0;
        bool found = false;

        while (heap.TryDequeue(out int st, out double f))
        {
            if (st == goalState) { found = true; break; }
            if (++explored > MaxAStarStates) break;

            int d = st & 3;
            int cellIdx = st >> 2;
            int x = cellIdx % W, y = cellIdx / W;
            double gs = g[st];

            // Bo qua ban ghi CU con sot trong heap: PriorityQueue khong co decrease-key nen mot
            // trang thai co the nam trong hang nhieu lan, va chi ban co f nho nhat con dung.
            if (f > gs + Heuristic(x, y, d) + 1e-9) continue;

            for (int nd = 0; nd < 4; nd++)
            {
                if (BoardKeys.All[nd] == BoardKeys.Opposite(BoardKeys.All[d])) continue;

                var v = BoardKeys.Vec(BoardKeys.All[nd]);
                int nx = x + v.X, ny = y + v.Y;
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                if (blocked.Data[ny * W + nx] != 0) continue;

                double c = Math.Max(0.25, clear[ny * W + nx]);
                double risk = ClearanceCost / ((c + 0.60) * (c + 0.60));
                double ng = gs + 1.0 + (nd == d ? 0.0 : TurnCost) + risk;

                int ns = Encode(nx, ny, nd);
                if (ng >= g[ns]) continue;

                g[ns] = ng;
                came[ns] = st;
                heap.Enqueue(ns, ng + Heuristic(nx, ny, nd));
            }
        }

        if (!found) return null;

        var states = new List<(int, int, int)>();
        for (int s = goalState; s != -1; s = came[s])
        {
            int idx = s >> 2;
            states.Add((idx % W, idx / W, s & 3));
            if (s == startState) break;
        }
        states.Reverse();
        return states;

        double Heuristic(int x, int y, int d) =>
            Math.Abs(x - gx) + Math.Abs(y - gy) + (d == gd ? 0 : 2);
    }

    private static (int X, int Y)? NearestFree(Mask blocked, int x, int y, int maxR)
    {
        int w = blocked.Width, h = blocked.Height;
        bool Free(int xx, int yy) =>
            xx >= 0 && yy >= 0 && xx < w && yy < h && blocked.Data[yy * w + xx] == 0;

        if (Free(x, y)) return (x, y);

        for (int r = 1; r <= maxR; r++)
        {
            for (int yy = y - r; yy <= y + r; yy++)
            {
                if (Free(x - r, yy)) return (x - r, yy);
                if (Free(x + r, yy)) return (x + r, yy);
            }
            for (int xx = x - r + 1; xx < x + r; xx++)
            {
                if (Free(xx, y - r)) return (xx, y - r);
                if (Free(xx, y + r)) return (xx, y + r);
            }
        }
        return null;
    }

    // ================================================================ trang thai -> doan

    /// <summary>
    /// Đổi chuỗi trạng thái A* thành các đoạn Manhattan.
    ///
    /// Trả null nếu tuyến không hợp lệ về nguyên tắc: phím đầu không khớp mặt START, phím cuối
    /// không khớp mặt GOAL, có hai đoạn liền nhau ngược hướng, hay có đoạn dài ≤1px. Thà không có
    /// tuyến còn hơn có tuyến sai — bot sẽ giữ và chờ khung sau.
    /// </summary>
    private static BoardSegment[] StatesToSegments(List<(int X, int Y, int D)> states, int cell,
                                                   BoardRole role, double scale)
    {
        if (states is null || states.Count == 0) return null;

        var keys = new List<string>();
        var turns = new List<PointF>();

        int prevD = states[0].D;
        for (int i = 1; i < states.Count; i++)
        {
            if (states[i].D == prevD) continue;

            // Cu re xay ra SAU khi da toi o truoc do, tren huong truoc do.
            var p = states[i - 1];
            keys.Add(BoardKeys.All[prevD]);
            turns.Add(new PointF(p.X * cell, p.Y * cell));
            prevD = states[i].D;
        }
        keys.Add(BoardKeys.All[prevD]);
        turns.Add(new PointF(role.GoalHit.X, role.GoalHit.Y));

        if (keys[0] != role.StartKey || keys[^1] != role.GoalFinalKey) return null;
        for (int i = 0; i + 1 < keys.Count; i++)
            if (keys[i + 1] == BoardKeys.Opposite(keys[i])) return null;

        // GOP NGOAN NGOEO CUOI: [V, H-ti, V] → [V] (va doi xung [H, V-ti, H]).
        //
        // A* di theo o luoi, con GOAL nam o toa do pixel bat ky. Khi cot luoi gan GOAL nhat lech
        // khoi GOAL.X, A* ha xuong theo cot do roi buoc ngang MOT o sang cot GOAL ngay truoc khi cam
        // — sinh ra hai doan ~11 px lien tiep. Doan 11 px thi bo dieu khien khong ban noi (xem
        // MinSegmentRef), va cot lech con lam doan ha xuong men sat mep ham cong: log 16:29 ngay
        // 04/09 cho khoang thoat 2.0 tai (260,1034) voi ham cong [259..283] — o ca 7 ban kinh no,
        // o ca luoi 13 va 11. RefineTurns cung bo tay: moi cach dich ba goc cuoi deu tao doan
        // < MinSegmentRef nen ca ba "giu nguyen" roi rollback. Cach dung la bo hai doan ti do di va
        // de doan ha xuong chay thang theo truc GOAL — dung hinh cua luot thang sinh doi 16:27
        // (A @968 → S @306 thang 83 px). Buoc canh truc GOAL ngay duoi day lo phan con lai.
        if (keys.Count >= 3 && keys[^1] == keys[^3])
        {
            var hopA = turns[^3];
            var hopB = turns[^2];
            double hop = Math.Abs(hopB.X - hopA.X) + Math.Abs(hopB.Y - hopA.Y);
            if (hop < MinSegmentRef * scale)
            {
                keys.RemoveRange(keys.Count - 2, 2);
                turns.RemoveRange(turns.Count - 2, 2);
                turns[^1] = new PointF(role.GoalHit.X, role.GoalHit.Y);
            }
        }

        // Cho cu re GAN CUOI canh lan cuoi khop dung truc cua GOAL, de doan cuoi cam thang vao.
        if (keys.Count >= 2)
        {
            var t = turns[^2];
            turns[^2] = BoardKeys.IsHorizontal(keys[^1])
                ? new PointF(t.X, role.GoalHit.Y)
                : new PointF(role.GoalHit.X, t.Y);
        }

        var axes = new List<double>();
        for (int i = 0; i < keys.Count - 1; i++)
            axes.Add(BoardKeys.IsHorizontal(keys[i]) ? turns[i].X : turns[i].Y);

        return SegmentsFromAxes(keys, axes, role);
    }

    /// <summary>
    /// Dựng đoạn từ danh sách phím + danh sách TRỤC rẽ. Trục là con số thật sự định nghĩa tuyến:
    /// mỗi lần rẽ là "đi tới khi toạ độ x (hoặc y) đạt giá trị này".
    /// </summary>
    private static BoardSegment[] SegmentsFromAxes(List<string> keys, List<double> axes, BoardRole role)
    {
        var cur = new PointF(role.StartPoint.X, role.StartPoint.Y);
        var goal = new PointF(role.GoalHit.X, role.GoalHit.Y);
        var outp = new List<BoardSegment>();

        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            PointF end;
            if (i == keys.Count - 1) end = goal;
            else
            {
                end = BoardKeys.IsHorizontal(key)
                    ? new PointF((float)axes[i], cur.Y)
                    : new PointF(cur.X, (float)axes[i]);
            }

            var v = BoardKeys.Vec(key);
            double dist = (end.X - cur.X) * v.X + (end.Y - cur.Y) * v.Y;
            if (dist <= 1.0) return null;

            // Sai so vuong goc phai rat nho, khong thi day khong con la doan Manhattan.
            double ox = (end.X - cur.X) - v.X * dist;
            double oy = (end.Y - cur.Y) - v.Y * dist;
            if (Math.Sqrt(ox * ox + oy * oy) > 2.5) return null;

            outp.Add(new BoardSegment
            {
                Key = key,
                Start = cur,
                End = end,
                Distance = dist,
                IsGoalEntry = i == keys.Count - 1
            });
            cur = end;
        }

        return outp.ToArray();
    }

    // ================================================================ chung chi an toan

    /// <summary>
    /// Khoảng thoát nhỏ nhất dọc cả tuyến, đo trên tường VẬT LÝ (chỉ mở hai đường hầm cổng).
    ///
    /// Bỏ qua 8 mẫu đầu của đoạn đầu và 8 mẫu cuối của đoạn cuối: hai chỗ đó nằm trong thân đầu
    /// nối, nên đo ở đó luôn ra gần 0 và sẽ loại mọi tuyến hợp lệ.
    /// </summary>
    /// <summary>
    /// Điểm có khoảng thoát NHỎ NHẤT trên tuyến — cùng phép đo với <see cref="Certificate"/> (bỏ 8
    /// mẫu đầu/cuối), trả chuỗi để gắn vào trace. Tách riêng để đường thắng không trả thêm gì: chỉ
    /// gọi khi tuyến ĐÃ trượt cổng. Một toạ độ soi được trên ảnh <c>hong-*.png</c> đáng giá hơn
    /// hẳn câu "khoảng thoát 2.0" trơ trọi.
    /// </summary>
    private static string Bottleneck(BoardSegment[] segs, float[] clear, BoardFrame f)
    {
        double minc = double.MaxValue;
        int at = -1;
        PointF where = default;

        for (int i = 0; i < segs.Length; i++)
        {
            PointF a = segs[i].Start, b = segs[i].End;
            var vals = SampleLine(clear, f.Width, f.Height, a, b, 2.0);
            int from = 0, to = vals.Count;
            if (i == 0 && vals.Count > 8) from = 8;
            if (i == segs.Length - 1 && vals.Count > 8) to = Math.Max(1, vals.Count - 8);

            for (int k = from; k < to; k++)
            {
                if (vals[k] >= minc) continue;
                minc = vals[k];
                at = i;
                double t = vals.Count > 1 ? k / (double)(vals.Count - 1) : 0.0;
                where = new PointF((float)(a.X + (b.X - a.X) * t), (float)(a.Y + (b.Y - a.Y) * t));
            }
        }

        return at < 0 ? "" : $" tại đoạn #{at} ({where.X:F0},{where.Y:F0})";
    }

    private static (double MinClear, double Total) Certificate(
        BoardSegment[] segs, float[] clear, BoardFrame f)
    {
        double minc = double.MaxValue, total = 0;
        for (int i = 0; i < segs.Length; i++)
        {
            var vals = SampleLine(clear, f.Width, f.Height, segs[i].Start, segs[i].End, 2.0);
            int from = 0, to = vals.Count;
            if (i == 0 && vals.Count > 8) from = 8;
            if (i == segs.Length - 1 && vals.Count > 8) to = Math.Max(1, vals.Count - 8);

            for (int k = from; k < to; k++) minc = Math.Min(minc, vals[k]);
            total += segs[i].Distance;
        }

        return (minc == double.MaxValue ? 0.0 : minc, total);
    }

    private static List<float> SampleLine(float[] clear, int w, int h, PointF a, PointF b, double step)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double d = Math.Sqrt(dx * dx + dy * dy);
        int n = Math.Max(2, (int)(d / step) + 1);

        var outp = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)(n - 1);
            int x = Math.Clamp((int)Math.Round(a.X + dx * t), 0, w - 1);
            int y = Math.Clamp((int)Math.Round(a.Y + dy * t), 0, h - 1);
            outp.Add(clear[y * w + x]);
        }
        return outp;
    }

    internal static double LineMinClear(float[] clear, int w, int h, PointF a, PointF b, double step = 1.0)
    {
        var vals = SampleLine(clear, w, h, a, b, step);
        double m = double.MaxValue;
        foreach (float v in vals) m = Math.Min(m, v);
        return m == double.MaxValue ? 0.0 : m;
    }

    /// <summary>
    /// Kiểm tra tuyến có nằm trong vùng bàn đo được hay không — CỐ TÌNH không dùng mặt nạ tường.
    ///
    /// Nhờ vậy một lỗi phân đoạn tường không thể tự chứng nhận cho một tuyến đi ra ngoài bàn. Hai
    /// đầu tuyến được miễn một đoạn <see cref="BoardExtentExemptRef"/> vì chúng nằm trong thân đầu
    /// nối, mà thân đầu nối có thể chạm mép vùng hợp lệ.
    /// </summary>
    private static bool ExtentAudit(BoardSegment[] segs, Rectangle legal, double scale, out string why)
    {
        double inset = Math.Max(1.0, BoardExtentInsetRef * scale);
        double exempt = Math.Max(16.0, BoardExtentExemptRef * scale);

        for (int i = 0; i < segs.Length; i++)
        {
            var s = segs[i];
            double dx = s.End.X - s.Start.X, dy = s.End.Y - s.Start.Y;
            double d = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
            int n = Math.Max(2, (int)(d / 2.0) + 1);

            for (int k = 0; k < n; k++)
            {
                double u = k / (double)(n - 1);
                double along = d * u;
                if (i == 0 && along <= exempt) continue;
                if (i == segs.Length - 1 && d - along <= exempt) continue;

                double x = s.Start.X + dx * u, y = s.Start.Y + dy * u;
                if (x < legal.Left + inset || x > legal.Right - inset ||
                    y < legal.Top + inset || y > legal.Bottom - inset)
                {
                    why = $"tuyến ra ngoài vùng bàn: đoạn {i} tại ({x:F0},{y:F0}), " +
                          $"vùng ({legal.Left},{legal.Top})-({legal.Right},{legal.Bottom})";
                    return false;
                }
            }
        }

        why = "ok";
        return true;
    }

    // ================================================================ tinh chinh nga re

    /// <summary>
    /// Dịch mỗi góc rẽ của A* về TÂM AN TOÀN NHẤT của cùng cái khe đó.
    ///
    /// A* chạy trên ô 8–12px, nên một góc thô có thể rơi đúng vào MÉP của một khe hợp lệ. Về hình
    /// học tuyến vẫn đúng, nhưng game có thể không nhận cú rẽ ở mép đó, và đầu dây đang tự chạy sẽ
    /// lao thẳng vào tường kế tiếp. Quét từng pixel trong ±24px, chấm theo khoảng thoát tệ nhất
    /// của: đuôi làn hiện tại, đoạn mở đầu làn kế, và cả làn kế.
    ///
    /// Không tìm được tâm an toàn thì GIỮ NGUYÊN trục thô — không bao giờ tự bịa ra một cú rẽ mới.
    /// </summary>
    private static (BoardSegment[] Segs, List<string> Notes) RefineTurns(
        BoardSegment[] segs, float[] clear, BoardFrame f, BoardRole role)
    {
        var notes = new List<string>();
        if (segs.Length < 2) return (segs, notes);

        var keys = segs.Select(s => s.Key).ToList();
        var rawAxes = new List<double>();
        for (int i = 0; i < segs.Length - 1; i++)
            rawAxes.Add(BoardKeys.IsHorizontal(segs[i].Key) ? segs[i].End.X : segs[i].End.Y);

        double scale = f.Scale;
        int search = Math.Max(12, (int)Math.Round(24.0 * scale));
        double minSafe = Math.Max(8.0, 11.0 * scale);
        double minSeg = MinSegmentRef * scale;
        double tailLen = Math.Max(14.0, 36.0 * scale);
        double previewLen = Math.Max(26.0, 88.0 * scale);

        var chosen = new List<double>();
        var cur = new PointF(role.StartPoint.X, role.StartPoint.Y);
        var goal = new PointF(role.GoalHit.X, role.GoalHit.Y);

        for (int i = 0; i < rawAxes.Count; i++)
        {
            string key = keys[i], next = keys[i + 1];
            var v = BoardKeys.Vec(key);
            var nv = BoardKeys.Vec(next);

            double nextAxis = i + 1 < rawAxes.Count
                ? rawAxes[i + 1]
                : (BoardKeys.IsHorizontal(next) ? goal.X : goal.Y);

            double bestScore = double.NegativeInfinity, bestClear = 0, bestAxis = rawAxes[i];
            int bestOff = 0;
            bool any = false;

            for (int off = -search; off <= search; off++)
            {
                double axis = rawAxes[i] + off;
                var corner = BoardKeys.IsHorizontal(key)
                    ? new PointF((float)axis, cur.Y)
                    : new PointF(cur.X, (float)axis);

                // Ngưỡng ở đây từng là 3px, và đó là chỗ đã SINH RA đoạn 4px giết một lượt chạy.
                // Xem <see cref="MinSegmentRef"/>.
                double d1 = (corner.X - cur.X) * v.X + (corner.Y - cur.Y) * v.Y;
                if (d1 < minSeg) continue;

                var nextEnd = BoardKeys.IsHorizontal(next)
                    ? new PointF((float)nextAxis, corner.Y)
                    : new PointF(corner.X, (float)nextAxis);

                double d2 = (nextEnd.X - corner.X) * nv.X + (nextEnd.Y - corner.Y) * nv.Y;
                if (d2 < minSeg) continue;

                var tailStart = new PointF(
                    (float)(corner.X - v.X * Math.Min(tailLen, d1)),
                    (float)(corner.Y - v.Y * Math.Min(tailLen, d1)));
                var frontEnd = new PointF(
                    (float)(corner.X + nv.X * Math.Min(previewLen, d2)),
                    (float)(corner.Y + nv.Y * Math.Min(previewLen, d2)));

                double cTail = LineMinClear(clear, f.Width, f.Height, tailStart, corner);
                double cFront = LineMinClear(clear, f.Width, f.Height, corner, frontEnd);
                double cNext = LineMinClear(clear, f.Width, f.Height, corner, nextEnd, 2.0);
                double cmin = Math.Min(cTail, Math.Min(cFront, cNext));
                if (cmin < minSafe) continue;

                // Muc tieu chinh la khoang thoat te nhat lon nhat; tru mot chut theo do lech de
                // hai vi tri an toan bang nhau thi giu nguyen hinh hoc cua A*.
                double score = cmin - 0.005 * Math.Abs(off);
                if (score <= bestScore) continue;

                bestScore = score;
                bestClear = cmin;
                bestAxis = axis;
                bestOff = off;
                any = true;
            }

            notes.Add(any
                ? $"#{i} {key}→{next} thô={rawAxes[i]:F1} chỉnh={bestAxis:F1} ({bestOff:+0;-0;0}px) thoát={bestClear:F1}"
                : $"#{i} {key}→{next} thô={rawAxes[i]:F1} giữ nguyên (không có tâm an toàn)");

            chosen.Add(bestAxis);
            cur = BoardKeys.IsHorizontal(key)
                ? new PointF((float)bestAxis, cur.Y)
                : new PointF(cur.X, (float)bestAxis);
        }

        var refined = SegmentsFromAxes(keys, chosen, role);
        if (refined is null)
        {
            notes.Add("bỏ tinh chỉnh: dựng lại đoạn không hợp lệ");
            return (segs, notes);
        }

        var (minc, _) = Certificate(refined, clear, f);
        if (minc < MinAcceptClearRef * scale)
        {
            notes.Add($"bỏ tinh chỉnh: chứng chỉ tụt xuống {minc:F1}px");
            return (segs, notes);
        }

        // Hai phep kiem d1/d2 o tren dung `nextAxis` THO cho doan sau, nen sau khi CA hai goc lien
        // ke deu dich thi do dai that van co the tut xuong duoi nguong. Kiem lai tuyen hoan chinh,
        // va chi giu ban tinh chinh khi no khong lam xuat hien doan ngan hon ban tho.
        double refMin = MinSegment(refined), rawMin = MinSegment(segs);
        if (refMin < MinSegmentRef * scale && refMin < rawMin)
        {
            notes.Add($"bỏ tinh chỉnh: sinh ra đoạn {refMin:F0}px < {MinSegmentRef * scale:F0}px " +
                      $"(bản thô {rawMin:F0}px)");
            return (segs, notes);
        }

        return (refined, notes);
    }

    /// <summary>Đoạn NGẮN NHẤT của tuyến. Xem <see cref="MinSegmentRef"/> để biết vì sao nó quan trọng.</summary>
    private static double MinSegment(BoardSegment[] segs)
    {
        if (segs is null || segs.Length == 0) return 0.0;
        double m = double.MaxValue;
        foreach (var s in segs) m = Math.Min(m, s.Distance);
        return m;
    }

    // ================================================================ dung tuyen

    /// <summary>
    /// Dựng tuyến hoàn chỉnh. Null nghĩa là CHƯA có tuyến nào được chứng nhận — bot phải giữ và
    /// chờ khung sạch hơn, tuyệt đối không bấm phím.
    ///
    /// Thử lần lượt các bán kính nở từ RỘNG tới HẸP (18→6): lấy được tuyến với lề rộng thì tốt
    /// hơn hẳn, chỉ khi bản đồ chật mới hạ lề xuống. Với mỗi bán kính thử ba cỡ lưới 12/10/8px.
    /// </summary>
    /// <param name="urgentOnly">
    /// Khung mới chỉ qua được làn MỘT khung (<see cref="StabilityLane.UrgentOnly"/>). Lúc đó chỉ
    /// lối nhanh được phép trả tuyến — nó có chứng chỉ khoảng thoát CHẶT hơn (10 thay vì 6) nên
    /// còn chịu nổi một mặt nạ tường mới chỉ một khung; bộ dựng tuyến đầy đủ thì không.
    /// </param>
    public static BoardPlan Plan(BoardFrame f, BoardRole role, WallScan scan, bool urgentOnly,
                                 out string why)
    {
        var sw = Stopwatch.StartNew();
        var wall = scan.Wall;
        var legal = LegalBounds(wall, f);
        double scale = f.Scale;

        // Mat na CHUNG NHAN: tuong vat ly + bien vung hop le, chi mo hai duong ham cong. Ca bay
        // ban kinh no va moi phep do khoang thoat deu suy tu day, nen tinh dung MOT lan.
        var certMask = ApplyEnvelope(wall, f, role, legal);
        var certClear = ImageOps.Clearance(certMask);

        BoardSegment[] bestSegs = null;
        Mask bestInf = null;
        double bestMinClear = 0, bestTotal = 0, bestRadius = 0;
        int bestCell = 0;
        string mode = "tinh-chinh";

        // ---------------- lan nhanh ----------------
        //
        // Vi sao ton tai: soi day TU CHAY ngay khi bang mo. Do tren bang that cua nguoi dung, cu re
        // dau tien chi cach START 130 px — khoang 0.2–0.5 giay. Ma bo dung tuyen day du mat ~180 ms
        // TREN nen ~350 ms cho hai khung on dinh. Neu ban do thuoc loai "re som" thi phai tra loi
        // bang mot tuyen tho nhung DA CHUNG NHAN, thay vi mot tuyen dep ma tra loi muon.
        //
        // Chi nhan khi: doan dau nam trong dai "nguy hiem vi ngan", khoang thoat vuot nguong CHAT
        // hon binh thuong (10 thay vi 6), va qua duoc kiem tra vung ban. Khong dat thi roi ve
        // duong tinh chinh ben duoi — khong bao gio ha tieu chuan an toan de doi lay toc do.
        // Ban kinh no 18 duoc dung O CA HAI cho: loi nhanh, va bac dau cua thang chinh
        // (InflationRadiiRef[0] cung la 18). Dung lai mot lan tinh — moi lan la mot phep no cong
        // mot distance transform tren 1.9 trieu pixel.
        var inflateCache = new Dictionary<double, (Mask Mask, float[] Clear)>();

        (Mask Mask, float[] Clear) Inflated(double radiusRef)
        {
            if (inflateCache.TryGetValue(radiusRef, out var hit)) return hit;

            var m = InflateFrom(certClear, f, role, radiusRef);
            var made = (m, ImageOps.Clearance(m));
            inflateCache[radiusRef] = made;
            return made;
        }

        var trace = new List<string>();

        // KHONG "tinh chinh de cuu tuyen truot cong" o day. Da thu ngay 04/09 va PHAI BO:
        // RefineTurns quet ±32px cho MOI goc (16 goc × 65 vi tri × 3 phep do khoang thoat), goi no
        // cho tung ung vien bi loai lam thoi gian dung tuyen nhay 180-197ms → 710/716ms. Soi day TU
        // CHAY, nen 710ms nghia la dau day da vuot qua nga re dau tien truoc khi tuyen kip dong bang:
        // hai ban 17:05 ngay 04/09 chet LateStart o dung day (vuot 21px va 121px). Doi mot ca hiem
        // "planner bo cuoc" lay mot ca thuong xuyen "dung tuyen qua muon" la lo nang.
        //
        // Ban thua 16:29 duoc cuu bang HAI thu re tien hon, van con o duoi: gop duoi ngoan ngoeo
        // trong StatesToSegments, va cong loi nhanh kep theo be rong ham cong.
        {
            var (urgentInf, urgentClear) = Inflated(UrgentRadiusRef);
            double segMin = UrgentFirstSegMinRef * scale, segMax = UrgentFirstSegMaxRef * scale;

            // Cong khoang thoat loi nhanh KHONG duoc cao hon nua be rong ham cong. Doan cuoi luon
            // chay trong cai ham ta tu khoet (nua rong PortCarveHalfRef = 12 px o 2K, do duoc 13.0),
            // nen 10 × 1.333 = 13.33 la khong bao gio dat — ca luot thang 16:27 lan luot thua 16:29
            // ngay 04/09 deu chet o "13.0 < 13.3" du moi goc sau tinh chinh deu 34–125 px. min()
            // chi ha cong xuong toi muc ham cong; nut that ben trong van bi doi cao hon lan day du.
            double urgentGate = Math.Min(UrgentMinClearRef, PortCarveHalfRef) * scale;

            foreach (double cellRef in UrgentGridRefs)
            {
                int cell = Math.Max(4, (int)Math.Round(cellRef * scale));
                var states = AStar(urgentInf, urgentClear, role, cell);
                if (states is null) { trace.Add($"lưới {cell}: A* không ra"); continue; }

                var segs = StatesToSegments(states, cell, role, scale);
                if (segs is null) { trace.Add($"lưới {cell}: tuyến không hợp lệ"); continue; }

                double firstLen = segs[0].Distance;
                if (firstLen < segMin || firstLen > segMax)
                {
                    // khong phai ban do "re som" — de duong tinh chinh lo
                    trace.Add($"lưới {cell}: đoạn đầu {firstLen:F0}px ngoài dải {segMin:F0}–{segMax:F0} → bỏ lối nhanh");
                    break;
                }

                var (minc, total) = Certificate(segs, certClear, f);
                if (minc < urgentGate)
                {
                    trace.Add($"lưới {cell}: khoảng thoát {minc:F1} < {urgentGate:F1}"
                              + Bottleneck(segs, certClear, f));
                    continue;
                }
                if (!ExtentAudit(segs, legal, scale, out string exWhy))
                {
                    trace.Add($"lưới {cell}: {exWhy}");
                    continue;
                }

                trace.Add($"lưới {cell}: NHẬN (đoạn đầu {firstLen:F0}px, thoát {minc:F1})");
                bestSegs = segs;
                bestInf = urgentInf;
                bestMinClear = minc;
                bestTotal = total;
                bestRadius = UrgentRadiusRef;
                bestCell = cell;
                mode = "lan-nhanh";
                break;
            }
        }

        if (bestSegs is not null)
        {
            // CO Y KHONG tinh chinh nga re o lan nhanh: goc tho da co chung chi khoang thoat chat
            // hon, va no thuong kich hoat som hon mot chut trong cung cai khe — doi lai duoc dung
            // cai dang thieu, la thoi gian phan ung cho cu re dau tien.
            why = "ok";
            trace.Add("lối nhanh: KHÔNG tinh chỉnh ngã rẽ");
            return Finish(f, role, scan, wall, bestInf, legal, bestSegs, bestMinClear, bestTotal,
                          bestRadius, bestCell, sw, trace, mode, certClear);
        }

        if (urgentOnly)
        {
            // Mot khung thi DUNG O DAY. Ban Python co ham rieng (plan_urgent) tra None thay vi roi
            // xuong duong tinh chinh, dung vi ly do nay: bo dung tuyen day du chi doi khoang thoat
            // 6, khong du de tin mot mat na tuong moi thay MOT lan.
            why = "mới một khung vẽ đậm, lối nhanh không ra tuyến — chờ khung thứ hai"
                  + FormatTrace(trace);
            return null;
        }

        // Duong DU PHONG: tuyen dat chung chi khoang thoat nhung CO doan ngan hon nguong. Giu rieng
        // ra chu khong loai thang — mot tuyen co doan ngan van con co hoi (bo dieu khien co luoi an
        // toan "vuot goc thi ban ngay"), trong khi khong co tuyen nao thi bot dung im va chac chan
        // mat bang. Chi dung den no khi khong ban do nao cho tuyen sach.
        BoardSegment[] fbSegs = null;
        Mask fbInf = null;
        double fbMinClear = 0, fbTotal = 0, fbRadius = 0, fbShortest = 0;
        int fbCell = 0;

        double minSegOk = MinSegmentRef * scale;
        bool firstRadius = true;

        foreach (double radiusRef in InflationRadiiRef)
        {
            // NGAN SACH. Kiem TRUOC khi mo mot ban kinh moi, va chi sau khi ban kinh dau da chay
            // tron — nho vay ket qua khong bao gio te hon truoc, chi nhanh hon.
            if (!firstRadius && fbSegs is not null && sw.Elapsed.TotalMilliseconds > FallbackBudgetMs)
            {
                trace.Add($"hết ngân sách {FallbackBudgetMs:F0}ms trước nở {radiusRef:F0} → " +
                          $"dùng dự phòng đang có (đoạn ngắn nhất {fbShortest:F0}px)");
                break;
            }
            firstRadius = false;

            // Khoang thoat cua mat na ĐÃ NỞ, tinh MOT lan cho ca ba co luoi. Truoc day A* tu tinh
            // nen mot lan dung tuyen co the goi distance transform toi 21 lan (7 ban kinh × 3 co
            // luoi) — do dung la cho lam ham dung tuyen ton 830ms tren ROI 2K.
            var (inf, infClear) = Inflated(radiusRef);
            bool got = false;

            foreach (double cellRef in GridFallbackRefs)
            {
                int cell = Math.Max(4, (int)Math.Round(cellRef * scale));
                var states = AStar(inf, infClear, role, cell);
                if (states is null)
                {
                    trace.Add($"lưới {cell} nở {radiusRef:F0}: A* không ra");
                    continue;
                }

                var segs = StatesToSegments(states, cell, role, scale);
                if (segs is null)
                {
                    trace.Add($"lưới {cell} nở {radiusRef:F0}: tuyến không hợp lệ");
                    continue;
                }

                var (minc, total) = Certificate(segs, certClear, f);
                if (minc < MinAcceptClearRef * scale)
                {
                    trace.Add($"lưới {cell} nở {radiusRef:F0}: khoảng thoát {minc:F1} " +
                              $"< {MinAcceptClearRef * scale:F1}" + Bottleneck(segs, certClear, f));
                    continue;
                }

                double shortest = MinSegment(segs);
                if (shortest < minSegOk)
                {
                    // Giu ban tot nhat trong so cac ban "co doan ngan": doan ngan nhat DAI hon la
                    // it nguy hiem hon.
                    if (fbSegs is null || shortest > fbShortest)
                    {
                        fbSegs = segs; fbInf = inf; fbMinClear = minc; fbTotal = total;
                        fbRadius = radiusRef; fbCell = cell; fbShortest = shortest;
                    }
                    trace.Add($"lưới {cell} nở {radiusRef:F0}: đoạn ngắn nhất {shortest:F0}px " +
                              $"< {minSegOk:F0}px → để dự phòng");
                    continue;
                }

                bestSegs = segs;
                bestInf = inf;
                bestMinClear = minc;
                bestTotal = total;
                bestRadius = radiusRef;
                bestCell = cell;
                got = true;
                break;
            }

            if (got) break;
        }

        if (bestSegs is null && fbSegs is not null)
        {
            bestSegs = fbSegs; bestInf = fbInf; bestMinClear = fbMinClear; bestTotal = fbTotal;
            bestRadius = fbRadius; bestCell = fbCell;
            trace.Add($"KHÔNG có tuyến nào tránh được đoạn ngắn — dùng dự phòng, " +
                      $"đoạn ngắn nhất {fbShortest:F0}px (< {minSegOk:F0}px, cú rẽ ở đó có thể trượt)");
        }

        if (bestSegs is null)
        {
            // Dinh kem TRACE. Truoc day trace bi vut o day va chi in ra qua Finish khi THANG —
            // dung cai luc can no nhat thi khong co. Log that 04/09 co ba luot "giai 0 bang" ma
            // dong duy nhat de lai la cau nay, khong noi duoc ban kinh nao chet vi A* va ban kinh
            // nao chet vi khoang thoat.
            why = "không dựng được tuyến nào đủ an toàn ở mọi bán kính nở"
                  + FormatTrace(trace);
            return null;
        }

        // RefineTurns chi chay MOT lan, tren tuyen DA duoc chung nhan. Day la ngan sach da do:
        // 180-197ms tong cho ca ham. Goi no them cho cac ung vien bi loai la cach lam bang 17:05
        // ngay 04/09 chet LateStart.
        var (refined, refineNotes) = RefineTurns(bestSegs, certClear, f, role);
        if (!ReferenceEquals(refined, bestSegs))
        {
            var (minc2, total2) = Certificate(refined, certClear, f);
            bestSegs = refined;
            bestMinClear = minc2;
            bestTotal = total2;
        }

        if (!ExtentAudit(bestSegs, legal, scale, out string extentWhy))
        {
            why = extentWhy + FormatTrace(trace);
            return null;
        }

        // KHONG chan theo DO DAI tuyen. Xem ghi chu o cuoi class de biet vi sao cho chan cu da
        // giet oan nhung ban do that.
        why = "ok";
        trace.AddRange(refineNotes);
        return Finish(f, role, scan, wall, bestInf, legal, bestSegs, bestMinClear, bestTotal,
                      bestRadius, bestCell, sw, trace, mode, certClear);
    }

    /// <summary>
    /// Gắn trace vào chuỗi lý do hỏng, mỗi bậc một dòng thụt lề. Rỗng thì trả chuỗi rỗng để câu
    /// lý do vẫn đọc được như cũ.
    /// </summary>
    private static string FormatTrace(List<string> trace)
    {
        if (trace is null || trace.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        foreach (string t in trace) sb.Append("\n    ").Append(t);
        return sb.ToString();
    }

    private static BoardPlan Finish(BoardFrame f, BoardRole role, WallScan scan, Mask wall,
                                    Mask inflated, Rectangle legal, BoardSegment[] segs,
                                    double minClear, double total, double radiusRef, int cell,
                                    Stopwatch sw, List<string> notes, string mode,
                                    float[] certClear) =>
        new()
        {
            Segments = segs,
            Role = role,
            Obstacles = wall,
            Inflated = inflated,
            CertClearance = certClear,
            LegalBounds = legal,
            ValueThreshold = scan.ValueThreshold,
            InflationRadiusRef = radiusRef,
            GridCell = cell,
            MinClearance = minClear,
            TotalLength = total,
            BuildMs = sw.Elapsed.TotalMilliseconds,
            LargeWalls = scan.LargeWalls,
            MicroWalls = scan.MicroWalls,
            SecondaryWalls = scan.SecondaryWalls,
            RefineNotes = notes,
            Mode = mode
        };

    // KHONG co dai do dai tuyen hop le o day, va day la ly do — de khong ai them lai:
    //
    // Ban C# dau tien chan tuyen ngoai dai 300–3000px (moc 1080p, tuc 400–4000px o 2K), lay tu
    // CFG.MIN/MAX_ROUTE_LENGTH_PX_1080P. Ngay 22/08 no giet mot ban do that: START @97,175 →
    // GOAL @192,1015 can tuyen 4343px, bi tu choi 15 lan lien roi bang tu dong vi day tu dam
    // tuong. Bot dung im ca luot ma khong bam mot phim nao.
    //
    // Truy lai thi hai hang so do CHI ton tai trong core_v10.py/core_v13.py, va chi duoc doc boi
    // hai ham DA CHET: build_plan (bo dung tuyen theo atlas cu) dung 300–3000, _planner_core dung
    // 80–4050. Chuoi Python dang chay that (v75.py -> v75_planner.plan) khong co MOT hang so do
    // dai nao — cu lay danh sach hang so cua v75_planner ra doc la thay. Tieu chi nhan cua no la
    // KHOANG THOAT (MIN_ACCEPT_CLEAR_REF), pham vi ban (extent audit) va tinh hop le hinh hoc cua
    // tung doan. Do dai chi la so bao cao.
    //
    // Cung mot loai bay voi TARGET_TOLERANCE/FINE_ZONE cua bo dieu khien (xem phan dau BoardBot):
    // hang so cua nhanh Python da bi bo, port sang C# thanh ra chan mat duong dung.
    //
    // Neu can chan tuyen qua dai thi chan theo NGAN SACH THOI GIAN luc chay, khong phan pixel —
    // BoardBot.RunRoute suy thoi gian cho phep tu chinh do dai tuyen.
}
