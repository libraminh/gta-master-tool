using System.Diagnostics;

namespace GtaMiniGameBot;

/// <summary>Đồng hồ giây đơn điệu dùng chung cho bộ điều hướng — thay <c>time.monotonic()</c>/<c>perf_counter()</c>.</summary>
internal static class NavClock
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static double Now => Sw.Elapsed.TotalSeconds;
}

/// <summary>Các phím bộ điều hướng có thể giữ. Bit để tính hiệu hai tập cho gọn.</summary>
[Flags]
internal enum NavKey
{
    None = 0,
    W = 1,
    A = 2,
    S = 4,
    D = 8,
    E = 16,
    Esc = 32,
    Shift = 64
}

internal enum MouseAxis { Both, X, Y }

/// <summary>
/// Port của <c>InputInjector</c> (Navigator Python, main.py 239–759): tập phím đang giữ, nhịp làm
/// tươi W, và LUỒNG CHUỘT 240 Hz.
///
/// Hai ý tưởng của bản gốc phải giữ nguyên vì chúng là lý do nó chạy ổn trong FiveM:
///
///   1. W không bao giờ được "giữ rồi quên". Game có thể nuốt một cú KEYDOWN đúng lúc đổi UI, và
///      bản cũ tin là W vẫn xuống mãi. Ở đây cứ 0.50 s lại gửi thêm một KEYDOWN W (không kèm KEYUP,
///      nên không có khoảng hụt), và sau một chuyển cảnh thật thì được phép đúng MỘT cú UP→DOWN
///      trong cửa sổ "rearm" (<see cref="DoublePressWStart"/>, <see cref="ForceWTakeoverOnce"/>).
///
///   2. Camera KHÔNG xoay theo delta từng khung. Bộ điều khiển chỉ đặt TỐC ĐỘ mong muốn
///      (counts/giây) qua <see cref="SetMouseXRate"/>; một luồng riêng chạy 240 Hz tích phân tốc độ
///      đó qua bộ lọc bậc nhất (tau 50 ms) với trần gia tốc, mang phần lẻ qua tick, và tự về 0 khi
///      quá 120 ms không ai làm mới ("lease"). Nhờ lease, bất kỳ nhánh nào quên gọi tắt chuột cũng
///      chỉ xoay thêm tối đa 120 ms.
///
/// Mọi cú gửi đi qua <see cref="InputSender"/> nên mang <c>dwExtraInfo = MAGIC</c> — hook của
/// UtilityService bỏ qua chúng. Bản Python có đường lùi <c>keybd_event</c> khi SendInput thiếu; ở
/// đây InputSender ném <see cref="InvalidOperationException"/> và NavBot đổi thành lý do dừng
/// <c>InputFailed</c>, còn luồng chuột thì ghi vào <see cref="Fault"/> rồi tự dừng.
/// </summary>
internal sealed class NavInput : IDisposable
{
    private const ushort VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44, VK_E = 0x45, VK_ESC = 0x1B;

    /// <summary>Thứ tự gửi phím — <c>sorted()</c> theo tên Python: A, D, E, ESC, S, SHIFT, W.</summary>
    private static readonly NavKey[] Order = { NavKey.A, NavKey.D, NavKey.E, NavKey.Esc, NavKey.S, NavKey.Shift, NavKey.W };

    // ---------------- W keep-alive (configure_resync, da kep) ----------------
    // input_w_heartbeat_s 0.45 -> max(0.50, .) = 0.50 ; input_w_post_transition_rearm_s 0.35 ;
    // input_w_hard_resync_pulses 1 ; input_w_post_minigame_soft_delay_s 0.12.
    private const double HeartbeatS = 0.50;
    private const double RearmIntervalS = 0.35;
    private const int HardResyncLimit = 1;
    private const double PostTakeoverSoftDelayS = 0.12;

    // ---------------- luong chuot (global_mouse_*) ----------------
    private const double StreamHz = 240.0;
    private const double XTauS = 0.050, XAccelCps2 = 36000.0;
    private const double YTauS = 0.010, YAccelCps2 = 250000.0;
    private const double LeaseS = 0.12;

    private readonly object _lock = new();
    private NavKey _held;

    private double _rearmUntil, _nextHeartbeat, _nextRearm;
    private int _hardRemaining;

    /// <summary>Số lần đã gửi KEYDOWN W làm tươi (SOFT) — để in ra log khi cần soi.</summary>
    public int SoftKeepaliveCount { get; private set; }

    public int HardResyncCount { get; private set; }

    private readonly object _mlock = new();
    private readonly double _xMultiplier;
    private double _tx, _ty, _rx, _ry, _fx, _fy, _ux, _uy;
    private readonly Thread _mouseThread;
    private volatile bool _mouseStop;

    /// <summary>Lỗi của luồng chuột (SendInput thiếu). Khác null là luồng đã tự dừng.</summary>
    public Exception Fault { get; private set; }

    /// <param name="mouseSpeedMultiplier"><c>mouse_global_speed_multiplier</c> — chỉ nhân trục X.</param>
    public NavInput(double mouseSpeedMultiplier)
    {
        _xMultiplier = mouseSpeedMultiplier;
        _mouseThread = new Thread(MouseLoop) { IsBackground = true, Name = "NavMouse240Hz" };
        _mouseThread.Start();
    }

    // ================================================================ phim

    public NavKey Held { get { lock (_lock) return _held; } }

    public bool IsHeld(NavKey k) => (Held & k) != 0;

    /// <summary>Bản Python thêm tay <c>held.add('SHIFT')</c> sau khi gửi SHIFT riêng — cần cùng chỗ ghi.</summary>
    public void MarkHeld(NavKey k)
    {
        lock (_lock) _held |= k;
    }

    private static ushort Vk(NavKey k) => k switch
    {
        NavKey.W => VK_W,
        NavKey.A => VK_A,
        NavKey.S => VK_S,
        NavKey.D => VK_D,
        NavKey.E => VK_E,
        NavKey.Esc => VK_ESC,
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };

    private static void KeyEvent(NavKey k, bool up)
    {
        if (k == NavKey.Shift)
        {
            if (up) InputSender.ShiftUp(); else InputSender.ShiftDown();
            return;
        }
        if (up) InputSender.KeyUp(Vk(k)); else InputSender.KeyDown(Vk(k));
    }

    /// <summary><c>_w_resync_mode</c>: null / "HARD" / "SOFT". Gọi khi W đang được muốn và đã giữ sẵn.</summary>
    private string WResyncMode(double now)
    {
        if (now < _rearmUntil)
        {
            if (now >= _nextRearm)
            {
                _nextRearm = now + RearmIntervalS;
                _nextHeartbeat = now + HeartbeatS;
                if (_hardRemaining > 0) { _hardRemaining--; return "HARD"; }
                return "SOFT";
            }
            return null;
        }
        if (now >= _nextHeartbeat)
        {
            _nextHeartbeat = now + HeartbeatS;
            return "SOFT";
        }
        return null;
    }

    /// <summary>
    /// <c>apply(wanted)</c>: nhả phím thừa, nhấn phím thiếu, và làm tươi W theo nhịp. Mọi thứ trong
    /// một khoá để không đan xen với <see cref="DoublePressWStart"/> từ luồng khác.
    /// </summary>
    public void Apply(NavKey wanted)
    {
        lock (_lock)
        {
            double now = NavClock.Now;
            NavKey ups = _held & ~wanted;
            NavKey downs = wanted & ~_held;
            bool wantW = (wanted & NavKey.W) != 0;
            bool freshW = wantW && (_held & NavKey.W) == 0;

            string mode = null;
            if (wantW && !freshW) mode = WResyncMode(now);

            foreach (var k in Order) if ((ups & k) != 0) KeyEvent(k, up: true);
            foreach (var k in Order) if ((downs & k) != 0) KeyEvent(k, up: false);

            if (mode == "HARD")
            {
                KeyEvent(NavKey.W, up: true);
                KeyEvent(NavKey.W, up: false);
                HardResyncCount++;
            }
            else if (mode == "SOFT")
            {
                KeyEvent(NavKey.W, up: false);
                SoftKeepaliveCount++;
            }

            if (freshW)
            {
                _nextHeartbeat = now + HeartbeatS;
                if (now < _rearmUntil) _nextRearm = now + RearmIntervalS;
                _hardRemaining = 0;     // cu W xuong that da dong bo voi game roi
            }

            _held = wanted;
        }
    }

    /// <summary>
    /// <c>double_press_w_start</c>: hai cú W nhanh, cú thứ hai giữ luôn. CHẶN luồng gọi
    /// (10 + hold + gap ms). Dùng khi bộ điều hướng vừa lấy lại W từ game/UI.
    /// </summary>
    public void DoublePressWStart(double gapMs = 24.0, double softRearmS = 1.60, double firstHoldMs = 34.0)
    {
        double gapS = Math.Clamp(gapMs / 1000.0, 0.012, 0.060);
        double holdS = Math.Clamp(firstHoldMs / 1000.0, 0.018, 0.070);

        lock (_lock) { InputSender.KeyUp(VK_W); _held &= ~NavKey.W; }
        Thread.Sleep(10);
        lock (_lock) { InputSender.KeyDown(VK_W); _held |= NavKey.W; }
        Thread.Sleep((int)Math.Round(holdS * 1000));
        lock (_lock) { InputSender.KeyUp(VK_W); _held &= ~NavKey.W; }
        Thread.Sleep((int)Math.Round(gapS * 1000));
        lock (_lock)
        {
            InputSender.KeyDown(VK_W);
            _held |= NavKey.W;
            double now = NavClock.Now;
            _hardRemaining = 0;
            _rearmUntil = Math.Max(_rearmUntil, now + Math.Max(0.30, softRearmS));
            _nextRearm = now + Math.Max(0.08, PostTakeoverSoftDelayS);
            _nextHeartbeat = now + HeartbeatS;
        }
    }

    /// <summary>
    /// <c>force_w_takeover_once</c>: một cú W UP thật, một khoảng trống nhìn thấy được, rồi W DOWN
    /// thật — để game trả quyền đi thẳng về sau khi minigame/UI đóng. Gap cho phép tới 140 ms vì
    /// 50 ms từng bị FiveM/NUI nuốt.
    /// </summary>
    public void ForceWTakeoverOnce(double gapMs = 18.0, double softRearmS = 1.50)
    {
        double gapS = Math.Clamp(gapMs / 1000.0, 0.012, 0.140);

        lock (_lock) { InputSender.KeyUp(VK_W); _held &= ~NavKey.W; }
        Thread.Sleep((int)Math.Round(gapS * 1000));
        lock (_lock)
        {
            InputSender.KeyDown(VK_W);
            _held |= NavKey.W;
            double now = NavClock.Now;
            _hardRemaining = 0;
            _rearmUntil = Math.Max(_rearmUntil, now + Math.Max(0.30, softRearmS));
            _nextRearm = now + PostTakeoverSoftDelayS;
            _nextHeartbeat = now + HeartbeatS;
        }
    }

    /// <summary>
    /// <c>send_tagged_key_event</c>: MỘT sự kiện phím, KHÔNG động vào tập <see cref="Held"/>. Dùng cho
    /// E một lần (down rồi up sau 90 ms), Esc, và SHIFT keep-alive.
    /// </summary>
    public bool SendKeyEvent(NavKey key, bool up)
    {
        lock (_lock) KeyEvent(key, up);
        return true;
    }

    /// <summary>
    /// Gõ một phím KHÔNG có trong <see cref="NavKey"/> (số hotbar khi ăn/uống). Không vào tập giữ:
    /// <see cref="Apply"/> tính hiệu hai tập mỗi tick, phím nhất thời lọt vào đó sẽ bị nhả ngay ở
    /// tick sau. Đây đúng cách E được gửi — down một tick, up ở tick sau.
    /// </summary>
    public bool SendRawKeyEvent(ushort vk, bool up)
    {
        lock (_lock)
        {
            if (up) InputSender.KeyUp(vk); else InputSender.KeyDown(vk);
        }
        return true;
    }

    /// <summary><c>force_key_up</c>: KEYUP vô điều kiện <paramref name="repeats"/> lần, rồi bỏ khỏi tập giữ.</summary>
    public void ForceKeyUp(NavKey key, int repeats = 1)
    {
        repeats = Math.Max(1, repeats);
        lock (_lock)
        {
            for (int i = 0; i < repeats; i++)
            {
                KeyEvent(key, up: true);
                Thread.Sleep(3);
            }
            _held &= ~key;
        }
    }

    /// <summary><c>click_screen</c>: đặt con trỏ rồi một cú click trái — cho nút trên bảng nghề (NUI có con trỏ).</summary>
    public void ClickScreen(int x, int y, double holdMs = 55.0)
    {
        InputSender.MoveCursorOnly(x, y);
        Thread.Sleep(15);
        InputSender.LeftDown();
        try { Thread.Sleep((int)Math.Round(Math.Max(20.0, holdMs))); }
        finally { InputSender.LeftUp(); }
    }

    /// <summary><c>release_all</c>: nhả hết theo diff, xoá quota HARD.</summary>
    public void ReleaseAll()
    {
        Apply(NavKey.None);
        lock (_lock) _hardRemaining = 0;
    }

    /// <summary>
    /// <c>release_owned_once</c>: nhả hết ĐÚNG MỘT LẦN rồi trơ — không còn heartbeat/rearm nào nổ sau
    /// khi người chơi cầm lại bàn phím. Cũng dừng chuột ngay.
    /// </summary>
    public void ReleaseOwnedOnce()
    {
        StopMouseStream(immediate: true);
        Apply(NavKey.None);
        lock (_lock)
        {
            _held = NavKey.None;
            _hardRemaining = 0;
            _rearmUntil = 0;
            _nextRearm = 0;
            _nextHeartbeat = NavClock.Now + HeartbeatS;
        }
    }

    // ================================================================ chuot

    /// <summary><c>set_mouse_stream_x_rate</c>: tốc độ yaw mong muốn (cps, TRƯỚC khi nhân hệ số).</summary>
    public void SetMouseXRate(double cps)
    {
        double now = NavClock.Now;
        lock (_mlock) { _tx = cps * _xMultiplier; _ux = now; }
    }

    /// <summary><c>set_mouse_stream_y_rate</c>: tốc độ pitch (cps) — KHÔNG nhân hệ số, dương = nhìn xuống.</summary>
    public void SetMouseYRate(double cps)
    {
        double now = NavClock.Now;
        lock (_mlock) { _ty = cps; _uy = now; }
    }

    /// <summary>
    /// <c>stop_mouse_stream</c>. <paramref name="immediate"/> false = chỉ hạ đích về 0 để tốc độ tắt dần
    /// qua tau (cú "dừng mềm" duy nhất của bản Python nằm ở ARC_ARRIVAL_COAST); true = cắt phăng.
    /// </summary>
    public void StopMouseStream(bool immediate = false, MouseAxis axis = MouseAxis.Both)
    {
        double now = NavClock.Now;
        lock (_mlock)
        {
            if (axis is MouseAxis.Both or MouseAxis.X)
            {
                _tx = 0; _ux = now;
                if (immediate) { _rx = 0; _fx = 0; }
            }
            if (axis is MouseAxis.Both or MouseAxis.Y)
            {
                _ty = 0; _uy = now;
                if (immediate) { _ry = 0; _fy = 0; }
            }
        }
    }

    /// <summary>
    /// <c>_mouse_stream_axis_step</c> — một bước tích phân của một trục. Public static để kiểm ngoài game.
    /// </summary>
    public static (double rate, double frac, int outCounts) AxisStep(
        double target, double rate, double frac, double dt, double tau, double accel)
    {
        double alpha = 1.0 - Math.Exp(-dt / Math.Max(0.006, tau));
        double desired = rate + alpha * (target - rate);
        double md = accel * dt;
        rate = Math.Max(rate - md, Math.Min(rate + md, desired));
        if (Math.Abs(target) < 1e-6 && Math.Abs(rate) < 5.0) rate = 0.0;
        frac += rate * dt;
        int outp = (int)Math.Truncate(frac);
        frac -= outp;
        return (rate, frac, outp);
    }

    private void MouseLoop()
    {
        double period = 1.0 / StreamHz;
        double last = NavClock.Now, next = last;

        while (!_mouseStop)
        {
            double now = NavClock.Now;
            if (now < next)
            {
                double rem = next - now;
                if (rem > 0.002) Thread.Sleep(1); else Thread.SpinWait(120);
                continue;
            }
            next = Math.Max(next + period, now);
            double dt = Math.Clamp(now - last, 0.001, 0.020);
            last = now;

            double tx, ty, ux, uy, rx, ry, fx, fy;
            lock (_mlock) { tx = _tx; ty = _ty; ux = _ux; uy = _uy; rx = _rx; ry = _ry; fx = _fx; fy = _fy; }

            if (ux <= 0.0 || now - ux > LeaseS) tx = 0.0;
            if (uy <= 0.0 || now - uy > LeaseS) ty = 0.0;

            (rx, fx, int dx) = AxisStep(tx, rx, fx, dt, XTauS, XAccelCps2);
            (ry, fy, int dy) = AxisStep(ty, ry, fy, dt, YTauS, YAccelCps2);

            if (dx != 0 || dy != 0)
            {
                try { InputSender.MoveRelative(dx, dy); }
                catch (Exception ex)
                {
                    Fault = ex;
                    break;
                }
            }

            lock (_mlock) { _rx = rx; _fx = fx; _ry = ry; _fy = fy; }
        }

        lock (_mlock) { _rx = _tx = _fx = 0; _ry = _ty = _fy = 0; }
    }

    /// <summary>Dừng luồng chuột (cờ + join ngắn), không nhả phím — người gọi tự nhả theo thứ tự của mình.</summary>
    public void Dispose()
    {
        StopMouseStream(immediate: true);
        _mouseStop = true;
        try { _mouseThread.Join(300); } catch { }
    }
}
