using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum FishingStopReason
{
    UserStopped,
    MissingRegions,
    Error
}

/// <summary>
/// Vòng câu: Tap 4 + Space → chờ cá cắn → giữ S → nhả khi đầy / HUD tắt → 4 lại.
/// FailNotice chỉ recast khi HUD đóng (ô thông báo dễ false-positive lúc đang kéo).
/// </summary>
internal sealed class FishingBot
{
    private const ushort VK_4 = 0x34;
    private const ushort VK_S = 0x53;
    private const ushort VK_SPACE = 0x20;

    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;
    private CancellationTokenSource _cts;
    private Thread _thread;
    private bool _holdingS;
    private bool _windowWarned;

    public FishingBot(FishingConfig cfg, Screen screen, FishingProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
    }

    public bool Running => _thread is { IsAlive: true };

    public event Action<string> Log;
    public event Action<FishingSnapshot> SnapshotReady;
    public event Action<FishingStopReason, string> Stopped;

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "FishingBot" };
        _thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>
    /// Huỷ rồi CHỜ luồng bot chết hẳn. <see cref="Stop"/> chỉ báo CTS và trả về ngay, nên nếu
    /// người gọi nhả phím ngay sau đó thì luồng bot còn sống vẫn kịp bấm lại — phím kẹt xuống
    /// dù panel đã báo "đã dừng". Hết thời gian chờ thì thôi, không treo UI.
    /// </summary>
    public void StopAndWait(int ms = 1500)
    {
        _cts?.Cancel();
        var t = _thread;
        if (t is null || !t.IsAlive) return;
        try { t.Join(ms); } catch { }
    }

    public static string TenLyDo(FishingStopReason r) => r switch
    {
        FishingStopReason.UserStopped => "người dùng bấm dừng",
        FishingStopReason.MissingRegions => "chưa khoanh thanh / cá",
        _ => "lỗi"
    };

    private void Run(CancellationToken ct)
    {
        var reason = FishingStopReason.UserStopped;
        string message = "người dùng bấm dừng";

        try
        {
            if (_profile is null || !_profile.Bar.IsSet || !_profile.Fish.IsSet)
                throw new InvalidOperationException("cần khoanh thanh và cá trước khi chạy");

            using var reader = new FishingReader(_cfg, _screen, _profile);
            if (reader.FishTemplateProblem is { } fp)
                throw new InvalidOperationException("mẫu cá: " + fp);

            if (!_profile.Reject.IsSet)
                Emit("cảnh báo: chưa khoanh thông báo — recast chỉ theo timeout");
            else if (reader.RejectTemplateProblem is { } rp)
                Emit("cảnh báo: mẫu thông báo — recast chỉ theo timeout (" + rp + ")");

            if (!_profile.Keep.IsSet)
                Emit("cảnh báo: chưa khoanh CẤT VÀO — sau khi câu được sẽ chỉ bấm 4, không nhận cá");
            else if (reader.KeepTemplateProblem is { } kp)
                Emit("cảnh báo: CẤT VÀO — sẽ click ô cố định, không dò được (" + kp + ")");
            else
                Emit($"dò CẤT VÀO trong vùng {reader.KeepBandRegion.Width}×{reader.KeepBandRegion.Height} " +
                     $"@ {reader.KeepBandRegion.X},{reader.KeepBandRegion.Y}, màu nền nút " +
                     $"#{reader.KeepColor.R:X2}{reader.KeepColor.G:X2}{reader.KeepColor.B:X2} ±{_cfg.KeepColorTol}");

            Emit($"bắt đầu. chờ cắn {_cfg.WaitBiteMs} ms, giữ S tối đa {_cfg.FightTimeoutMs} ms, " +
                 $"xong khi fill ≥ {_cfg.DoneFill01:0.00}");
            Emit($"{HotkeyText.Job()} = bật/tắt. Cửa sổ game phải đang focus (" + _cfg.WindowMatch + ").");
            Emit($"mỗi lần 4 sẽ bấm Space sau {_cfg.CastSpaceDelayMs} ms — tắt hotkey 4 trong AutoHotkey.");

            Cast(ct, "thả câu");

            int biteFrames = 0;
            bool fighting = false;
            bool sawHud = false;
            var waitSw = Stopwatch.StartNew();
            var fightSw = new Stopwatch();
            var ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                WaitWindow(ct);

                var snap = reader.Read();
                SnapshotReady?.Invoke(snap);

                if (!fighting)
                {
                    if (snap.FishBite) biteFrames++;
                    else biteFrames = 0;

                    if (biteFrames >= _cfg.BiteDebounceFrames)
                    {
                        Emit($"cá cắn (ncc={snap.FishScore:F3}) — giữ S");
                        HoldS();
                        fighting = true;
                        sawHud = snap.UiOpen;
                        fightSw.Restart();
                        continue;
                    }

                    bool rejectOk = snap.FailNotice && !snap.UiOpen && DateTime.UtcNow >= ignoreFailUntil;
                    if (rejectOk)
                    {
                        Emit($"chê mồi (ncc={snap.RejectScore:F3}, HUD đóng) — câu lại");
                        Sleep(ct, _cfg.RejectRecastMs);
                        Cast(ct, "câu lại", waitRelease: false);
                        biteFrames = 0;
                        waitSw.Restart();
                        ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                        continue;
                    }

                    if (waitSw.ElapsedMilliseconds >= _cfg.WaitBiteMs)
                    {
                        Emit($"hết {_cfg.WaitBiteMs} ms không cắn — câu lại");
                        Cast(ct, "câu lại (timeout)");
                        biteFrames = 0;
                        waitSw.Restart();
                        ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                    }
                }
                else
                {
                    if (snap.UiOpen) sawHud = true;

                    bool full = snap.BlueFill01 >= _cfg.DoneFill01;
                    bool hudGone = sawHud && !snap.UiOpen;
                    if (full || hudGone)
                    {
                        Emit(full
                            ? $"xong — fill {snap.BlueFill01 * 100:0.0}%"
                            : "xong — HUD tắt");
                        CollectThenCast(reader, ct);
                        fighting = false;
                        sawHud = false;
                        biteFrames = 0;
                        waitSw.Restart();
                        ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                        continue;
                    }

                    if (fightSw.ElapsedMilliseconds >= _cfg.FightTimeoutMs)
                    {
                        Emit($"giữ S quá {_cfg.FightTimeoutMs} ms — nhả và câu lại");
                        Cast(ct, "câu lại (timeout kéo)");
                        fighting = false;
                        sawHud = false;
                        biteFrames = 0;
                        waitSw.Restart();
                        ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                    }
                }

                Sleep(ct, _cfg.PollMs);
            }
        }
        catch (OperationCanceledException)
        {
            reason = FishingStopReason.UserStopped;
            message = "người dùng bấm dừng";
        }
        catch (InvalidOperationException ex)
        {
            reason = FishingStopReason.MissingRegions;
            message = ex.Message;
            Emit(message);
        }
        catch (Exception ex)
        {
            reason = FishingStopReason.Error;
            message = ex.Message;
            Emit("lỗi: " + ex.Message);
        }
        finally
        {
            ReleaseS();
            HeldKeys.ReleaseAll();
            Stopped?.Invoke(reason, message);
        }
    }

    private void CollectThenCast(FishingReader reader, CancellationToken ct)
    {
        ReleaseS();
        Sleep(ct, _cfg.KeepAppearMs);

        if (!_profile.Keep.IsSet)
        {
            Cast(ct, "thả câu", waitRelease: false);
            return;
        }

        var found = WaitForKeep(reader, ct);
        if (found is null)
        {
            // Không dò được: về cách cũ, click ô đã khoanh. Đúng với con cá tên ngắn.
            var abs = FishingConfig.ToAbsolute(_screen, _profile.Keep);
            Emit($"không dò được nút trong {_cfg.WaitKeepMs} ms — click ô đã khoanh");
            ClickKeep(new Point(abs.Left + abs.Width / 2, abs.Top + abs.Height / 2), ct);
        }
        else
        {
            Emit($"thấy nút {found.KeepRect.Width}×{found.KeepRect.Height} @ {found.KeepRect.X},{found.KeepRect.Y}" +
                 $"  dens={found.KeepDensity:F2}  ncc={found.KeepScore:F3}");
            ClickKeep(found.KeepClick, ct);

            for (int i = 0; i < _cfg.KeepClickRetries; i++)
            {
                var still = WaitForKeepGone(reader, ct);
                if (still is null) break;
                Emit($"nút vẫn còn sau {_cfg.KeepGoneMs} ms — click lại (lần {i + 1}/{_cfg.KeepClickRetries})");
                ClickKeep(still.KeepClick, ct);
            }
        }

        try { SnapshotReady?.Invoke(reader.Read()); } catch { }
        Cast(ct, "thả câu", waitRelease: false);
    }

    /// <summary>
    /// Chờ dò được nút, tối đa <see cref="FishingConfig.WaitKeepMs"/>. Null = không thấy.
    /// Panel hiện chậm hay nhanh tùy con cá nên không thể click theo một mốc thời gian cố định.
    /// </summary>
    private FishingSnapshot WaitForKeep(FishingReader reader, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            WaitWindow(ct);

            var snap = reader.Read();
            SnapshotReady?.Invoke(snap);

            if (snap.KeepVisible) return snap;
            if (!snap.KeepConfigured) return null;      // thiếu mẫu/vùng — poll thêm cũng vô ích
            if (sw.ElapsedMilliseconds >= _cfg.WaitKeepMs) return null;

            Sleep(ct, _cfg.PollMs);
        }
    }

    /// <summary>
    /// Chờ nút tắt sau khi click, tối đa <see cref="FishingConfig.KeepGoneMs"/>.
    /// Null = đã tắt; khác null = vẫn còn, kèm toạ độ mới để click lại.
    /// </summary>
    private FishingSnapshot WaitForKeepGone(FishingReader reader, CancellationToken ct)
    {
        if (_cfg.KeepGoneMs <= 0) return null;

        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var snap = reader.Read();
            SnapshotReady?.Invoke(snap);

            if (!snap.KeepVisible) return null;
            if (sw.ElapsedMilliseconds >= _cfg.KeepGoneMs) return snap;

            Sleep(ct, _cfg.PollMs);
        }
    }

    private void ClickKeep(Point p, CancellationToken ct)
    {
        WaitWindow(ct);
        Emit($"click CẤT VÀO @ {p.X},{p.Y}");
        InputSender.MoveSmooth(p.X, p.Y, _cfg.KeepMoveSteps);
        Sleep(ct, _cfg.KeepHoverMs);
        InputSender.LeftDown();
        Sleep(ct, 60);
        InputSender.LeftUp();
    }

    private void Cast(CancellationToken ct, string why, bool waitRelease = true)
    {
        ReleaseS();
        if (waitRelease)
            Sleep(ct, _cfg.AfterReleaseMs);
        WaitWindow(ct);
        InputSender.TapKey(VK_4);
        Sleep(ct, _cfg.CastSpaceDelayMs);
        InputSender.TapKey(VK_SPACE);
        Emit("bấm 4 + space — " + why);
    }

    private void HoldS()
    {
        if (_holdingS) return;
        InputSender.KeyDown(VK_S);
        _holdingS = true;
    }

    private void ReleaseS()
    {
        if (!_holdingS) return;
        try { InputSender.KeyUp(VK_S); } catch { }
        _holdingS = false;
    }

    private void WaitWindow(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) return;
        while (!ct.IsCancellationRequested)
        {
            var title = Native.ForegroundTitle();
            if (title.Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase))
            {
                _windowWarned = false;
                return;
            }
            if (!_windowWarned)
            {
                Emit($"chờ cửa sổ “{_cfg.WindowMatch}” (đang focus: “{title}”) — click vào game");
                _windowWarned = true;
            }
            Sleep(ct, 200);
        }
        ct.ThrowIfCancellationRequested();
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms))
            throw new OperationCanceledException();
    }

    private void Emit(string line) => Log?.Invoke(line);
}
