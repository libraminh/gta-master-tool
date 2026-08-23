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

    /// <summary>Đã có đủ lịch sử phủ hết cửa sổ chưa.</summary>
    public bool Ready(long now) => _hist.Count >= 2 && now - _hist[0].T >= _nav.ProgressWindowMs;

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

    /// <summary>Cự ly lúc bắt đầu quãng chấm điểm gần nhất — mốc để nói "có thoát hay không".</summary>
    public double StartDist { get; private set; }

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
        return true;
    }

    public void Close()
    {
        Active = false;
        Rung = 0;
        _flipped = false;
        _jumped = false;
    }

    /// <summary>Cập nhật mốc cự ly trước khi đi lại — dùng sau mỗi bậc.</summary>
    public void MarkDistance(double dist) => StartDist = dist;

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
