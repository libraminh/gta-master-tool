namespace GtaMiniGameBot;

/// <summary>Bảng nghề của NPC thợ điện đã nhận được — <c>_job_recovery_board_info</c>.</summary>
internal sealed class JobBoardInfo
{
    /// <summary>EMPLOYED (nút 3 = "Nghỉ việc", rộng hơn), UNEMPLOYED (nút 3 = "Xin việc") hoặc UNKNOWN (dải chết 0.86–0.88).</summary>
    public string State { get; init; }

    /// <summary>Tâm nút thứ 3, toạ độ MÀN (tương đối góc màn).</summary>
    public int Cx { get; init; }

    public int Cy { get; init; }
    public Rectangle Rect { get; init; }

    /// <summary>width(nút 3) / width(nút 2).</summary>
    public double Ratio { get; init; }
}

/// <summary>
/// Nhận bảng nghề bằng hình học nút cyan, KHÔNG OCR (main.py 5809–5852): trong dải 0.15–0.86 × 0.54–0.82
/// màn, các khối cyan cỡ 7–18 % bề rộng × 3.5–9 % chiều cao màn là nút; sắp theo x; nút thứ ba là nút
/// hành động và bề rộng của nó so với nút thứ hai cho biết đang có việc hay không.
/// </summary>
internal static class JobBoardReader
{
    private static bool IsCyan(int b, int g, int r)
    {
        int max = Math.Max(r, Math.Max(g, b));
        if (max < NavTuning.JobButtonCyanVMin) return false;
        int min = Math.Min(r, Math.Min(g, b));
        int d = max - min;
        int s = (d * 255 + max / 2) / max;
        if (s < NavTuning.JobButtonCyanSMin) return false;
        var (h, _, _) = ImageOps.HsvOf(b, g, r);
        return h >= NavTuning.JobButtonCyanHLo && h <= NavTuning.JobButtonCyanHHi;
    }

    public static JobBoardInfo Read(NavFrame f, NavScale s)
    {
        int w = s.ScreenW, h = s.ScreenH;
        if (h < 540 || w < 900) return null;
        int x0 = (int)(w * NavTuning.JobButtonRoiX0), x1 = (int)(w * NavTuning.JobButtonRoiX1);
        int y0 = (int)(h * NavTuning.JobButtonRoiY0), y1 = (int)(h * NavTuning.JobButtonRoiY1);
        var local = f.ToLocal(new Rectangle(x0, y0, x1 - x0, y1 - y0));
        if (local.Width < 8 || local.Height < 8) return null;

        var m = new Mask(local.Width, local.Height);
        for (int y = 0; y < local.Height; y++)
        {
            int row = (local.Y + y) * f.Stride;
            int orow = y * local.Width;
            for (int x = 0; x < local.Width; x++)
            {
                int i = row + (local.X + x) * 4;
                if (IsCyan(f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2])) m.Data[orow + x] = 1;
            }
        }
        m = ImageOps.Close(ImageOps.Close(m, 5), 5);        // morphologyEx(CLOSE, ones(5,5), iterations=2)

        int offX = f.OriginX + local.X, offY = f.OriginY + local.Y;
        var rects = new List<Blob>();
        foreach (var b in ImageOps.Blobs(m))
        {
            int ww = b.Box.Width, hh = b.Box.Height;
            if (!(0.07 * w <= ww && ww <= 0.18 * w)) continue;
            if (!(0.035 * h <= hh && hh <= 0.09 * h)) continue;
            if (b.Area < 0.30 * ww * hh) continue;
            int yScreen = offY + b.Box.Y;
            if (!(y0 <= yScreen && yScreen <= y1)) continue;
            rects.Add(b);
        }
        rects.Sort((a, b) => a.Box.X.CompareTo(b.Box.X));
        if (rects.Count < 3) return null;

        int w2 = rects[1].Box.Width;
        var r3 = rects[2].Box;
        double ratio = r3.Width / Math.Max(1.0, w2);
        string state = ratio >= NavTuning.JobBoardStateEmployedRatioMin ? "EMPLOYED"
            : ratio <= NavTuning.JobBoardStateUnemployedRatioMax ? "UNEMPLOYED" : "UNKNOWN";
        return new JobBoardInfo
        {
            State = state,
            Cx = offX + r3.X + r3.Width / 2,
            Cy = offY + r3.Y + r3.Height / 2,
            Rect = new Rectangle(offX + r3.X, offY + r3.Y, r3.Width, r3.Height),
            Ratio = ratio
        };
    }
}

/// <summary>
/// Quy trình "reset nghề" khi mất điểm vàng — <c>_job_recovery_*</c> (main.py 5658–6447): đi tới icon
/// tia sét trên minimap, bấm E vào NPC, đọc bảng, "Nghỉ việc" rồi "Xin việc", quét 360° chờ điểm vàng
/// mới. Chạy TRƯỚC luồng prompt/minigame trong vòng lặp vì bảng nghề cũng là một panel cyan lớn.
///
/// <see cref="Step"/> trả true = chiếm khung (vòng lặp <c>continue</c>). Mọi pha UI nhả hết phím mỗi khung.
/// </summary>
internal sealed class JobRecovery
{
    private readonly NavScale _s;
    private readonly Screen _screen;
    private readonly NavInput _input;
    private readonly NavController _ctl;
    private readonly NavWatchdog _watchdog;
    private readonly DotTracker _tracker;
    private readonly NavCapture _capture;
    private readonly double _originX, _originY;

    public event Action<string> Log;

    /// <summary>Kết thúc thật (finished=true) → NavBot bật khiên bỏ E của NPC.</summary>
    public event Action Finished;

    public string Phase { get; private set; }
    public bool MustRehire { get; private set; }

    private double _started, _phaseStarted;
    private bool _manual;
    private int _eStreak, _restoreStreak, _clickRetry;
    private (double x, double y, double t)? _lightningLast;
    private double? _lastDist, _lastRel;
    private int _postRehireScanDir = 1;
    private bool _employmentConfirmed, _hireClickSent;
    private double _lastActionT = -999.0;
    private string _boardStateLast;
    private int _boardStateStreak;
    private double? _navBestDist;
    private double _navLastProgressT, _navLastEscapeT = -999.0, _nav30sCycleStarted;
    private double? _navBlindSince;
    private bool _eDown;
    private double _eUpAt;
    private double? _blindSince;
    private double _lastFinish = -999.0;

    public JobRecovery(NavScale s, Screen screen, NavInput input, NavController ctl, NavWatchdog watchdog,
                       DotTracker tracker, NavCapture capture, double originX, double originY)
    {
        _s = s; _screen = screen; _input = input; _ctl = ctl; _watchdog = watchdog; _tracker = tracker; _capture = capture;
        _originX = originX; _originY = originY;
    }

    private void Emit(string line) => Log?.Invoke(line);

    // ================================================================ kich hoat

    /// <summary><c>_job_recovery_should_start</c>: mù ≥ 6 s liên tục, ≥ 3 vòng SEARCH360, cách lần trước ≥ 20 s.</summary>
    public bool ShouldStart(double now, TargetOutput target, WorldMarker world, int rawCandidates, bool backoutActive)
    {
        if (Phase is not null) return false;
        if (backoutActive) return false;
        if (now - _lastFinish < NavTuning.JobRecoveryCooldownS) return false;
        bool rawYellow = rawCandidates > 0;
        bool goodTarget = target.HasPos && target.Confidence >= NavTuning.JobRecoveryTargetConf;
        bool goodWorld = world.Present && world.Confidence >= NavTuning.JobRecoveryWorldConf;
        if (rawYellow || goodTarget || goodWorld) { _blindSince = null; return false; }
        if (_blindSince is null) { _blindSince = now; return false; }
        double blind = now - _blindSince.Value;
        if (now <= _ctl.ArrivalShieldUntil) return false;
        return blind >= NavTuning.JobRecoveryBlindTriggerS && _ctl.Search360Round >= NavTuning.JobRecoveryAfterSearchRounds;
    }

    public void ResetBlind() => _blindSince = null;

    /// <summary><c>_job_recovery_start</c>.</summary>
    public void Start(double now, string reason, bool manual = false)
    {
        if (Phase is not null) return;
        ResetSession(now, manual);
        Phase = "SEEK_LIGHTNING";
        _capture.WantBoard = false;
        Emit($"[RESET NGHỀ BẮT ĐẦU] {reason}{(manual ? " (tay)" : "")} → tia sét → bảng NPC → có việc");
    }

    /// <summary>Bảng nghề đã mở sau E — bỏ SEEK_LIGHTNING, vào WaitBoard.</summary>
    public void EnterAtOpenBoard(double now, string reason)
    {
        if (Phase is "WAIT_EMPLOYED_BOARD" or "WAIT_UNEMPLOYED_BOARD") return;
        if (Phase is null) ResetSession(now, manual: false);
        Phase = "WAIT_EMPLOYED_BOARD";
        _phaseStarted = now;
        ClearBoardState();
        _eStreak = 0;
        _capture.WantBoard = true;
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        Emit($"[RESET NGHỀ] {reason} → bảng đã mở, bỏ tìm tia sét");
    }

    private void ResetSession(double now, bool manual)
    {
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        _ctl.ResetTransient();
        _watchdog.Reset();
        _tracker.Reset();
        _started = _phaseStarted = now;
        _eStreak = _restoreStreak = _clickRetry = 0;
        _lightningLast = null;
        _lastDist = _lastRel = null;
        _postRehireScanDir = 1;
        _manual = manual;
        MustRehire = false;
        _employmentConfirmed = _hireClickSent = false;
        _lastActionT = -999.0;
        _boardStateLast = null; _boardStateStreak = 0;
        _navBestDist = null;
        _navLastProgressT = now; _navLastEscapeT = -999.0; _nav30sCycleStarted = now;
        _navBlindSince = null;
    }

    /// <summary><c>_job_recovery_cancel</c>. <paramref name="finished"/> true = hoàn tất → cooldown 20 s + khiên bỏ E NPC.</summary>
    public void Cancel(double now, string reason, bool finished)
    {
        ReleaseETick(now);
        if (_eDown) _input.SendKeyEvent(NavKey.E, up: true);
        _eDown = false; _eUpAt = 0;
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        Phase = null;
        _phaseStarted = _started = 0;
        _eStreak = _restoreStreak = _clickRetry = 0;
        _lightningLast = null;
        _lastDist = _lastRel = null;
        _postRehireScanDir = 1;
        _blindSince = null;
        _manual = false;
        MustRehire = false;
        _employmentConfirmed = _hireClickSent = false;
        _boardStateLast = null; _boardStateStreak = 0;
        _navBestDist = null;
        _navLastProgressT = now; _navLastEscapeT = -999.0; _nav30sCycleStarted = now;
        _navBlindSince = null;
        _capture.WantBoard = false;
        _ctl.ResetTransient();
        _ctl.ResetSearch360();
        _watchdog.Reset();
        _tracker.Reset();
        if (finished)
        {
            _lastFinish = now;
            Finished?.Invoke();
        }
        Emit($"[RESET NGHỀ {(finished ? "XONG" : "HUỶ")}] {reason}");
    }

    // ================================================================ phim

    private void ReleaseETick(double now)
    {
        if (_eDown && now >= _eUpAt)
        {
            _input.SendKeyEvent(NavKey.E, up: true);
            _eDown = false; _eUpAt = 0;
        }
    }

    private bool ActionReady(double now, double? minGapS = null) => now - _lastActionT >= Math.Max(0.0, minGapS ?? NavTuning.JobActionMinGapS);

    private bool PressE(double now, string label)
    {
        ReleaseETick(now);
        if (_eDown || !ActionReady(now)) return false;
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        _input.SendKeyEvent(NavKey.E, up: false);
        _eDown = true;
        _eUpAt = now + NavTuning.JobRecoveryEHoldS;
        _lastActionT = now;
        Emit($"[RESET NGHỀ] E → {label}");
        return true;
    }

    private bool TapEsc(double now, string label)
    {
        if (!ActionReady(now)) return false;
        _input.ForceKeyUp(NavKey.Esc, 2);
        _input.SendKeyEvent(NavKey.Esc, up: false);
        _input.SendKeyEvent(NavKey.Esc, up: true);
        _lastActionT = now;
        Emit($"[RESET NGHỀ] ESC → {label}");
        return true;
    }

    private void Click(JobBoardInfo b)
    {
        _input.ClickScreen(_screen.Bounds.X + b.Cx, _screen.Bounds.Y + b.Cy, NavTuning.JobButtonClickHoldMs);
    }

    private bool BoardStateStable(string state)
    {
        if (state is null || state == "UNKNOWN") { _boardStateLast = null; _boardStateStreak = 0; return false; }
        if (_boardStateLast == state) _boardStateStreak++;
        else { _boardStateLast = state; _boardStateStreak = 1; }
        return _boardStateStreak >= NavTuning.JobButtonStableFrames;
    }

    private void ClearBoardState()
    {
        _boardStateLast = null;
        _boardStateStreak = 0;
    }

    // ================================================================ tia set

    /// <summary>
    /// <c>_job_recovery_lightning</c>: khối vàng NHỎ, KHÔNG tròn (circ &lt; 0.78 hoặc fill &lt; 0.56 hoặc
    /// aspect ngoài 0.72–1.38) trong ROI minimap; ưu tiên liên tục với vị trí trước (1.25 s), không thì gần
    /// mốc <c>lightning_anchor_ref</c>; nhớ 3.6 s. Trả toạ độ màn.
    /// </summary>
    public (double x, double y)? Lightning(NavFrame f, double now)
    {
        var t = NavTuning.TargetRoiRef;
        var local = f.ToLocal(_s.RoiRef(t[0], t[1], t[2], t[3]));
        if (local.IsEmpty) return null;
        var mask = YellowDotDetector.YellowMask(f, local);
        double sx = _s.Sx, sy = _s.Sy;
        double lax = NavTuning.LightningAnchorXRef * sx, lay = NavTuning.LightningAnchorYRef * sy;
        int offX = f.OriginX + local.X, offY = f.OriginY + local.Y;
        var prev = _lightningLast;

        (double score, double cx, double cy)? best = null;
        foreach (var cnt in NavGeometry.FindContours(mask))
        {
            double an = cnt.Area / (sx * sy + 1e-9);
            if (an < NavTuning.JobLightningAreaMin || an > NavTuning.JobLightningAreaMax) continue;
            double bwn = cnt.Box.Width / Math.Max(sx, 1e-9), bhn = cnt.Box.Height / Math.Max(sy, 1e-9);
            if (!(1.5 <= bwn && bwn <= 22.0 && 2.5 <= bhn && bhn <= 28.0)) continue;
            double circ = cnt.Circularity, fill = cnt.Fill, aspect = bwn / Math.Max(1e-6, bhn);
            bool lightningLike = circ < 0.78 || fill < 0.56 || aspect < 0.72 || aspect > 1.38;
            if (!lightningLike) continue;
            double cx, cy;
            if (!cnt.HasCentroid) { cx = offX + cnt.Box.X + cnt.Box.Width / 2.0; cy = offY + cnt.Box.Y + cnt.Box.Height / 2.0; }
            else { cx = offX + cnt.Cx; cy = offY + cnt.Cy; }
            double anchorD = Math.Sqrt((cx - lax) * (cx - lax) + (cy - lay) * (cy - lay)) / Math.Max(_s.Max, 1e-9);
            double continuity = 999.0;
            if (prev is not null && now - prev.Value.t <= 1.25)
                continuity = Math.Sqrt((cx - prev.Value.x) * (cx - prev.Value.x) + (cy - prev.Value.y) * (cy - prev.Value.y)) / Math.Max(_s.Max, 1e-9);
            double score = (continuity < 999 ? continuity : anchorD * 0.55) + Math.Max(0.0, circ) * 8.0;
            if (best is null || score < best.Value.score) best = (score, cx, cy);
        }
        if (best is not null)
        {
            _lightningLast = (best.Value.cx, best.Value.cy, now);
            return (best.Value.cx, best.Value.cy);
        }
        if (prev is not null && now - prev.Value.t <= NavTuning.JobLightningMemoryS) return (prev.Value.x, prev.Value.y);
        return null;
    }

    // ================================================================ buoc chinh

    /// <summary>
    /// <c>_job_recovery_step</c>. <paramref name="mini"/> = khung minimap vừa chụp; <paramref name="snap"/> =
    /// snapshot world (prompt, bảng NPC). Trả true khi chiếm khung.
    /// </summary>
    public bool Step(NavFrame mini, WorldSnapshot snap, double now, bool focused)
    {
        string phase = Phase;
        if (phase is null) return false;
        ReleaseETick(now);
        if (!focused)
        {
            _input.ReleaseOwnedOnce();
            _input.StopMouseStream(immediate: true);
            return true;
        }

        _capture.WantBoard = phase is "WAIT_EMPLOYED_BOARD" or "WAIT_UNEMPLOYED_BOARD" or "WAIT_OUTSIDE_PROMPT" or "WAIT_YELLOW";

        if (phase == "SEEK_LIGHTNING") return SeekLightning(mini, snap, now);
        if (phase is "WAIT_EMPLOYED_BOARD" or "WAIT_UNEMPLOYED_BOARD") return WaitBoard(phase, snap, now);
        if (phase == "WAIT_OUTSIDE_PROMPT") return WaitOutsidePrompt(snap, now);
        if (phase == "WAIT_YELLOW") return WaitYellow(mini, snap, now);
        if (phase == "POST_REHIRE_SCAN360") return PostRehireScan(mini, now);

        Cancel(now, "PHA LẠ " + phase, finished: false);
        return false;
    }

    private bool YellowVisible(NavFrame mini) => YellowDotDetector.Detect(mini, _s, _originX, _originY).Count > 0;

    private void ApplyFromController((NavKey keys, string state) r)
    {
        _input.Apply(r.keys & ~NavKey.E);
        StateNote = "JOB_LIGHTNING_" + r.state;
    }

    /// <summary>Tên trạng thái để in log (JOB_LIGHTNING_…).</summary>
    public string StateNote { get; private set; } = "";

    private bool SeekLightning(NavFrame mini, WorldSnapshot snap, double now)
    {
        // Vang that quay lai -> huy ngay (chi khi khong phai chay tay).
        if (!_manual)
        {
            bool back = YellowVisible(mini);
            _restoreStreak = back ? _restoreStreak + 1 : 0;
            if (_restoreStreak >= NavTuning.JobRecoveryRestoreFrames)
            {
                Emit("[RESET NGHỀ HUỶ] điểm vàng đã trở lại trong lúc tìm tia sét → về điểm vàng");
                Cancel(now, "ĐIỂM VÀNG TRỞ LẠI TRƯỚC NPC", finished: false);
                return false;
            }
        }
        else _restoreStreak = 0;

        // E chi duoc bam khi da tung thay/tien gan tia set: prompt bat ky khong duoc coi la NPC.
        bool visible = snap.PromptVisible;
        bool lightningLock = false;
        if (_lightningLast is not null && now - _lightningLast.Value.t <= NavTuning.JobRecoveryPromptLightningRecentS) lightningLock = true;
        if (_lastDist is not null && _lastDist.Value <= NavTuning.JobRecoveryPromptLightningMaxDistPx * _s.Px) lightningLock = true;
        _eStreak = visible && lightningLock ? _eStreak + 1 : 0;
        if (_eStreak >= NavTuning.JobRecoveryPromptFrames)
        {
            _input.StopMouseStream(immediate: true);
            _input.ReleaseOwnedOnce();
            _ctl.ResetTransient();
            _watchdog.Reset();
            if (PressE(now, "MỞ BẢNG NGHỀ / ĐANG CÓ VIỆC"))
            {
                Phase = "WAIT_EMPLOYED_BOARD";
                _phaseStarted = now;
                ClearBoardState();
            }
            return true;
        }

        var lightning = Lightning(mini, now);
        if (lightning is null)
        {
            // KET1 dang chay thi tiep tuc theo vector tia set cuoi.
            if (_ctl.Active is not null && _lastDist is not null && _lastRel is not null)
            {
                var r = _ctl.RecoveryStep(now, _lastDist, _lastRel);
                if (r is not null) { ApplyFromController(r.Value); return true; }
            }

            _navBlindSince ??= now;
            double blind = now - _navBlindSince.Value;
            bool canBlindEscape = _lastRel is not null && _lastDist is not null
                                  && blind >= NavTuning.JobLightningBlindEscapeS
                                  && now - _navLastEscapeT >= NavTuning.JobLightningEscapeRearmS;
            if (canBlindEscape && _ctl.Active is null)
            {
                _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
                _ctl.StartKet1Recovery(now, _lastRel.Value, "LIGHTNING_BLIND");
                _navLastEscapeT = now;
                _navBlindSince = now;
                Emit($"[TIA SÉT MẤT {blind:F1}s SAU KHI KHOÁ] → KET1 trước khi quét lại");
                var r = _ctl.RecoveryStep(now, _lastDist, _lastRel);
                if (r is not null) { ApplyFromController(r.Value); return true; }
            }

            // Quet tim: W (khong SHIFT) + yaw doi ben moi 1.6 s tinh tu luc bat dau.
            int side = ((int)((now - _started) / 1.6)) % 2 == 0 ? 1 : -1;
            _input.Apply(NavKey.W);
            _input.SetMouseXRate(side * NavTuning.JobLightningScanRateCps);
            StateNote = "JOB_LIGHTNING_SCAN";
            return true;
        }

        var (lx, ly) = lightning.Value;
        _navBlindSince = null;
        double dx = lx - _originX, dy = ly - _originY;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double rel = NavController.Wrap(Math.Atan2(dx, -dy) * 180.0 / Math.PI);
        _lastDist = dist; _lastRel = rel;

        double progressPx = NavTuning.JobLightningProgressPx * _s.Px;
        if (_navBestDist is null) { _navBestDist = dist; _navLastProgressT = now; _nav30sCycleStarted = now; }
        else if (_navBestDist.Value - dist >= progressPx) { _navBestDist = dist; _navLastProgressT = now; _nav30sCycleStarted = now; }

        _watchdog.Add(now, dx, dy);
        bool forwardRequested = _input.IsHeld(NavKey.W) && !_input.IsHeld(NavKey.S);
        bool impactStall = _watchdog.ImpactStuck(now, forwardRequested, true, dist, rel);
        bool progressStall = forwardRequested && now - _navLastProgressT >= NavTuning.JobLightningNoProgressEscapeS;
        bool severe30 = now - _nav30sCycleStarted >= NavTuning.JobLightningSeekWatchdogS;
        bool forceEscape = impactStall || progressStall || severe30;

        if (forceEscape)
        {
            int side = _capture.AnalyzeObstacleSide(now, out string note);
            _ctl.SetObstacleSide(side);
            if (_ctl.Active is null && now - _navLastEscapeT >= NavTuning.JobLightningEscapeRearmS)
            {
                string src = impactStall ? "LIGHTNING_IMPACT" : severe30 ? "LIGHTNING_30S" : "LIGHTNING_NO_PROGRESS";
                _ctl.StartKet1Recovery(now, rel, src);
                _navLastEscapeT = now;
                _navLastProgressT = now;
                if (severe30) _nav30sCycleStarted = now;
                Emit($"[KẸT TRÊN ĐƯỜNG TỚI TIA SÉT] {src} dist={dist:F1} rel={rel:+0.0;-0.0} bên={(side > 0 ? "PHẢI" : side < 0 ? "TRÁI" : "TỰ")} {note} → KET1");
            }
        }

        var fake = new TargetOutput
        {
            State = "TRACK", Visible = true, X = lx, Y = ly, Confidence = 0.99, CandidateCount = 1,
            Quality = "LIGHTNING_RECOVERY", RawGeometry = 0.99
        };
        var res = _ctl.Compute(now, fake, dist, rel, dx, dy, forceEscape);
        ApplyFromController(res);
        if (forceEscape) _watchdog.Cooldown(now);
        return true;
    }

    private bool WaitBoard(string phase, WorldSnapshot snap, double now)
    {
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        double elapsed = now - _phaseStarted;
        var info = snap.Board;
        if (info is null)
        {
            ClearBoardState();
            if (elapsed > NavTuning.JobBoardOpenRetryS)
            {
                Phase = phase == "WAIT_EMPLOYED_BOARD" ? "SEEK_LIGHTNING" : "WAIT_OUTSIDE_PROMPT";
                _phaseStarted = now;
                _eStreak = 0;
            }
            return true;
        }

        bool stable = BoardStateStable(info.State);
        if (!stable || elapsed < NavTuning.JobBoardActionMinWaitS) return true;

        if (phase == "WAIT_EMPLOYED_BOARD")
        {
            if (info.State == "UNEMPLOYED")
            {
                MustRehire = true;
                Phase = "WAIT_UNEMPLOYED_BOARD";
                _phaseStarted = now;
                ClearBoardState();
                Emit($"[RESET NGHỀ] bảng báo CHƯA CÓ VIỆC ratio={info.Ratio:F3} → bỏ bước nghỉ, phải xin việc");
                return true;
            }
            if (info.State != "EMPLOYED") return true;
            if (!ActionReady(now)) return true;
            Click(info);
            _lastActionT = now;
            MustRehire = true;
            _clickRetry = 0;
            Phase = "WAIT_OUTSIDE_PROMPT";
            _phaseStarted = now;
            _eStreak = 0;
            ClearBoardState();
            Emit($"[RESET NGHỀ CLICK] đang CÓ VIỆC → NGHỈ VIỆC tại ({info.Cx},{info.Cy}) ratio={info.Ratio:F3}");
            return true;
        }

        // WAIT_UNEMPLOYED_BOARD: chi duoc click XIN VIEC khi bang noi ro CHUA CO VIEC.
        if (info.State == "EMPLOYED")
        {
            _employmentConfirmed = true;
            MustRehire = false;
            TapEsc(now, "ĐÃ CÓ VIỆC / ĐÓNG BẢNG");
            Phase = "WAIT_YELLOW";
            _phaseStarted = now;
            Emit($"[RESET NGHỀ] bảng đã CÓ VIỆC ratio={info.Ratio:F3} → không bấm nút thứ 3");
            return true;
        }
        if (info.State != "UNEMPLOYED") return true;
        if (!ActionReady(now)) return true;
        Click(info);
        _lastActionT = now;
        _hireClickSent = true;
        MustRehire = true;
        _clickRetry++;
        Phase = "WAIT_YELLOW";
        _phaseStarted = now;
        ClearBoardState();
        Emit($"[RESET NGHỀ CLICK] CHƯA CÓ VIỆC → XIN VIỆC tại ({info.Cx},{info.Cy}) ratio={info.Ratio:F3}");
        return true;
    }

    private bool WaitOutsidePrompt(WorldSnapshot snap, double now)
    {
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        double elapsed = now - _phaseStarted;
        var info = snap.Board;
        if (info is not null)
        {
            if (!BoardStateStable(info.State)) return true;
            if (info.State == "UNEMPLOYED")
            {
                MustRehire = true;
                if (elapsed >= NavTuning.JobAfterQuitWaitS) TapEsc(now, "ĐÃ NGHỈ / ĐÓNG BẢNG CHƯA CÓ VIỆC");
                return true;
            }
            if (info.State == "EMPLOYED" && elapsed >= Math.Max(2.0, NavTuning.JobAfterQuitWaitS))
            {
                if (ActionReady(now, 1.5))
                {
                    Click(info);
                    _lastActionT = now;
                    _phaseStarted = now;
                    Emit($"[RESET NGHỀ THỬ LẠI] vẫn CÓ VIỆC → bấm lại NGHỈ VIỆC ratio={info.Ratio:F3}");
                }
                return true;
            }
            return true;
        }

        ClearBoardState();
        if (elapsed < NavTuning.JobAfterQuitWaitS) return true;
        _eStreak = snap.PromptVisible ? _eStreak + 1 : 0;
        if (_eStreak >= NavTuning.JobRecoveryPromptFrames)
        {
            if (PressE(now, "MỞ BẢNG NGHỀ / CHƯA CÓ VIỆC"))
            {
                Phase = "WAIT_UNEMPLOYED_BOARD";
                _phaseStarted = now;
                ClearBoardState();
            }
        }
        return true;
    }

    private bool WaitYellow(NavFrame mini, WorldSnapshot snap, double now)
    {
        _input.StopMouseStream(immediate: true);
        _input.ReleaseOwnedOnce();
        double elapsed = now - _phaseStarted;

        if (YellowVisible(mini))
        {
            _employmentConfirmed = true;
            MustRehire = false;
            Cancel(now, "ĐIỂM VÀNG ĐÃ CÓ / ĐANG CÓ VIỆC", finished: true);
            return false;
        }

        var info = snap.Board;
        if (info is not null)
        {
            if (!BoardStateStable(info.State)) return true;
            if (info.State == "EMPLOYED")
            {
                _employmentConfirmed = true;
                MustRehire = false;
                if (elapsed >= NavTuning.JobAfterApplyWaitS && TapEsc(now, "ĐÃ XIN VIỆC XONG / ĐÓNG BẢNG"))
                    _phaseStarted = now;
                return true;
            }
            if (info.State == "UNEMPLOYED")
            {
                MustRehire = true;
                double retryS = Math.Max(1.5, NavTuning.JobHireRetryMinS);
                if (elapsed >= retryS && ActionReady(now, retryS))
                {
                    Click(info);
                    _lastActionT = now;
                    _hireClickSent = true;
                    _clickRetry++;
                    _phaseStarted = now;
                    Emit($"[RESET NGHỀ THỬ LẠI] vẫn CHƯA CÓ VIỆC → XIN VIỆC #{_clickRetry} ratio={info.Ratio:F3}");
                }
                return true;
            }
            return true;
        }

        ClearBoardState();
        if (_hireClickSent || _employmentConfirmed)
        {
            _employmentConfirmed = true;
            MustRehire = false;
            if (elapsed < NavTuning.JobAfterApplyWaitS) return true;
            Phase = "POST_REHIRE_SCAN360";
            _phaseStarted = now;
            _postRehireScanDir = 1;
            _eStreak = 0;
            _restoreStreak = 0;
            Emit("[RESET NGHỀ] bảng đóng sau XIN VIỆC → đã có việc, quét 360° một vòng tìm điểm vàng");
            return true;
        }
        return true;
    }

    private bool PostRehireScan(NavFrame mini, double now)
    {
        if (YellowVisible(mini))
        {
            _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
            Cancel(now, "ĐIỂM VÀNG XUẤT HIỆN TRONG LÚC QUÉT 360", finished: true);
            return false;
        }
        double scanS = Math.Max(0.25, NavTuning.JobPostRehireScan360DurationS);
        if (now - _phaseStarted < scanS)
        {
            _input.SetMouseXRate(_postRehireScanDir * Math.Abs(NavTuning.JobPostRehireScan360RateCps));
            _input.Apply(NavKey.None);                      // job_post_rehire_scan360_move_forward = false
            StateNote = "JOB_POST_REHIRE_SCAN360";
            return true;
        }
        _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
        Cancel(now, "QUÉT 360 XONG / KHÔNG RESET LẠI NGAY", finished: true);
        return false;
    }
}
