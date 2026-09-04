namespace GtaMiniGameBot;

/// <summary>
/// Cài đặt bộ tự đi tới điểm làm việc — CHỈ những khoá phụ thuộc máy hoặc là cờ bật/tắt sống của
/// bản Python. Mọi hằng số mô tả hành vi game nằm trong <see cref="NavTuning"/> dưới dạng const.
///
/// Vì sao rút từ ~90 trường xuống 6: bộ điều hướng cũ để mọi ngưỡng ra JSON để "chỉnh trong game",
/// rồi không ai chỉnh — còn bản Python đi kèm 899 khoá config mà ~345 khoá không được đọc ở đâu.
/// Đây là port đúng chuỗi sống của một cấu hình đã chứng minh trong game, nên số nào không phải
/// khẩu vị người dùng thì không cần ra file.
/// </summary>
internal sealed class NavSettings
{
    /// <summary>
    /// Hệ số cho các ngưỡng PIXEL THÔ của bản Python (cổng bám 34 px, chạm 14 px, khiên tới đích
    /// 18.5 px, kẹt 0.16/1.15 px…). Bản Python KHÔNG scale chúng và được chỉnh ở 1920×1080; ở 2K
    /// chúng chặt hơn 33 %. 0 = tự lấy <c>sx = W/1920</c> (4/3 ở 2K) để giữ đúng hành vi 1080p.
    /// </summary>
    public double ScreenPxScale { get; set; }

    /// <summary>
    /// <c>mouse_global_speed_multiplier</c> — nhân vào mọi tốc độ yaw trước khi ra OS (không nhân
    /// pitch). 4.0 là số của bản Python; log thật cho thấy máy Python ≈ 15–17 counts/độ, cùng cỡ
    /// 16.89 đo trên máy này ngày 25/08, nên giữ nguyên. Chỉnh nếu KET1 quay quá ít (&lt; 60°) hay
    /// quá nhiều (&gt; 140°) trong 950 ms.
    /// </summary>
    public double MouseSpeedMultiplier { get; set; } = 4.0;

    /// <summary>
    /// Gốc mũi tên người chơi trên minimap, mốc 1080p. 0 = mặc định Python <c>player_origin_ref
    /// [163, 980.4]</c>. Đây là số phụ thuộc HUD của từng máy; <c>--verify-nav</c> có ca kiểm nó
    /// trên ảnh chụp thật.
    /// </summary>
    public double PlayerOriginXRef { get; set; }

    public double PlayerOriginYRef { get; set; }

    /// <summary><c>job_recovery_enabled</c> — mất điểm vàng lâu thì đi tới NPC tia sét reset nghề.</summary>
    public bool JobRecoveryEnabled { get; set; } = true;

    /// <summary>Nhịp ghi dòng trạng thái vào log — <c>console_interval_s</c> 0.16 của Python.</summary>
    public int LogEveryMs { get; set; } = 160;

    /// <summary>Chỉ kẹp giá trị, KHÔNG ném: <see cref="ElectricConfig.Load"/> nuốt mọi exception và trả config mới.</summary>
    public void Normalize()
    {
        if (double.IsNaN(ScreenPxScale) || ScreenPxScale < 0) ScreenPxScale = 0;
        if (ScreenPxScale > 4) ScreenPxScale = 4;
        if (double.IsNaN(MouseSpeedMultiplier) || MouseSpeedMultiplier <= 0) MouseSpeedMultiplier = 4.0;
        MouseSpeedMultiplier = Math.Clamp(MouseSpeedMultiplier, 0.25, 20.0);
        if (double.IsNaN(PlayerOriginXRef) || PlayerOriginXRef < 0 || PlayerOriginXRef > 1920) PlayerOriginXRef = 0;
        if (double.IsNaN(PlayerOriginYRef) || PlayerOriginYRef < 0 || PlayerOriginYRef > 1080) PlayerOriginYRef = 0;
        LogEveryMs = Math.Clamp(LogEveryMs <= 0 ? 160 : LogEveryMs, 50, 5000);
    }
}

/// <summary>
/// Hệ quy đổi của một màn hình: <c>sx = W/1920</c>, <c>sy = H/1080</c> đúng như bản Python tính lại
/// mỗi khung, cộng <see cref="Px"/> cho các ngưỡng pixel thô (xem <see cref="NavSettings.ScreenPxScale"/>).
/// </summary>
internal readonly struct NavScale
{
    public readonly int ScreenW, ScreenH;
    public readonly double Sx, Sy, Px;

    public NavScale(int screenW, int screenH, double pxScale)
    {
        ScreenW = screenW;
        ScreenH = screenH;
        Sx = screenW / ElectricConfig.RefW;
        Sy = screenH / ElectricConfig.RefH;
        Px = pxScale > 0 ? pxScale : Sx;
    }

    /// <summary><c>max(sx, sy)</c> — bản Python dùng cho bán kính quanh mũi tên / tia sét.</summary>
    public double Max => Math.Max(Sx, Sy);

    /// <summary><c>sx·sy</c> — chia diện tích contour về đơn vị tham chiếu.</summary>
    public double Area => Sx * Sy;

    /// <summary>
    /// ROI <c>[x0,y0,x1,y1]</c> mốc 1080p → toạ độ màn (tương đối góc màn), cắt kiểu <c>int()</c> và
    /// kẹp trong màn như mọi detector Python làm.
    /// </summary>
    public Rectangle RoiRef(double x0, double y0, double x1, double y1)
    {
        int rx0 = Math.Max(0, (int)(x0 * Sx));
        int ry0 = Math.Max(0, (int)(y0 * Sy));
        int rx1 = Math.Min(ScreenW, (int)(x1 * Sx));
        int ry1 = Math.Min(ScreenH, (int)(y1 * Sy));
        if (rx1 <= rx0 || ry1 <= ry0) return Rectangle.Empty;
        return new Rectangle(rx0, ry0, rx1 - rx0, ry1 - ry0);
    }
}

/// <summary>
/// Hằng số của bộ điều hướng, chép ĐÚNG giá trị trong <c>config.json</c> của bản Python
/// CAROT2 V6.7.34 (không lấy default trong code — hai bộ số khác nhau ở nhiều khoá). Mỗi hằng ghi tên
/// khoá gốc. Chỉ có mặt các khoá mà chuỗi sống thật sự đọc; ~345 khoá chết không được chép sang.
///
/// Đơn vị: <c>Ref</c> = mốc 1080p (nhân sx/sy hoặc chia ngược khi so), <c>Px</c> = pixel thô của
/// bản Python (nhân <see cref="NavScale.Px"/>), còn lại là độ, giây, hoặc counts/giây (cps, TRƯỚC
/// khi nhân <see cref="NavSettings.MouseSpeedMultiplier"/>).
/// </summary>
internal static class NavTuning
{
    // ================================================================ moc HUD (ref 1080p)
    public const double PlayerOriginXRef = 163.0;              // player_origin_ref
    public const double PlayerOriginYRef = 980.4;
    public static readonly double[] TargetRoiRef = { 18, 770, 320, 1026 };          // target_roi_ref
    public static readonly double[] WorldRoiRef = { 0, 60, 1920, 950 };            // world_roi_ref
    public static readonly double[] WorldExcludeBottomLeftRef = { 0, 820, 620, 1080 };
    public static readonly double[] WorldExcludeTopRightRef = { 1540, 0, 1920, 250 };
    public static readonly double[] ObstacleRoiRef = { 500, 180, 1420, 840 };      // obstacle_roi_ref
    public const double LightningAnchorXRef = 174.0;           // lightning_anchor_ref
    public const double LightningAnchorYRef = 967.0;

    // ================================================================ mask vang minimap
    // yellow_hsv (18,132,138)-(45,255,255) ∪ yellow_hsv_relaxed (16,108,118)-(47,255,255): hop = relaxed.
    public const int YellowHLo = 16, YellowHHi = 47, YellowSMin = 108, YellowVMin = 118;

    // ================================================================ cham vang (YellowDotDetector)
    public const double DotAreaMin = 48.0, DotAreaMax = 215.0;          // dot_area_min/max (ref)
    public const double DotWMin = 8.0, DotWMax = 22.0, DotHMin = 8.0, DotHMax = 22.0;
    public const double DotAspectMin = 0.84, DotAspectMax = 1.20;
    public const double DotCircularityMin = 0.80, DotFillMin = 0.58, DotSolidityMin = 0.90;
    public const double DotRadialCvMax = 0.46;
    public const double DotIdealArea = 108.0, DotIdealCircularity = 0.88, DotIdealFill = 0.65;

    public const double LightningGuardRadiusRef = 20.0;                  // lightning_guard_radius_px (ref)
    public const double LightningGuardCircularity = 0.84, LightningGuardAspectMin = 0.89, LightningGuardAspectMax = 1.13;
    public const double LightningGuardFill = 0.62, LightningGuardSolidity = 0.91, LightningGuardRadialCvMax = 0.30;

    public const double LightningAnchorRoundGuardRadiusRef = 15.5;
    public const double LightningAnchorFullCircularity = 0.85, LightningAnchorFullAspectMin = 0.90, LightningAnchorFullAspectMax = 1.12;
    public const double LightningAnchorFullFill = 0.63, LightningAnchorFullSolidity = 0.92, LightningAnchorRadialCvMax = 0.27;

    // ================================================================ manh canh mui ten
    public const double FragmentRadiusRef = 21.0;                       // fragment_radius_px
    public const double FragmentAreaMin = 16.0, FragmentAreaMax = 105.0;
    public const double FragmentWMin = 4.0, FragmentWMax = 17.0, FragmentHMin = 4.0, FragmentHMax = 17.0;
    public const double FragmentBootstrapMaxDistRef = 18.5;             // ×max(sx,sy) trong Python
    public const double LightningFragmentAnchorRadiusRef = 15.5;
    public const double LightningFragmentPlayerOverrideDistRef = 11.5;
    public static readonly double[] LightningFragmentBoxRef = { -16.0, -10.0, 16.0, 22.0 };

    // ================================================================ tracker
    public const double BootstrapGeometryMin = 0.72;
    public const double TrackAcceptScore = 0.66;
    public const double TrackGatePx = 34.0;
    public const int TrackRebootstrapAfterMisses = 9;
    public const int TrackForgetAfterMisses = 22;
    public const double TrackAlpha = 0.58, TrackBeta = 0.075;
    public const double TrackVelocityCapPxS = 320.0;
    public const double OcclusionNearDistancePx = 15.0;
    public const double OcclusionHoldS = 0.42;
    public const double FragmentTrackGateStrictPx = 10.0;
    public const double FragmentRequireRecentFullS = 1.10;
    public const double OverlapBridgeS = 1.25;
    public const double OverlapBootstrapMaxDistPx = 12.0;
    public const double OverlapBootstrapMinAreaRef = 35.0;
    public const double OverlapBootstrapTargetAreaRef = 55.0;
    public const double OverlapBootstrapMinSidePx = 6.0;
    public const double OverlapBootstrapMinSolidity = 0.44;
    public const double FragmentAlpha = 0.44;
    public const double OverlapBootConf = 0.86, FragmentTrackConf = 0.86, FragmentBootConf = 0.78;

    // ================================================================ dau noi vang 3D (WorldMarkerDetector)
    public const int WorldHLo = 17, WorldHHi = 47, WorldSMin = 105, WorldVMin = 125;   // world_hsv_low/high
    public const double WorldMinArea = 1200.0, WorldMaxArea = 80000.0;
    public const double WorldMinWidth = 18.0, WorldMinHeight = 45.0, WorldMaxWidth = 420.0, WorldMaxHeight = 420.0;
    public const double WorldMinBboxBottomRef = 430.0;
    public const double WorldMinFill = 0.06, WorldMinSat = 135.0, WorldMinVal = 165.0;
    public const double WorldBottomFractionStart = 0.60;
    public const int WorldBottomMinPixels = 20;
    public const double WorldAcceptScore = 0.46;
    public const int WorldConfirmFrames = 2;
    public const double WorldEmaAlpha = 0.50;
    public const double WorldGraceS = 0.700;                             // world_grace_ms

    // ================================================================ watchdog va cham (impact_stuck)
    public const double StuckWindowS = 0.720;                            // stuck_window_ms — lich su giu ×1.35
    public const double ImpactMaxHeadingErrorDeg = 55.0;
    public const double ImpactMinDistancePx = 4.0;
    public const double ImpactWindowS = 0.900;
    public const int ImpactMinSamples = 12;
    public const double ImpactRequiredProgressPx = 0.16;
    public const double ImpactMaxRadialSpanPx = 1.15;
    public const double ImpactConfirmS = 0.180;
    public const double ImpactMinTargetConf = 0.50;
    public const double StuckPostCooldownS = 0.950;                      // stuck_post_cooldown_ms
    public const double WorldSkipMinimapStuckConf = 0.55;                // world_direct_skip_minimap_stuck_conf
    public const double WorldSkipMinimapStuckArea = 1200.0;

    // ================================================================ obstacle (chi lay side)
    public const int ObstacleCannyLow = 50, ObstacleCannyHigh = 135;
    public const double ObstacleObserveIntervalS = 0.180;
    public const double ObstacleHistoryS = 6.5;
    public const double TransformerMemoryS = 5.2;
    public const double ObstacleSideDeadzone = 0.003;
    public const double ObstacleStrongEdgeDensity = 0.020;               // obstacle_transformer_strong_edge_density
    public const double ObstacleWeakEdgeDensity = 0.032;                 // obstacle_transformer_edge_density

    // ================================================================ servo minimap (Controller.compute)
    public const double HumanNoEscapeInsidePx = 3.2;
    public const double RamLineMinConf = 0.54;
    public const double RamTargetLockErrorEmaAlpha = 0.30;
    public const double RamTargetLockLargeErrorDeg = 24.0;
    public const double RamTargetLockLargeErrorAlpha = 0.70;
    public const double RamSnapRawErrorDeg = 24.0;
    public const double RamSnapBlendErrorDeg = 10.0;
    public const double RamSnapLiveErrorWeight = 0.52;
    public const double ArrivalShieldEntryDistPx = 18.5;
    public const double ArrivalShieldEntryAngleDeg = 13.0;
    public const double ArrivalShieldMinConf = 0.64;
    public const double ArrivalShieldDurationS = 3.300;
    public const double RamTouchDistPx = 14.0;
    public const double RamShiftResumeDistPx = 16.5;
    public const double RamDriveMinConf = 0.46;
    public const double RamLinePassTriggerDistPx = 14.0;
    public const double RamLinePassTriggerAngleDeg = 12.0;
    public const double RamLineVisiblePassS = 0.900;
    public const double RamTargetLockSettleS = 0.16;
    public const double RamTargetLockNearDistPx = 23.0;
    public const double RamTargetLockNearDeadzoneDeg = 3.6;
    public const double RamTargetLockDeadzoneDeg = 2.4;
    public const double RamPrecisionRateScaleUnder8 = 0.72;
    public const double RamPrecisionRateScaleUnder14 = 0.84;
    public const double RamApproachBrakeStartDeg = 22.0;
    public const double RamApproachBrakeFastRateDps = 42.0, RamApproachBrakeFastScale = 0.46;
    public const double RamApproachBrakeMediumRateDps = 18.0, RamApproachBrakeMediumScale = 0.68;
    public const double RamTargetLockMouseMaxRateCps = 1650.0;
    public const double RamTargetLockNearMouseMaxRateCps = 520.0;
    public const double RamSoftStartYawInitialScale = 0.30;
    public const double RamSoftStartYawRampS = 0.85;
    public const double RamSnapLargeMinScale = 0.82, RamSnapMediumMinScale = 0.58, RamSnapSmallMinScale = 0.38;
    public const double RamSnapShiftOffErrorDeg = 14.0;
    public const double RamSnapShiftResumeErrorDeg = 5.5;
    public const double RamAntiOscCenterReleaseDeg = 3.8;
    public const double RamAntiOscImmediateFlipErrorDeg = 24.0;
    public const double RamAntiOscFlipMinErrorDeg = 7.5;
    public const double RamAntiOscFlipConfirmS = 0.220;
    public const double CenterNavMinConf = 0.46;                         // center_nav_min_conf (khoa song duy nhat cua center_*)
    public const double RamLineWorldOverrideHoldS = 1.8;                 // ram_line_world_override_hold_s

    // ================================================================ world drive (world_step)
    public const double WorldInstantTakeoverConf = 0.79;
    public const double WorldInstantTakeoverMinArea = 1550.0;
    public const double WorldStrongOverrideConf = 0.62;
    public const double WorldLockMinimapMaxDistPx = 30.0;
    public const double WorldRequireTargetConf = 0.40;
    public const double WorldDirectCenterAcquirePx = 72.0;
    public const double WorldDirectCenterReleasePx = 125.0;
    public const double WorldMouseMaxRateCps = 3300.0;
    public const double WorldArcShiftAreaMax = 12500.0;
    public const double WorldArcShiftErrorPx = 330.0;
    public const double WorldArrivalCoastS = 1.250;                      // world_arrival_coast_ms
    public const double WorldArcMemoryS = 0.500;                         // world_arc_memory_ms
    public const double WorldBreakoutTimeoutS = 5.0;                     // chi de log
    public const double WorldImpactWindowS = 1.200;
    public const int WorldImpactMinSamples = 18;
    public const double WorldImpactConfirmS = 0.220;
    public const double WorldImpactMaxErrorPx = 180.0;
    public const double WorldProgressAreaGrowthPct = 0.035;
    public const double WorldProgressHeightGrowthPx = 5.0;
    public const double WorldProgressAreaSpanPct = 0.065;
    public const double WorldProgressHeightSpanPx = 9.0;
    public const double WorldImpactMaxAreaGrowthAbsPct = 0.014;
    public const double WorldImpactMaxHeightGrowthAbsPx = 2.0;
    public const double WorldImpactMaxAreaSpanPct = 0.038;
    public const double WorldImpactMaxHeightSpanPx = 5.2;

    // ================================================================ mat cham (lost_step)
    public const double RamLineLostStraightS = 1.800;
    public const double ArrivalShieldLostDistPx = 20.0;
    public const double ArrivalShieldLostAngleDeg = 24.0;
    public const double RamPassThroughS = 1.250;
    public const double RamPassUturnS = 0.760;
    public const double RamPassUturnRateCps = 1180.0;
    public const double ArcLostCarryS = 0.420;
    public const double HumanArrivalLostDistPx = 11.0;
    public const double HumanArrivalCoastMaxRelDeg = 18.0;
    public const double HumanArrivalCoastS = 1.200;
    public const double ArcSteerLeadS = 0.035;
    public const double ArcMouseDeadzoneDeg = 0.8;
    public const double ArcMouseMaxRateCps = 3100.0;
    public const double ArcNearMouseMaxRateCps = 1750.0;
    public const double Lost360RateCps = 1850.0;
    public const double Lost360DurationS = 0.825;

    // ================================================================ KET1
    public const double Ket1UturnTargetDeg = 168.0;
    public const double Ket1UturnRateFarCps = 420.0, Ket1UturnRateMidCps = 270.0, Ket1UturnRateNearCps = 135.0;
    public const double Ket1UturnHardMaxS = 0.950;
    public const double Ket1SideTurnTargetDeg = 42.0;
    public const double Ket1SideTurnRateFarCps = 300.0, Ket1SideTurnRateNearCps = 120.0;
    public const double Ket1SideTurnHardMaxS = 0.480;
    public const double Ket1ClearForwardS = 0.650;
    public const double Ket1RearmS = 0.500;

    // ================================================================ lop san phim (_apply_world_nav_input)
    public const double RamStartWGapMs = 24.0, RamStartWFirstHoldMs = 34.0, RamStartWSoftRearmS = 1.6;
    public const double NormalMoveShiftKeepaliveS = 0.45;
    public const double TransitionWTakeoverGapMs = 18.0;                 // dung lam GIA TRI cho watchdog/resume
    public const double InputWPostMinigameTakeoverGapMs = 18.0;
    public const double InputWPostMinigameSoftRearmS = 1.5;

    // ================================================================ prompt -> E -> cho bang (simple flow)
    public const double SimpleERoiX0 = 0.45, SimpleERoiX1 = 0.73, SimpleERoiY0 = 0.42, SimpleERoiY1 = 0.68;
    public const int SimpleEWhiteThreshold = 175;
    public const double SimpleEKeycapMinFill = 0.42;
    public const int SimpleEMinTextGlyphs = 4;
    public const int SimplePromptStableFrames = 2;
    public const int SimplePromptRearmAbsentFrames = 3;
    public const double SimpleEHoldS = 0.090;
    public const double SimpleWaitBoardS = 4.0;
    public const int SimpleBoardClearFrames = 5;
    public const double SimpleCloseSettleS = 0.60;
    public const double SimplePostCheckS = 1.50;
    public const double SimplePostEWaitS = 10.0;
    public const double SimpleRecentBoardExitGuardS = 8.0;

    // ================================================================ reset camera + W reclaim
    public const double CameraResetSettleS = 0.070;
    public const double CameraResetDownS = 0.780, CameraResetDownRateCps = 3300.0;
    public const double CameraResetGroundHoldS = 0.070;
    public const double CameraResetUpS = 0.525, CameraResetUpRateCps = 1950.0;
    public const double CameraResetFinalSettleS = 0.090;
    public const double PostMiniWReclaimDelayS = 0.260;
    public const double PostMiniWReclaimGapMs = 85.0;
    public const double PostMiniWReclaimConfirmS = 0.520;
    public const double PostMiniWReclaimConfirmGapMs = 85.0;

    // ================================================================ watchdog 30 s / watch sau minigame
    public const double AutorunIdleWatchdogS = 30.0;
    public const double AutorunWatchdogWRearmS = 2.0;
    public const double AutorunWatchdogTargetConfMin = 0.42;
    public const double AutorunWatchdogDistProgressPx = 1.4;
    public const double AutorunWatchdogWorldConfMin = 0.55;
    public const double AutorunWatchdogWorldAreaMin = 1000.0;
    public const double AutorunWatchdogWorldAreaRatio = 0.16;
    public const double AutorunWatchdogWorldHeightPx = 7.0;
    public const double AutorunWatchdogWorldYPx = 3.0;
    public const double PostMinigameRestartTimeoutS = 30.0;
    public const int PostMinigameRestartSevereAfterFailedRestarts = 2;
    public const double PostMinigameRestartSevereBackoutS = 2.0;

    // ================================================================ reset nghe (job recovery)
    public const int JobRecoveryAfterSearchRounds = 3;
    public const double JobRecoveryBlindTriggerS = 6.0;
    public const double JobRecoveryCooldownS = 20.0;
    public const double JobRecoveryTargetConf = 0.42;
    public const double JobRecoveryWorldConf = 0.50;
    public const int JobRecoveryPromptFrames = 4;
    public const double JobRecoveryEHoldS = 0.090;
    public const int JobRecoveryRestoreFrames = 1;
    public const double JobRecoveryPromptLightningRecentS = 3.2;
    public const double JobRecoveryPromptLightningMaxDistPx = 30.0;
    public const double JobActionMinGapS = 0.75;
    public const int JobButtonCyanHLo = 84, JobButtonCyanHHi = 96, JobButtonCyanSMin = 90, JobButtonCyanVMin = 80;
    public const double JobButtonRoiX0 = 0.15, JobButtonRoiX1 = 0.86, JobButtonRoiY0 = 0.54, JobButtonRoiY1 = 0.82;
    public const int JobButtonStableFrames = 4;
    public const double JobButtonClickHoldMs = 80.0;
    public const double JobBoardStateEmployedRatioMin = 0.88;
    public const double JobBoardStateUnemployedRatioMax = 0.86;
    public const double JobBoardOpenRetryS = 4.0;
    public const double JobBoardActionMinWaitS = 0.9;
    public const double JobAfterQuitWaitS = 1.2;
    public const double JobAfterApplyWaitS = 2.0;
    public const double JobHireRetryMinS = 2.5;
    public const double JobLightningAreaMin = 2.0, JobLightningAreaMax = 90.0;
    public const double JobLightningMemoryS = 3.6;
    public const double JobLightningScanRateCps = 720.0;
    public const double JobLightningProgressPx = 1.0;
    public const double JobLightningNoProgressEscapeS = 3.0;
    public const double JobLightningBlindEscapeS = 2.8;
    public const double JobLightningEscapeRearmS = 2.0;
    public const double JobLightningSeekWatchdogS = 30.0;
    public const double JobPostRehireScan360DurationS = 0.825;
    public const double JobPostRehireScan360RateCps = 1850.0;
    public const double JobPostRehireMinGuardS = 1.2;
    public const int JobPostRehirePromptClearFrames = 8;
    public const double JobPostRehireNoPromptTimeoutS = 3.5;

    // ================================================================ vong lap
    /// <summary>
    /// Nhịp vòng lặp chính. Bản Python nhắm 90 Hz nhưng log thật đo ~40 Hz (780 dòng / 19.5 s hoạt
    /// động); mọi bộ lọc theo frame (EMA 0.30, miss 9/22, streak) được chỉnh ở nhịp đó, nên ở đây
    /// giữ 25 ms chứ không chạy nhanh hơn dù chụp minimap chỉ tốn ~3 ms.
    /// </summary>
    public const int TickMs = 25;

    public const double FocusGraceS = 1.5;                               // focus_unknown_title_grace_s

    // ================================================================ an uong (SurvivalGaugeDetector)
    /// <summary>
    /// Vùng chụp bao cả hai đồng hồ tròn ở góc dưới trái. KHÔNG dùng lại được <see cref="WorldRoiRef"/>
    /// vì vùng đó dừng ở y=950 còn hai icon nằm ở y≈1047. Rộng hơn tâm icon mỗi bên ~30 px để còn chỗ
    /// cho vành ngoài (rmax 23) và cho người dùng chỉnh tâm vài chục pixel mà không phải sửa ROI.
    /// </summary>
    public static readonly double[] SurvivalRoiRef = { 110, 995, 270, 1080 };

    public const double SurvivalCoreRadiusRef = 10.0;                    // survival_core_radius_px
    public const int SurvivalCoreMinPixels = 16;                         // survival_core_min_pixels (dien tich)

    /// <summary>
    /// Dải bán kính DỰ PHÒNG, dùng khi chưa tự dò được vành — đúng hai số của bản Python
    /// (<c>survival_ring_rmin_px</c>/<c>rmax</c>). Xem <see cref="SurvivalGauge"/> để biết vì sao
    /// tin tuyệt đối vào chúng là nguồn của lỗi "ăn sớm".
    /// </summary>
    public const double SurvivalRingRminRef = 17.0;

    public const double SurvivalRingRmaxRef = 23.0;

    // ---- tu do vanh cung: dai quet, be day, va do lech tam cho phep ----
    /// <summary>Bán kính nhỏ nhất còn có thể là vành. Phải lớn hơn đĩa lõi để hình icon không lẫn vào.</summary>
    public const double SurvivalRingSearchRminRef = 12.0;

    public const double SurvivalRingSearchRmaxRef = 32.0;

    /// <summary>Nửa bề dày dải đo độ phủ góc quanh bán kính đã dò.</summary>
    public const double SurvivalRingHalfWidthRef = 3.0;

    /// <summary>Tâm icon trong config được phép lệch bấy nhiêu pixel mỗi trục; quá thì phải sửa config.</summary>
    public const double SurvivalCenterSearchRef = 4.0;

    /// <summary>
    /// Số pixel tối thiểu trong dải mới dám chốt bán kính (diện tích, nhân <c>_s.Area</c>).
    ///
    /// Đo trên HUD thật ở 2560×1440: vành dày ~2 px, bán kính 28 px → cả vòng đầy chỉ ~350 pixel.
    /// Để 80 nghĩa là cần vành còn khoảng 40 % mới dò; cao hơn nữa thì đúng lúc cần nhất (vạch đang
    /// tụt) lại không dò được. Viền đĩa lõi — thứ duy nhất có thể nhận nhầm — chỉ cho ~50 pixel.
    /// </summary>
    public const int SurvivalCalibMinPixels = 80;

    /// <summary>
    /// Vành phải phủ ít nhất bấy nhiêu phần trăm góc thì mới được dò ĐỘ LỆCH TÂM. Một cung ngắn
    /// khớp với vô số đường tròn nên bộ dò sẽ chọn cái làm nó dày nhất — tâm sai, phần trăm phồng.
    /// </summary>
    public const double SurvivalCalibMinCoveragePct = 70.0;

    /// <summary>Số pixel trong một nan quạt để tính là "còn màu".</summary>
    public const int SurvivalAngleMinPixels = 2;

    /// <summary>
    /// Bản Python chia 180 nan (2° mỗi nan). Ở đây 90 nan (4°) là CỐ Ý: vành HUD chỉ dày ~4 px nên
    /// một nan 2° chỉ nhặt được 3–4 pixel, sát ngưỡng <see cref="SurvivalAngleMinPixels"/> tới mức
    /// răng cưa của một vòng cung bo góc là đủ làm rụng nan — tức là đọc hụt, đúng cái sinh ra lỗi
    /// "ăn sớm". Nan 4° cho gấp đôi biên an toàn, mà độ phân giải 1.1 % vẫn thừa cho một quyết định
    /// nhị phân "trên hay dưới ngưỡng".
    /// </summary>
    public const int SurvivalAngleBins = 90;                             // survival_angle_bins (Python: 180)
    public const double SurvivalScanIntervalS = 0.25;                    // survival_scan_interval_s
    public const double SurvivalEmaAlpha = 0.45;                         // survival_ema_alpha

    /// <summary>Mặc định của <see cref="SurvivalSettings.LowThresholdPct"/> — người dùng chỉnh được.</summary>
    public const double SurvivalLowThresholdPct = 50.0;                  // survival_low_threshold_pct

    public const double SurvivalThresholdMinPct = 10.0;
    public const double SurvivalThresholdMaxPct = 90.0;

    public const int SurvivalLowConfirmScans = 3;                        // survival_low_confirm_scans
    public const double SurvivalPreUseSettleS = 0.20;                    // survival_pre_use_settle_s
    public const double SurvivalKeyHoldS = 0.150;                        // survival_key_hold_ms

    /// <summary>
    /// <c>survival_fixed_wait_s</c>. Đứng CHẾT đúng 10 s rồi mới đọc đồng hồ một lần. Bản Python từng
    /// có đường thoát sớm (<c>survival_use_confirm_min_s/max_s/delta_pct</c>) và bỏ hẳn ở V6.59 vì
    /// đọc giữa animation ăn cho số loạn — ba khoá đó nay nằm chết trong config.json của họ.
    /// </summary>
    public const double SurvivalFixedWaitS = 10.0;

    public const double SurvivalSuccessDeltaPct = 3.0;                   // survival_success_delta_pct
    public const double SurvivalFailedBlockS = 30.0;                     // survival_failed_resource_block_s

    /// <summary>
    /// Bao nhiêu bữa hỏng liên tiếp thì thôi hẳn loại đó cho tới khi tắt/bật lại job. Bấm hết ô mà
    /// đồng hồ không nhúc nhích gần như chắc chắn là hết đồ trong túi — bot không nhìn được túi nên
    /// nó chỉ đoán được thế. Bản Python thử lại vô hạn, mỗi vòng mất ~20 giây đứng chết; ở đây thử
    /// đúng hai bữa (một lần + một lần nữa) rồi dừng.
    /// </summary>
    public const int SurvivalMaxMealAttempts = 2;
    public const double SurvivalPostUseWRearmS = 1.2;                    // survival_post_use_w_rearm_s

    // food_hsv_low/high (14,80,70)-(35,255,255) — vong cung vang/cam.
    public const int FoodHLo = 14, FoodHHi = 35, FoodSMin = 80, FoodVMin = 70;

    // water_hsv_low/high (88,80,70)-(110,255,255) — vong cung xanh cyan.
    public const int WaterHLo = 88, WaterHHi = 110, WaterSMin = 80, WaterVMin = 70;
}
