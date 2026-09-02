namespace GtaMiniGameBot;

/// <summary>
/// Bộ lái của bộ điều hướng — port <c>Controller.compute</c> (servo minimap) và các phần SỐNG của
/// <c>HumanLearnedController</c> (world_step, lost_step, search360, KET1) trong main.py của bản
/// Python CAROT2 V6.7.34. Mọi hàm trả về TẬP PHÍM muốn giữ và TÊN TRẠNG THÁI; chuột không trả về
/// theo khung mà được đặt trực tiếp thành tốc độ vào <see cref="NavInput.SetMouseXRate"/>.
///
/// Những gì bản gốc còn trong code nhưng đã chết và KHÔNG có ở đây: KET2 (không caller), toàn bộ
/// máy trạng thái thoát kẹt tường/máy biến áp của lớp gốc, hành lang chống quay lại sau KET1
/// (<c>ket1_avoid_return_until</c> luôn 0), breakout SEARCH360 khi world quá 5 s (không bao giờ
/// được arm), <c>heading_reacquire_step</c> (không tới được), và mọi nhánh E — bộ lái không bao
/// giờ bấm E (<c>_e_active</c> luôn False).
///
/// Quy ước góc: <c>rel</c> là góc từ hướng đi tới đích, dương = đích ở BÊN PHẢI → yaw dương.
/// </summary>
internal sealed class NavController
{
    /// <summary>Bản ghi một lượt thoát kẹt KET1 (<c>self.escape</c>).</summary>
    public sealed class Escape
    {
        public string Phase;            // TURN_AROUND | SIDE_TURN | CLEAR_FORWARD
        public double PhaseStart, Start;
        public int Side;                // +1 phai, -1 trai: ben THOAT (side turn)
        public int Level;               // tinh ra nhung khong dung (giu de log giong Python)
        public int Serial;
        public string Source;           // MINIMAP | WORLD | LIGHTNING_*
        public double? StartDist;
        public int UturnSide;           // = -Side
        public double? UturnRefRel, SideRefRel;
        public double? UturnMeasured, SideMeasured;
    }

    private readonly NavScale _s;
    private readonly NavInput _input;

    public event Action<string> Log;

    // ---------------- Controller (lop goc) ----------------
    private double _worldLastErr;
    public string State { get; private set; } = "SEARCH";
    public Escape Active { get; private set; }
    private double? _lostSince;
    private (double t, double dist, double rel, double dx, double dy)? _lastNav;
    public bool WorldLatched { get; private set; }
    private double? _worldCoastStarted;
    private int? _pendingObstacleSide;
    private double _recoveryBlockUntil;
    private int _ramSteerSide, _ramSteerFlipCandidate;
    private double? _ramSteerFlipSince;
    private double? _ramServoPrevErr, _ramServoPrevT;

    // ---------------- HumanLearnedController ----------------
    private int _humanLastRecoverySide = 1;
    private double _humanLastRecoveryT = -999.0;
    private int _humanRecoverySerial;
    private bool _worldCenteredOnce, _worldCenterHold;
    private readonly LinkedList<(double t, double area, double h, double w, double err)> _worldProgress = new();
    private double? _worldImpactCandidateSince;
    private double? _worldChaseStarted;
    private bool _worldDirectTimeoutLogged;
    private double? _arcPrevErr, _arcPrevT;
    private double _arcErrRate;
    private double? _search360Start;
    private int _search360Dir = 1;
    public int Search360Round { get; private set; }
    private double _search360PauseUntil;
    public double ArrivalShieldUntil { get; private set; }
    private bool _centerShiftLatched;
    private double? _ramSoftStartT;
    private bool _ramLineActive;
    public bool RamLineHardLocked { get; private set; }
    private double _ramLineStartedT;
    public double RamLineLastSeenT { get; private set; } = -999.0;
    private double? _ramLineErrEma;
    private double _ramLinePassUntil;
    private int _ramPassCycle;
    private int _ramPassUturnSide = 1;

    public NavController(NavScale s, NavInput input)
    {
        _s = s;
        _input = input;
    }

    private void Emit(string line) => Log?.Invoke(line);

    public static double Wrap(double a)
    {
        double r = (a + 180.0) % 360.0;
        if (r < 0) r += 360.0;
        return r - 180.0;
    }

    // ================================================================ trang thai chung

    /// <summary>Bên vật cản do <c>ObstacleClassifier</c> đo lúc kẹt (+1 → thoát phải). 0 = không rõ.</summary>
    public void SetObstacleSide(int side) => _pendingObstacleSide = side;

    public void ClearPendingObstacle() => _pendingObstacleSide = null;

    public bool HasPendingObstacle => _pendingObstacleSide is not null;

    /// <summary><c>reset_transient</c> của cả hai lớp.</summary>
    public void ResetTransient()
    {
        Active = null;
        _lostSince = null;
        WorldLatched = false;
        _worldCoastStarted = null;
        _pendingObstacleSide = null;
        _recoveryBlockUntil = 0;
        _ramSteerSide = 0;
        _ramSteerFlipCandidate = 0;
        _ramSteerFlipSince = null;
        _ramServoPrevErr = null;
        _ramServoPrevT = null;
    }

    /// <summary><c>_reset_search360</c>.</summary>
    public void ResetSearch360()
    {
        _search360Start = null;
        Search360Round = 0;
        _search360PauseUntil = 0;
    }

    /// <summary><c>_reset_smooth_mouse</c>: cắt chuột và quên trạng thái lead của ARC.</summary>
    public void ResetSmoothMouse()
    {
        _input.StopMouseStream(immediate: true);
        _arcPrevErr = null;
        _arcPrevT = null;
        _arcErrRate = 0;
    }

    private void RememberNav(double now, double dist, double rel, double dx, double dy)
    {
        if (double.IsNaN(dist) || double.IsNaN(rel) || double.IsNaN(dx) || double.IsNaN(dy)) return;
        _lastNav = (now, dist, rel, dx, dy);
        _lostSince = null;
    }

    private void ClearLineLatches()
    {
        _centerShiftLatched = false;
        _ramLineActive = false;
        RamLineHardLocked = false;
        _ramLineErrEma = null;
        _ramLinePassUntil = 0;
    }

    // ================================================================ servo minimap

    /// <summary><c>_ram_soft_yaw_factor</c>: 0.30 → 1.0 theo smoothstep trong 0.85 s kể từ khi bắt đầu một phiên lái.</summary>
    private double SoftYawFactor(double now)
    {
        _ramSoftStartT ??= now;
        double age = Math.Max(0.0, now - _ramSoftStartT.Value);
        double ramp = Math.Max(0.25, NavTuning.RamSoftStartYawRampS);
        double initial = Math.Clamp(NavTuning.RamSoftStartYawInitialScale, 0.15, 1.0);
        if (age >= ramp) return 1.0;
        double x = Math.Clamp(age / ramp, 0.0, 1.0);
        double sm = x * x * (3.0 - 2.0 * x);
        return initial + (1.0 - initial) * sm;
    }

    /// <summary><c>_ram_anti_shake_side</c>: bên yaw với trễ đảo dấu, W không bao giờ bị nhả bởi latch này.</summary>
    public int AntiShakeSide(double err, double dead, double now)
    {
        double ae = Math.Abs(err);
        double center = Math.Max(dead, NavTuning.RamAntiOscCenterReleaseDeg);
        if (ae <= center)
        {
            _ramSteerFlipCandidate = 0;
            _ramSteerFlipSince = null;
            return 0;
        }
        int wanted = err > 0 ? 1 : -1;
        if (_ramSteerSide == 0)
        {
            _ramSteerSide = wanted;
            return wanted;
        }
        if (wanted == _ramSteerSide)
        {
            _ramSteerFlipCandidate = 0;
            _ramSteerFlipSince = null;
            return wanted;
        }
        double immediate = Math.Max(6.0, NavTuning.RamAntiOscImmediateFlipErrorDeg);
        if (ae >= immediate)
        {
            _ramSteerSide = wanted;
            _ramSteerFlipCandidate = 0;
            _ramSteerFlipSince = null;
            return wanted;
        }
        double minErr = Math.Max(center, NavTuning.RamAntiOscFlipMinErrorDeg);
        if (ae < minErr)
        {
            // Vuot tam mot ti: dung yaw ngan thay vi danh nguoc. W va watchdog van chay.
            _ramSteerFlipCandidate = 0;
            _ramSteerFlipSince = null;
            return 0;
        }
        if (_ramSteerFlipCandidate != wanted || _ramSteerFlipSince is null)
        {
            _ramSteerFlipCandidate = wanted;
            _ramSteerFlipSince = now;
            return 0;
        }
        double confirm = Math.Max(0.04, NavTuning.RamAntiOscFlipConfirmS);
        if (now - _ramSteerFlipSince.Value < confirm) return 0;
        _ramSteerSide = wanted;
        _ramSteerFlipCandidate = 0;
        _ramSteerFlipSince = null;
        return wanted;
    }

    /// <summary>Đường cong yaw cps theo |err| (trước precision/brake/cap/soft) — tách riêng để kiểm ngoài game.</summary>
    public static double ServoCurve(double ae, double dead)
    {
        if (ae < 5.0) return 24.0 + Math.Max(0.0, ae - dead) * 18.0;
        if (ae < 12.0) return 80.0 + (ae - 5.0) * 30.0;
        if (ae < 24.0) return 290.0 + (ae - 12.0) * 38.0;
        if (ae < 40.0) return 746.0 + (ae - 24.0) * 36.0;
        if (ae < 65.0) return 1322.0 + (ae - 40.0) * 24.0;
        return 1922.0 + (Math.Min(ae, 120.0) - 65.0) * 8.0;
    }

    /// <summary>
    /// <c>Controller.compute</c>: lái theo chấm minimap. <paramref name="dist"/> px màn,
    /// <paramref name="rel"/> độ. Trả tập phím và tên trạng thái (đã có hậu tố _SHIFT/_W).
    /// </summary>
    public (NavKey keys, string state) Compute(double now, TargetOutput target, double dist, double rel, double dx, double dy, bool stuck)
    {
        if (Active is not null)
        {
            ClearLineLatches();
            var r = RecoveryStep(now, dist, rel);
            if (r is not null) { State = r.Value.state; return r.Value; }
        }

        if (!target.HasPos || !double.IsFinite(dist) || !double.IsFinite(rel))
        {
            _centerShiftLatched = false;
            return LostStep(now);
        }

        _lostSince = null;
        if (target.Quality is not ("NEAR_FRAGMENT" or "HOLD_LAST_ID" or "PREDICT_ONLY")) ResetSearch360();
        RememberNav(now, dist, rel, dx, dy);

        double d = dist;
        double rawErr = rel;
        double rawAe = Math.Abs(rawErr);
        RamLineLastSeenT = now;
        double px = _s.Px;

        // Thoat ket van la Impact-First, va di THANG vao KET1.
        if (stuck && d > NavTuning.HumanNoEscapeInsidePx * px)
        {
            ClearLineLatches();
            if (StartKet1Recovery(now, rawErr, "MINIMAP"))
            {
                var r = RecoveryStep(now, d, rawErr);
                if (r is not null) { State = r.Value.state; return r.Value; }
            }
        }

        double minConf = NavTuning.RamLineMinConf;
        bool strong = target.Confidence >= minConf && target.Quality != "PREDICT_ONLY";
        if (!_ramLineActive && strong)
        {
            _ramLineActive = true;
            RamLineHardLocked = false;
            _ramLineStartedT = now;
            _ramLineErrEma = rawErr;
            _ramSoftStartT = now;
            Emit($"[RAM FAST TARGET SNAP] dist={d:F1} rel={rawErr:+0.0;-0.0} → W liên tục / lái nhẹ");
        }

        // EMA vong cua loi goc; goc lon thi alpha lon.
        if (_ramLineErrEma is null) _ramLineErrEma = rawErr;
        else
        {
            double alpha = NavTuning.RamTargetLockErrorEmaAlpha;
            if (rawAe >= NavTuning.RamTargetLockLargeErrorDeg) alpha = Math.Max(alpha, NavTuning.RamTargetLockLargeErrorAlpha);
            double delta = Wrap(rawErr - _ramLineErrEma.Value);
            _ramLineErrEma = Wrap(_ramLineErrEma.Value + alpha * delta);
        }

        // FAST TARGET SNAP: loi lon dung goc song, loi vua tron, loi nho dung EMA.
        double emaErr = _ramLineErrEma.Value;
        double err;
        if (rawAe >= NavTuning.RamSnapRawErrorDeg) err = rawErr;
        else if (rawAe >= NavTuning.RamSnapBlendErrorDeg)
            err = Wrap(emaErr + NavTuning.RamSnapLiveErrorWeight * Wrap(rawErr - emaErr));
        else err = emaErr;
        double ae = Math.Abs(err);

        // Khien toi dich: chap nhan mat cham ngay sau do ma khong quay tim.
        if (d <= NavTuning.ArrivalShieldEntryDistPx * px
            && rawAe <= NavTuning.ArrivalShieldEntryAngleDeg
            && target.Confidence >= NavTuning.ArrivalShieldMinConf
            && target.Quality != "PREDICT_ONLY")
        {
            ArrivalShieldUntil = Math.Max(ArrivalShieldUntil, now + NavTuning.ArrivalShieldDurationS);
        }

        var keys = NavKey.W;
        double touchDist = NavTuning.RamTouchDistPx * px;
        double shiftResume = NavTuning.RamShiftResumeDistPx * px;
        bool weak = target.Quality is "PREDICT_ONLY" or "HOLD_LAST_ID" && target.Confidence < NavTuning.RamDriveMinConf;

        // Pass-through: da cham vong tron thi giu huong, di xuyen qua chu khong duoi cham quanh mui ten.
        if (now >= _ramLinePassUntil
            && d <= NavTuning.RamLinePassTriggerDistPx * px
            && rawAe <= NavTuning.RamLinePassTriggerAngleDeg
            && target.Confidence >= minConf)
        {
            _ramLinePassUntil = now + NavTuning.RamLineVisiblePassS;
        }
        if (now < _ramLinePassUntil)
        {
            _centerShiftLatched = false;
            _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
            State = "RAM_V63_PASS_THROUGH_W";
            return (NavKey.W, State);
        }

        double age = Math.Max(0.0, now - _ramLineStartedT);
        if (_ramLineActive && !RamLineHardLocked && age >= NavTuning.RamTargetLockSettleS)
            RamLineHardLocked = true;

        bool near = d <= NavTuning.RamTargetLockNearDistPx * px;
        double dead = near ? NavTuning.RamTargetLockNearDeadzoneDeg : NavTuning.RamTargetLockDeadzoneDeg;

        // Toc do hoi tu cua loi goc (cung dau) -> phanh truoc khi vuot tam.
        double closingRate = 0.0;
        if (_ramServoPrevErr is not null && _ramServoPrevT is not null)
        {
            double dt = Math.Clamp(now - _ramServoPrevT.Value, 0.008, 0.120);
            double pe = _ramServoPrevErr.Value;
            if (pe * err > 0.0) closingRate = Math.Max(0.0, (Math.Abs(pe) - ae) / dt);
        }
        _ramServoPrevErr = err;
        _ramServoPrevT = now;

        int steerSide = AntiShakeSide(err, dead, now);
        if (steerSide == 0)
        {
            _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
            State = ae > dead ? "RAM_V620_AIM_STABLE" : "RAM_V63_AIM_CENTERED";
        }
        else
        {
            double desired = ServoCurve(ae, dead);
            if (ae < 8.0) desired *= NavTuning.RamPrecisionRateScaleUnder8;
            else if (ae < 14.0) desired *= NavTuning.RamPrecisionRateScaleUnder14;

            if (ae <= NavTuning.RamApproachBrakeStartDeg)
            {
                if (closingRate >= NavTuning.RamApproachBrakeFastRateDps) desired *= NavTuning.RamApproachBrakeFastScale;
                else if (closingRate >= NavTuning.RamApproachBrakeMediumRateDps) desired *= NavTuning.RamApproachBrakeMediumScale;
            }

            double cap = NavTuning.RamTargetLockMouseMaxRateCps;
            if (near) cap = Math.Min(cap, NavTuning.RamTargetLockNearMouseMaxRateCps);
            desired = Math.Min(desired, cap);

            double soft = SoftYawFactor(now);
            if (ae >= 35.0) soft = Math.Max(soft, NavTuning.RamSnapLargeMinScale);
            else if (ae >= 18.0) soft = Math.Max(soft, NavTuning.RamSnapMediumMinScale);
            else if (ae >= 8.0) soft = Math.Max(soft, NavTuning.RamSnapSmallMinScale);
            desired *= soft;

            _input.SetMouseXRate(steerSide * desired);
            State = "RAM_V63_FAST_TARGET_SNAP";
        }

        // SHIFT latch: tat khi cham/manh/loi lon, bat lai khi xa va thang; giua giu nguyen.
        if (d <= touchDist || target.Quality == "NEAR_FRAGMENT") _centerShiftLatched = false;
        else if (ae >= NavTuning.RamSnapShiftOffErrorDeg) _centerShiftLatched = false;
        else if (d >= shiftResume && !weak && ae <= NavTuning.RamSnapShiftResumeErrorDeg) _centerShiftLatched = true;

        if (_centerShiftLatched) keys |= NavKey.Shift;
        State += _centerShiftLatched ? "_SHIFT" : "_W";
        return (keys, State);
    }

    // ================================================================ ARC (chi con dung khi mat cham ngan)

    /// <summary><c>_arc_lead_error</c>: thêm một chút dẫn trước theo tốc độ đổi của lỗi góc.</summary>
    private double ArcLeadError(double err, double now)
    {
        if (_arcPrevErr is null || _arcPrevT is null)
        {
            _arcPrevErr = err;
            _arcPrevT = now;
            return err;
        }
        double dt = Math.Clamp(now - _arcPrevT.Value, 0.008, 0.080);
        double rawRate = Math.Clamp(Wrap(err - _arcPrevErr.Value) / dt, -260.0, 260.0);
        _arcErrRate = 0.72 * _arcErrRate + 0.28 * rawRate;
        _arcPrevErr = err;
        _arcPrevT = now;
        return Math.Clamp(err + _arcErrRate * NavTuning.ArcSteerLeadS, -179.0, 179.0);
    }

    /// <summary><c>_human_mouse</c>: đường cong yaw ARC — chỉ còn được gọi từ ARC_LOST_CARRY với scale 0.75.</summary>
    private void HumanMouse(double err, double now, bool near, double scale)
    {
        double e = ArcLeadError(err, now);
        double ae = Math.Abs(e);
        if (ae <= NavTuning.ArcMouseDeadzoneDeg) { _input.SetMouseXRate(0.0); return; }

        double desired;
        if (ae < 4.0) desired = 120.0 + ae * 55.0;
        else if (ae < 12.0) desired = 340.0 + (ae - 4.0) * 78.0;
        else if (ae < 28.0) desired = 964.0 + (ae - 12.0) * 55.0;
        else if (ae < 50.0) desired = 1844.0 + (ae - 28.0) * 36.0;
        else if (ae < 75.0) desired = 2636.0 + (ae - 50.0) * 10.0;
        else desired = NavTuning.ArcMouseMaxRateCps;
        desired = Math.Min(desired, NavTuning.ArcMouseMaxRateCps);
        if (near) desired = Math.Min(desired, NavTuning.ArcNearMouseMaxRateCps);
        desired *= scale;
        _input.SetMouseXRate((e > 0 ? 1.0 : -1.0) * desired);
    }

    // ================================================================ world drive

    private void ResetWorldImpactProof()
    {
        _worldProgress.Clear();
        _worldImpactCandidateSince = null;
    }

    /// <summary><c>_world_direct_progress</c>: (đang tiến, kẹt đã xác nhận) dựa trên diện tích/chiều cao marker.</summary>
    private (bool progressing, bool stuck) WorldDirectProgress(double now, WorldMarker marker, double errPx)
    {
        if (!marker.Present)
        {
            ResetWorldImpactProof();
            return (false, false);
        }
        double area = Math.Max(1.0, marker.Area), height = Math.Max(1.0, marker.Height), width = Math.Max(1.0, marker.Width);
        _worldProgress.AddLast((now, area, height, width, Math.Abs(errPx)));
        double win = NavTuning.WorldImpactWindowS;
        while (_worldProgress.Count > 0 && now - _worldProgress.First.Value.t > win) _worldProgress.RemoveFirst();

        var arr = _worldProgress.ToArray();
        if (arr.Length < NavTuning.WorldImpactMinSamples || arr[^1].t - arr[0].t < win * 0.78)
        {
            _worldImpactCandidateSince = null;
            return (true, false);            // dang "warming": coi la tien
        }

        var a = arr.Select(x => x.area).ToArray();
        var h = arr.Select(x => x.h).ToArray();
        var e = arr.Select(x => x.err).ToArray();
        int n = arr.Length, k = Math.Max(4, n / 3);
        double a0 = NavGeometry.Median(a.Take(k)), a1 = NavGeometry.Median(a.Skip(n - k));
        double h0 = NavGeometry.Median(h.Take(k)), h1 = NavGeometry.Median(h.Skip(n - k));
        double areaGrowth = (a1 - a0) / Math.Max(1.0, a0);
        double heightGrowth = h1 - h0;
        var aS = (double[])a.Clone(); Array.Sort(aS);
        var hS = (double[])h.Clone(); Array.Sort(hS);
        double areaSpan = (NavGeometry.Percentile(aS, 90) - NavGeometry.Percentile(aS, 10)) / Math.Max(1.0, NavGeometry.Percentile(aS, 50));
        double heightSpan = NavGeometry.Percentile(hS, 90) - NavGeometry.Percentile(hS, 10);

        bool progressing = areaGrowth >= NavTuning.WorldProgressAreaGrowthPct
                           || heightGrowth >= NavTuning.WorldProgressHeightGrowthPx
                           || areaSpan >= NavTuning.WorldProgressAreaSpanPct
                           || heightSpan >= NavTuning.WorldProgressHeightSpanPx;
        if (progressing)
        {
            _worldImpactCandidateSince = null;
            return (true, false);
        }

        bool aligned = NavGeometry.Median(e.Skip(n - k)) <= NavTuning.WorldImpactMaxErrorPx * _s.Px;
        bool frozen = Math.Abs(areaGrowth) <= NavTuning.WorldImpactMaxAreaGrowthAbsPct
                      && Math.Abs(heightGrowth) <= NavTuning.WorldImpactMaxHeightGrowthAbsPx
                      && areaSpan <= NavTuning.WorldImpactMaxAreaSpanPct
                      && heightSpan <= NavTuning.WorldImpactMaxHeightSpanPx;
        if (!(aligned && frozen))
        {
            _worldImpactCandidateSince = null;
            return (false, false);
        }
        if (_worldImpactCandidateSince is null)
        {
            _worldImpactCandidateSince = now;
            return (false, false);
        }
        return (false, now - _worldImpactCandidateSince.Value >= NavTuning.WorldImpactConfirmS);
    }

    /// <summary><c>_human_world_mouse</c>: yaw theo lệch ngang của marker với latch tâm 72/125 px.</summary>
    private void HumanWorldMouse(double errPx, double now)
    {
        double px = _s.Px;
        double ae = Math.Abs(errPx);
        double acquire = NavTuning.WorldDirectCenterAcquirePx * px;
        double release = NavTuning.WorldDirectCenterReleasePx * px;

        if (_worldCenterHold)
        {
            if (ae <= release) { _input.StopMouseStream(immediate: true, axis: MouseAxis.X); return; }
            _worldCenterHold = false;
        }
        else if (ae <= acquire)
        {
            _worldCenterHold = true;
            _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
            return;
        }

        double desired;
        if (ae < 180.0 * px) desired = 260.0 + Math.Max(0.0, ae - acquire) / px * 2.4;
        else if (ae < 360.0 * px) desired = 520.0 + (ae / px - 180.0) * 4.0;
        else if (ae < 620.0 * px) desired = 1240.0 + (ae / px - 360.0) * 4.0;
        else desired = NavTuning.WorldMouseMaxRateCps;
        desired = Math.Clamp(desired, 220.0, NavTuning.WorldMouseMaxRateCps);
        desired *= SoftYawFactor(now);
        _input.SetMouseXRate((errPx > 0 ? 1.0 : -1.0) * desired);
    }

    /// <summary>
    /// <c>world_step</c>: lái thẳng vào đầu nối vàng 3D khi nó hiện. Trả <c>null</c> khi không xử lý
    /// (đã hết coast) để vòng lặp quay về đường minimap.
    /// </summary>
    public (NavKey keys, string state)? WorldStep(double now, WorldMarker marker, int screenW, bool isStuck, double dist)
    {
        double err;
        if (marker.X is not null)
        {
            err = marker.X.Value - screenW / 2.0;
            _worldLastErr = err;
        }
        else err = _worldLastErr;

        var (worldProgressing, worldImpactStuck) = WorldDirectProgress(now, marker, err);

        // WORLD-OVER-KET: marker that dang hien thi KET1 tu minimap bi huy ngay trong khung nay.
        if (marker.Present && Active is not null && Active.Source != "WORLD")
        {
            Emit($"[WORLD>KET TAKEOVER] huỷ KET1 nguồn {Active.Source} → lái thẳng vào đầu nối");
            _input.StopMouseStream(immediate: true);
            Active = null;
            _recoveryBlockUntil = now;
            _pendingObstacleSide = null;
            _ramLineActive = false;
            RamLineHardLocked = false;
            _centerShiftLatched = false;
            ResetSmoothMouse();
        }

        if (Active is not null)
        {
            if (marker.Present && Active.Source == "WORLD" && worldProgressing)
            {
                Emit("[WORLD PROGRESS RESUMED] huỷ KET1 world → W thẳng");
                _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
                Active = null;
                _recoveryBlockUntil = now;
                _pendingObstacleSide = null;
            }
            else
            {
                var r = RecoveryStep(now, dist, null);
                if (r is not null) { State = r.Value.state; return r.Value; }
            }
        }

        double ae = Math.Abs(err);

        // Ket world (marker dung im du W) -> KET1 nguon WORLD.
        if (worldImpactStuck && Active is null)
        {
            double worldDead = NavTuning.WorldDirectCenterAcquirePx * _s.Px;
            double pseudoRel = err > worldDead ? 25.0 : err < -worldDead ? -25.0 : 0.0;
            Emit($"[WORLD-IMPACT-CONFIRMED] area={marker.Area:F0} cao={marker.Height:F0} err={err:+0;-0}px → KET1");
            if (StartKet1Recovery(now, pseudoRel, "WORLD"))
            {
                var r = RecoveryStep(now, dist, pseudoRel);
                if (r is not null) { State = r.Value.state; return r.Value; }
            }
        }

        if (marker.Present)
        {
            ResetSearch360();
            WorldLatched = true;
            _worldCoastStarted = null;
            _worldChaseStarted ??= now;
            double chase = now - _worldChaseStarted.Value;
            if (chase >= NavTuning.WorldBreakoutTimeoutS)
            {
                if (!_worldDirectTimeoutLogged)
                {
                    Emit($"[WORLD DIRECT] thấy đầu nối {chase:F1}s chưa tới — vẫn lái thẳng, chờ va chạm thật");
                    _worldDirectTimeoutLogged = true;
                }
            }
            else _worldDirectTimeoutLogged = false;

            HumanWorldMouse(err, now);
            var keys = NavKey.W;
            if (marker.Area <= NavTuning.WorldArcShiftAreaMax && ae <= NavTuning.WorldArcShiftErrorPx * _s.Px) keys |= NavKey.Shift;

            if (_worldCenterHold || ae <= NavTuning.WorldDirectCenterAcquirePx * _s.Px)
            {
                _worldCenteredOnce = true;
                State = "WORLD_DIRECT_CENTERED";
            }
            else State = "WORLD_DIRECT_TURN";
            return (keys, State);
        }

        if (WorldLatched)
        {
            _worldCoastStarted ??= now;
            double age = now - _worldCoastStarted.Value;

            if (_worldCenteredOnce && age <= NavTuning.WorldArrivalCoastS)
            {
                State = "WORLD_TRIGGER_COAST";
                _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
                return (NavKey.W, State);
            }
            if (age <= NavTuning.WorldArcMemoryS)
            {
                HumanWorldMouse(_worldLastErr, now);
                State = "WORLD_DIRECT_MEMORY";
                return (NavKey.W, State);
            }

            WorldLatched = false;
            _worldCenteredOnce = false;
            _worldCenterHold = false;
            _worldCoastStarted = null;
            _worldChaseStarted = null;
            _worldDirectTimeoutLogged = false;
            ResetWorldImpactProof();
        }
        State = "WORLD_RELEASE";
        return null;
    }

    // ================================================================ mat cham

    /// <summary><c>_search360_step</c>: W (không SHIFT) + yaw 1850 cps, mỗi lượt 825 ms rồi đảo chiều.</summary>
    private (NavKey keys, string state) Search360Step(double now)
    {
        if (_search360Start is null)
        {
            _search360Start = now;
            Search360Round++;
            if (_arcPrevErr is not null && Math.Abs(_arcPrevErr.Value) > 2.0) _search360Dir = _arcPrevErr.Value > 0 ? 1 : -1;
            else if (Search360Round > 1) _search360Dir = -_search360Dir;
            ResetSmoothMouse();
            Emit($"[SEARCH360] vòng {Search360Round} hướng {(_search360Dir > 0 ? "PHẢI" : "TRÁI")}");
        }

        if (now < _search360PauseUntil)
        {
            _input.SetMouseXRate(_search360Dir * NavTuning.Lost360RateCps);
            State = "SEARCH360_W_CONTINUE";
            return (NavKey.W, State);
        }

        double elapsed = now - _search360Start.Value;
        if (elapsed >= NavTuning.Lost360DurationS)
        {
            _search360Start = null;
            _search360Dir = -_search360Dir;
            _search360PauseUntil = now;               // lost_360_pause_ms = 0
            _input.SetMouseXRate(_search360Dir * NavTuning.Lost360RateCps);
            State = "SEARCH360_NEXT_W";
            return (NavKey.W, State);
        }

        _input.SetMouseXRate(_search360Dir * NavTuning.Lost360RateCps);
        State = "SEARCH360_MOVING";
        return (NavKey.W, State);
    }

    /// <summary><c>lost_step</c>: không có đích dùng được. Thứ tự nhánh giữ đúng bản Python.</summary>
    public (NavKey keys, string state) LostStep(double now)
    {
        if (Active is not null)
        {
            var r = RecoveryStep(now, null, null);
            if (r is not null) { State = r.Value.state; return r.Value; }
        }

        _lostSince ??= now;
        double lostS = now - _lostSince.Value;
        double px = _s.Px;

        // Mat detector ngan KHONG phai la phep quay: da khoa duong thang thi giu huong 1.8 s.
        if (_lastNav is not null && RamLineHardLocked)
        {
            double age = now - _lastNav.Value.t;
            double lastDist = _lastNav.Value.dist;
            bool nearArrival = lastDist <= NavTuning.ArrivalShieldLostDistPx * px && now <= ArrivalShieldUntil;
            if (!nearArrival && age <= NavTuning.RamLineLostStraightS)
            {
                _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
                var keys = NavKey.W;
                if (lastDist >= NavTuning.RamShiftResumeDistPx * px) keys |= NavKey.Shift;
                State = "RAM_V6_LOST_KEEP_STRAIGHT";
                return (keys, State);
            }
        }

        if (_lastNav is not null)
        {
            double age = now - _lastNav.Value.t;
            double dist = _lastNav.Value.dist;
            double rel = _lastNav.Value.rel;

            // PASS-THROUGH: cham bien mat vi mui ten che -> di xuyen, roi U-turn co gioi han.
            if (now <= ArrivalShieldUntil
                && dist <= NavTuning.ArrivalShieldLostDistPx * px
                && Math.Abs(rel) <= NavTuning.ArrivalShieldLostAngleDeg)
            {
                ResetSearch360();
                double passS = NavTuning.RamPassThroughS, uturnS = NavTuning.RamPassUturnS;
                if (lostS <= passS)
                {
                    _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
                    State = "RAM_PASS_THROUGH_W";
                    return (NavKey.W, State);
                }
                if (lostS <= passS + uturnS)
                {
                    if (lostS <= passS + 0.030)
                    {
                        _ramPassCycle++;
                        _ramPassUturnSide = (_ramPassCycle % 2) != 0 ? 1 : -1;
                        if (Math.Abs(rel) >= 4.0) _ramPassUturnSide = rel > 0 ? 1 : -1;
                    }
                    _input.SetMouseXRate(_ramPassUturnSide * NavTuning.RamPassUturnRateCps);
                    State = "RAM_PASS_UTURN_W";
                    return (NavKey.W, State);
                }
                ArrivalShieldUntil = 0.0;
                State = "RAM_PASS_REACQUIRE_W";
                return (NavKey.W, State);
            }

            // Mang theo cu re dang lam ngan mot chut.
            if (age <= NavTuning.ArcLostCarryS)
            {
                var keys = NavKey.W;
                if (dist > 15.0 * px && Math.Abs(rel) <= 25.0) keys |= NavKey.Shift;
                State = "ARC_LOST_CARRY";
                HumanMouse(rel, now, near: dist <= 10.0 * px, scale: 0.75);
                return (keys, State);
            }

            // Gan trigger va da thang: di tiep, chuot tat MEM.
            if (dist <= NavTuning.HumanArrivalLostDistPx * px
                && Math.Abs(rel) <= NavTuning.HumanArrivalCoastMaxRelDeg
                && lostS <= NavTuning.HumanArrivalCoastS)
            {
                State = "ARC_ARRIVAL_COAST";
                _input.StopMouseStream(immediate: false, axis: MouseAxis.X);
                return (NavKey.W, State);
            }
        }

        return Search360Step(now);
    }

    // ================================================================ KET1

    /// <summary>
    /// <c>_start_ket1_recovery</c>. Bên thoát: theo vật cản đo được, không thì theo dấu góc tới đích
    /// (|rel| ≥ 10°), không thì ngược bên lần trước; kẹt lại trong 6.5 s thì ép ngược bên lần trước.
    /// </summary>
    public bool StartKet1Recovery(double now, double relAngle, string source, bool force = false, int? forceSide = null)
    {
        if ((Active is not null && !force) || (now < _recoveryBlockUntil && !force)) return false;
        if (force) Active = null;

        int visualSide = _pendingObstacleSide ?? 0;
        int forced = forceSide is 1 or -1 ? forceSide.Value : 0;
        int side;
        if (forced != 0) side = forced;
        else if (visualSide != 0) side = visualSide > 0 ? 1 : -1;
        else if (Math.Abs(relAngle) >= 10.0) side = relAngle > 0 ? 1 : -1;
        else side = -_humanLastRecoverySide;

        double gap = now - _humanLastRecoveryT;
        int level;
        if (gap < 3.2) { level = 3; if (forced == 0) side = -_humanLastRecoverySide; }
        else if (gap < 6.5) { level = 2; if (forced == 0) side = -_humanLastRecoverySide; }
        else level = 1;

        double? startDist = _lastNav is not null && double.IsFinite(_lastNav.Value.dist) ? _lastNav.Value.dist : null;

        _humanLastRecoverySide = side;
        _humanLastRecoveryT = now;
        _humanRecoverySerial++;

        bool relOk = double.IsFinite(relAngle);
        Active = new Escape
        {
            Phase = "TURN_AROUND",
            PhaseStart = now,
            Start = now,
            Side = side,
            Level = level,
            Serial = _humanRecoverySerial,
            Source = source,
            StartDist = startDist,
            UturnSide = -side,
            UturnRefRel = relOk ? relAngle : null,
            SideRefRel = null
        };
        _pendingObstacleSide = null;
        ResetSmoothMouse();
        Emit($"[KET1-START] #{_humanRecoverySerial} nguồn={source} mức={level} bên={(side > 0 ? "PHẢI" : "TRÁI")} | quay đầu → bẻ bên → W ngắn → về điểm vàng");
        return true;
    }

    /// <summary>
    /// <c>_human_recovery_step</c>: TURN_AROUND (đích 168°, cap 950 ms) → SIDE_TURN (42°, cap 480 ms) →
    /// CLEAR_FORWARD (W 650 ms). Góc quay đo bằng thay đổi bearing sống tới đích; không có bearing
    /// (đích mất, nguồn WORLD) thì chỉ còn cap thời gian. Trả <c>null</c> khi xong.
    /// </summary>
    public (NavKey keys, string state)? RecoveryStep(double now, double? dist, double? relAngle)
    {
        var e = Active;
        if (e is null) return null;

        int side = e.Side > 0 ? 1 : -1;
        double ms = (now - e.PhaseStart) * 1000.0;

        double? BearingTurn(double? refRel)
        {
            if (refRel is null || relAngle is null) return null;
            if (!double.IsFinite(relAngle.Value) || !double.IsFinite(refRel.Value)) return null;
            return Math.Abs(Wrap(relAngle.Value - refRel.Value));
        }

        if (e.Phase == "TURN_AROUND")
        {
            int uSide = e.UturnSide > 0 ? 1 : -1;
            double target = Math.Clamp(NavTuning.Ket1UturnTargetDeg, 135.0, 176.0);
            double? turned = BearingTurn(e.UturnRefRel);
            double hardMs = Math.Max(450.0, NavTuning.Ket1UturnHardMaxS * 1000.0);
            double remaining = target - (turned ?? 0.0);
            bool done = turned is not null && turned.Value >= target || ms >= hardMs;
            if (!done)
            {
                double rate = turned is null ? NavTuning.Ket1UturnRateFarCps
                    : remaining > 55.0 ? NavTuning.Ket1UturnRateFarCps
                    : remaining > 20.0 ? NavTuning.Ket1UturnRateMidCps
                    : NavTuning.Ket1UturnRateNearCps;
                _input.SetMouseXRate(uSide * rate);
                return (NavKey.None, "KET1_TURN_AROUND");
            }
            _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
            e.Phase = "SIDE_TURN";
            e.PhaseStart = now;
            e.SideRefRel = relAngle is not null && double.IsFinite(relAngle.Value) ? relAngle : null;
            e.UturnMeasured = turned;
            _input.SetMouseXRate(side * NavTuning.Ket1SideTurnRateFarCps);
            Emit($"[KET1-U-TURN-DONE] quay {(turned is null ? "theo giờ" : $"{turned.Value:F1}°")} trong {ms:F0} ms → bẻ {(side > 0 ? "PHẢI" : "TRÁI")} ngay, không W ở giữa");
            return (NavKey.None, "KET1_SIDE_TURN");
        }

        if (e.Phase == "SIDE_TURN")
        {
            double target = Math.Clamp(NavTuning.Ket1SideTurnTargetDeg, 24.0, 58.0);
            double? turned = BearingTurn(e.SideRefRel);
            double hardMs = Math.Max(180.0, NavTuning.Ket1SideTurnHardMaxS * 1000.0);
            double remaining = target - (turned ?? 0.0);
            bool done = turned is not null && turned.Value >= target || ms >= hardMs;
            if (!done)
            {
                double rate = turned is null || remaining > 18.0 ? NavTuning.Ket1SideTurnRateFarCps : NavTuning.Ket1SideTurnRateNearCps;
                _input.SetMouseXRate(side * rate);
                return (NavKey.None, "KET1_SIDE_TURN");
            }
            _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
            e.Phase = "CLEAR_FORWARD";
            e.PhaseStart = now;
            e.SideMeasured = turned;
            Emit($"[KET1-SIDE-DONE] bẻ {(turned is null ? "theo giờ" : $"{turned.Value:F1}°")} trong {ms:F0} ms → W thẳng ngắn");
            return (NavKey.W, "KET1_CLEAR_FORWARD");
        }

        if (e.Phase == "CLEAR_FORWARD")
        {
            _input.StopMouseStream(immediate: true, axis: MouseAxis.X);
            double clearMs = Math.Clamp(NavTuning.Ket1ClearForwardS * 1000.0, 350.0, 1100.0);
            if (ms < clearMs) return (NavKey.W, "KET1_CLEAR_FORWARD");

            Active = null;
            _recoveryBlockUntil = now + NavTuning.Ket1RearmS;
            ResetSmoothMouse();
            Emit("[KET1-CLEAR] xong đoạn thẳng → lái theo điểm vàng");
            return null;
        }

        Active = null;
        return null;
    }
}
