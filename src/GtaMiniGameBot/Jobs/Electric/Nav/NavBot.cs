using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum NavStopReason
{
    /// <summary>Đã bấm E và minigame đã hiện — điều phối giao cho bộ giải.</summary>
    Arrived,

    UserStopped,
    InputFailed,
    Error
}

/// <summary>
/// Tự đi tới điểm làm việc của nghề Thợ điện rồi bấm E — port vòng lặp chính của Navigator Python
/// CAROT2 V6.7.34 (<c>V3Phase2App.loop</c> và các máy trạng thái vòng đời quanh nó).
///
/// Khác mọi bot khác trong repo, bot này có HAI luồng phụ: luồng chuột 240 Hz trong <see cref="NavInput"/>
/// (camera xoay theo tốc độ, không theo delta từng khung) và luồng quét khung nhìn thế giới trong
/// <see cref="NavCapture"/> (chụp cả màn 2K quá chậm cho nhịp 25 ms của luồng chính). Cả hai đều là
/// background thread và được dừng theo thứ tự cố định trong <c>finally</c> để không phím nào còn xuống.
///
/// Bản Python không bao giờ tự bỏ cuộc: mất điểm vàng thì quét 360°, lâu quá thì đi reset nghề, 30 s
/// không tiến thì khởi động lại mềm. Ở đây cũng vậy — lý do dừng chỉ có "tới nơi", "người dùng dừng",
/// "không gửi được input", "lỗi".
/// </summary>
internal sealed class NavBot
{
    private readonly ElectricConfig _cfg;
    private readonly Screen _screen;
    private readonly ElectricProfile _profile;

    private CancellationTokenSource _cts;
    private Thread _thread;

    /// <summary>Vừa giải xong một minigame — vào thẳng hậu minigame (kiểm prompt, reset camera, W reclaim).</summary>
    public bool AfterMinigame { get; init; }

    /// <summary>Bảng/panel đã biến mất bao lâu trước khi bot này bắt đầu (BoardBot báo Solved sau 3 s, WireBot 1.5 s).</summary>
    public int PanelGoneAgoMs { get; init; }

    /// <summary>Do <see cref="ElectricBot"/> cấp: minigame có đang hiện không. CHỈ gọi từ luồng nav chính.</summary>
    public Func<bool> PanelVisible { get; set; }

    public bool Running => _thread is { IsAlive: true };

    public event Action<string> Log;
    public event Action<NavStopReason, string> Stopped;

    /// <summary>
    /// Chuyện đáng báo ra ngoài game (tiêu đề, chi tiết) — hiện chỉ có "hết bánh/nước trong túi".
    /// <see cref="ElectricBot"/> bắt và đẩy sang Discord; giữ kiến thức Discord ngoài bộ điều hướng.
    /// </summary>
    public event Action<string, string> Alert;

    public NavBot(ElectricConfig cfg, Screen screen, ElectricProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "NavBot" };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _input?.StopMouseStream(immediate: true);     // chuot phai tat NGAY, khong cho join
    }

    public void StopAndWait(int ms = 2500)
    {
        Stop();
        var t = _thread;
        if (t is null || !t.IsAlive) return;
        try { t.Join(ms); } catch { }
    }

    public static string TenLyDo(NavStopReason r) => r switch
    {
        NavStopReason.Arrived => "đã tới điểm làm việc và mở minigame",
        NavStopReason.UserStopped => "người dùng bấm dừng",
        NavStopReason.InputFailed => "không gửi được phím/chuột vào game",
        _ => "lỗi"
    };

    // ================================================================ trang thai

    private NavScale _s;
    private NavInput _input;
    private NavCapture _capture;
    private NavController _ctl;
    private DotTracker _tracker;
    private NavWatchdog _watchdog;
    private JobRecovery _job;
    private double _originX, _originY;

    // focus
    private double _focusLastGood;
    private bool _focusedLast = true;

    // simple flow
    private string _simplePhase = "WORLD";
    private int _promptStreak, _promptAbsentStreak;
    private int _promptSeq = -1;
    private bool _promptConsumed;
    private double _closeUntil, _postCheckUntil, _wait10Until;
    private double _recentBoardExitUntil;
    private bool _eDown;
    private double _eUpAt;
    private double _lastPanelPoll;
    private bool _arrived;

    // E song song voi dieu huong: SETTLE ngan roi WATCH (lai + tham do panel).
    private string _ixPhase;
    private double _ixSettleUntil, _ixWatchUntil, _eRetryUntil;
    private string _lastCtlState;
    private double _lastPromptSkipLog;

    // khien bo E cua NPC sau khi xin viec
    private bool _postJobIgnoreNpcE;
    private double _postJobStarted;
    private bool _postJobSeen;
    private int _postJobAbsentFrames;
    private double _postJobLastLog;

    // camera reset
    private string _cameraPhase;
    private double _cameraPhaseStart;
    private string _cameraReason;

    // W reclaim
    private bool _wReclaimPending;
    private int _wReclaimStage;
    private double _wReclaimAt, _wReclaimConfirmAt;

    // an uong
    private SurvivalGauge _gauge;
    private bool _survivalActive;
    private string _survivalPhase;
    private double _survivalPhaseStart;
    private readonly Queue<SurvivalItem> _survivalQueue = new();
    private SurvivalItem _survivalCurrent;
    private double _survivalFoodBlockUntil, _survivalWaterBlockUntil;
    private bool _survivalKeyDown;
    private double _survivalKeyUpAt;
    private ushort _survivalKeyVk;
    private double _survivalTickT;

    // autorun watchdog
    private double _wdLastProgressT;
    private double? _wdBestDist;
    private (double a, double h, double y)? _wdAnchorWorld;
    private int _wdRestartCount;

    // watch 30 s sau minigame
    private bool _watchActive, _watchPending, _backoutActive;
    private double _watchStarted, _backoutUntil;
    private int _watchCount;

    // lop san phim
    private double _lastShiftKeepaliveT;

    // log
    private double _lastStatusLog;
    private readonly Stopwatch _tickSw = new();
    private double _tickMsEma;
    private int _grabFails;

    private void Emit(string line) => Log?.Invoke(line);

    // ================================================================ vong doi

    private void Run(CancellationToken ct)
    {
        var reason = NavStopReason.UserStopped;
        string message = TenLyDo(NavStopReason.UserStopped);
        bool timer = false;

        try
        {
            var b = _screen.Bounds;
            _s = new NavScale(b.Width, b.Height, _cfg.Nav.ScreenPxScale);
            _originX = (_cfg.Nav.PlayerOriginXRef > 0 ? _cfg.Nav.PlayerOriginXRef : NavTuning.PlayerOriginXRef) * _s.Sx;
            _originY = (_cfg.Nav.PlayerOriginYRef > 0 ? _cfg.Nav.PlayerOriginYRef : NavTuning.PlayerOriginYRef) * _s.Sy;

            timer = Native.timeBeginPeriod(1) == 0;

            _input = new NavInput(_cfg.Nav.MouseSpeedMultiplier);
            _capture = new NavCapture(_screen, _s);
            _ctl = new NavController(_s, _input);
            _ctl.Log += Emit;
            _tracker = new DotTracker(_s);
            _watchdog = new NavWatchdog(_s);
            _job = new JobRecovery(_s, _screen, _input, _ctl, _watchdog, _tracker, _capture, _originX, _originY);
            _job.Log += Emit;
            _job.Finished += OnJobFinished;
            _gauge = new SurvivalGauge(_cfg.Survival, _s);

            Emit($"điều hướng: màn {_s.ScreenW}×{_s.ScreenH}, sx={_s.Sx:F3}, px×{_s.Px:F3}, chuột ×{_cfg.Nav.MouseSpeedMultiplier:F1}, " +
                 $"gốc mũi tên ({_originX:F0},{_originY:F0}), minimap {_capture.MinimapRegion.Width}×{_capture.MinimapRegion.Height}, " +
                 $"world {_capture.WorldRegion.Width}×{_capture.WorldRegion.Height}" +
                 (timer ? "" : ", timeBeginPeriod THẤT BẠI"));

            double now0 = NavClock.Now;
            _focusLastGood = 0;
            _wdLastProgressT = now0;
            _lastShiftKeepaliveT = now0;

            if (_cfg.Survival.Enabled)
                Emit($"ăn uống: BẬT — bánh ô {_cfg.Survival.FoodSlots}, nước ô {_cfg.Survival.WaterSlots}, " +
                     $"dưới {NavTuning.SurvivalLowThresholdPct:F0}% thì đứng yên {NavTuning.SurvivalFixedWaitS:F0}s để dùng");

            if (AfterMinigame) EnterPostMinigame(now0);

            _capture.StartScanner();
            Loop(ct);

            if (_arrived)
            {
                reason = NavStopReason.Arrived;
                message = "minigame đã hiện";
            }
        }
        catch (OperationCanceledException)
        {
            reason = NavStopReason.UserStopped;
            message = TenLyDo(reason);
        }
        catch (InvalidOperationException ex)
        {
            reason = NavStopReason.InputFailed;
            message = ex.Message;
            Emit(message);
        }
        catch (Exception ex)
        {
            reason = NavStopReason.Error;
            message = ex.Message;
            Emit("lỗi điều hướng: " + ex);
        }
        finally
        {
            // Thu tu dung: chuot -> nha phim so huu -> nha toan bo -> luong quet -> reader -> timer.
            try { _input?.Dispose(); } catch { }
            try { _input?.ReleaseOwnedOnce(); } catch { }
            try { HeldKeys.ReleaseAll(); } catch { }
            try { _capture?.Dispose(); } catch { }
            if (timer) { try { Native.timeEndPeriod(1); } catch { } }
            Stopped?.Invoke(reason, message);
        }
    }

    /// <summary>
    /// Vào thẳng hậu minigame khi <see cref="ElectricBot"/> vừa giải xong. Bản Python vào CLOSE_SETTLE
    /// ~125 ms sau khi bảng mất; ở đây bảng đã mất <see cref="PanelGoneAgoMs"/> nên rút CLOSE_SETTLE và
    /// POST_CHECK tương ứng (tối thiểu vẫn nhìn prompt 0.5 s), và khiên 8 s tính từ lúc bảng mất.
    /// </summary>
    private void EnterPostMinigame(double now)
    {
        double gone = Math.Max(0.0, PanelGoneAgoMs / 1000.0);
        _promptStreak = _promptAbsentStreak = 0;
        _promptSeq = -1;
        _promptConsumed = false;
        _eRetryUntil = 0;
        ClearPendingE();
        _recentBoardExitUntil = now + Math.Max(0.0, NavTuning.SimpleRecentBoardExitGuardS - gone);
        double remaining = NavTuning.SimpleCloseSettleS + NavTuning.SimplePostCheckS - gone;
        if (gone < NavTuning.SimpleCloseSettleS)
        {
            _simplePhase = "CLOSE_SETTLE";
            _closeUntil = now + (NavTuning.SimpleCloseSettleS - gone);
        }
        else
        {
            _simplePhase = "POST_CHECK";
            _postCheckUntil = now + Math.Max(0.5, remaining);
        }
        Emit($"sau minigame (bảng mất {gone:F1}s trước) → {_simplePhase}: kiểm prompt rồi reset camera");
    }

    private void Loop(CancellationToken ct)
    {
        double period = NavTuning.TickMs / 1000.0;
        double next = NavClock.Now;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double now = NavClock.Now;
            if (now < next)
            {
                double rem = next - now;
                if (rem > 0.002) Thread.Sleep(1); else Thread.SpinWait(200);
                continue;
            }
            next = Math.Max(next + period, now);
            _tickSw.Restart();

            if (_input.Fault is not null) throw new InvalidOperationException("luồng chuột: " + _input.Fault.Message);
            if (_capture.Fault is not null) throw new Exception("luồng quét màn hình dừng: " + _capture.Fault.Message);

            bool focused = GameFocus(now);

            // GDI co the tu choi chup mot luc (khoa man, UAC, doi che do hien thi). Ban Python bo qua
            // khung None; o day nha phim, cho 250 ms, va chi bo cuoc sau 5 s lien tuc.
            NavFrame mini;
            try
            {
                mini = _capture.GrabMinimap(now);
                _grabFails = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_grabFails++ == 0) Emit("không chụp được minimap: " + ex.Message);
                if (_grabFails >= 20) throw new Exception("không chụp được màn hình 5 giây liên tục: " + ex.Message);
                _input.ReleaseOwnedOnce();
                Thread.Sleep(250);
                continue;
            }
            var snap = _capture.Latest;

            if (Tick(ct, now, focused, mini, snap)) return;    // Arrived
        }
    }

    /// <summary>Một vòng của <c>loop()</c>. Trả true khi đã tới nơi (kết thúc bot).</summary>
    private bool Tick(CancellationToken ct, double now, bool focused, NavFrame mini, WorldSnapshot snap)
    {
        // Backout S nghiem trong cua watch 30 s so huu input truoc moi thu.
        if (_backoutActive && RestartWatchStep(now, focused)) return false;

        // Bua an DANG DO dung ngay day, tren ca SimpleFlow — xem chu thich SurvivalOwnStep.
        if (SurvivalOwnStep(now, focused))
        {
            StatusLine(now, $"[ĂN UỐNG] pha={_survivalPhase} món={_survivalCurrent?.Name ?? "-"}", snap);
            return false;
        }

        // Reset nghe truoc simple flow: bang nghe la panel cyan lon.
        if (_job.Phase is not null)
        {
            if (_job.Step(mini, snap, now, focused))
            {
                StatusLine(now, $"[RESET NGHỀ] pha={_job.Phase} {_job.StateNote}", snap);
                return false;
            }
        }

        if (SimpleFlowStep(now, focused, snap, out bool arrived))
        {
            if (arrived) { _arrived = true; return true; }
            StatusLine(now, $"[PROMPT/E] pha={_simplePhase}", snap);
            return false;
        }

        if (PendingSettleStep(now, focused, out arrived))
        {
            if (arrived) { _arrived = true; return true; }
            StatusLine(now, $"[PROMPT/E] pha={_ixPhase}", snap);
            return false;
        }

        if (RestartWatchStep(now, focused)) return false;

        if (_wReclaimPending && _cameraPhase is null)
        {
            if (WReclaimStep(now, focused)) return false;
        }

        if (_cameraPhase is not null)
        {
            if (focused)
            {
                if (CameraResetStep(now))
                {
                    StatusLine(now, $"[RESET CAMERA] pha={_cameraPhase}", snap);
                    return false;
                }
            }
            else
            {
                _input.ReleaseAll();
                return false;
            }
        }

        // Mo bua moi: dung sau moi may trang thai tren va truoc dieu huong — cau "camera reset /
        // minigame > survival > navigation" cua ban Python.
        if (SurvivalMaybeStart(now, focused))
        {
            StatusLine(now, $"[ĂN UỐNG] pha={_survivalPhase} món={_survivalCurrent?.Name ?? "-"}", snap);
            return false;
        }

        if (PendingWatchPoll(now, focused, out arrived))
        {
            _arrived = true;
            return true;
        }

        // ---------------- nhan dang ----------------
        var candidates = YellowDotDetector.Detect(mini, _s, _originX, _originY);
        var fragments = YellowDotDetector.DetectNearFragments(mini, _s, _originX, _originY);
        var target = _tracker.Update(candidates, _originX, _originY, now, fragments);
        var world = snap.Marker;

        if (_job.ShouldStart(now, target, world, candidates.Count, _backoutActive))
        {
            _job.Start(now, "MẤT HẲN ĐIỂM VÀNG SAU SEARCH360");
            if (_job.Step(mini, snap, now, focused)) return false;
        }

        double dist = double.NaN, rel = double.NaN, dx = double.NaN, dy = double.NaN;
        if (target.HasPos)
        {
            dx = target.X.Value - _originX;
            dy = target.Y.Value - _originY;
            dist = Math.Sqrt(dx * dx + dy * dy);
            rel = NavController.Wrap(Math.Atan2(dx, -dy) * 180.0 / Math.PI);
            _watchdog.Add(now, dx, dy);
        }
        bool forwardRequested = _input.IsHeld(NavKey.W) && !_input.IsHeld(NavKey.S);

        if (TryArmWorldE(now, focused, snap, target, world, dist, rel))
        {
            StatusLine(now, $"[PROMPT/E] pha={_ixPhase ?? _simplePhase}", snap);
            return false;
        }

        // ---------------- ket ----------------
        bool isStuck = false;
        bool worldDirectCandidate = world.Present
                                    && world.Confidence >= NavTuning.WorldSkipMinimapStuckConf
                                    && world.Area >= NavTuning.WorldSkipMinimapStuckArea;
        if (worldDirectCandidate) _watchdog.ClearCandidate();
        else if (!double.IsNaN(dx))
        {
            bool targetAvailable = target.HasPos && target.Confidence >= NavTuning.ImpactMinTargetConf;
            isStuck = _watchdog.ImpactStuck(now, forwardRequested, targetAvailable, dist, rel);
        }
        if (isStuck)
        {
            int side = _capture.AnalyzeObstacleSide(now, out string note);
            _ctl.SetObstacleSide(side);
            Emit($"[KẸT XÁC NHẬN] bên={(side > 0 ? "PHẢI" : side < 0 ? "TRÁI" : "TỰ")} {note} dist={dist:F1} rel={rel:+0.0;-0.0}");
        }

        // ---------------- cong world ----------------
        bool strongWorld = world.Present && world.Confidence >= NavTuning.WorldInstantTakeoverConf
                                         && world.Area >= NavTuning.WorldInstantTakeoverMinArea;
        bool worldAllowed = false;
        if (strongWorld) worldAllowed = true;
        else if (world.Locked)
        {
            if (world.Confidence >= NavTuning.WorldStrongOverrideConf) worldAllowed = true;
            else if (!double.IsNaN(dist) && dist <= NavTuning.WorldLockMinimapMaxDistPx * _s.Px
                     && target.Confidence >= NavTuning.WorldRequireTargetConf) worldAllowed = true;
            else if (_ctl.WorldLatched) worldAllowed = true;
        }
        if (world.Present && worldAllowed) _ctl.ClearPendingObstacle();

        bool worldEscapeTakeover = world.Present && worldAllowed && _ctl.Active is not null && _ctl.Active.Source != "WORLD";
        bool centerMinimapReady = target.HasPos && double.IsFinite(dist) && double.IsFinite(rel)
                                  && target.Confidence >= NavTuning.CenterNavMinConf;
        bool lineCommitActive = _ctl.RamLineHardLocked && now - _ctl.RamLineLastSeenT <= NavTuning.RamLineWorldOverrideHoldS;

        if (!worldEscapeTakeover && AutorunWatchdogStep(now, focused, dist, target, world)) return false;

        // ---------------- dieu phoi ----------------
        NavKey keys;
        string state;
        if (!focused)
        {
            _input.ReleaseOwnedOnce();
            state = "WAIT_FOCUS";
            keys = NavKey.None;
        }
        else if (worldEscapeTakeover)
        {
            var r = _ctl.WorldStep(now, world, _s.ScreenW, false, dist) ?? (NavKey.W, "WORLD_KET_TAKEOVER_W");
            keys = ApplyWorldNavInput(r.keys, now, r.state);
            state = r.state;
        }
        else if (world.Present && worldAllowed && !centerMinimapReady && !lineCommitActive)
        {
            var r = _ctl.WorldStep(now, world, _s.ScreenW, false, dist)
                    ?? (double.IsNaN(rel) ? _ctl.LostStep(now) : _ctl.Compute(now, target, dist, rel, dx, dy, isStuck));
            keys = ApplyWorldNavInput(r.keys, now, r.state);
            state = r.state;
        }
        else if ((worldAllowed || _ctl.WorldLatched) && !centerMinimapReady && !lineCommitActive)
        {
            var r = _ctl.WorldStep(now, world, _s.ScreenW, isStuck, dist)
                    ?? (double.IsNaN(rel) ? _ctl.LostStep(now) : _ctl.Compute(now, target, dist, rel, dx, dy, isStuck));
            keys = ApplyWorldNavInput(r.keys, now, r.state);
            state = r.state;
            if (isStuck) _watchdog.Cooldown(now);
        }
        else if (double.IsNaN(rel))
        {
            var r = _ctl.LostStep(now);
            keys = ApplyWorldNavInput(r.keys, now, r.state);
            state = r.state;
        }
        else
        {
            var r = _ctl.Compute(now, target, dist, rel, dx, dy, isStuck);
            keys = ApplyWorldNavInput(r.keys, now, r.state);
            state = r.state;
            if (isStuck) _watchdog.Cooldown(now);
        }

        _lastCtlState = state;

        _tickMsEma = _tickMsEma <= 0 ? _tickSw.Elapsed.TotalMilliseconds : 0.9 * _tickMsEma + 0.1 * _tickSw.Elapsed.TotalMilliseconds;
        if (now - _lastStatusLog >= _cfg.Nav.LogEveryMs / 1000.0)
        {
            _lastStatusLog = now;
            string re = double.IsNaN(rel) ? "---" : $"{rel:+0.0;-0.0}";
            string ds = double.IsNaN(dist) ? "---" : $"{dist:0.0}";
            string wx = world.X is null ? "---" : $"{world.X.Value:0}";
            Emit($"[{state}] Q={target.Quality} C={target.Confidence:F2} rel={re} dist={ds} " +
                 $"WQ={world.Quality} WC={world.Confidence:F2} Wx={wx} A={world.Area:0} keys={KeysText(keys)} " +
                 $"tick={_tickMsEma:0.0}ms quét={snap.Hz:0}Hz");
        }
        return false;
    }

    private void StatusLine(double now, string head, WorldSnapshot snap)
    {
        if (now - _lastStatusLog < 0.5) return;
        _lastStatusLog = now;
        Emit($"{head} quét={snap.Hz:0}Hz");
    }

    private static string KeysText(NavKey k)
    {
        if (k == NavKey.None) return "-";
        var parts = new List<string>();
        if ((k & NavKey.Shift) != 0) parts.Add("SHIFT");
        if ((k & NavKey.W) != 0) parts.Add("W");
        if ((k & NavKey.S) != 0) parts.Add("S");
        if ((k & NavKey.A) != 0) parts.Add("A");
        if ((k & NavKey.D) != 0) parts.Add("D");
        return string.Join("+", parts);
    }

    // ================================================================ focus

    /// <summary><c>game_focus</c>: tiêu đề cửa sổ chứa WindowMatch; mất tiêu đề dưới 1.5 s vẫn coi là còn focus.</summary>
    private bool GameFocus(double now)
    {
        bool ok;
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) ok = true;
        else
        {
            string title = Native.ForegroundTitle();
            bool titleOk = title.Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase);
            if (titleOk) { _focusLastGood = now; ok = true; }
            else ok = _focusLastGood > 0 && now - _focusLastGood <= NavTuning.FocusGraceS;
        }
        if (ok != _focusedLast)
        {
            _focusedLast = ok;
            Emit(ok ? "game đã focus lại — chạy tiếp" : $"mất focus “{_cfg.WindowMatch}” — nhả phím, chờ");
        }
        return ok;
    }

    // ================================================================ lop san phim

    /// <summary>
    /// <c>_apply_world_nav_input</c>: luôn thêm W; ngoài KET1/KET2 luôn thêm W+SHIFT; SHIFT xuống TRƯỚC cú
    /// double-tap W khi vừa lấy lại W; sau đó SHIFT keydown-only mỗi 0.45 s. Trả tập phím THẬT đã gửi.
    /// </summary>
    private NavKey ApplyWorldNavInput(NavKey keys, double now, string state)
    {
        bool isKet = state.StartsWith("KET1", StringComparison.Ordinal) || state.StartsWith("KET2", StringComparison.Ordinal);
        bool explicitReverse = (keys & NavKey.S) != 0;

        if (!explicitReverse) keys |= NavKey.W;
        bool normalSprint = !isKet && !explicitReverse;
        if (normalSprint) keys |= NavKey.W | NavKey.Shift;

        if (normalSprint && !_input.IsHeld(NavKey.Shift))
        {
            _input.SendKeyEvent(NavKey.Shift, up: false);
            _input.MarkHeld(NavKey.Shift);
        }

        if ((keys & NavKey.W) != 0 && !_input.IsHeld(NavKey.W))
        {
            _input.DoublePressWStart(NavTuning.RamStartWGapMs, NavTuning.RamStartWSoftRearmS, NavTuning.RamStartWFirstHoldMs);
            Emit($"[W×2 + SHIFT] bắt đầu chạy, trạng thái {state}");
        }

        _input.Apply(keys);

        if (normalSprint && (keys & NavKey.Shift) != 0)
        {
            double interval = Math.Max(0.20, NavTuning.NormalMoveShiftKeepaliveS);
            if (now >= _lastShiftKeepaliveT + interval)
            {
                _input.SendKeyEvent(NavKey.Shift, up: false);
                _lastShiftKeepaliveT = now;
            }
        }
        else _lastShiftKeepaliveT = now;

        return keys;
    }

    // ================================================================ prompt -> E -> cho bang

    private bool PromptStable(WorldSnapshot snap, double now) =>
        NavInteraction.NotePrompt(snap.PromptVisible, snap.Seq, ref _promptSeq, ref _promptStreak,
                                  ref _promptAbsentStreak, ref _promptConsumed, now, _eRetryUntil);

    private void ReleaseETick(double now)
    {
        if (_eDown && now >= _eUpAt)
        {
            _input.SendKeyEvent(NavKey.E, up: true);
            _eDown = false;
            _eUpAt = 0;
        }
    }

    private bool PressEOnce(double now, string reason)
    {
        if (reason == "E_TUONG_TAC" && now < _recentBoardExitUntil)
        {
            Emit($"[CHẶN E] vừa thoát bảng {(_recentBoardExitUntil - now):F1}s trước — E thường bị cấm");
            return false;
        }
        if (_eDown) return false;
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        _input.SendKeyEvent(NavKey.E, up: false);
        _promptConsumed = true;
        _eDown = true;
        _eUpAt = now + NavTuning.SimpleEHoldS;
        Emit($"[E MỘT LẦN] {reason}");
        return true;
    }

    private void HoldEnter(string reason)
    {
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        Emit($"[GIỮ] {reason}");
    }

    private bool BeginPostEWait(double now, string source)
    {
        _cameraPhase = null;
        _simplePhase = "POST_WAIT10";
        _wait10Until = now + NavTuning.SimplePostEWaitS;
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        Emit($"[SAU E] {source} → chờ {NavTuning.SimplePostEWaitS:F0}s, không E");
        return true;
    }

    private void ClearPendingE()
    {
        _ixPhase = null;
        _ixSettleUntil = _ixWatchUntil = 0;
    }

    private void StartPendingE(double now)
    {
        _ixPhase = NavInteraction.Settle;
        _ixSettleUntil = now + NavTuning.InteractionSettleS;
        _ixWatchUntil = 0;
        _lastPanelPoll = 0;
        _eRetryUntil = now + NavTuning.InteractionRetryS;
    }

    private bool PollPanel(double now, bool focused)
    {
        if (!focused || now - _lastPanelPoll < 0.125) return false;
        _lastPanelPoll = now;
        return PanelVisible?.Invoke() == true;
    }

    private bool MarkArrived()
    {
        ReleaseETick(double.MaxValue);
        _input.ReleaseOwnedOnce();
        ClearPendingE();
        Emit("[MINIGAME MỞ] giao cho bộ giải");
        return true;
    }

    /// <summary>SETTLE: đứng yên nhận phím E. Hết settle mà chưa có bảng → WATCH (lai tiếp).</summary>
    private bool PendingSettleStep(double now, bool focused, out bool arrived)
    {
        arrived = false;
        ReleaseETick(now);
        if (_ixPhase != NavInteraction.Settle) return false;
        if (PollPanel(now, focused)) { arrived = true; return MarkArrived(); }
        if (now < _ixSettleUntil)
        {
            _input.StopMouseStream(immediate: true);
            _input.ReleaseOwnedOnce();
            return true;
        }
        _ixPhase = NavInteraction.Watch;
        _ixWatchUntil = now + NavTuning.SimpleWaitBoardS;
        _input.StopMouseStream(immediate: true, axis: MouseAxis.Y);
        _input.DoublePressWStart(NavTuning.InputWPostMinigameTakeoverGapMs, NavTuning.InputWPostMinigameSoftRearmS);
        AutorunResetTimer(now);
        Emit("[SAU E] hết settle → lái tiếp, thăm dò panel trong nền");
        return false;
    }

    /// <summary>WATCH: không chiếm frame — chỉ thăm dò panel hoặc hết hạn.</summary>
    private bool PendingWatchPoll(double now, bool focused, out bool arrived)
    {
        arrived = false;
        if (_ixPhase != NavInteraction.Watch) return false;
        if (PollPanel(now, focused)) { arrived = true; return MarkArrived(); }
        if (now < _ixWatchUntil) return false;
        _promptConsumed = false;
        _eRetryUntil = now + NavTuning.InteractionRetryS;
        ClearPendingE();
        Emit("[VỀ WORLD] ONE_E_NO_BOARD → tiếp tục lái, không đứng chờ");
        return false;
    }

    /// <summary>WORLD: prompt + tiếp cận điểm vàng mới được bấm E. Trả true = occupy tick.</summary>
    private bool TryArmWorldE(double now, bool focused, WorldSnapshot snap, TargetOutput target,
                              WorldMarker world, double dist, double rel)
    {
        if (_ixPhase == NavInteraction.Settle || _simplePhase != "WORLD" || !focused || _postJobIgnoreNpcE) return false;
        ReleaseETick(now);

        bool stable = PromptStable(snap, now);
        // consumed chi chan spam cung mot lan hien prompt; E that bai van duoc thu lai sau
        // cooldown du prompt con hien — ApproachReady moi la cong sat diem vang.
        if (!stable || !NavInteraction.RetryReady(now, _eRetryUntil)) return false;

        bool ready = NavInteraction.ApproachReady(dist, rel, _s.Px, target.Quality, target.Confidence,
            world.Present, world.Confidence, world.Area, _lastCtlState, _ctl.ArrivalShieldUntil, now);
        string ds = double.IsNaN(dist) ? "---" : $"{dist:0.0}";
        string re = double.IsNaN(rel) ? "---" : $"{rel:+0.0;-0.0}";
        string why = $"Q={target.Quality} dist={ds} rel={re} WQ={world.Quality} WC={world.Confidence:F2}";

        if (!ready)
        {
            if (now - _lastPromptSkipLog >= 0.5)
            {
                _lastPromptSkipLog = now;
                Emit($"[BỎ PROMPT] chưa sát điểm vàng — {why}");
            }
            return false;
        }

        if (now < _recentBoardExitUntil)
        {
            HoldEnter("E MUỘN SAU BẢNG");
            return BeginPostEWait(now, "LATE_WORLD_E_AFTER_BOARD");
        }

        HoldEnter("PROMPT E");
        if (!PressEOnce(now, "E_TUONG_TAC")) return false;
        Emit($"[E ARM] {why}");
        StartPendingE(now);
        return true;
    }

    private void ResumeWorld(double now, string reason)
    {
        _simplePhase = "WORLD";
        _closeUntil = _postCheckUntil = _wait10Until = 0;
        ClearPendingE();
        bool finalExit = reason is "BOARD_CLOSED_NO_E" or "POST_E_GONE";
        if (finalExit)
        {
            ArmRestartWatch(now, "SIMPLE_" + reason);
            AutorunResetTimer(now);
            StartCameraReset(now, reason);
            Emit($"[HẬU MINIGAME] {reason} → nhìn xuống đất → ngẩng lên → tìm lại điểm vàng → W");
            return;
        }
        _input.StopMouseStream(immediate: true, axis: MouseAxis.Y);
        _input.DoublePressWStart(NavTuning.InputWPostMinigameTakeoverGapMs, NavTuning.InputWPostMinigameSoftRearmS);
        AutorunResetTimer(now);
        Emit($"[VỀ WORLD] {reason} → lấy lại W, không reset camera");
    }

    /// <summary>
    /// Pha hậu minigame và khiên NPC. E lúc WORLD do <see cref="TryArmWorldE"/> sau nhận dạng.
    /// </summary>
    private bool SimpleFlowStep(double now, bool focused, WorldSnapshot snap, out bool arrived)
    {
        arrived = false;
        ReleaseETick(now);
        string phase = _simplePhase;

        if (phase == "CLOSE_SETTLE")
        {
            if (now < _closeUntil) return true;
            _simplePhase = "POST_CHECK";
            _postCheckUntil = now + NavTuning.SimplePostCheckS;
            _promptStreak = 0;
            _promptSeq = -1;
            return true;
        }

        if (phase == "POST_CHECK")
        {
            if (!_promptConsumed && PromptStable(snap, now)) return BeginPostEWait(now, "POST_CHECK_E");
            if (now < _postCheckUntil) return true;
            ResumeWorld(now, "BOARD_CLOSED_NO_E");
            return false;
        }

        if (phase == "POST_WAIT10")
        {
            if (now < _wait10Until) return true;
            if (PromptStable(snap, now))
            {
                if (PressEOnce(now, "POST_MINIGAME_10S"))
                {
                    _recentBoardExitUntil = 0;
                    _simplePhase = "WORLD";
                    StartPendingE(now);
                }
                return true;
            }
            _recentBoardExitUntil = 0;
            ResumeWorld(now, "POST_E_GONE");
            return false;
        }

        _simplePhase = "WORLD";
        if (!focused) return false;

        // Khien bo E cua NPC sau khi xin viec: prompt hien thi khong bam, van di tiep.
        if (_postJobIgnoreNpcE)
        {
            if (snap.PromptVisible)
            {
                _postJobSeen = true;
                _postJobAbsentFrames = 0;
                _promptConsumed = true;
                _promptStreak = _promptAbsentStreak = 0;
                _promptSeq = -1;
                if (now - _postJobLastLog >= 1.0)
                {
                    _postJobLastLog = now;
                    Emit("[KHIÊN E NPC] prompt NPC còn hiện → bỏ qua, đi tiếp về điểm vàng");
                }
                return false;
            }
            _postJobAbsentFrames++;
            double elapsed = now - _postJobStarted;
            int clearFrames = Math.Max(2, NavTuning.JobPostRehirePromptClearFrames);
            bool clearAfterSeen = _postJobSeen && elapsed >= NavTuning.JobPostRehireMinGuardS && _postJobAbsentFrames >= clearFrames;
            bool clearWithoutSeen = !_postJobSeen && elapsed >= NavTuning.JobPostRehireNoPromptTimeoutS && _postJobAbsentFrames >= clearFrames;
            if (clearAfterSeen || clearWithoutSeen)
            {
                _postJobIgnoreNpcE = false;
                _postJobStarted = 0;
                _postJobSeen = false;
                _postJobAbsentFrames = 0;
                _promptConsumed = false;
                _promptStreak = _promptAbsentStreak = 0;
                _promptSeq = -1;
                Emit("[KHIÊN E NPC GỠ] prompt NPC đã mất → E sẵn sàng cho điểm vàng");
            }
            else return false;
        }

        return false;
    }

    private void OnJobFinished()
    {
        _postJobIgnoreNpcE = true;
        _postJobStarted = NavClock.Now;
        _postJobSeen = false;
        _postJobAbsentFrames = 0;
        _postJobLastLog = 0;
        _promptConsumed = true;
        _promptStreak = _promptAbsentStreak = 0;
        _promptSeq = -1;
        _eRetryUntil = 0;
        ClearPendingE();
        Emit("[KHIÊN E NPC] xin việc xong → bỏ prompt NPC, đi về điểm vàng");
    }

    // ================================================================ reset camera + W reclaim

    private void StartCameraReset(double now, string reason)
    {
        _input.ReleaseAll();
        _cameraPhase = "SETTLE";
        _cameraPhaseStart = now;
        _cameraReason = reason;
        _input.StopMouseStream(immediate: true, axis: MouseAxis.Y);
        _watchdog.Reset();
        _ctl.ResetTransient();
        _capture.ResetWorld();
        Emit($"[RESET CAMERA] bắt đầu ({reason})");
    }

    /// <summary><c>_camera_reset_step</c>: SETTLE → DOWN (3300 cps, 780 ms) → GROUND_HOLD → UP (1950 cps, 525 ms) → FINAL. Nhả hết phím mỗi khung.</summary>
    private bool CameraResetStep(double now)
    {
        string phase = _cameraPhase;
        if (phase is null) return false;
        double elapsed = now - _cameraPhaseStart;
        _input.ReleaseAll();

        switch (phase)
        {
            case "SETTLE":
                if (elapsed >= NavTuning.CameraResetSettleS) { _cameraPhase = "DOWN_TO_GROUND"; _cameraPhaseStart = now; }
                return true;
            case "DOWN_TO_GROUND":
                if (elapsed >= NavTuning.CameraResetDownS)
                {
                    _cameraPhase = "GROUND_HOLD"; _cameraPhaseStart = now;
                    _input.StopMouseStream(immediate: true, axis: MouseAxis.Y);
                    _input.ReleaseAll();
                    return true;
                }
                _input.SetMouseYRate(NavTuning.CameraResetDownRateCps);
                return true;
            case "GROUND_HOLD":
                if (elapsed >= NavTuning.CameraResetGroundHoldS)
                {
                    _cameraPhase = "UP_TO_NORMAL"; _cameraPhaseStart = now;
                    _input.StopMouseStream(immediate: true, axis: MouseAxis.Y);
                }
                return true;
            case "UP_TO_NORMAL":
                if (elapsed >= NavTuning.CameraResetUpS)
                {
                    _cameraPhase = "FINAL_SETTLE"; _cameraPhaseStart = now;
                    _input.StopMouseStream(immediate: true, axis: MouseAxis.Y);
                    _input.ReleaseAll();
                    return true;
                }
                _input.SetMouseYRate(-NavTuning.CameraResetUpRateCps);
                return true;
            case "FINAL_SETTLE":
                if (elapsed < NavTuning.CameraResetFinalSettleS) return true;
                _cameraPhase = null;
                _tracker.Reset();
                _watchdog.Reset();
                _ctl.ResetTransient();
                _capture.ResetWorld();
                _input.ReleaseAll();
                ScheduleWReclaim(now);
                AutorunResetTimer(now);
                Emit("[RESET CAMERA XONG] → lấy lại W → chạy tiếp");
                return false;
        }

        _cameraPhase = null;
        _input.ReleaseAll();
        _input.DoublePressWStart(NavTuning.InputWPostMinigameTakeoverGapMs, NavTuning.InputWPostMinigameSoftRearmS);
        AutorunResetTimer(now);
        return false;
    }

    private void ScheduleWReclaim(double now)
    {
        _input.ForceKeyUp(NavKey.W, 2);
        _wReclaimPending = true;
        _wReclaimStage = 0;
        _wReclaimAt = now + Math.Max(0.12, NavTuning.PostMiniWReclaimDelayS);
        _wReclaimConfirmAt = 0;
    }

    /// <summary>
    /// <c>_post_mini_w_reclaim_step</c>: giữ W sạch 260 ms, rồi hai cạnh UP→DOWN thật cách nhau 520 ms để
    /// FiveM trả quyền W cho gameplay sau NUI. Bản Python còn chờ file bắt tay với bộ giải Water ở tiến
    /// trình khác — ở đây BoardBot/WireBot cùng tiến trình đã nhả phím trước khi bot này khởi động.
    /// </summary>
    private bool WReclaimStep(double now, bool focused)
    {
        if (!_wReclaimPending) return false;
        if (!focused) { _input.ForceKeyUp(NavKey.W, 1); return true; }

        if (_wReclaimStage == 0)
        {
            if (now < _wReclaimAt) { _input.ForceKeyUp(NavKey.W, 1); return true; }
            _input.ForceWTakeoverOnce(NavTuning.PostMiniWReclaimGapMs, NavTuning.InputWPostMinigameSoftRearmS);
            _wReclaimStage = 1;
            _wReclaimConfirmAt = now + Math.Max(0.20, NavTuning.PostMiniWReclaimConfirmS);
            Emit("[LẤY LẠI W] cạnh #1");
            return true;
        }
        if (_wReclaimStage == 1)
        {
            if (now < _wReclaimConfirmAt) return true;
            _input.ForceWTakeoverOnce(NavTuning.PostMiniWReclaimConfirmGapMs, NavTuning.InputWPostMinigameSoftRearmS);
            _wReclaimPending = false;
            _wReclaimStage = 0;
            Emit("[LẤY LẠI W] cạnh #2 → điều hướng tiếp");
            return false;
        }
        _wReclaimPending = false;
        return false;
    }

    // ================================================================ an uong

    /// <summary>Một tài nguyên đang chờ/đang được dùng. <c>items</c> của bản Python (main.py 7258).</summary>
    private sealed class SurvivalItem
    {
        public string Name { get; init; }
        public ushort[] Slots { get; init; }
        public int SlotIdx { get; set; }

        /// <summary>% đo được ngay trước khi bấm phím — mốc để chấm điểm sau 10 s.</summary>
        public double Baseline { get; set; }

        public ushort Key => Slots[SlotIdx];
        public char KeyText => (char)Slots[SlotIdx];
    }

    /// <summary><c>release_all</c> cho phím số: cú UP ở tick sau, sao <see cref="ReleaseETick"/>.</summary>
    private void ReleaseSurvivalKeyTick(double now)
    {
        if (_survivalKeyDown && now >= _survivalKeyUpAt)
        {
            _input.SendRawKeyEvent(_survivalKeyVk, up: true);
            _survivalKeyDown = false;
            _survivalKeyUpAt = 0;
        }
    }

    private void CancelSurvival(double now, string reason)
    {
        if (!_survivalActive) return;
        if (_survivalKeyDown)
        {
            _input.SendRawKeyEvent(_survivalKeyVk, up: true);
            _survivalKeyDown = false;
            _survivalKeyUpAt = 0;
        }
        _survivalActive = false;
        _survivalPhase = null;
        _survivalPhaseStart = 0;
        _survivalCurrent = null;
        _survivalQueue.Clear();
        _gauge.Reset();
        Emit($"[ĂN UỐNG] huỷ — {reason}");
    }

    /// <summary>
    /// <c>_survival_step</c>. Trả true = nuốt frame (đang sở hữu chuyển động).
    ///
    /// Mất focus KHÔNG huỷ bữa: người chơi alt-tab giữa lúc chờ 10 giây thì bữa vẫn còn đó, chỉ là
    /// đồng hồ pha đứng lại — huỷ ở đây là mở đường cho một chu kỳ bấm phím nữa vào cửa sổ khác.
    /// </summary>
    /// <summary>
    /// Bữa ĐANG DỞ sở hữu frame, gọi sớm trong <see cref="Tick"/> — ngay sau backout S.
    ///
    /// Vì sao phải sớm chứ không đứng chung chỗ với nhánh mở bữa: prompt E (kể cả SETTLE ngắn) từng
    /// nuốt frame trước bữa. Nhân vật đứng chết 10 giây cạnh một vật tương tác được là đủ để prompt
    /// hiện lên; E giữa bữa rồi đứng chờ panel thì bữa KHÔNG BAO GIỜ chạy tiếp — <c>_survivalActive</c>
    /// kẹt true, mà cờ đó lại vừa tắt cả hai watchdog 30 giây. Đúng một cái treo cứng.
    ///
    /// Bản Python chặn cùng chuyện này bằng <c>set_e_suppressed(True)</c> suốt bữa; ở đây quyền ưu
    /// tiên frame làm luôn việc đó, gọn hơn một lá cờ nữa.
    ///
    /// Mất focus KHÔNG huỷ bữa: người chơi alt-tab giữa lúc chờ thì bữa vẫn còn, chỉ là đồng hồ pha
    /// đứng lại — huỷ ở đây là mở đường cho một chu kỳ bấm phím nữa vào cửa sổ khác.
    /// </summary>
    private bool SurvivalOwnStep(double now, bool focused)
    {
        ReleaseSurvivalKeyTick(now);

        // Cu E-up cua SimpleFlow: no chi duoc lo trong SimpleFlowStep, ma suot bua thi ham do khong
        // chay. Khong lo o day thi mot cu E lo dang xuong se nam duoi ca 10 giay.
        ReleaseETick(now);

        double since = _survivalTickT > 0 ? now - _survivalTickT : 0;
        _survivalTickT = now;

        if (!_cfg.Survival.Enabled)
        {
            CancelSurvival(now, "người dùng tắt ăn uống");
            return false;
        }

        if (!_survivalActive) return false;

        if (!focused)
        {
            _input.ReleaseAll();

            // Treo dong ho pha bang DUNG thoi gian troi qua that: nhip tick khong dam bao 25 ms,
            // va alt-tab lau thi cong don sai so se cat ngan cu chuyen pha khi quay lai.
            _survivalPhaseStart += since;
            return true;
        }

        return SurvivalRun(now);
    }

    /// <summary>
    /// Mở bữa mới — ưu tiên THẤP NHẤT, gọi ngay trước khối nhận dạng. Đặt ở đây nghĩa là chỉ ăn khi
    /// <c>_simplePhase == "WORLD"</c> và không máy trạng thái nào khác đang giữ frame: không bao giờ
    /// mở bữa giữa lúc đang bấm E, đang chờ bảng, hay đang reset camera.
    /// </summary>
    private bool SurvivalMaybeStart(double now, bool focused)
    {
        if (_survivalActive || !focused || !_cfg.Survival.Enabled) return false;

        // Vi tri trong chuoi da lo SETTLE/POST_*/camera (nhung nhanh do deu nuot frame, va ba
        // duong ra "return false" cua SimpleFlow deu goi ResumeWorld truoc). Reset nghe thi KHONG:
        // _job.Step co the tra false ma pha van con, va chen mot bua 10-20 s vao giua chuyen di tim
        // NPC xin viec la lam hai thu tuc dai dan chan nhau.
        if (_job.Phase is not null || _simplePhase != "WORLD" || _ixPhase == NavInteraction.Settle) return false;

        if (!_gauge.Due(now)) return false;

        var reading = _gauge.Update(_capture.GrabSurvival(now), now);
        if (!reading.FoodLow && !reading.WaterLow) return false;
        return StartSurvival(now, reading);
    }

    /// <summary><c>_start_survival</c>: dựng hàng đợi rồi giành quyền điều khiển.</summary>
    private bool StartSurvival(double now, SurvivalReading r)
    {
        var items = new List<SurvivalItem>(2);

        if (r.FoodLow && now >= _survivalFoodBlockUntil)
        {
            var slots = SurvivalSettings.SlotKeys(_cfg.Survival.FoodSlots);
            if (slots.Length > 0)
                items.Add(new SurvivalItem { Name = "BÁNH", Slots = slots, Baseline = r.FoodPct });
        }

        if (r.WaterLow && now >= _survivalWaterBlockUntil)
        {
            var slots = SurvivalSettings.SlotKeys(_cfg.Survival.WaterSlots);
            if (slots.Length > 0)
                items.Add(new SurvivalItem { Name = "NƯỚC", Slots = slots, Baseline = r.WaterPct });
        }

        if (items.Count == 0) return false;

        // Thieu nang hon thi lam truoc: neu bi cat giua chung (nguoi dung dung bot, minigame hien)
        // thi cai da an la cai gap nhat.
        items.Sort((a, b) => a.Baseline.CompareTo(b.Baseline));

        _survivalQueue.Clear();
        foreach (var it in items) _survivalQueue.Enqueue(it);
        _survivalCurrent = _survivalQueue.Dequeue();
        _survivalActive = true;

        // Dung cap ban giao input cua PressEOnce: cat chuot roi nha het DUNG MOT LAN.
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        EnterSurvivalPhase(now, "SETTLE");

        string desc = string.Join(", ",
            new[] { _survivalCurrent }.Concat(_survivalQueue).Select(x => $"{x.Name}={x.Baseline:F0}%"));
        Emit($"[ĂN UỐNG] THIẾU → {desc} | DỪNG → DÙNG ĐỒ → CHỜ {NavTuning.SurvivalFixedWaitS:F0}s → CHẠY TIẾP");
        return true;
    }

    private void EnterSurvivalPhase(double now, string phase)
    {
        _survivalPhase = phase;
        _survivalPhaseStart = now;
    }

    /// <summary>Ba pha của một món: đứng im → bấm phím → chờ cứng rồi chấm điểm đúng một lần.</summary>
    private bool SurvivalRun(double now)
    {
        var item = _survivalCurrent;
        if (item is null) { CancelSurvival(now, "hàng đợi rỗng"); return false; }

        double elapsed = now - _survivalPhaseStart;
        _input.StopMouseStream(immediate: true);

        if (_survivalPhase == "SETTLE")
        {
            _input.ReleaseAll();
            if (elapsed < NavTuning.SurvivalPreUseSettleS) return true;

            _survivalKeyVk = item.Key;
            _survivalKeyDown = true;
            _survivalKeyUpAt = now + NavTuning.SurvivalKeyHoldS;
            _input.SendRawKeyEvent(_survivalKeyVk, up: false);
            EnterSurvivalPhase(now, "PRESS");
            Emit($"[ĂN UỐNG] dùng {item.Name} phím {item.KeyText} — mốc {item.Baseline:F1}%");
            return true;
        }

        if (_survivalPhase == "PRESS")
        {
            // ReleaseSurvivalKeyTick o dau SurvivalStep da lo cu UP; cho no xong roi moi tinh gio 10 s.
            if (_survivalKeyDown) return true;

            _input.ReleaseAll();
            _gauge.Reset();
            EnterSurvivalPhase(now, "WAIT");
            Emit($"[ĂN UỐNG] đã gõ phím {item.KeyText} → chờ {NavTuning.SurvivalFixedWaitS:F1}s");
            return true;
        }

        if (_survivalPhase != "WAIT") { CancelSurvival(now, "pha lạ: " + _survivalPhase); return false; }

        _input.ReleaseAll();

        // Van quet trong luc cho de EMA song lai sau khi Reset — nhung KHONG cham diem som.
        if (_gauge.Due(now)) _gauge.Update(_capture.GrabSurvival(now), now);
        if (elapsed < NavTuning.SurvivalFixedWaitS) return true;

        double? after = SurvivalValue(_gauge.Last, item.Name);
        double before = item.Baseline;
        bool ok = after is not null
                  && (after.Value >= before + NavTuning.SurvivalSuccessDeltaPct
                      || after.Value >= NavTuning.SurvivalLowThresholdPct);

        if (ok)
        {
            Emit($"[ĂN UỐNG] {item.Name} ĐƯỢC phím {item.KeyText}: {before:F1}% → {after.Value:F1}%");
            return SurvivalAdvance(now);
        }

        string aft = after is null ? "?" : $"{after.Value:F1}%";

        if (item.SlotIdx + 1 < item.Slots.Length)
        {
            item.SlotIdx++;
            if (after is not null) item.Baseline = after.Value;
            EnterSurvivalPhase(now, "SETTLE");
            Emit($"[ĂN UỐNG] {item.Name} phím {(char)item.Slots[item.SlotIdx - 1]} KHÔNG ĐỔI " +
                 $"({before:F1}% → {aft}) → thử ô dự phòng {item.KeyText}");
            return true;
        }

        // Ca hai o deu truot. Gan nhu chac chan la het do trong tui: bot khong nhin duoc tui do, no
        // chi bam phim roi nhin dong ho ma doan. Chan tai nguyen do mot lat roi VAN di tiep.
        double block = NavTuning.SurvivalFailedBlockS;
        if (item.Name == "BÁNH") _survivalFoodBlockUntil = now + block;
        else _survivalWaterBlockUntil = now + block;

        Emit($"[ĂN UỐNG] {item.Name} HỎNG CẢ HAI Ô ({before:F1}% → {aft}) → " +
             $"trả lại quyền đi, chặn {block:F0}s");
        Alert?.Invoke($"⚠️ Job Điện — có thể hết {item.Name.ToLowerInvariant()}",
            $"Đã thử ô {string.Join(" và ", item.Slots.Select(k => (char)k))} mà đồng hồ không nhúc nhích " +
            $"({before:F1}% → {aft}). Nhân vật vẫn đi tiếp, nhưng cứ {block:F0}s lại mất " +
            $"{NavTuning.SurvivalFixedWaitS:F0}s đứng thử lại — nên tiếp tế sớm.");
        return SurvivalAdvance(now);
    }

    private static double? SurvivalValue(SurvivalReading r, string name)
    {
        bool valid = name == "BÁNH" ? r.FoodValid : r.WaterValid;
        double v = name == "BÁNH" ? r.FoodPct : r.WaterPct;
        return valid && double.IsFinite(v) ? v : null;
    }

    /// <summary><c>_survival_advance</c>: món tiếp theo, hoặc kết thúc và trả W về cho Auto Move.</summary>
    private bool SurvivalAdvance(double now)
    {
        if (_survivalQueue.Count > 0)
        {
            _survivalCurrent = _survivalQueue.Dequeue();
            EnterSurvivalPhase(now, "SETTLE");
            _input.StopMouseStream(immediate: true);
            _input.ReleaseAll();
            return true;
        }

        _survivalActive = false;
        _survivalPhase = null;
        _survivalPhaseStart = 0;
        _survivalCurrent = null;
        _gauge.Reset();

        // Suot bua an Tick tra ve som nen ba bo dem duoi day khong duoc lam tuoi; khong xoa thi
        // frame dau tien sau bua bi cham oan:
        //  - _job: bo dem mu tuong da mat diem vang ca 20 giay -> di reset nghe vo co.
        //  - _watchdog: cua so va cham thung mot lo 20 giay -> co the ket luan "ket".
        //  - autorun: dong ho tien do (da duoc AutorunResetTimer giu tuoi qua intentionalBlock).
        _job.ResetBlind();
        _watchdog.Reset();
        AutorunResetTimer(now);

        // CO Y khong reset _tracker / _ctl: V6.59 ghi ro "no tracker/controller/navigation reset".
        _input.DoublePressWStart(NavTuning.TransitionWTakeoverGapMs, NavTuning.SurvivalPostUseWRearmS);
        _input.Apply(NavKey.W);
        Emit("[ĂN UỐNG] xong → trả lại W → chạy tiếp");
        return false;
    }

    // ================================================================ watchdog 30 s khong tien

    private void AutorunResetTimer(double now)
    {
        _wdLastProgressT = now;
        _wdBestDist = null;
        _wdAnchorWorld = null;
    }

    private void AutorunRestart(double now, string reason)
    {
        _wdRestartCount++;
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        _cameraPhase = null;
        _simplePhase = "WORLD";
        _closeUntil = _postCheckUntil = _wait10Until = 0;
        _promptConsumed = false;
        _eRetryUntil = 0;
        _promptStreak = _promptAbsentStreak = 0;
        _promptSeq = -1;
        ClearPendingE();
        _tracker.Reset();
        _watchdog.Reset();
        _ctl.ResetTransient();
        _ctl.ResetSearch360();
        _ctl.ResetSmoothMouse();
        _capture.ResetWorld();
        _capture.Obstacle.Clear();
        _input.DoublePressWStart(NavTuning.TransitionWTakeoverGapMs, NavTuning.AutorunWatchdogWRearmS);
        _input.Apply(NavKey.W);
        AutorunResetTimer(now);
        Emit($"[WATCHDOG 30s] #{_wdRestartCount} {reason} → khởi động lại mềm, lấy lại W");
    }

    /// <summary><c>_autorun_idle_watchdog_step</c>: tiến = bán kính tới đích NHỎ HƠN mốc tốt nhất ≥ 1.4 px, hoặc marker world lớn lên rõ.</summary>
    private bool AutorunWatchdogStep(double now, bool focused, double dist, TargetOutput target, WorldMarker world)
    {
        if (!focused) { AutorunResetTimer(now); return false; }
        bool intentionalBlock = _simplePhase != "WORLD" || _cameraPhase is not null || _survivalActive
                                || _ixPhase == NavInteraction.Settle;
        if (intentionalBlock) { AutorunResetTimer(now); return false; }

        bool progress = false;
        if (target.HasPos && target.Confidence >= NavTuning.AutorunWatchdogTargetConfMin && double.IsFinite(dist))
        {
            double improve = NavTuning.AutorunWatchdogDistProgressPx * _s.Px;
            if (_wdBestDist is null) { _wdBestDist = dist; _wdLastProgressT = now; }
            else if (_wdBestDist.Value - dist >= improve) { progress = true; _wdBestDist = dist; }
        }
        if (world.Present && world.Confidence >= NavTuning.AutorunWatchdogWorldConfMin && world.Area >= NavTuning.AutorunWatchdogWorldAreaMin)
        {
            var sig = (world.Area, world.Height, world.Y ?? 0.0);
            if (_wdAnchorWorld is null) { _wdAnchorWorld = sig; _wdLastProgressT = now; }
            else
            {
                var (a0, h0, y0) = _wdAnchorWorld.Value;
                double areaGain = (sig.Area - a0) / Math.Max(1.0, Math.Abs(a0));
                double hGain = sig.Height - h0;
                double yGain = sig.Item3 - y0;
                bool approach = areaGain >= NavTuning.AutorunWatchdogWorldAreaRatio
                                && hGain >= NavTuning.AutorunWatchdogWorldHeightPx
                                && yGain >= NavTuning.AutorunWatchdogWorldYPx * _s.Px;
                if (approach) { progress = true; _wdAnchorWorld = sig; }
            }
        }
        if (progress) { _wdLastProgressT = now; return false; }

        double idle = now - _wdLastProgressT;
        if (idle >= NavTuning.AutorunIdleWatchdogS)
        {
            AutorunRestart(now, $"KHÔNG TIẾN {idle:F1}s");
            return true;
        }
        return false;
    }

    // ================================================================ watch 30 s sau minigame

    private void ArmRestartWatch(double now, string reason)
    {
        _watchCount = 0;
        _backoutActive = false;
        _backoutUntil = 0;
        _watchActive = true;
        _watchStarted = now;
        _watchPending = false;
        Emit($"[WATCH 30s] arm sau {reason}: minigame kế phải mở trong {NavTuning.PostMinigameRestartTimeoutS:F0}s");
    }

    private void RestartPrepareCore(double now)
    {
        _job.ResetBlind();
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        _watchdog.Reset();
        _ctl.ResetTransient();
        _tracker.Reset();
        _capture.ResetWorld();
        _promptStreak = _promptAbsentStreak = 0;
        _promptSeq = -1;
        _promptConsumed = false;
        _eRetryUntil = 0;
        ClearPendingE();
    }

    /// <summary><c>_post_mini_restart_watch_step</c>: 30 s không có minigame mới → reset camera + W; từ lần thứ 3 giữ S 2 s trước.</summary>
    private bool RestartWatchStep(double now, bool focused)
    {
        if (!_watchActive) return false;
        if (!focused)
        {
            if (_backoutActive) { _input.ReleaseOwnedOnce(); _input.StopMouseStream(immediate: true); }
            return false;
        }

        if (_backoutActive)
        {
            if (now < _backoutUntil)
            {
                _input.StopMouseStream(immediate: true);
                _input.Apply(NavKey.S);
                return true;
            }
            _input.ReleaseOwnedOnce();
            _input.StopMouseStream(immediate: true);
            _backoutActive = false;
            _backoutUntil = 0;
            RestartPrepareCore(now);
            Emit("[WATCH 30s NẶNG] lùi S xong → reset camera → W → chạy");
            StartCameraReset(now, "MINIGAME_IDLE_60S_RESTART");
            return true;
        }

        double timeout = Math.Max(10.0, NavTuning.PostMinigameRestartTimeoutS);
        double elapsed = now - _watchStarted;
        if (elapsed < timeout && !_watchPending) return false;

        bool unsafeNow = _simplePhase != "WORLD" || _cameraPhase is not null || _job.Phase is not null
                         || _survivalActive || _ixPhase == NavInteraction.Settle;
        if (unsafeNow)
        {
            if (!_watchPending)
            {
                _watchPending = true;
                Emit($"[WATCH 30s] {elapsed:F1}s trôi qua; chờ WORLD rảnh rồi khởi động lại");
            }
            return false;
        }
        _watchPending = false;

        bool severe = _watchCount >= Math.Max(2, NavTuning.PostMinigameRestartSevereAfterFailedRestarts);
        _watchCount++;
        _watchStarted = now;

        if (severe)
        {
            double back = Math.Max(0.5, NavTuning.PostMinigameRestartSevereBackoutS);
            RestartPrepareCore(now);
            _cameraPhase = null;
            _backoutActive = true;
            _backoutUntil = now + back;
            _input.Apply(NavKey.S);
            Emit($"[WATCH 30s NẶNG] hai lần khởi động lại thất bại; chu kỳ #{_watchCount}: lùi S {back:F1}s → reset camera → W");
            return true;
        }

        RestartPrepareCore(now);
        Emit($"[WATCH 30s] {elapsed:F1}s không có minigame mới → chu kỳ #{_watchCount}: reset camera → W → chạy");
        StartCameraReset(now, "MINIGAME_IDLE_60S_RESTART");
        return true;
    }
}
