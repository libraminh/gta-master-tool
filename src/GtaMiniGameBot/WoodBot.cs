using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum WoodStopReason
{
    UserStopped,

    /// <summary>Không thấy prompt nào: cây hết gỗ, hoặc đứng sai chỗ.</summary>
    NoPrompt,

    InputFailed,
    Error
}

/// <summary>
/// Job Thợ mộc: đứng cạnh cây, thấy "KHAI THÁC" thì bấm E, chặt xong prompt tự hiện lại thì bấm
/// tiếp.
///
/// Chỉ cần MỘT tín hiệu: prompt có hay không. Lúc đang chặt, dòng chữ đổi thành "ĐANG KHAI THÁC"
/// và mẫu neo trái không còn khớp — tức "không thấy prompt" đã bao trọn nghĩa "đang bận". Cái giá
/// của việc bỏ mẫu thứ hai: bot không phân biệt được ĐANG CHẶT với HẾT CÂY ngay lập tức, cả hai
/// đều là không thấy prompt, nên nó phải chờ hết <see cref="WoodConfig.MaxChopMs"/> mới dám báo.
///
/// Khác <see cref="MinerBot"/> ở hai chỗ:
///   1. KHÔNG giữ W/Shift — thợ mộc đứng một chỗ, không có phím nào bị giữ nên chờ focus được
///      phép CHẶN (như <see cref="FishingBot"/>) thay vì phải quay về vòng lặp để nhả phím.
///   2. Bấm E theo TÍN HIỆU chứ không theo nhịp.
///
/// Chưa hiệu chuẩn thì hạ xuống đúng hành vi gõ mù của <see cref="MinerBot"/>: tab vẫn dùng được
/// ngay, chỉ kém. Từ chối chạy vì thiếu ROI là thứ người dùng không sửa được lúc đang trong game.
/// </summary>
internal sealed class WoodBot
{
    private const ushort VK_E = 0x45;

    private readonly WoodConfig _cfg;
    private readonly Screen _screen;
    private readonly WoodProfile _profile;

    private CancellationTokenSource _cts;
    private Thread _thread;
    private bool _windowWarned;
    private long _lastBlindTap;
    private int _chops;

    public WoodBot(WoodConfig cfg, Screen screen, WoodProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
    }

    public bool Running => _thread is { IsAlive: true };

    /// <summary>Số nhát chặt đã hoàn thành trong phiên này.</summary>
    public int Chops => _chops;

    public event Action<string> Log;
    public event Action<WoodSnapshot> SnapshotReady;
    public event Action<int> ChopsChanged;
    public event Action<WoodStopReason, string> Stopped;

    public void Start()
    {
        if (Running) return;
        _chops = 0;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "WoodBot" };
        _thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>
    /// Huỷ rồi CHỜ luồng chết hẳn. <see cref="Stop"/> chỉ báo CTS và trả về ngay, nên người gọi
    /// nhả phím ngay sau đó vẫn có thể bị luồng còn sống bấm chồng lên.
    /// </summary>
    public void StopAndWait(int ms = 1500)
    {
        _cts?.Cancel();
        var t = _thread;
        if (t is null || !t.IsAlive) return;
        try { t.Join(ms); } catch { }
    }

    public static string TenLyDo(WoodStopReason r) => r switch
    {
        WoodStopReason.UserStopped => "người dùng bấm dừng",
        WoodStopReason.NoPrompt => "không thấy prompt khai thác (cây hết gỗ hoặc đứng sai chỗ)",
        WoodStopReason.InputFailed => "không gửi được phím vào game",
        _ => "lỗi"
    };

    // ---------------------------------------------------------------- vong lap

    private void Run(CancellationToken ct)
    {
        var reason = WoodStopReason.UserStopped;
        string message = "người dùng bấm dừng";
        WoodReader reader = null;

        try
        {
            reader = WoodReader.Open(_cfg, _screen, _profile);
            if (reader.Configured)
                Emit($"bắt đầu. vùng quét {reader.BandRegion.Width}×{reader.BandRegion.Height}, " +
                     $"ngưỡng {_cfg.NccMin:F2}.");
            else
                Emit($"CHƯA KHOANH VÙNG ({reader.Problem}) — chạy chế độ gõ mù: " +
                     $"bấm E mỗi {_cfg.TapEveryMs} ms. Bấm “Khoanh vùng HUD…” để bot đọc được HUD.");

            Emit($"{HotkeyText.Job()} = bật/tắt. Cửa sổ game phải đang focus ({_cfg.WindowMatch}).");

            bool chopping = false;               // đã bấm E, đang chờ prompt hiện lại
            var awaySw = Stopwatch.StartNew();   // từ lần cuối THẤY prompt
            var blindSw = new Stopwatch();       // từ cú bấm E gần nhất

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (WaitWindow(ct))
                {
                    // Vua alt-tab ve: moc thoi gian cu vo nghia, dung de no bao "het cay".
                    awaySw.Restart();
                    blindSw.Reset();
                    chopping = false;
                }

                var snap = reader.Read();
                SnapshotReady?.Invoke(snap);

                if (!snap.Configured)
                {
                    BlindTap();
                    Sleep(ct, _cfg.PollMs);
                    continue;
                }

                if (snap.Ready)
                {
                    awaySw.Restart();

                    // Vua bam xong thi lam ngo: HUD mat mot nhip moi doi sang "DANG KHAI THAC",
                    // khong chan thi bot thay prompt cu con do va bam them phat nua.
                    if (blindSw.IsRunning && blindSw.ElapsedMilliseconds < _cfg.AfterTapBlindMs)
                    {
                        Sleep(ct, _cfg.PollMs);
                        continue;
                    }

                    Tap(ct);
                    blindSw.Restart();
                    chopping = true;

                    _chops++;
                    ChopsChanged?.Invoke(_chops);
                    Emit($"bấm E — nhát #{_chops}");

                    Sleep(ct, _cfg.PollMs);
                    continue;
                }

                // Khong thay prompt: hoac dang chat (chu doi thanh "DANG KHAI THAC"), hoac het cay
                // / dung sai cho. Phan biet bang thoi gian — day dung la cai gia cua viec chi giu
                // mot mau.
                long limit = chopping ? _cfg.MaxChopMs : _cfg.NoPromptMs;
                if (awaySw.ElapsedMilliseconds >= limit)
                {
                    reason = WoodStopReason.NoPrompt;
                    message = chopping
                        ? $"bấm E xong nhưng {limit / 1000}s prompt không hiện lại — cây hết gỗ?"
                        : $"{limit / 1000}s không thấy prompt khai thác " +
                          $"({snap.LineCount} dòng chữ, ncc cao nhất {snap.Score:F2})";
                    Emit(message);
                    return;
                }

                Sleep(ct, _cfg.PollMs);
            }
        }
        catch (OperationCanceledException)
        {
            reason = WoodStopReason.UserStopped;
            message = "người dùng bấm dừng";
        }
        catch (InvalidOperationException ex)
        {
            // InputSender.Send nem cai nay khi SendInput khong lot — thuong la game chay quyen
            // Admin con app thi khong. Thong diep cua no da noi ro cach sua, dung nuot mat.
            reason = WoodStopReason.InputFailed;
            message = ex.Message;
            Emit(message);
        }
        catch (Exception ex)
        {
            reason = WoodStopReason.Error;
            message = ex.Message;
            Emit("lỗi: " + message);
        }
        finally
        {
            reader?.Dispose();
            HeldKeys.ReleaseAll();
            Stopped?.Invoke(reason, message);
        }
    }

    // ---------------------------------------------------------------- phim

    private void Tap(CancellationToken ct)
    {
        WaitWindow(ct);
        InputSender.TapKey(VK_E, _cfg.TapHoldMs);
    }

    /// <summary>Chế độ chưa hiệu chuẩn: gõ E theo nhịp, đúng như job Thợ mỏ đang làm.</summary>
    private void BlindTap()
    {
        long now = Environment.TickCount64;
        if (_lastBlindTap != 0 && now - _lastBlindTap < _cfg.TapEveryMs) return;

        // Ghi moc TRUOC khi bam: TapKey giu luong het TapHoldMs, lay moc sau thi nhip that =
        // TapEveryMs + TapHoldMs.
        _lastBlindTap = now;
        InputSender.TapKey(VK_E, _cfg.TapHoldMs);
    }

    /// <summary>
    /// Chặn tới khi game là cửa sổ foreground. Trả về true nếu đã PHẢI chờ — người gọi dùng nó để
    /// reset các mốc thời gian, vì thời gian alt-tab không phải thời gian bot không thấy cây.
    /// </summary>
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
        if (ct.WaitHandle.WaitOne(ms))
            throw new OperationCanceledException();
    }

    private void Emit(string line) => Log?.Invoke(line);
}
