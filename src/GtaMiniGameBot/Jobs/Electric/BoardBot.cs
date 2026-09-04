using System.Diagnostics;

namespace GtaMiniGameBot;

/// <summary>
/// Theo dõi ĐẦU DÂY đang tự chạy trên bảng.
///
/// Vì sao phải so với một khung THAM CHIẾU chụp lúc dựng tuyến: nét mạch trang trí trên bảng cũng
/// màu xanh. Chỉ lọc "xanh sáng" thôi là bắt cả chúng. Nên một pixel chỉ tính là đầu dây khi nó
/// xanh sáng VÀ (đã đổi so với khung tham chiếu HOẶC sáng bất thường ≥165) — cái đang chạy thì
/// khác khung cũ, còn nét vẽ tĩnh thì không. Vệt dây đã vẽ TRƯỚC khung tham chiếu cũng bị loại
/// theo đúng luật đó, và đó là điều mong muốn: chỉ còn lại phần dây vẽ ra SAU, tức phần đầu.
///
/// Chỉ quét một DẢI HẸP theo hướng cần đo, chứ không quét cả ROI: vừa rẻ, vừa loại luôn mọi thứ
/// đang động ở nơi khác trên bảng.
///
/// Ảnh vào là MỘT MIẾNG nhỏ (xem <see cref="BoardReader.GrabPatch"/>) chứ không phải cả ROI, nên
/// mọi hàm ở đây nhận thêm <c>patchRect</c> — toạ độ của miếng trong ROI. Đọc miếng theo toạ độ
/// miếng, đọc khung tham chiếu theo toạ độ ROI; khung tham chiếu VẪN là khung ROI đầy đủ.
/// </summary>
internal static class BoardTracker
{
    private const int DiffThreshold = 10;     // CFG.TRACK_DIFF_THRESHOLD
    private const int GreenHueMin = 65;       // CFG.TRACK_GREEN_H_MIN
    private const int GreenHueMax = 100;
    private const int GreenSatMin = 105;
    private const int GreenValMin = 130;
    private const int AxisMinPixels = 2;      // CFG.TRACK_AXIS_MIN_PIXELS

    // KHONG co ngoai le "sang qua muc thi khoi can da doi" (BrightOverride 165) o day nua. Day la ly
    // do, do duoc chu khong suy:
    //
    // Tren anh bang that o 2560x1440, quet 120 hang dau cua ROI voi dung bo nguong nay: pixel xanh
    // sang V>=165 xuat hien DUNG O HANG y=0 va khong hang nao khac — do la vien tren cua khoi bang.
    // No trai tu x=700 den x=1699; o x=800-899 co 27 pixel, o x=1100-1199 co 36 pixel. Dai quet cua
    // TrackTip rong 43px nen chua ~11 pixel vien, vuot xa nguong 2 pixel/hang.
    //
    // Ngay 22/08 no giet mot luot: doan 5 di LEN o x~855 huong toi goc y=88; khi dau day leo den
    // y<=127 thi dinh dai cham hang 0, `tip` NHAY thang len (869,0), rem thanh -88 va bot ban phim
    // re som 88px — day re som roi dam tuong. Log tu to giac: cung luc do phep do CountDynamic tai
    // (869,0) bao "0 pixel da doi", tuc thu duoc nhan la dau day o day khong he doi — chi song nho
    // ngoai le sang.
    //
    // Ngoai le do lay tu core_v13.py:1144, va do la FILE CHET. Bo theo doi dang chay that
    // (DotHeadTracker._dynamic_green trong v10_engine, lop ma TurboDotHeadTracker cua v71_controller
    // ke thua) doi `diff_ref >= HEAD_REF_DIFF_MIN` (=10, trung dung DiffThreshold duoi day) va KHONG
    // co ngoai le nao. Dau day dang chay thi luon khac khung tham chieu, nen dieu kien mot cau la du.

    public const double StripHalfRef = 16.0;  // CFG.TRACK_STRIP_HALF_PX_1080P
    public const double MaxJumpRef = 95.0;
    private const double BackTolRef = 10.0;

    /// <summary>
    /// Nền của cửa chặn bước nhảy mỗi khung, mốc 1080p. Python:
    /// <c>HEAD_MAX_JUMP_BASE_PX_1080P = 10</c> trong <c>v10_engine</c>.
    /// </summary>
    public const double MaxJumpBaseRef = 10.0;

    /// <summary>Hệ số nới cho quãng đường đầu dây CÓ THỂ đã đi. Python: <c>* 1.35</c>.</summary>
    public const double MaxJumpSpeedSlack = 1.35;

    /// <summary>
    /// Trần thời gian được phép cộng dồn vào cửa chặn khi mất dấu. Python:
    /// <c>HEAD_MISS_PREDICT_MS = 150</c> — không có nó thì mù càng lâu cửa càng mở rộng vô hạn.
    /// </summary>
    public const double MaxJumpBlindCapMs = 150.0;

    /// <summary>
    /// Dải quét NGANG rộng hơn lúc đang chờ xác nhận cú rẽ.
    ///
    /// Lúc chạy thẳng thì đầu dây nằm đúng trên làn, dải ±16px là đủ. Nhưng lúc rẽ, đầu dây rời
    /// điểm bắn phím theo trục CŨ vài chục pixel trước khi cú rẽ có hiệu lực vật lý — dải ±16px
    /// quanh điểm bắn sẽ mất dấu nó đúng vào lúc cần bằng chứng nhất.
    /// </summary>
    private const double TurnStripHalfRef = 40.0;

    private const int RedHueLowMax = 15;      // CFG.FAIL_RED_H_LOW_MAX
    private const int RedHueHighMin = 170;
    private const int RedSatMin = 115;
    private const int RedValMin = 70;

    /// <summary>
    /// Toạ độ (trong ROI) của pixel SỐNG xa nhất theo hướng <paramref name="key"/>, tính từ
    /// <paramref name="anchor"/>. Null nghĩa là không thấy gì phía trước.
    ///
    /// Đây là hạt nhân duy nhất của cả bộ theo dõi — dùng cho ba việc: đi thẳng theo làn, đo dịch
    /// chuyển trên trục MỚI sau khi bắn phím rẽ, và đo dịch chuyển còn lại trên trục CŨ. Ba việc
    /// đó chỉ khác nhau ở anchor/hướng/độ rộng dải.
    ///
    /// Chỉ nhận điểm ĐI TRƯỚC anchor theo hướng đang đo: một pixel xanh phía sau là vệt dây đã vẽ.
    /// </summary>
    public static Point? FurthestAlong(byte[] patch, Rectangle patchRect,
                                       byte[] reference, int refW, int refH,
                                       Point anchor, string key, int half, int reach, int back)
    {
        int xa, xb, ya, yb;
        switch (key)
        {
            case BoardKeys.Left:
                xa = anchor.X - reach; xb = anchor.X + back + 1;
                ya = anchor.Y - half; yb = anchor.Y + half + 1;
                break;
            case BoardKeys.Right:
                xa = anchor.X - back; xb = anchor.X + reach + 1;
                ya = anchor.Y - half; yb = anchor.Y + half + 1;
                break;
            case BoardKeys.Up:
                xa = anchor.X - half; xb = anchor.X + half + 1;
                ya = anchor.Y - reach; yb = anchor.Y + back + 1;
                break;
            default:
                xa = anchor.X - half; xb = anchor.X + half + 1;
                ya = anchor.Y - back; yb = anchor.Y + reach + 1;
                break;
        }

        // Giao voi CA miếng đang có trong tay LAN ROI: ra ngoai mieng thi khong co pixel de doc.
        int px0 = patchRect.Left, py0 = patchRect.Top;
        xa = Math.Max(xa, Math.Max(0, px0));
        xb = Math.Min(xb, Math.Min(refW, patchRect.Right));
        ya = Math.Max(ya, Math.Max(0, py0));
        yb = Math.Min(yb, Math.Min(refH, patchRect.Bottom));
        if (xb <= xa || yb <= ya) return null;

        int pw = patchRect.Width;
        int cw = xb - xa, ch = yb - ya;
        var live = new bool[cw * ch];

        for (int y = ya; y < yb; y++)
        {
            int pRow = (y - py0) * pw;
            int rRow = y * refW;
            for (int x = xa; x < xb; x++)
            {
                int pi = (pRow + (x - px0)) * 3;
                int b = patch[pi], g = patch[pi + 1], r = patch[pi + 2];
                var (hh, ss, vv) = ImageOps.HsvOf(b, g, r);

                if (hh < GreenHueMin || hh > GreenHueMax || ss < GreenSatMin || vv < GreenValMin)
                    continue;

                int ri = (rRow + x) * 3;
                int diff = Math.Max(Math.Abs(b - reference[ri]),
                           Math.Max(Math.Abs(g - reference[ri + 1]), Math.Abs(r - reference[ri + 2])));
                if (diff <= DiffThreshold) continue;

                live[(y - ya) * cw + (x - xa)] = true;
            }
        }

        if (BoardKeys.IsHorizontal(key))
        {
            int pick = -1;
            for (int lx = 0; lx < cw; lx++)
            {
                int n = 0;
                for (int ly = 0; ly < ch; ly++) if (live[ly * cw + lx]) n++;
                if (n < AxisMinPixels) continue;

                int gx = lx + xa;
                if (key == BoardKeys.Left)
                {
                    if (gx < anchor.X && (pick < 0 || gx < pick)) pick = gx;
                }
                else
                {
                    if (gx > anchor.X && (pick < 0 || gx > pick)) pick = gx;
                }
            }
            if (pick < 0) return null;

            // Toa do vuong goc: trung vi cua cac hang co pixel song quanh cot vua chon.
            var rows = new List<int>();
            for (int lx = Math.Max(0, pick - xa - 2); lx <= Math.Min(cw - 1, pick - xa + 2); lx++)
            for (int ly = 0; ly < ch; ly++)
                if (live[ly * cw + lx]) rows.Add(ly + ya);

            return new Point(pick, rows.Count > 0 ? Median(rows) : anchor.Y);
        }

        int pickY = -1;
        for (int ly = 0; ly < ch; ly++)
        {
            int n = 0;
            for (int lx = 0; lx < cw; lx++) if (live[ly * cw + lx]) n++;
            if (n < AxisMinPixels) continue;

            int gy = ly + ya;
            if (key == BoardKeys.Up)
            {
                if (gy < anchor.Y && (pickY < 0 || gy < pickY)) pickY = gy;
            }
            else
            {
                if (gy > anchor.Y && (pickY < 0 || gy > pickY)) pickY = gy;
            }
        }
        if (pickY < 0) return null;

        var cols = new List<int>();
        for (int ly = Math.Max(0, pickY - ya - 2); ly <= Math.Min(ch - 1, pickY - ya + 2); ly++)
        for (int lx = 0; lx < cw; lx++)
            if (live[ly * cw + lx]) cols.Add(lx + xa);

        return new Point(cols.Count > 0 ? Median(cols) : anchor.X, pickY);
    }

    /// <summary>
    /// Xa nhất mà đầu dây CÓ THỂ đã đi kể từ phép đo được nhận gần nhất — nền
    /// <see cref="MaxJumpBaseRef"/> cộng quãng đường theo tốc độ đã đo.
    ///
    /// Python: <c>v10_engine</c> dòng 341, <c>max_jump = sc(HEAD_MAX_JUMP_BASE_PX_1080P, scale) +
    /// speed * dt * 1.35</c>. Bản C# đầu tiên bỏ mất cửa chặn này, và đó là lý do một phép đo lệch
    /// có thể dịch đầu dây đi 88px trong một khung.
    /// </summary>
    public static int MaxJump(double scale, double speedPxPerMs, double dtMs)
    {
        double dt = Math.Clamp(dtMs, 0.0, MaxJumpBlindCapMs);
        return Math.Max(4, (int)Math.Round(
            MaxJumpBaseRef * scale + Math.Max(0.0, speedPxPerMs) * dt * MaxJumpSpeedSlack));
    }

    /// <summary>
    /// Đầu dây đã tiến tới đâu trên làn đang đi. Null nghĩa là không thấy nó nhích.
    ///
    /// Tầm quét CHÍNH LÀ cửa chặn <see cref="MaxJump"/>, không phải một con số phẳng. Làm thế thì
    /// cửa chặn vừa là bộ lọc vừa là giới hạn quét — nếu quét rộng rồi mới loại điểm quá xa thì ta
    /// mất luôn điểm gần hợp lệ trong cùng khung. Đây cũng là cách rẻ nhất để có được tính chất
    /// "chỉ tìm quanh vị trí dự đoán" của bản Python mà không phải dựng cả bộ dự đoán.
    /// </summary>
    public static Point? TrackTip(byte[] patch, Rectangle patchRect, byte[] reference,
                                  int refW, int refH, Point tip, string key, double scale,
                                  double speedPxPerMs, double dtMs)
        => FurthestAlong(patch, patchRect, reference, refW, refH, tip, key,
                         Math.Max(8, (int)Math.Round(StripHalfRef * scale)),
                         MaxJump(scale, speedPxPerMs, dtMs),
                         Math.Max(5, (int)Math.Round(BackTolRef * scale)));

    /// <summary>
    /// Đầu dây đã đi bao xa theo <paramref name="key"/> tính từ <paramref name="cmdPos"/> — điểm
    /// lúc bắn phím rẽ. Dùng dải rộng <see cref="TurnStripHalfRef"/>, xem giải thích ở đó.
    ///
    /// Ở đây tầm quét CỐ TÌNH giữ nguyên <see cref="MaxJumpRef"/> chứ không áp cửa chặn per-khung:
    /// hàm này không theo dõi đầu dây tiến từng khung, nó neo ở <paramref name="cmdPos"/> và đo
    /// DỊCH CHUYỂN tích luỹ trong tối đa <c>TurnMaxHoldMs</c> (230ms) — quãng đó tới ~130px. Áp cửa
    /// chặn 16px/khung vào đây là làm mù chính phép đo dùng để xác nhận cú rẽ.
    /// </summary>
    public static Point? TrackTurn(byte[] patch, Rectangle patchRect, byte[] reference,
                                   int refW, int refH, Point cmdPos, string key, double scale)
        => FurthestAlong(patch, patchRect, reference, refW, refH, cmdPos, key,
                         Math.Max(20, (int)Math.Round(TurnStripHalfRef * scale)),
                         Math.Max(30, (int)Math.Round(MaxJumpRef * scale)),
                         0);

    // KHONG co "RedNearTip" o day nua, va day la ly do — de khong ai viet lai:
    //
    // No dem pixel do da doi trong ban kinh 105px×ti le (= 140px o 2K) quanh dau day va goi do la
    // va tuong. Nhung dau noi DICH la mot khoi DO co dinh, va tuyen bao gio cung ket thuc o do —
    // nen o quang cuoi moi tuyen, dau noi dich luon nam trong hop kiem. Ngay 22/08 no giet mot luot
    // dang thang: dau day o (265,145), doan 17/19, con 27px la tram; phep kiem 140px bao co du
    // pixel do, con phep do chan doan 80px (CountDynamic) bao DO = 0. Than dau noi GOAL @(238,51)
    // nam tron trong hop 140px va tron NGOAI hop 80px — khop chinh xac. Bang truoc do thoat duoc
    // chi vi tuyen cua no it hon mot doan, nen luc di ngang qua dich thi da o doan CUOI, cho da co
    // co che tha do.
    //
    // Hai hang so do (FAIL_RED_MIN_PIXELS / FAIL_CHECK_RADIUS) cung la CFG cua nhanh Python DA
    // CHET: chi ton tai trong core_v10/core_v13, chi duoc doc boi dynamic_red_near_tip, va ham do
    // chi duoc goi tu hai cho TRONG CUNG FILE CHET. Chuoi dang chay that (v75 -> v71_controller)
    // KHONG kiem mau do o dau ca.
    //
    // Thay the: day va tuong thi no NGUNG CHAY, va nguong TrackerBlindMs 200ms trong BoardBot bat
    // duoc. Do la bang chung vat ly truc tiep, manh hon suy tu mau. CountDynamic o duoi van giu, va
    // doi vai thanh dung cu CHAN DOAN — moi lan bo do no in ra quanh dau day co bao nhieu pixel
    // do/xanh da doi, du de phan biet "va tuong" voi "mat dau".

    private static int Median(List<int> vals)
    {
        vals.Sort();
        return vals[vals.Count / 2];
    }

    /// <summary>
    /// Đếm pixel ĐÃ ĐỔI so với khung tham chiếu quanh đầu dây, tách theo màu: xanh sáng, đỏ, và
    /// tổng số đổi.
    ///
    /// Đây là dụng cụ chẩn đoán, không tham gia điều khiển. Lý do nó tồn tại: bản Python giả định
    /// đầu dây màu XANH và coi ĐỎ là tín hiệu va tường, nhưng hai ảnh chụp thật ban đầu lại cho vệt
    /// dây màu ĐỎ — cả hai đều là bảng ĐÃ THUA nên không phân biệt được. Con số đo tại chỗ bỏ dở
    /// của lượt chạy ngày 22/08 đã chốt được: 823 xanh / 0 đỏ, tức dây XANH và bộ theo dõi đúng
    /// màu. Giữ lại vì nó vẫn là cách rẻ nhất để loại bỏ "nghi vấn màu" trong mọi lần bỏ dở sau.
    /// </summary>
    public static (int Green, int Red, int Changed) CountDynamic(
        byte[] patch, Rectangle patchRect, byte[] reference, int refW, int refH,
        Point tip, double scale)
    {
        int r = Math.Max(45, (int)Math.Round(60.0 * scale));
        int xa = Math.Max(tip.X - r, Math.Max(0, patchRect.Left));
        int xb = Math.Min(tip.X + r + 1, Math.Min(refW, patchRect.Right));
        int ya = Math.Max(tip.Y - r, Math.Max(0, patchRect.Top));
        int yb = Math.Min(tip.Y + r + 1, Math.Min(refH, patchRect.Bottom));
        if (xb <= xa || yb <= ya) return (0, 0, 0);

        int pw = patchRect.Width, px0 = patchRect.Left, py0 = patchRect.Top;
        int green = 0, red = 0, changed = 0;

        for (int y = ya; y < yb; y++)
        {
            int pRow = (y - py0) * pw;
            int rRow = y * refW;
            for (int x = xa; x < xb; x++)
            {
                int pi = (pRow + (x - px0)) * 3;
                int b = patch[pi], g = patch[pi + 1], rr = patch[pi + 2];

                int ri = (rRow + x) * 3;
                int diff = Math.Max(Math.Abs(b - reference[ri]),
                           Math.Max(Math.Abs(g - reference[ri + 1]), Math.Abs(rr - reference[ri + 2])));
                if (diff <= DiffThreshold) continue;
                changed++;

                var (hh, ss, vv) = ImageOps.HsvOf(b, g, rr);
                if (hh >= GreenHueMin && hh <= GreenHueMax && ss >= GreenSatMin && vv >= GreenValMin) green++;
                else if ((hh <= RedHueLowMax || hh >= RedHueHighMin) && ss >= RedSatMin && vv >= RedValMin) red++;
            }
        }
        return (green, red, changed);
    }
}

internal enum BoardStopReason
{
    UserStopped,

    /// <summary>Đi hết tuyến và cắm được vào đầu nối GOAL.</summary>
    Solved,

    /// <summary>Không thấy bảng nào: đứng sai chỗ, hoặc chưa mở minigame.</summary>
    NoBoard,

    /// <summary>
    /// Đầu dây không nhích nữa. Giữa tuyến thì gần như luôn nghĩa là ĐÃ VA TƯỜNG — dây va tường là
    /// nó ngừng chạy. Không có <c>Collision</c> riêng: xem ghi chú chỗ đã bỏ <c>RedNearTip</c>.
    /// </summary>
    NoProgress,

    /// <summary>Dây không tự phóng ra khỏi đầu nối START.</summary>
    WireDidNotStart,

    /// <summary>Dựng tuyến xong thì dây đã đi quá ngã rẽ đầu tiên — không cứu được.</summary>
    LateStart,

    /// <summary>Đã gõ phím rẽ nhưng dây KHÔNG rẽ. Tuyến đúng, cú bấm không vào.</summary>
    TurnNotConfirmed,

    InputFailed,
    Error
}

/// <summary>
/// Sau khi cắm đích, khóa dựng tuyến: bảng thắng còn hiện vài trăm ms và sẽ bị nhận
/// lại như bảng mới nếu vòng ngoài chạy tiếp. Chỉ trả <see cref="BoardStopReason.Solved"/>
/// khi tiêu đề đã vắng liên tục đủ lâu.
/// </summary>
internal sealed class BoardAfterSolvePolicy
{
    public const int BoardGoneMs = 3_000;

    public bool AllowPlan { get; private set; } = true;

    private long? _goneSinceMs;

    public void OnRouteSuccess()
    {
        AllowPlan = false;
        _goneSinceMs = null;
    }

    public BoardStopReason? Tick(bool boardOpen, long nowMs)
    {
        if (AllowPlan) return null;
        if (boardOpen)
        {
            _goneSinceMs = null;
            return null;
        }

        _goneSinceMs ??= nowMs;
        return nowMs - _goneSinceMs.Value >= BoardGoneMs
            ? BoardStopReason.Solved
            : null;
    }
}

/// <summary>
/// Job Thợ điện, phần minigame BẢNG WATER &amp; POWER: dẫn một đầu dây tự chạy đi từ đầu nối xanh
/// tới đầu nối đỏ mà không đụng thân bảng nào.
///
/// Ba pha, và thứ tự này là bất di bất dịch:
///   1. CHỜ ỔN ĐỊNH — các chữ ký 128×72 từ những frame khác nhau phải gần như y hệt
///      (<see cref="BoardWallSignature"/>), rồi mới quét tường đầy đủ đúng một lần.
///   2. DỰNG TUYẾN — A* + chứng chỉ an toàn + tinh chỉnh ngã rẽ. Không dựng nổi thì GIỮ, không
///      bấm phím nào.
///   3. CHẠY — tuyến đã ĐÓNG BĂNG, lúc chạy không bao giờ tính lại.
///
/// PHÍM CHỈ ĐỂ RẼ, KHÔNG PHẢI ĐỂ ĐI. Đây là điều duy nhất cần hiểu về pha 3, và là chỗ bản trước
/// làm sai. Sợi dây tự chạy ngay khi bảng mở; nhả phím không làm nó dừng. Nên tới góc thì việc phải
/// làm là RẼ, không phải "hạ đúng pixel góc rồi đứng lại". Bản trước giữ phím để đi rồi gõ nhịp
/// ngắn canh đúng góc, vượt quá thì bỏ đoạn — và nó chết ở đoạn thứ hai của tuyến thật (dài 62px,
/// vượt 14px > ngưỡng 12px), vì với dây tự chạy thì mọi đoạn ngắn đều vượt góc và cái vượt đó là
/// BÌNH THƯỜNG. Những hằng số nó dùng (TARGET_TOLERANCE / ACCEPT_OVERSHOOT / FINE_ZONE) chỉ còn
/// nằm trong <c>core_v13.py</c> như CFG chết; chuỗi Python đang chạy thật
/// (<c>v75</c> → <c>v71_controller</c>) không đọc cái nào.
///
/// Nên pha 3 là một máy trạng thái CHECKPOINT RẼ, sao đúng <c>v71_controller</c>:
///   - giữa hai góc: KHÔNG giữ phím nào, chỉ nhìn;
///   - tới góc: bắn phím của đoạn kế tiếp, giữ kiểu sticky (bơm lại KeyDown mỗi 14ms mà không nhả);
///   - rồi XÁC NHẬN bằng chuyển động thật là dây đã rẽ, mới nhả phím và sang góc sau;
///   - không bao giờ có phím "sửa hướng" giữa hai góc. Sai thì dừng, không chữa.
/// </summary>
internal sealed class BoardBot
{
    // Nhung hang so nay khong nam trong BoardSettings vi nguoi dung khong co ly do gi de sua
    // chung — chung mo ta hanh vi cua game, khong phai khau vi. Tat ca lay tu v71_controller.
    //
    // Chu y: vong chay KHONG co lenh nghi nao. Mot lan GrabPatch da ton ~3.3ms (do duoc), tuc vong
    // da chay o ~300 luot/giay — dung nhip ma ban Python nham toi, va no chi yield khi vong nhanh
    // hon 3ms. Them SleepTight(1) vao day la tu bo 25% so lan nhin day de doi lay khong gi ca.

    /// <summary>Bắn phím rẽ khi còn cách góc dưới ngần này. Python: <c>TURN_TRIGGER_EPS_REF</c>.</summary>
    private const double TriggerEpsRef = 2.2;

    /// <summary>Vượt góc tới ngần này vẫn còn bắn được. Python: <c>TURN_LATE_FIRE_REF</c>.</summary>
    private const double LateFireRef = 5.0;
    private const double PredictiveLeadMaxRef = 12.0;
    private const double InitialInputLatencyMs = 4.0;

    private const int InputRepeatDownMs = 14;   // CFG.INPUT_REPEAT_DOWN_MS
    private const int InputMaxRepeats = 14;
    private const int TurnMaxHoldMs = 230;

    private const double TurnVectorConfirmRef = 5.0;
    private const double TurnVectorStrongRef = 8.0;
    private const double VectorDominance = 1.10;
    private const double StrongOldRatio = 0.90;

    private const double TemporalWindowMs = 14.0;
    private const double TemporalNewRef = 2.8;
    private const double TemporalCumRef = 4.5;
    private const double TemporalDominance = 1.15;

    private const double FinalGoalRadiusRef = 26.0;
    private const int FinalHoldMs = 70;

    private const int AutoStartTimeoutMs = 1_600;
    private const double LaunchProgressRef = 3.0;

    /// <summary>
    /// Sàn tốc độ đầu dây (px/giây, mốc 1080p) dùng để suy ngân sách thời gian của cả tuyến.
    ///
    /// Đo được trên bảng thật ở 2K: ~355px/s, tức ~266px/s ở mốc 1080p. Lấy 110 là để rộng gấp
    /// 2.4× — con số này KHÔNG điều khiển gì, nó chỉ là lưới chặn vòng lặp vô hạn. Một trần phẳng
    /// (18s) thì bản đồ nào có tuyến dài thật là bị giết oan: tuyến 4343px đã cần ~12 giây.
    /// </summary>
    private const double MinWireSpeedRef = 110.0;

    /// <summary>Bao lâu không thấy đầu dây nhích thì coi là mất dấu. Python: <c>TRACKER_BLIND_TRIGGER_MS</c>.</summary>
    private const int TrackerBlindMs = 200;

    private readonly ElectricConfig _cfg;
    private readonly Screen _screen;
    private readonly ElectricProfile _profile;
    private readonly BoardRouteCache _routeCache;
    private readonly IScreenCaptureSession _capture;

    private CancellationTokenSource _cts;
    private Thread _thread;
    private bool _windowWarned;
    private int _rounds;
    private string _heldKey;

    /// <summary>
    /// Đệm cho lượt chụp CẢ ROI. Ở 2560×1440 mỗi lượt là 5.7 MB, và một lượt chụp cả ROI tốn
    /// 16 ms — nên vòng chạy KHÔNG dùng nó; nó chỉ phục vụ bước dò đầu dây lúc bắt đầu (một lần
    /// mỗi bảng) và lượt chẩn đoán lúc bỏ dở. Vòng chạy dùng cửa sổ nhỏ 3.3 ms của
    /// <see cref="BoardReader.GrabPatch"/>.
    ///
    /// KHÔNG bao giờ dùng đệm này làm khung tham chiếu.
    /// </summary>
    private byte[] _roiBuf;

    public BoardBot(ElectricConfig cfg, Screen screen, ElectricProfile profile,
                    BoardRouteCache routeCache = null, IScreenCaptureSession capture = null)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _routeCache = routeCache ?? BoardRouteCache.CreateEmpty();
        _capture = capture;
    }

    public bool Running => _thread is { IsAlive: true };

    /// <summary>Số bảng đã giải xong trong phiên này.</summary>
    public int Rounds => _rounds;

    public event Action<string> Log;
    public event Action<int> RoundsChanged;
    public event Action<BoardStopReason, string> Stopped;

    public void Start()
    {
        if (Running) return;
        _rounds = 0;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "BoardBot" };
        _thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    public void StopAndWait(int ms = 3000)
    {
        _cts?.Cancel();
        var t = _thread;
        if (t is null || !t.IsAlive) return;
        try { t.Join(ms); } catch { }
    }

    public static string TenLyDo(BoardStopReason r) => r switch
    {
        BoardStopReason.UserStopped => "người dùng bấm dừng",
        BoardStopReason.Solved => "đã cắm tới đầu nối đích",
        BoardStopReason.NoBoard => "không thấy bảng nước/điện",
        BoardStopReason.NoProgress => "đầu dây đứng im (thường là va tường)",
        BoardStopReason.WireDidNotStart => "dây không tự phóng khỏi đầu nối",
        BoardStopReason.LateStart => "dựng tuyến xong thì dây đã vượt ngã rẽ đầu tiên",
        BoardStopReason.TurnNotConfirmed => "gõ phím rẽ mà dây không rẽ",
        BoardStopReason.InputFailed => "không gửi được phím vào game",
        _ => "lỗi"
    };

    // ---------------------------------------------------------------- vong ngoai

    private void Run(CancellationToken ct)
    {
        var reason = BoardStopReason.UserStopped;
        string message = "người dùng bấm dừng";
        BoardReader reader = null;

        try
        {
            reader = BoardReader.Open(_cfg, _screen, _profile, _capture);
            if (!reader.Configured)
            {
                reason = BoardStopReason.Error;
                message = reader.Problem ?? "không mở được ROI bảng";
                Emit("không chạy được: " + message);
                return;
            }

            Emit($"ROI bảng {reader.RoiRegion.Width}×{reader.RoiRegion.Height} " +
                 $"@{reader.RoiRegion.X},{reader.RoiRegion.Y}  (tỉ lệ {_profile.Scale:F3}, " +
                 $"capture {reader.CaptureBackend})");
            Emit($"cửa sổ game phải đang focus ({_cfg.WindowMatch}).");

            var signatureHistory = new List<BoardWallSignature>();
            var sinceSeen = Stopwatch.StartNew();
            var lastHold = Stopwatch.StartNew();
            Stopwatch boardLatency = null;
            bool everSeen = false;
            var afterSolve = new BoardAfterSolvePolicy();
            var waitClock = Stopwatch.StartNew();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (WaitWindow(ct)) sinceSeen.Restart();

                if (!afterSolve.AllowPlan)
                {
                    bool stillOpen = reader.BoardOpen(conservativeOnCaptureFailure: true);
                    var done = afterSolve.Tick(stillOpen, waitClock.ElapsedMilliseconds);
                    if (done == BoardStopReason.Solved)
                    {
                        reason = BoardStopReason.Solved;
                        message = $"bảng đã đóng — giải {_rounds} bảng";
                        Emit("bảng đã đóng — tiếp tục chạy liên tục");
                        return;
                    }
                    Sleep(ct, _cfg.Board.WatchPollMs);
                    continue;
                }

                var signature = reader.TryQuickSignature(out string why);
                if (signature is null)
                {
                    signatureHistory.Clear();
                    boardLatency = null;
                    if (sinceSeen.ElapsedMilliseconds >= (everSeen ? BoardAfterSolvePolicy.BoardGoneMs : _cfg.Board.NoBoardMs))
                    {
                        reason = everSeen ? BoardStopReason.Solved : BoardStopReason.NoBoard;
                        message = everSeen
                            ? $"bảng đã đóng — giải {_rounds} bảng"
                            : $"{_cfg.Board.NoBoardMs / 1000}s không thấy bảng ({why})";
                        Emit(message);
                        return;
                    }
                    Sleep(ct, _cfg.Board.WatchPollMs);
                    continue;
                }

                everSeen = true;
                sinceSeen.Restart();
                boardLatency ??= Stopwatch.StartNew();

                signatureHistory.Add(signature);
                if (signatureHistory.Count > _cfg.Board.WallStableFrames)
                    signatureHistory.RemoveAt(0);
                if (!BoardWallSignature.Stable(
                        signatureHistory, _cfg.Board.WallStableFrames, out string stableWhy))
                {
                    Throttle(lastHold, $"giữ, chưa bấm gì: {stableWhy}");
                    continue;
                }

                var frame = reader.TryReadLast(out why);
                if (frame is null)
                {
                    signatureHistory.Clear();
                    boardLatency = null;
                    Throttle(lastHold, $"không đọc được frame ổn định: {why}");
                    continue;
                }

                var role = BoardReader.DetectRole(frame, out string roleWhy);
                if (role is null)
                {
                    signatureHistory.Clear();
                    boardLatency = null;
                    Throttle(lastHold, $"chưa chốt được START/GOAL: {roleWhy}");
                    Sleep(ct, _cfg.Board.WatchPollMs);
                    continue;
                }

                var scanWatch = Stopwatch.StartNew();
                var scan = BoardPlanner.ScanWalls(frame);
                scanWatch.Stop();

                Emit($"tường ổn định sau {boardLatency.ElapsedMilliseconds}ms — {stableWhy}; " +
                     $"{role.Describe()}");
                Emit($"tường: {scan.LargeWalls} khối lớn, {scan.MicroWalls} khối nhỏ, " +
                     $"{scan.SecondaryWalls} khối lớp bảo thứ hai, ngưỡng V={scan.ValueThreshold}, " +
                     $"quét {scanWatch.ElapsedMilliseconds}ms");

                string cacheKey = BoardRouteCache.MakeKey(frame, role, scan.Wall);
                BoardPlan plan = null;
                string planWhy = null;

                if (_routeCache.TryGet(cacheKey, role, frame.Width, frame.Height, out var cached))
                {
                    plan = BoardPlanner.ValidateCached(frame, role, scan, cached, out string cacheWhy);
                    if (plan is not null) Emit($"cache tuyến: HIT, chứng nhận lại {plan.BuildMs:F0}ms");
                    else Emit("cache tuyến: loại — " + cacheWhy);
                }

                if (plan is null)
                {
                    plan = BoardPlanner.Plan(frame, role, scan, out planWhy);
                    if (plan is not null) _routeCache.Put(cacheKey, plan);
                }
                if (plan is null)
                {
                    signatureHistory.Clear();
                    boardLatency = null;
                    Throttle(lastHold, $"giữ, chưa bấm gì: {planWhy}");
                    Sleep(ct, _cfg.Board.WatchPollMs);
                    continue;
                }

                Emit(plan.Describe());
                foreach (string n in plan.RefineNotes) Emit("  ngã rẽ " + n);
                foreach (var s in plan.Segments) Emit("  đoạn " + s);

                var (ok, fail, note) = RunRoute(reader, frame, plan, ct);
                if (ok)
                {
                    _rounds++;
                    RoundsChanged?.Invoke(_rounds);
                    Emit($"xong bảng #{_rounds} — {note}");
                    Emit("đã giải — chờ bảng đóng");
                    afterSolve.OnRouteSuccess();
                    waitClock.Restart();
                    signatureHistory.Clear();
                    boardLatency = null;
                    continue;
                }

                reason = fail?.Reason ?? BoardStopReason.Error;
                message = fail?.Message ?? note;
                Emit("dừng: " + message);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            reason = BoardStopReason.UserStopped;
            message = "người dùng bấm dừng";
        }
        catch (InvalidOperationException ex)
        {
            reason = BoardStopReason.InputFailed;
            message = ex.Message;
            Emit(message);
        }
        catch (Exception ex)
        {
            reason = BoardStopReason.Error;
            message = ex.Message;
            Emit("lỗi: " + message);
        }
        finally
        {
            ReleaseHeld();
            reader?.Dispose();
            HeldKeys.ReleaseAll();
            Stopped?.Invoke(reason, message);
        }
    }

    /// <summary>Log lặp lại (kiểu "đang giữ, chưa bấm") tối đa một lần mỗi 700 ms.</summary>
    private void Throttle(Stopwatch sw, string line)
    {
        if (sw.ElapsedMilliseconds < 700) return;
        sw.Restart();
        Emit(line);
    }

    // ---------------------------------------------------------------- chay tuyen

    private readonly record struct Failure(BoardStopReason Reason, string Message);

    /// <summary>
    /// Chạy một tuyến đã đóng băng bằng máy trạng thái checkpoint rẽ. Xem phần đầu
    /// <see cref="BoardBot"/> để biết vì sao KHÔNG có pha "đi tới đích của đoạn".
    /// </summary>
    private (bool Ok, Failure? Fail, string Note) RunRoute(
        BoardReader reader, BoardFrame frame, BoardPlan plan, CancellationToken ct)
    {
        // Khung THAM CHIEU: chinh khung da dung tuyen. Moi phep so "da doi chua" cua bo theo doi
        // dua vao no, nen no phai la khung TRUOC khi bam phim dau tien.
        var reference = frame.Bgr;
        int w = frame.Width, h = frame.Height;
        double scale = frame.Scale;
        var segs = plan.Segments;

        // Cua so chup phai bao duoc tam nhin toi cua bo theo doi (MaxJumpRef 95px) cong le quet
        // ngang, khong thi phep do cham bia mieng anh. O 2K ra 180 => cua so 361×361, van nam trong
        // vung phang 3.3ms cua bang do (do duoc toi 384×384). Le 24 la de du cho pha cho xac nhan
        // re: quan sat thuc te 16-39ms, tuc 8-23px, xa duoi tran.
        int patchHalf = (int)Math.Ceiling(
            (BoardTracker.MaxJumpRef + BoardTracker.StripHalfRef + 24) * scale);
        if (!reader.OpenPatch(patchHalf, out string patchWhy))
            return (false, new Failure(BoardStopReason.Error, patchWhy), patchWhy);

        int need = w * h * 3;
        if (_roiBuf is null || _roiBuf.Length != need) _roiBuf = new byte[need];

        int eps = Math.Max(1, (int)Math.Round(TriggerEpsRef * scale));
        int lateTol = Math.Max(2, (int)Math.Round(LateFireRef * scale));

        int routeTimeoutMs = (int)Math.Max(
            6_000, plan.TotalLength / (MinWireSpeedRef * scale) * 1000.0 + 3_000.0);

        // ---------------- pha BAT DAU: day dang o dau roi? ----------------
        var (tip, acqFail) = Acquire(reader, reference, w, h, scale, plan, lateTol, ct);
        if (acqFail is { } af) return (false, af, af.Message);

        Emit($"bắt đầu chạy: đầu dây ở ({tip.X},{tip.Y}), cửa sổ chụp {reader.PatchSide}×{reader.PatchSide}");

        // ---------------- vong chinh ----------------
        int idx = 0;
        string currentKey = segs[0].Key;
        var stats = new LoopStats();

        // Trang thai cua CUA CHAN buoc nhay (xem BoardTracker.MaxJump): toc do dau day do duoc, va
        // da bao lau chua nhan duoc phep do nao. Khoi tao toc do bang so danh nghia cua ban Python
        // (420 px/giay o moc 1080p) de cua chan khong bo qua chat trong nhung khung dau, truoc khi
        // co du lieu that. Do duoc trong game: 2.0px moi khung 3.35ms = 0.60 px/ms o 2K, tuc 0.45
        // px/ms o moc 1080p — con so danh nghia kia gan nhu dung y.
        double speed = 0.42 * scale;              // px/ms
        double inputLatencyMs = InitialInputLatencyMs;
        var motion = new BoardMotionEstimator();
        motion.Reset(0, reader.PatchTimestamp != 0
            ? reader.PatchTimestamp
            : Stopwatch.GetTimestamp(), speed);
        var sinceAccepted = Stopwatch.StartNew();

        void Accept(Point from, Point to, string key)
        {
            int adv = Along(from, to, key);
            double dt = sinceAccepted.Elapsed.TotalMilliseconds;
            if (adv > 0 && dt > 0.5) speed = 0.7 * speed + 0.3 * (adv / dt);
            if (adv > 0)
            {
                long frameTs = reader.PatchTimestamp != 0
                    ? reader.PatchTimestamp
                    : Stopwatch.GetTimestamp();
                motion.Update(motion.Position + adv, frameTs);
                if (motion.SpeedPxPerMs > 0)
                    speed = 0.7 * speed + 0.3 * motion.SpeedPxPerMs;
            }
            stats.Advance(adv);
            sinceAccepted.Restart();
        }
        var route = Stopwatch.StartNew();
        var blind = Stopwatch.StartNew();
        var focus = Stopwatch.StartNew();

        bool pending = false;
        var cmd = new Stopwatch();
        Point cmdPos = tip;
        double nextRepeatMs = 0;
        int repeats = 0, vectorHits = 0;
        double bestNew = 0, baseNew = 0, baseOld = 0;
        var samples = new List<(double Ms, double New, double Old)>();
        Stopwatch goalSince = null;
        int? prevRem = null;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (route.ElapsedMilliseconds > routeTimeoutMs)
                    return Fail(reader, reference, w, h, scale, tip, BoardStopReason.NoProgress,
                                $"quá {routeTimeoutMs / 1000}s chưa xong tuyến dài " +
                                $"{plan.TotalLength:F0}px, đang ở góc #{idx}", stats);

                // Focus la BAT BIEN luc chay, khong phai kiem tra mot lan luc bat dau: InputSender
                // ban vao cua so dang focus, nen mat focus giua tuyen la phim re bay sang cua so
                // khac. Bo di khong cuu duoc — day van tu chay ma khong con ai lai — nen dung han
                // va noi ro, chu khong im lang gui tiep phim vao dau do.
                if (focus.ElapsedMilliseconds >= 120)
                {
                    focus.Restart();
                    if (!string.IsNullOrWhiteSpace(_cfg.WindowMatch))
                    {
                        string fg = Native.ForegroundTitle();
                        if (!fg.Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase))
                            return Fail(reader, reference, w, h, scale, tip, BoardStopReason.InputFailed,
                                        $"mất focus game giữa tuyến ở góc #{idx} (đang focus “{fg}”)", stats);
                    }
                }

                var rect = reader.GrabPatch(tip);
                if (rect.IsEmpty)
                {
                    // Chup loi: nghi mot nhip roi thu lai, dung quay ban vao mot loi lap lai.
                    InputSender.SleepTight(2);
                    continue;
                }
                var patch = reader.PatchBuffer;
                stats.Tick();

                // KHONG co phep kiem mau DO o day. Bang chung va tuong la VAT LY: day va tuong thi
                // no ngung chay, va nguong TrackerBlindMs duoi day bat duoc trong 200ms. Xem ghi chu
                // o cuoi ElectricConfig.BoardSettings de biet phep kiem do cu da giet oan mot luot
                // dang thang nhu the nao.

                // ---- doan CUOI: khong con checkpoint, chi cho cam vao dau noi ----
                if (idx >= segs.Length - 1)
                {
                    var nt = BoardTracker.TrackTip(patch, rect, reference, w, h, tip, currentKey, scale,
                                                   speed, sinceAccepted.Elapsed.TotalMilliseconds);
                    if (nt is { } np) { Accept(tip, np, currentKey); tip = np; blind.Restart(); }

                    if (Dist(tip, plan.Role.GoalHit) <= FinalGoalRadiusRef * scale)
                    {
                        goalSince ??= Stopwatch.StartNew();
                        if (goalSince.ElapsedMilliseconds >= FinalHoldMs)
                            return (true, null,
                                    $"cắm vào đích tại ({tip.X},{tip.Y}) — {stats.Describe()}");
                    }
                    else goalSince = null;

                    if (blind.ElapsedMilliseconds > TrackerBlindMs)
                    {
                        // Mat dau o doan cuoi co hai nghia trai nguoc nhau: game da dong bang vi
                        // THANG, hoac bo theo doi hong. Hoi mot cau roi hay ket luan — bao sai o day
                        // la ghi mot luot thang thanh loi va tat ca job.
                        if (!reader.BoardOpen())
                            return (true, null,
                                    $"bảng đóng ngay sau khi dây tới ({tip.X},{tip.Y}), " +
                                    $"còn {Dist(tip, plan.Role.GoalHit):F0}px tới đích — {stats.Describe()}");

                        return Fail(reader, reference, w, h, scale, tip, BoardStopReason.NoProgress,
                                    $"bảng còn mở mà đầu dây đứng im ở đoạn cuối — gần như luôn là " +
                                    $"ĐÃ VA TƯỜNG (xem số đỏ/xanh ngay dưới); còn " +
                                    $"{Dist(tip, plan.Role.GoalHit):F0}px tới đích", stats);
                    }

                    continue;
                }

                var seg = segs[idx];
                var next = segs[idx + 1];
                var corner = new Point((int)Math.Round(seg.End.X), (int)Math.Round(seg.End.Y));

                // ---- chua ban phim: nhin day tien tren lan, cho tin hieu goc ----
                if (!pending)
                {
                    var nt = BoardTracker.TrackTip(patch, rect, reference, w, h, tip, currentKey, scale,
                                                   speed, sinceAccepted.Elapsed.TotalMilliseconds);
                    if (nt is { } np) { Accept(tip, np, currentKey); tip = np; blind.Restart(); }
                    else if (blind.ElapsedMilliseconds > TrackerBlindMs)
                        return Fail(reader, reference, w, h, scale, tip, BoardStopReason.NoProgress,
                                    $"đầu dây đứng im — gần như luôn là ĐÃ VA TƯỜNG (xem số đỏ/xanh " +
                                    $"ngay dưới); {TrackerBlindMs}ms không nhích ở đoạn {currentKey} " +
                                    $"(góc #{idx}), còn {Along(tip, corner, currentKey)}px tới góc", stats);

                    int rem = Along(tip, corner, currentKey);
                    double lead = motion.LeadDistance(
                        Stopwatch.GetTimestamp(),
                        processingMs: 0.5,
                        inputLatencyMs,
                        PredictiveLeadMaxRef * scale);
                    int predictiveEps = eps + (int)Math.Ceiling(lead);

                    // `crossed` la dieu kien quan trong hon `inZone`: no bat duoc ca khi vong chay
                    // cham va day da vuot qua goc giua hai lan nhin. Nho no, nhip vong te chi lam
                    // cu re TRE chu khong lam BO goc.
                    bool crossed = prevRem is { } pr && pr > predictiveEps && rem <= predictiveEps;
                    bool inZone = rem >= -lateTol && rem <= predictiveEps;

                    // LUOI AN TOAN: goc nay da bi VUOT ngay tu lan nhin dau tien (prevRem con null,
                    // tuc ta vua xac nhan xong cu re truoc). Xay ra khi doan qua ngan: bam phim
                    // khong lam day re ngay — do duoc ~17px o 2K — nen luc xac nhan xong cu re
                    // truoc thi day da troi qua goc sau.
                    //
                    // Truoc day khong co nhanh nay, va no la ly do mot luot chet: doan dai 4px, khi
                    // xac nhan xong goc #6 thi day da vuot goc #7 dung 8px, ngoai cua so (−7…+3) co
                    // 1px. Bot dung nhin 200ms roi day dam tuong. Ban ngay thi cu re se MUON va co
                    // the van clip tuong, nhung bo trang mot goc thi chac chan mat bang.
                    //
                    // Duong dung la khong sinh ra doan ngan ngay tu buoc dung tuyen — xem
                    // BoardPlanner.MinSegmentRef. Nhanh nay chi la bao hiem cho ban do khong con
                    // lua chon nao khac.
                    bool alreadyPast = prevRem is null && rem < -lateTol;

                    if (crossed || inZone || alreadyPast)
                    {
                        cmdPos = tip;

                        // MOC TRU, do TRUOC khi gui phim, tren dung khung anh nay.
                        //
                        // Vi sao can: phep do "da di bao xa tren truc MOI" lay toa do XA NHAT co
                        // pixel song, khong phai tam khoi. Soi day day khoang 10px, nen ngay khi
                        // chua re gi, ban thân be day cua no da nho qua cmdPos chung 5px tren truc
                        // moi — sat ngay nguong xac nhan 6.7px. Do moc tai cho roi tru di thi ca
                        // hai con so tro thanh DICH CHUYEN KE TU LUC BAM, dung nghia ma
                        // v71_controller dung, va be day day tu triet tieu du no bao nhieu.
                        baseNew = BoardTracker.TrackTurn(patch, rect, reference, w, h,
                                                        cmdPos, next.Key, scale) is { } bn
                                  ? Along(cmdPos, bn, next.Key) : 0;
                        baseOld = BoardTracker.TrackTurn(patch, rect, reference, w, h,
                                                        cmdPos, currentKey, scale) is { } bo
                                  ? Along(cmdPos, bo, currentKey) : 0;

                        FireTurn(next.Key);
                        pending = true;
                        cmd.Restart();
                        nextRepeatMs = InputRepeatDownMs;
                        repeats = 0;
                        vectorHits = 0;
                        bestNew = 0;
                        samples.Clear();
                        prevRem = null;

                        Emit($"  #{idx} {currentKey}→{next.Key} bắn tại ({tip.X},{tip.Y}) " +
                             $"t={route.Elapsed.TotalMilliseconds:F0}ms rem={rem}px " +
                             $"lead={lead:F1}px " +
                             $"{(alreadyPast ? "[ĐÃ VƯỢT — bắn muộn]" : crossed ? "[qua góc]" : "[trong vùng]")} " +
                             $"mốc trừ {baseNew:F0}/{baseOld:F0}px | {stats.Describe()}" +
                             $", tốc độ {speed * 1000:F0}px/s, chặn nhảy " +
                             $"{BoardTracker.MaxJump(scale, speed, LoopStats.NominalDtMs)}px");
                        continue;
                    }

                    prevRem = rem;
                    continue;
                }

                // ---- da ban phim: giu sticky va cho bang chung vat ly la day da re ----
                double ms = cmd.Elapsed.TotalMilliseconds;
                if (repeats < InputMaxRepeats && ms >= nextRepeatMs)
                {
                    // Bom lai KEYDOWN ma KHONG nha: bo xu ly theo su kien nhan them mot nhip moi,
                    // bo xu ly theo trang thai van thay phim dang xuong lien tuc. Khong duoc tao
                    // khoang KEY-UP quanh dung luc re.
                    RepeatHeld();
                    repeats++;
                    nextRepeatMs += InputRepeatDownMs;
                }

                // KHONG doi `tip` trong luc dang cho xac nhan: mieng anh giu tam o cmdPos, va no bao
                // duoc ±172px — hon han quang duong toi da 129px cua 230ms cho phep. Nhich tam theo
                // mot phep do trung gian chi lam mieng anh chay theo nhieu.
                var onNew = BoardTracker.TrackTurn(patch, rect, reference, w, h, cmdPos, next.Key, scale);
                var onOld = BoardTracker.TrackTurn(patch, rect, reference, w, h, cmdPos, currentKey, scale);

                double newProg = (onNew is { } a ? Along(cmdPos, a, next.Key) : 0) - baseNew;
                double oldProg = (onOld is { } c ? Along(cmdPos, c, currentKey) : 0) - baseOld;
                samples.Add((ms, newProg, oldProg));

                string src = ConfirmTurn(samples, scale, newProg, oldProg, ref vectorHits, ref bestNew);
                if (src is not null)
                {
                    ReleaseHeld();
                    if (onNew is { } np2) tip = np2;
                    blind.Restart();

                    // Cua chan buoc nhay phai tinh lai tu DAY: trong ca pha cho xac nhan, `tip`
                    // khong duoc cap nhat (co y), nen neu khong dat lai moc thi khung dau tien cua
                    // doan moi se thay mot khoang mu 20-40ms va mo cua chan rong ra vo co.
                    sinceAccepted.Restart();
                    Emit($"  #{idx} {currentKey}→{next.Key} rẽ xong sau {ms:F0}ms — " +
                         $"trục mới {newProg:F1}px, trục cũ {oldProg:F1}px [{src}]");

                    idx++;
                    currentKey = next.Key;
                    inputLatencyMs = 0.8 * inputLatencyMs + 0.2 * EstimateInputOnsetMs(samples, scale, ms);
                    motion.Reset(0, reader.PatchTimestamp != 0
                        ? reader.PatchTimestamp
                        : Stopwatch.GetTimestamp(), speed);
                    pending = false;
                    continue;
                }

                if (ms >= TurnMaxHoldMs)
                {
                    ReleaseHeld();
                    return Fail(reader, reference, w, h, scale, tip, BoardStopReason.TurnNotConfirmed,
                                $"góc #{idx} {currentKey}→{next.Key}: giữ {ms:F0}ms mà dây không rẽ — " +
                                $"trục mới {newProg:F1}px, trục cũ {oldProg:F1}px, " +
                                $"{repeats + 1} cú bấm", stats);
                }
            }
        }
        finally { ReleaseHeld(); }
    }

    /// <summary>
    /// Dò xem đầu dây ĐANG ở đâu trước khi bấm phím đầu tiên.
    ///
    /// KHÔNG tin <see cref="BoardRole.StartPoint"/>: từ lúc bảng mở tới lúc tuyến đóng băng đã mất
    /// ~600ms (hai khung tường ×175ms + dựng ~250ms), mà ngã rẽ đầu tiên đo được trên bảng thật chỉ
    /// cách START 216px — cỡ 600ms đường đi. Nên lúc bắt đầu chạy, dây đã đi được một quãng đáng kể
    /// và bấm theo StartPoint là bấm theo một vị trí không còn tồn tại.
    ///
    /// Chỉ quét hành lang đoạn 0. Dây không thể tự rẽ — hướng phóng ban đầu chính là phím của đoạn
    /// 0 — nên đầu dây chắc chắn còn trên làn đó. Nếu nó đã vượt góc 0 quá <paramref name="lateTol"/>
    /// thì cú rẽ đầu đã trượt và không cứu được: báo <see cref="BoardStopReason.LateStart"/> chứ
    /// KHÔNG bỏ qua một ngã rẽ (bỏ qua là đâm tường mà không biết vì sao).
    /// </summary>
    private (Point Tip, Failure? Fail) Acquire(BoardReader reader, byte[] reference,
                                               int w, int h, double scale, BoardPlan plan,
                                               int lateTol, CancellationToken ct)
    {
        var seg0 = plan.Segments[0];
        var start = plan.Role.StartPoint;
        var corner0 = new Point((int)Math.Round(seg0.End.X), (int)Math.Round(seg0.End.Y));
        var whole = new Rectangle(0, 0, w, h);

        int half = Math.Max(8, (int)Math.Round(BoardTracker.StripHalfRef * scale));

        // Quet CA mot doan dai qua goc 0: neu day da vuot goc thi con so "vuot bao nhieu px" phai
        // dung, vi do la thu duy nhat noi cho biet tre bao nhieu — quet sat goc thi bao cao nao
        // cung ra "vuot 32px" du that ra la 300px.
        int reach = (int)Math.Round(seg0.Distance + 240 * scale);
        double minProgress = LaunchProgressRef * scale;

        var sw = Stopwatch.StartNew();
        int tries = 0;

        while (sw.ElapsedMilliseconds < AutoStartTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            var cur = reader.GrabRoi(_roiBuf);
            tries++;
            if (cur is null) { Sleep(ct, 8); continue; }

            var found = BoardTracker.FurthestAlong(cur, whole, reference, w, h,
                                                   start, seg0.Key, half, reach, 0);
            if (found is { } p && Along(start, p, seg0.Key) >= minProgress)
            {
                int past = -Along(p, corner0, seg0.Key);
                if (past > lateTol)
                    return (p, new Failure(BoardStopReason.LateStart,
                        $"đầu dây đã ở ({p.X},{p.Y}), vượt ngã rẽ đầu tiên {past}px > {lateTol}px " +
                        $"— tuyến dựng xong quá muộn"));

                Emit($"thấy dây tự phóng: ({p.X},{p.Y}), đi được " +
                     $"{Along(start, p, seg0.Key)}px từ START sau {sw.ElapsedMilliseconds}ms / {tries} lượt chụp");
                return (p, null);
            }
        }

        return (start, new Failure(BoardStopReason.WireDidNotStart,
            $"{AutoStartTimeoutMs}ms không thấy đầu dây rời đầu nối START ({tries} lượt chụp)"));
    }

    /// <summary>
    /// Cú rẽ đã có hiệu lực VẬT LÝ chưa. Trả về tên nhánh đã chứng minh, hoặc null.
    ///
    /// Bốn nhánh, ngưỡng lấy nguyên của <c>v71_controller</c>. Đòi trục MỚI lấn át trục CŨ chứ không
    /// chỉ "có tồn tại": bản Python từng nhận <c>mới=4px / cũ=5px</c> là đã rẽ, và cái đó cho script
    /// chạy tiếp trong khi đầu dây thật vẫn phần lớn đang đi trên làn cũ.
    ///
    /// <paramref name="newProg"/>/<paramref name="oldProg"/> là DỊCH CHUYỂN KỂ TỪ LÚC BẤM, đã trừ
    /// mốc đo tại chỗ — xem chỗ tính mốc trong <see cref="RunRoute"/> để biết vì sao không được đưa
    /// số thô vào đây.
    ///
    /// Đã BỎ hai nhánh còn lại của bản Python, và đây là chỗ nhìn trước nếu sau này gặp
    /// <see cref="BoardStopReason.TurnNotConfirmed"/> lặp lại: <c>tracker-dir</c> cần trạng thái
    /// <c>observed_direction</c> mà bản C# không nuôi, và <c>wire-frontier</c> cần cả
    /// <c>WireRouteProgressTracker</c>. Cả hai chỉ là bảo hiểm cho bốn nhánh dưới đây.
    /// </summary>
    private static string ConfirmTurn(List<(double Ms, double New, double Old)> samples, double scale,
                                      double newProg, double oldProg,
                                      ref int vectorHits, ref double bestNew)
    {
        double oldAbs = Math.Abs(oldProg);

        if (newProg >= TurnVectorConfirmRef * scale &&
            newProg >= Math.Max(2.0 * scale, oldAbs * VectorDominance))
            return "vector-áp-đảo";

        if (newProg >= TurnVectorStrongRef * scale &&
            newProg >= Math.Max(2.0 * scale, oldAbs * StrongOldRatio))
            return "vector-mạnh";

        // Hai mau lien tiep deu ap dao: chong mot phep do le lam nhay script.
        if (newProg >= 3.5 * scale && newProg >= bestNew - 0.4 * scale &&
            newProg >= Math.Max(1.8 * scale, oldAbs * 1.00)) vectorHits++;
        else vectorHits = 0;
        bestNew = Math.Max(bestNew, newProg);

        if (vectorHits >= 2 && newProg >= TurnVectorConfirmRef * scale &&
            newProg >= Math.Max(2.0 * scale, oldAbs * 1.05))
            return "vector-2-khung";

        // Nhanh THOI GIAN: so hai vi tri DO DUOC cach nhau ~14ms. Bat duoc ca truong hop truc cu
        // con du chuyen dong nhung da phang lai, ma phep cong don khong thay.
        if (samples.Count >= 2)
        {
            var last = samples[^1];
            double cutoff = last.Ms - TemporalWindowMs;
            (double Ms, double New, double Old)? older = null;
            foreach (var s in samples)
            {
                if (s.Ms <= cutoff) older = s;
                else break;
            }
            if (older is { } o)
            {
                double incNew = last.New - o.New;
                double incOld = last.Old - o.Old;
                if (last.New >= TemporalCumRef * scale &&
                    incNew >= TemporalNewRef * scale &&
                    incNew >= Math.Max(1.1 * scale, Math.Abs(incOld) * TemporalDominance))
                    return "thời-gian";
            }
        }

        return null;
    }

    /// <summary>
    /// Độ trễ phím = lúc đầu dây BẮT ĐẦU chạy trên trục mới, không phải lúc đủ mạnh để xác nhận
    /// rẽ (cái sau còn gồm quãng đi và bỏ phiếu nhiều khung). Học nhầm thời gian xác nhận sẽ
    /// đẩy lead ngày càng sớm rồi rẽ trước cửa an toàn.
    /// </summary>
    internal static double EstimateInputOnsetMs(
        List<(double Ms, double New, double Old)> samples, double scale, double fallbackMs)
    {
        double onsetPx = Math.Max(1.2, 1.6 * scale);
        if (samples is not null)
        {
            foreach (var s in samples)
            {
                if (s.New >= onsetPx) return Math.Clamp(s.Ms, 1.0, 12.0);
            }
        }
        return Math.Clamp(fallbackMs, 1.0, 12.0);
    }

    /// <summary>
    /// Dừng có chẩn đoán: đo màu quanh đầu dây tại đúng chỗ bỏ dở rồi mới trả lỗi. Xem
    /// <see cref="BoardTracker.CountDynamic"/> để biết vì sao ba con số đó đáng một lượt chụp.
    /// </summary>
    private (bool Ok, Failure? Fail, string Note) Fail(BoardReader reader, byte[] reference,
                                                       int w, int h, double scale, Point tip,
                                                       BoardStopReason reason, string message,
                                                       LoopStats stats)
    {
        ReleaseHeld();

        var rect = reader.GrabPatch(tip);
        if (!rect.IsEmpty)
        {
            var (green, red, changed) = BoardTracker.CountDynamic(
                reader.PatchBuffer, rect, reference, w, h, tip, scale);
            Emit($"đo tại chỗ bỏ dở ({tip.X},{tip.Y}): {changed} pixel đã đổi — " +
                 $"xanh {green}, đỏ {red}");
        }
        Emit("  " + stats.Describe());

        return (false, new Failure(reason, message), message);
    }

    /// <summary>Nhịp vòng chạy và quãng đường mỗi khung — hai số phải nhìn đầu tiên khi tuyến sai.</summary>
    private sealed class LoopStats
    {
        /// <summary>
        /// Nhịp vòng danh nghĩa, chỉ để IN RA cửa chặn bước nhảy ở một mốc thời gian dễ so sánh
        /// giữa các dòng log. Không tham gia điều khiển — chỗ điều khiển dùng thời gian ĐO ĐƯỢC kể
        /// từ phép đo được nhận gần nhất. Con số 3.4ms là nhịp đo được trong game ở 2K.
        /// </summary>
        public const double NominalDtMs = 3.4;

        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly List<double> _dts = new(1024);
        private double _last;
        private double _advance;
        private int _advances;

        public void Tick()
        {
            double now = _sw.Elapsed.TotalMilliseconds;
            double dt = now - _last;
            _last = now;
            if (dt > 0 && dt < 100 && _dts.Count < 4096) _dts.Add(dt);
        }

        public void Advance(int px)
        {
            if (px <= 0) return;
            _advance += px;
            _advances++;
        }

        public string Describe()
        {
            if (_dts.Count == 0) return "chưa có nhịp vòng";
            var sorted = new List<double>(_dts);
            sorted.Sort();
            double mean = 0;
            foreach (double d in sorted) mean += d;
            mean /= sorted.Count;
            double p95 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))];
            string step = _advances > 0 ? $"{_advance / _advances:F1}px" : "—";
            return $"nhịp vòng tb {mean:F1}ms p95 {p95:F1}ms ({sorted.Count} khung), mỗi bước dây đi {step}";
        }
    }

    /// <summary>Quãng đường từ <paramref name="from"/> tới <paramref name="to"/> ĐO THEO TRỤC của phím.</summary>
    private static int Along(Point from, Point to, string key) => key switch
    {
        BoardKeys.Left => from.X - to.X,
        BoardKeys.Right => to.X - from.X,
        BoardKeys.Up => from.Y - to.Y,
        _ => to.Y - from.Y
    };

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ---------------------------------------------------------------- phim

    /// <summary>
    /// Bắn phím rẽ: nhả phím cũ (nếu còn) rồi nhấn giữ phím mới.
    ///
    /// Ghi lại phím đang giữ vào <see cref="_heldKey"/> chứ không tin vào luồng gọi: nếu bot chết
    /// giữa cú rẽ thì <see cref="ReleaseHeld"/> ở khối finally phải biết nhả cái gì. Kẹt phím hướng
    /// trong game là nhân vật tự chạy mãi.
    /// </summary>
    private void FireTurn(string key)
    {
        ReleaseHeld();

        int idx = BoardKeys.Index(key);
        if (idx < 0) return;

        InputSender.KeyDown(BoardKeys.Vk[idx]);
        _heldKey = key;
    }

    /// <summary>
    /// Bơm lại KEYDOWN của phím đang giữ mà KHÔNG nhả. Xem chỗ gọi để biết vì sao không được nhả.
    /// </summary>
    private void RepeatHeld()
    {
        if (_heldKey is null) return;
        int idx = BoardKeys.Index(_heldKey);
        if (idx < 0) return;
        InputSender.KeyDown(BoardKeys.Vk[idx]);
    }

    private void ReleaseHeld()
    {
        if (_heldKey is null) return;

        int idx = BoardKeys.Index(_heldKey);
        _heldKey = null;
        if (idx < 0) return;

        try { InputSender.KeyUp(BoardKeys.Vk[idx]); } catch { }
    }

    // ---------------------------------------------------------------- chung

    private bool WaitWindow(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) return false;

        bool waited = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var title = Native.ForegroundTitle();
            if (title.Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase))
            {
                if (_windowWarned)
                {
                    Emit("game đã focus lại — chạy tiếp");
                    _windowWarned = false;
                }
                return waited;
            }

            // Mat focus giua luc dang giu phim: nha ngay, khong cho vong sau.
            ReleaseHeld();
            if (!_windowWarned)
            {
                Emit($"tạm dừng: chưa focus “{_cfg.WindowMatch}” (đang focus: “{title}”)");
                _windowWarned = true;
            }
            waited = true;
            Sleep(ct, 200);
        }
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
    }

    private void Emit(string line) => Log?.Invoke(line);
}
