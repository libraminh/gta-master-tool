using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum MinerStopReason
{
    UserStopped,
    InputFailed
}

/// <summary>Số liệu một phiên cày, panel hiện lên và ghi log.</summary>
internal sealed class MinerStats
{
    public int Mined { get; init; }
    public int Trips { get; init; }
    public int LastMineMs { get; init; }
}

/// <summary>
/// Job Thợ mỏ: giữ W + Left Shift để chạy tới, bấm E đều đặn, và — khi đã khoanh vùng HUD —
/// đọc màn hình để biết mình đang ở pha nào.
///
/// Không dùng SendInput một phát rồi ngủ trọn nhịp E: vòng lặp phải tick nhanh để (1) ping lại
/// W/Shift trước khi game bỏ rơi phím giữ lâu, (2) nhả phím ngay khi người chơi alt-tab, và
/// (3) đọc HUD đủ dày để không bỏ lỡ toast tiền chỉ sống vài giây. Đây đúng là hình dạng tick
/// của <see cref="UtilityService"/>, chỉ khác là đặt trên một luồng huỷ được thay vì Timer, để
/// khớp khuôn bot của các job khác (Start/Stop/StopAndWait/Stopped).
///
/// Chưa khoanh vùng thì bot chạy y hệt bản đầu — giữ phím và gõ E mù. Đó là chủ ý: người dùng
/// phải cày được ngay từ trước khi ngồi hiệu chỉnh.
/// </summary>
internal sealed class MinerBot
{
    private const ushort VK_E = 0x45;

    /// <summary>Ping lại phím giữ sau ngần này ms — cùng con số đã dùng ở UtilityService.</summary>
    private const int KeepAliveMs = 400;

    private readonly MinerConfig _cfg;
    private readonly Screen _screen;
    private readonly MinerProfile _profile;

    private CancellationTokenSource _cts;
    private Thread _thread;

    private bool _held;
    private long _lastPing;
    private long _lastTap;
    private long _lastLift;
    private bool _windowWarned;

    private bool _wasMining;
    private bool _cashLatched;
    private readonly Stopwatch _mineSw = new();
    private int _mined;
    private int _trips;
    private int _lastMineMs;

    public MinerBot(MinerConfig cfg, Screen screen, MinerProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
    }

    public bool Running => _thread is { IsAlive: true };

    public event Action<string> Log;
    public event Action<MinerSnapshot> SnapshotReady;
    public event Action<MinerStats> StatsChanged;
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
        MinerReader reader = null;

        try
        {
            reader = new MinerReader(_cfg, _screen, _profile);
            ReportSetup(reader);

            Emit($"bắt đầu. {(_cfg.HoldRun ? "giữ W" + (_cfg.HoldShift ? " + Left Shift" : "") : "KHÔNG giữ W — bạn tự lái")}, " +
                 $"bấm E mỗi {_cfg.TapEveryMs} ms (giữ {_cfg.TapHoldMs} ms).");
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

                var snap = reader.Read();
                SnapshotReady?.Invoke(snap);

                if (HandleMining(snap)) continue;
                HandleCash(snap);
                if (HandleLift(snap)) continue;

                if (_cfg.HoldRun) EnsureHeld();
                else ReleaseHeld();

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
            reader?.Dispose();
            Emit($"tổng kết: {_mined} lượt đào, {_trips} chuyến giao.");
            Stopped?.Invoke(reason, message);
        }
    }

    private void ReportSetup(MinerReader reader)
    {
        foreach (var p in new[] { reader.MiningProblem, reader.LiftProblem, reader.CashProblem })
            if (p is not null) Emit("cảnh báo: " + p);

        if (reader.MiningProblem is not null)
            Emit("chưa đọc được ô đào — sẽ gõ E mù suốt, kể cả trong lúc đang đào. " +
                 "Bấm “Khoanh vùng HUD” để sửa.");
    }

    /// <summary>
    /// Đang đào thì phải ĐỨNG YÊN và ngừng gõ E: gõ thêm chỉ tổ huỷ tiến trình hoặc mở nhầm
    /// thứ khác, còn giữ W thì đi lệch khỏi cục quặng giữa chừng.
    /// Trả true nghĩa là vòng này xử lý xong, đừng làm gì nữa.
    /// </summary>
    private bool HandleMining(MinerSnapshot snap)
    {
        if (!snap.MiningConfigured) return false;

        if (snap.Mining)
        {
            ReleaseHeld();
            if (!_wasMining)
            {
                _wasMining = true;
                _mineSw.Restart();
                Emit("đang đào — đứng yên, ngừng gõ E");
            }
            return true;
        }

        if (!_wasMining) return false;

        _wasMining = false;
        _mineSw.Stop();
        _lastMineMs = (int)_mineSw.ElapsedMilliseconds;
        _mined++;
        // Nhip E lui lai mot nhip de cu E dau tien sau khi dao khong ban ra giua animation.
        _lastTap = Environment.TickCount64;
        Emit($"đào xong sau {_lastMineMs} ms — lượt {_mined}");
        RaiseStats();
        return true;
    }

    /// <summary>
    /// Toast tiền sống vài giây nên sẽ đọc ra true nhiều vòng liền; chốt cạnh lên để mỗi lần
    /// hiện chỉ đếm một chuyến.
    /// </summary>
    private void HandleCash(MinerSnapshot snap)
    {
        if (!snap.CashConfigured) return;

        if (!snap.CashToast) { _cashLatched = false; return; }
        if (_cashLatched) return;

        _cashLatched = true;
        _trips++;
        Emit($"giao hàng xong — chuyến {_trips}");
        RaiseStats();
    }

    /// <summary>
    /// Đứng đúng giếng thang thì bấm E một lần rồi khoá lại: bấm phát thứ hai lúc màn hình còn
    /// đen là gọi thang đi ngược xuống. Trả true để vòng này không gõ E theo nhịp nữa.
    /// </summary>
    private bool HandleLift(MinerSnapshot snap)
    {
        if (!snap.LiftConfigured || !snap.LiftPrompt) return false;

        long now = Environment.TickCount64;
        if (now - _lastLift < _cfg.LiftCooldownMs) return true;

        ReleaseHeld();
        _lastLift = now;
        _lastTap = now;
        InputSender.TapKey(VK_E, _cfg.TapHoldMs);
        Emit("thấy gợi ý thang máy — bấm E");
        return true;
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

    private void RaiseStats() =>
        StatsChanged?.Invoke(new MinerStats { Mined = _mined, Trips = _trips, LastMineMs = _lastMineMs });

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms))
            throw new OperationCanceledException();
    }

    private void Emit(string line) => Log?.Invoke(line);
}
