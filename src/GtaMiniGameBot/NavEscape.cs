namespace GtaMiniGameBot;

/// <summary>
/// Trọng tài "có đang tiến tới đích không", đo bằng CỰ LY chấm trên minimap.
///
/// Vì sao cự ly chứ không phải sai phân khung: xoay camera đổi GÓC của chấm chứ không đổi CỰ LY,
/// nên tín hiệu này miễn nhiễm với chính thứ bộ lái đang làm suốt. Đo trong game 23/08:
///   đi thật  → 42→31, 29→7, 32→13, giảm đều;
///   kẹt cứng → đứng nguyên 31 suốt 30 s, rồi 12↔13 suốt 25 s.
/// Cùng lúc đó sai phân khung sai CẢ HAI CHIỀU: 2.5 khi đang đi (dưới ngưỡng → báo kẹt oan) và
/// 9.4 khi đang húc tường (trên ngưỡng → bỏ sót).
///
/// Bản Python cũng đo trên minimap nhưng cửa sổ chỉ 1.05 s nên phải bắt một tín hiệu dưới hai
/// pixel (<c>stuck_displacement_px 1.15</c>) — quá mỏng để tin. Cửa sổ 3 s thì lượng dịch đủ to.
/// </summary>
internal sealed class ProgressTracker
{
    private readonly NavSettings _nav;
    private readonly List<(long T, double D)> _hist = new();

    public ProgressTracker(NavSettings nav) => _nav = nav;

    public void Reset() => _hist.Clear();

    public void Push(long now, double distRef)
    {
        _hist.Add((now, distRef));

        // Giu du phu cua so, cong mot chut de con giu diem NGAY TRUOC moc cua so.
        long keep = now - _nav.ProgressWindowMs - 1500;
        int drop = 0;
        while (drop < _hist.Count - 1 && _hist[drop].T < keep) drop++;
        if (drop > 0) _hist.RemoveRange(0, drop);
    }

    /// <summary>
    /// Đã có đủ lịch sử phủ hết cửa sổ chưa — VÀ mẫu mới nhất có còn tươi không.
    ///
    /// Vế thứ hai bắt buộc phải có, và thiếu nó là lỗi đã giết cả lượt chạy 25/08. Mất dấu chấm
    /// thì không có <see cref="Push"/> mới nào, nhưng <c>_hist</c> vẫn còn nguyên: mốc cửa sổ cứ
    /// trôi tới cho tới khi MỌI mẫu đều nằm trước nó, lúc đó <see cref="Delta"/> so mẫu cuối với
    /// chính nó và trả 0. Mà 0 > −MinProgressRef, nên <see cref="Stalled"/> báo kẹt VĨNH VIỄN.
    ///
    /// Log 25/08 01:00:13 — bot đang khoá mốc 3D, lệch 1.4°, đất trôi 3.3 (trên ngưỡng, tức đang
    /// đi thật) — vẫn bị tuyên "kẹt (Δxa=+1.0/3s)". Cú trượt sau đó xoay camera văng 164°, mất
    /// sạch tín hiệu, quét 12 s rồi hỏng lượt.
    ///
    /// Hết tươi thì trả false để bộ lái rơi về tín hiệu đất trôi, chứ KHÔNG phải để nó tự suy ra
    /// "không đo được nghĩa là đang kẹt".
    /// </summary>
    public bool Ready(long now) =>
        _hist.Count >= 2
        && now - _hist[0].T >= _nav.ProgressWindowMs
        && now - _hist[^1].T <= _nav.ProgressStaleMs;

    /// <summary>Cự ly bây giờ trừ cự ly đầu cửa sổ. Âm = đang tiến tới gần.</summary>
    public double Delta(long now)
    {
        if (_hist.Count < 2) return 0;

        long edge = now - _nav.ProgressWindowMs;
        double old = _hist[0].D;
        foreach (var s in _hist)
        {
            if (s.T > edge) break;
            old = s.D;
        }
        return _hist[^1].D - old;
    }

    public bool Stalled(long now) => Ready(now) && Delta(now) > -_nav.MinProgressRef;
}

internal enum EscapeAction
{
    /// <summary>Trượt ngang thuần (A hoặc D, KHÔNG kèm W).</summary>
    Strafe,

    /// <summary>Lùi lại rồi đổi bên; bậc đặt lại từ đầu cho bên mới.</summary>
    BackupAndFlip,

    Jump,

    /// <summary>Hết thang — bỏ lượt.</summary>
    Exhausted
}

internal readonly record struct EscapeStep(EscapeAction Action, bool Right, int DurationMs, int Rung)
{
    public override string ToString() => Action switch
    {
        EscapeAction.Strafe => $"trượt {(Right ? "PHẢI" : "TRÁI")} bậc {Rung} ({DurationMs} ms)",
        EscapeAction.BackupAndFlip => $"lùi {DurationMs} ms rồi đổi sang {(Right ? "PHẢI" : "TRÁI")}",
        EscapeAction.Jump => "nhảy",
        _ => "hết thang"
    };
}

/// <summary>
/// Thang thoát kẹt — phần QUYẾT ĐỊNH, không gửi phím. Tách ra để kiểm được ngoài game bằng
/// <c>--verify-nav</c>: đây đúng là chỗ bản đầu tiên sai, và cái sai đó chỉ lộ ra khi nhìn CHUỖI
/// hành động chứ không nhìn từng bước.
///
/// Hai luật, cả hai đều rút từ log 23/08:
///
///   1. MỘT ĐỢT KẸT, KHÔNG PHẢI MỖI LẦN MỘT VÁN. Còn kẹt mà cự ly chưa cải thiện thì vẫn là cùng
///      đợt: giữ nguyên bên và LEO BẬC. Bản đầu bắt đầu lại từ bậc 1 mỗi lần nên thang không bao
///      giờ leo tới bậc lùi hay nhảy.
///
///   2. BÊN CHỌN MỘT LẦN RỒI GIỮ. Bản đầu chọn bên theo dấu sai số, mà lúc kẹt sai số dao động
///      quanh 0 (+0.9° rồi −0.2°) nên bên bị lật trái–phải–trái–phải. Log lặp đúng như vậy 8 lần
///      trong 25 giây với cự ly đứng nguyên 12↔13. Ghi chú V6.8 của bản Python đã cảnh báo đúng
///      cái bẫy này.
/// </summary>
internal sealed class EscapeLadder
{
    private readonly NavSettings _nav;
    private bool _flipped, _jumped;

    public EscapeLadder(NavSettings nav) => _nav = nav;

    public bool Active { get; private set; }

    /// <summary>Bên đang men theo. true = phải.</summary>
    public bool Right { get; private set; }

    /// <summary>Số bậc trượt đã dùng của BÊN hiện tại.</summary>
    public int Rung { get; private set; }

    /// <summary>
    /// Cự ly lúc MỞ ĐỢT. Bất biến suốt đợt — đây mới là mốc trả lời "đã thoát hay chưa".
    ///
    /// Tách khỏi <see cref="LastDist"/> vì gộp chung là lỗi đã thấy trong log 25/08: đợt mở ở
    /// xa=7, thang đẩy nhân vật ra 8 → 7 → 10, rồi cú lùi kéo về 9 và được tuyên "thoát" — đóng
    /// đợt ở chỗ XA HƠN lúc mở. Mốc bị đặt lại mỗi bậc nên bậc nào cũng chỉ phải hơn bậc trước.
    /// </summary>
    public double StartDist { get; private set; }

    /// <summary>Cự ly ngay trước bậc vừa thi hành — chỉ để đo bậc đó có nhúc nhích gì không.</summary>
    public double LastDist { get; private set; }

    /// <summary>Mở đợt mới. Trả false nếu đợt đang mở (chỉ nhập vào, giữ nguyên bên và bậc).</summary>
    public bool Open(double dist, bool preferRight)
    {
        if (Active) return false;

        Active = true;
        Right = preferRight;
        Rung = 0;
        _flipped = false;
        _jumped = false;
        StartDist = dist;
        LastDist = dist;
        return true;
    }

    public void Close()
    {
        Active = false;
        Rung = 0;
        _flipped = false;
        _jumped = false;
    }

    /// <summary>
    /// Ghi cự ly ngay trước khi thi hành một bậc. CHỈ đụng <see cref="LastDist"/> —
    /// <see cref="StartDist"/> phải giữ nguyên tới hết đợt, xem chú thích ở đó.
    /// </summary>
    public void MarkDistance(double dist) => LastDist = dist;

    /// <summary>Bậc tiếp theo. Mỗi lần gọi trả về ĐÚNG MỘT hành động vật lý.</summary>
    public EscapeStep Next()
    {
        int rungs = _nav.StrafeRungsMs.Length;

        if (Rung < rungs)
        {
            int ms = _nav.StrafeRungsMs[Rung];
            Rung++;
            return new EscapeStep(EscapeAction.Strafe, Right, ms, Rung);
        }

        if (!_flipped)
        {
            _flipped = true;
            Right = !Right;
            Rung = 0;
            return new EscapeStep(EscapeAction.BackupAndFlip, Right, _nav.BackupMs, 0);
        }

        if (_nav.UseJump && !_jumped)
        {
            _jumped = true;
            return new EscapeStep(EscapeAction.Jump, Right, 0, 0);
        }

        return new EscapeStep(EscapeAction.Exhausted, Right, 0, 0);
    }
}
