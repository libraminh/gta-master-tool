namespace GtaMiniGameBot;

internal enum MinerStopReason
{
    UserStopped,
    InputFailed
}

/// <summary>
/// Job Thợ mỏ: giữ W + Left Shift để chạy tới, và bấm E đều đặn theo nhịp đặt trong panel.
///
/// Không dùng SendInput một phát rồi ngủ trọn nhịp E: vòng lặp phải tick nhanh để (1) ping lại
/// W/Shift trước khi game bỏ rơi phím giữ lâu, và (2) nhả phím ngay khi người chơi alt-tab.
/// Đây đúng là hình dạng tick của <see cref="UtilityService"/>, chỉ khác là đặt trên một luồng
/// huỷ được thay vì Timer, để khớp khuôn bot của các job khác (Start/Stop/StopAndWait/Stopped).
/// </summary>
internal sealed class MinerBot
{
    private const ushort VK_E = 0x45;

    /// <summary>Ping lại phím giữ sau ngần này ms — cùng con số đã dùng ở UtilityService.</summary>
    private const int KeepAliveMs = 400;

    private readonly MinerConfig _cfg;
    private CancellationTokenSource _cts;
    private Thread _thread;

    private bool _held;
    private long _lastPing;
    private long _lastTap;
    private bool _windowWarned;

    public MinerBot(MinerConfig cfg) => _cfg = cfg;

    public bool Running => _thread is { IsAlive: true };

    public event Action<string> Log;
    public event Action<MinerStopReason, string> Stopped;

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "MinerBot" };
        _thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>
    /// Huỷ rồi CHỜ luồng chết hẳn. <see cref="Stop"/> chỉ báo CTS và trả về ngay, nên nếu người
    /// gọi nhả phím ngay sau đó thì luồng còn sống vẫn kịp giữ lại — W kẹt xuống dù panel đã báo
    /// "đã dừng". Hết thời gian chờ thì thôi, không treo UI.
    /// </summary>
    public void StopAndWait(int ms = 1500)
    {
        _cts?.Cancel();
        var t = _thread;
        if (t is null || !t.IsAlive) return;
        try { t.Join(ms); } catch { }
    }

    public static string TenLyDo(MinerStopReason r) => r switch
    {
        MinerStopReason.UserStopped => "người dùng bấm dừng",
        _ => "không gửi được phím vào game"
    };

    private void Run(CancellationToken ct)
    {
        var reason = MinerStopReason.UserStopped;
        string message = "người dùng bấm dừng";

        try
        {
            Emit($"bắt đầu. giữ W{(_cfg.HoldShift ? " + Left Shift" : "")}, bấm E mỗi {_cfg.TapEveryMs} ms " +
                 $"(giữ {_cfg.TapHoldMs} ms).");
            Emit($"{HotkeyText.Job()} = bật/tắt. Cửa sổ game phải đang focus ({_cfg.WindowMatch}).");

            while (true)
            {
                Sleep(ct, _cfg.PollMs);

                if (!GameForeground())
                {
                    ReleaseHeld();
                    // Quên mốc bấm cũ: vào lại game là bấm E ngay, và không dồn được tràng E
                    // vì mỗi vòng chỉ bấm tối đa một cú.
                    _lastTap = 0;
                    continue;
                }

                EnsureHeld();

                long now = Environment.TickCount64;
                if (_lastTap != 0 && now - _lastTap < _cfg.TapEveryMs) continue;

                // Ghi moc TRUOC khi bam: TapKey nam giu luong het TapHoldMs, lay moc sau thi
                // nhip that = TapEveryMs + TapHoldMs — dat 200 ma nhan duoc 260.
                _lastTap = now;
                InputSender.TapKey(VK_E, _cfg.TapHoldMs);
            }
        }
        catch (OperationCanceledException)
        {
            reason = MinerStopReason.UserStopped;
            message = "người dùng bấm dừng";
        }
        catch (InvalidOperationException ex)
        {
            // InputSender.Send ném cái này khi SendInput không lọt — thường là game chạy quyền
            // Admin còn app thì không. Thông điệp của nó đã nói rõ cách sửa, đừng nuốt mất.
            reason = MinerStopReason.InputFailed;
            message = ex.Message;
            Emit(message);
        }
        catch (Exception ex)
        {
            reason = MinerStopReason.InputFailed;
            message = ex.Message;
            Emit("lỗi: " + message);
        }
        finally
        {
            ReleaseHeld();
            HeldKeys.ReleaseAll();
            Stopped?.Invoke(reason, message);
        }
    }

    /// <summary>
    /// Giữ W (+ Shift). Bắn lại sau mỗi <see cref="KeepAliveMs"/> chứ không chỉ một lần lúc đầu:
    /// game hay bỏ rơi phím giữ lâu, và một cú KeyDown lặp lại thì vô hại.
    /// </summary>
    private void EnsureHeld()
    {
        long now = Environment.TickCount64;
        if (_held && now - _lastPing < KeepAliveMs) return;

        InputSender.KeyDown(HeldKeys.VK_W);
        if (_cfg.HoldShift) InputSender.ShiftDown();
        _held = true;
        _lastPing = now;
    }

    private void ReleaseHeld()
    {
        if (!_held) return;
        try { InputSender.KeyUp(HeldKeys.VK_W); } catch { }
        try { InputSender.ShiftUp(); } catch { }
        _held = false;
    }

    /// <summary>
    /// Khác WaitWindow của các bot khác: ở đây KHÔNG chặn chờ, vì đang giữ phím — phải quay về
    /// vòng lặp để nhả W/Shift rồi mới ngồi đợi. Chỉ log một lần mỗi lần đổi trạng thái.
    /// </summary>
    private bool GameForeground()
    {
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) return true;

        var title = Native.ForegroundTitle();
        if (title.Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase))
        {
            if (_windowWarned)
            {
                Emit("game đã focus lại — chạy tiếp");
                _windowWarned = false;
            }
            return true;
        }

        if (!_windowWarned)
        {
            Emit($"tạm nhả phím: chưa focus “{_cfg.WindowMatch}” (đang focus: “{title}”)");
            _windowWarned = true;
        }
        return false;
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms))
            throw new OperationCanceledException();
    }

    private void Emit(string line) => Log?.Invoke(line);
}
