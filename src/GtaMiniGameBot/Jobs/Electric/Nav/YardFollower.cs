namespace GtaMiniGameBot;

/// <summary>Tên các pha của bộ bám waypoint — hiện nguyên trong dòng trạng thái (<c>MAP_&lt;pha&gt;|…</c>).</summary>
internal static class YardFollow
{
    /// <summary>Không có bản đồ, hoặc <c>NavSettings.UseYardMap=false</c> — nằm im vĩnh viễn.</summary>
    public const string Off = "OFF";

    /// <summary>Chưa có FIX2 nào trong chuyến này — đoạn đầu vẫn lái theo chấm như cũ.</summary>
    public const string WaitFix = "CHỜ_FIX2";

    public const string NoPose = "MẤT_POSE";

    /// <summary>Có pose nhưng chấm đích chưa rơi vào máy nào đã biết.</summary>
    public const string NoMarker = "CHƯA_RÕ_MÁY";

    public const string Locking = "ĐANG_KHOÁ";

    public const string NoPath = "KHÔNG_CÓ_ĐƯỜNG";

    public const string Follow = "BÁM";

    /// <summary>Đã giao lại 3 m cuối cho luồng cũ, tắt tới hết chuyến.</summary>
    public const string Handover = "GIAO_LẠI";

    /// <summary>Thang thoát kẹt đang cầm lái — bám nhường quyền nhưng pose vẫn chạy.</summary>
    public const string Escape = "THOÁT_KẸT";
}

/// <summary>
/// Kết quả một tick của bộ bám. <see cref="Synth"/> là một <see cref="TargetOutput"/> TỔNG HỢP: nó
/// không phải chấm vàng thật, nó là "chấm mà nếu có thì bộ lái sẽ đi đúng đường bản đồ".
/// </summary>
internal sealed class FollowOutput
{
    public bool Active { get; init; }
    public string State { get; init; } = YardFollow.Off;
    public TargetOutput Synth { get; init; } = TargetOutput.Lost;
    public double RelDeg { get; init; } = double.NaN;
    public double DistPx { get; init; } = double.NaN;

    /// <summary>Vector bán kính đưa cho watchdog — độ dài = cung còn lại (px), KHÁC <see cref="DistPx"/>.</summary>
    public double Dx { get; init; } = double.NaN;

    public double Dy { get; init; } = double.NaN;

    public bool NoSprint { get; init; }
    public double RemainingMu { get; init; } = -1;
    public double OffPathMu { get; init; }
    public double TurnAheadDeg { get; init; }
    public int MarkerId { get; init; } = -1;

    public static FollowOutput Idle(string state) => new() { State = state };
}

/// <summary>
/// BÁM WAYPOINT theo bản đồ sân đã dạy — lớp duy nhất của PR3 biết "đi đường nào".
///
/// Vì sao nó TỔNG HỢP một <see cref="TargetOutput"/> thay vì thêm một đường lái riêng:
/// <see cref="NavController.Compute"/> chỉ đọc <c>target.HasPos/Quality/Confidence</c> cộng
/// <c>dist/rel/dx/dy</c> — không bao giờ đọc <c>target.X/Y</c> để lái. Nên cách rẻ nhất (và ít rủi ro
/// nhất) để "đi tới waypoint" là đưa cho bộ lái ĐÃ CHỈNH KỸ một chấm giả đặt đúng hướng waypoint. Mọi
/// khiên/latch/servo/anti-shake của nó tiếp tục làm việc y như với chấm thật.
///
/// Ba con số ra khỏi đây, mỗi con một việc:
///   • <see cref="FollowOutput.RelDeg"/> — thông tin THẬT: góc phải bẻ để đi đúng đường.
///   • <see cref="FollowOutput.DistPx"/> — giữ trong vùng "xa" (sàn <see cref="NavTuning.YardSynthDistMinPx"/>·Px)
///     để servo không rơi vào vùng cận đích: waypoint 3 m chỉ ~6–8 px, để số thật vào thì pass-through
///     và khiên tới đích bật ngay từ waypoint đầu tiên và bot ngừng lái.
///   • <see cref="FollowOutput.Dx"/>/<see cref="FollowOutput.Dy"/> — bán kính CUNG CÒN LẠI cho radial
///     watchdog: đi vòng qua vật cản thì bán kính tới chấm thật đứng phẳng (báo kẹt oan), còn cung đường
///     thì vẫn giảm đều. Kẹt thật thì cả hai đều phẳng.
///
/// Bộ này không chạm input, không chạm ảnh, không đọc đồng hồ hệ thống — <c>--verify-map</c> lùa được
/// bằng pose tổng hợp.
/// </summary>
internal sealed class YardFollower
{
    private readonly YardMap _map;
    private readonly YardGrid _grid;
    private readonly double _px, _ox, _oy;

    // nhan dang may dich
    private int _markerId = -1, _candId = -1, _candTicks, _unlockTicks;

    // duong dang bam
    private List<Vec2> _route;
    private int _routeMarker = -1;
    private double _lastPlanT, _nextPlanEarliest;
    private bool _forcePlan;
    private string _lastPlanWhy = "";

    // vong doi chuyen
    private bool _sawFix2, _handover, _needRegainPlan;
    private double _lastUsableT;

    // "bo bam co that su lam viec khong" — so DUY NHAT thieu trong buoi thu 18/09, luc do khong cach nao
    // biet no giao lai ngay tick dau hay da di duoc mot doan. Ghi cung + cuoi doan bam roi in ra luc giao.
    private double _driveStartT = -1, _driveStartRemainingMu = -1, _driveLastRemainingMu = -1;

    public YardFollower(YardMap map, NavScale s, double originX, double originY)
    {
        _map = map is { Grid: not null, Markers.Count: > 0 } ? map : null;
        _grid = _map?.Grid;
        _px = s.Px > 0 ? s.Px : 1.0;
        _ox = originX;
        _oy = originY;
    }

    public event Action<string> Log;

    private void Emit(string line) => Log?.Invoke(line);

    /// <summary>Có bản đồ dùng được không (không có = hành vi y hệt khi chưa có PR3).</summary>
    public bool HasMap => _map is not null;

    /// <summary>Tick vừa rồi bộ bám có CẦM LÁI không — dùng để chặn reset nghề khi đang bám.</summary>
    public bool Active { get; private set; }

    public int MarkerId => _markerId;

    /// <summary>Cung còn lại (mu) của tick vừa rồi; âm = không bám.</summary>
    public double RemainingMu { get; private set; } = -1;

    public string State { get; private set; } = YardFollow.Off;

    /// <summary>Số lần A* chạy được — <c>--verify-map</c> đọc nó để kiểm "có lập lại đường không".</summary>
    public int PlanCount { get; private set; }

    // ================================================================ mot tick

    /// <param name="pose">Pose sân của tick này (có thể null khi chưa dựng được).</param>
    /// <param name="target">Chấm vàng THẬT — chỉ dùng để biết lúc nào giao lại, không dùng để lái.</param>
    /// <param name="escapeActive">Thang thoát kẹt / chu kỳ nặng đang cầm input.</param>
    public bool Step(double now, YardPose pose, TargetOutput target, WorldMarker world, bool worldAllowed,
                     double dist, double rel, bool escapeActive, out FollowOutput fo)
    {
        Active = false;
        RemainingMu = -1;
        fo = FollowOutput.Idle(YardFollow.Off);
        if (_map is null) { State = YardFollow.Off; return false; }

        // ---------------- 1. pose ----------------
        if (pose is null) return Idle(ref fo, YardFollow.NoPose);

        if (pose.Reinit) Unlock(now, "pose nhảy (respawn/teleport)");
        if (pose.Quality is YardQuality.Fix2 or YardQuality.Fix2Lm) _sawFix2 = true;

        bool usable = pose.Usable && pose.S > 1e-6;
        if (usable)
        {
            _lastUsableT = now;
            if (_needRegainPlan) { _needRegainPlan = false; _forcePlan = true; }
        }
        else
        {
            // Mat pose ngan (< YardLostAfterS) thi van bam theo P suy theo van toc — bo qua lai theo
            // cham ngay rồi quay lai bam sau 0,2 s chi lam bot lac.
            _needRegainPlan = true;
            bool dead = pose.Quality is YardQuality.Lost or YardQuality.None
                        || _lastUsableT <= 0 || now - _lastUsableT > NavTuning.YardLostAfterS;
            if (dead) { _route = null; return Idle(ref fo, YardFollow.NoPose); }
        }

        // Sau minigame game co the nuot delta chuot nen huong suy theo count sai HAN; doi FIX2 dau tien
        // cua chuyen roi moi vao viec (doan dau lai theo cham nhu cu, thuong keo ✕/🍕 vao khung).
        if (!_sawFix2) return Idle(ref fo, YardFollow.WaitFix);

        // ---------------- 2. may dich ----------------
        UpdateMarkerLock(pose, now);
        if (_markerId < 0) return Idle(ref fo, _candId >= 0 ? YardFollow.Locking : YardFollow.NoMarker);
        var marker = _map.Markers.FirstOrDefault(m => m.Id == _markerId);
        if (marker is null) { Unlock(now, "máy biến mất khỏi bản đồ"); return Idle(ref fo, YardFollow.NoMarker); }

        // ---------------- 3. ban giao ----------------
        if (_handover) return Idle(ref fo, YardFollow.Handover);
        if (world.Present && worldAllowed) return Handover(ref fo, now, "thấy cột vàng 3D");
        if (target is { HasPos: true, Quality: "FULL_LOCK" }
            && double.IsFinite(dist) && dist <= NavTuning.YardHandoverDotPx * _px
            && double.IsFinite(rel) && Math.Abs(rel) <= NavTuning.YardHandoverDotDeg)
            return Handover(ref fo, now, $"chấm thật khoá ở {dist:F0}px/{rel:+0;-0}°");

        // ---------------- 4. thang thoat ket ----------------
        // Thang cam lai trong Compute; bo bam nhuong han quyen nhung pose van chay (count van cong don),
        // va khi thang tra quyen thi ep lap lai duong — cho dung sau khi thoat khong con la cho cu.
        if (escapeActive) { _forcePlan = true; return Idle(ref fo, YardFollow.Escape); }

        // ---------------- 5. lap duong ----------------
        var goal = GoalOf(marker);
        bool need = _route is null || _routeMarker != _markerId || _forcePlan
                    || now - _lastPlanT >= NavTuning.YardReplanS;

        int seg = 0;
        double t = 0, off = 0;
        if (_route is not null)
        {
            (seg, t, off) = YardPath.Project(_route, pose.P);
            if (off > NavTuning.YardOffPathReplanMu) need = true;
        }

        if (need && now >= _nextPlanEarliest)
        {
            Plan(now, pose, goal);
            if (_route is not null) (seg, t, off) = YardPath.Project(_route, pose.P);
        }
        if (_route is null) return Idle(ref fo, YardFollow.NoPath);

        // ---------------- 6. bam ----------------
        double remaining = YardPath.Remaining(_route, seg, t);
        double handoverMu = HandoverMuOf(marker);
        if (remaining <= handoverMu)
            return Handover(ref fo, now, $"còn {remaining:F1} mu cung đường (≤ {handoverMu:F0})");

        var w = YardPath.PointAhead(_route, seg, t, NavTuning.YardLookaheadMu);
        double turn = YardPath.TurnAheadDeg(_route, seg, t, NavTuning.YardTurnWindowMu);

        var vs = YardPoseSolver.ToScreen(w, pose.S, pose.Phi, pose.P);
        double vlen = vs.Len;
        if (!double.IsFinite(vlen) || vlen < 1e-6) return Idle(ref fo, YardFollow.NoPath);

        var unit = (1.0 / vlen) * vs;
        double relDeg = YardPoseSolver.Wrap(Math.Atan2(vs.X, -vs.Y) * YardPoseSolver.Rad2Deg);
        double radiusPx = remaining * pose.S;
        double distSynth = Math.Clamp(radiusPx,
                                      NavTuning.YardSynthDistMinPx * _px,
                                      NavTuning.YardSynthDistMaxPx * _px);

        bool noSprint = remaining < NavTuning.YardSprintMinRemainingMu
                        || turn > NavTuning.YardSprintMaxTurnDeg
                        || pose.Conf < NavTuning.YardFollowMinConf;

        Active = true;
        RemainingMu = remaining;
        State = YardFollow.Follow;
        if (_driveStartT < 0) { _driveStartT = now; _driveStartRemainingMu = remaining; }
        _driveLastRemainingMu = remaining;
        fo = new FollowOutput
        {
            Active = true,
            State = YardFollow.Follow,
            Synth = new TargetOutput
            {
                State = "MAP_FOLLOW",
                Visible = true,
                X = _ox + unit.X * distSynth,
                Y = _oy + unit.Y * distSynth,
                Confidence = NavTuning.YardSynthConf,
                CandidateCount = 1,
                Quality = "FULL_LOCK",
                RawGeometry = 1.0
            },
            RelDeg = relDeg,
            DistPx = distSynth,
            Dx = unit.X * radiusPx,
            Dy = unit.Y * radiusPx,
            NoSprint = noSprint,
            RemainingMu = remaining,
            OffPathMu = off,
            TurnAheadDeg = turn,
            MarkerId = _markerId
        };
        return true;
    }

    private bool Idle(ref FollowOutput fo, string state)
    {
        State = state;
        fo = FollowOutput.Idle(state);
        return false;
    }

    private bool Handover(ref FollowOutput fo, double now, string why)
    {
        if (!_handover)
        {
            _handover = true;
            _route = null;
            string drove = _driveStartT < 0
                ? "CHƯA BÁM ĐƯỢC BƯỚC NÀO"
                : $"đã bám {Math.Max(0.0, _driveStartRemainingMu - _driveLastRemainingMu):F1} mu " +
                  $"trong {now - _driveStartT:F1}s";
            Emit($"[BẢN ĐỒ] giao lại đoạn cuối cho luồng cũ — máy {_markerId}, {why} ({drove})");
        }
        return Idle(ref fo, YardFollow.Handover);
    }

    // ================================================================ may dich

    /// <summary>Đích của một máy: tư thế tiếp cận nếu có, không thì chỗ người đã ĐỨNG lúc bảng mở.</summary>
    public static Vec2 GoalOf(YardMarker m)
    {
        if (m.Approach is not null) return new Vec2(m.Approach.X, m.Approach.Y);
        if (m.Stand is { Length: 2 }) return new Vec2(m.Stand[0], m.Stand[1]);
        return new Vec2(m.X, m.Y);
    }

    /// <summary>
    /// Bán kính giao lại. Tư thế chỉ quan sát một lần, hoặc máy suy từ vị trí đứng, thì số đo có thể
    /// lệch vài mu — bám sát tới 6 mu là bám vào một con số không có thật.
    /// </summary>
    private static double HandoverMuOf(YardMarker m)
    {
        bool weak = m.FromStand || m.Approach is null || m.Approach.LowConfidence;
        return weak ? NavTuning.YardHandoverLowConfMu : NavTuning.YardHandoverMu;
    }

    private void UpdateMarkerLock(YardPose pose, double now)
    {
        if (pose.T is null) return;
        var t = pose.T.Value;

        if (_markerId >= 0)
        {
            var cur = _map.Markers.FirstOrDefault(m => m.Id == _markerId);
            double d = cur is null ? double.PositiveInfinity : (new Vec2(cur.X, cur.Y) - t).Len;
            if (d > NavTuning.YardMarkerUnlockMu)
            {
                if (++_unlockTicks >= NavTuning.YardMarkerUnlockTicks)
                    Unlock(now, $"điểm vàng đã rời máy {_markerId} {d:F0} mu — chuyến mới");
            }
            else _unlockTicks = 0;
            if (_markerId >= 0) return;
        }

        int best = -1;
        double bestD = double.PositiveInfinity;
        foreach (var m in _map.Markers)
        {
            double d = (new Vec2(m.X, m.Y) - t).Len;
            if (d < bestD) { bestD = d; best = m.Id; }
        }
        if (best < 0 || bestD > NavTuning.YardMarkerIdRadiusMu)
        {
            _candId = -1;
            _candTicks = 0;
            return;
        }

        if (best != _candId) { _candId = best; _candTicks = 1; }
        else _candTicks++;

        if (_candTicks >= NavTuning.YardMarkerLockTicks)
        {
            _markerId = best;
            _candId = -1;
            _candTicks = 0;
            _unlockTicks = 0;
            _handover = false;
            _forcePlan = true;
            var mk = _map.Markers.First(m => m.Id == best);
            Emit($"[BẢN ĐỒ] khoá máy {best} (T lệch {bestD:F1} mu) — đích " +
                 $"{(mk.Approach is null ? "vị trí đứng" : "tư thế tiếp cận")} {GoalOf(mk)}" +
                 (HandoverMuOf(mk) > NavTuning.YardHandoverMu ? ", tư thế yếu → giao lại sớm" : ""));
        }
    }

    private void Unlock(double now, string why)
    {
        if (_markerId >= 0) Emit($"[BẢN ĐỒ] mở khoá máy {_markerId}: {why}");
        _markerId = -1;
        _candId = -1;
        _candTicks = 0;
        _unlockTicks = 0;
        _handover = false;
        _route = null;
        _routeMarker = -1;
        _lastPlanWhy = "";
        _driveStartT = -1;
        _driveStartRemainingMu = -1;
        _driveLastRemainingMu = -1;
    }

    // ================================================================ lap duong

    private void Plan(double now, YardPose pose, Vec2 goal)
    {
        _lastPlanT = now;
        _nextPlanEarliest = now + NavTuning.YardReplanMinS;
        _forcePlan = false;

        var route = YardPath.PlanRoute(_grid, pose.P, goal, NavTuning.YardOffGridSearchMu, out string why);
        if (route is null || route.Count < 2)
        {
            if (_lastPlanWhy != why)
            {
                _lastPlanWhy = why;
                Emit($"[BẢN ĐỒ] chưa lập được đường tới máy {_markerId}: {why} (lái như cũ)");
            }
            _route = null;
            _routeMarker = -1;
            return;
        }

        bool first = _route is null || _routeMarker != _markerId;
        PlanCount++;
        _route = route;
        _routeMarker = _markerId;
        _lastPlanWhy = "";
        if (first)
            Emit($"[BẢN ĐỒ] lập đường tới máy {_markerId}: {route.Count} khúc, " +
                 $"{YardPath.Remaining(route, 0, 0):F0} mu từ {pose.P}");
    }

    // ================================================================ ben thoat ket theo ban do

    /// <summary>
    /// Bên nào quanh mình còn nhiều ô ĐÃ ĐI hơn (+1 phải, −1 trái, 0 = không rõ / pose yếu).
    ///
    /// Đây là điểm mở rộng của <see cref="NavEscapeLadder.ChooseSide"/>: mật độ biên Canny chỉ nhìn
    /// được một khung hình trước mặt, còn bản đồ biết bên nào thật sự có lối — người đã đi bên đó
    /// hàng chục lần.
    /// </summary>
    public int FreeSideOf(YardPose pose)
    {
        if (_grid is null || pose is null || !pose.Usable || pose.Conf < NavTuning.YardFollowMinConf) return 0;

        double th = pose.ThetaDeg * YardPoseSolver.Deg2Rad;
        var fwd = new Vec2(Math.Cos(th), Math.Sin(th));
        var right = new Vec2(-fwd.Y, fwd.X);                 // y-xuong: quay +90° la ben phai nguoi choi

        double r = NavTuning.YardEscapeSideRadiusMu;
        int cells = Math.Max(1, (int)Math.Ceiling(r / Math.Max(0.01, _grid.CellMu)));
        var (cx, cy) = _grid.CellOf(pose.P);
        int left = 0, rightN = 0;

        for (int y = cy - cells; y <= cy + cells; y++)
        for (int x = cx - cells; x <= cx + cells; x++)
        {
            if (!_grid.IsFree(x, y)) continue;
            var d = _grid.CenterOf(x, y) - pose.P;
            if (d.Len > r || Vec2.Dot(d, fwd) < 0) continue;   // chi dem nua truoc mat
            double side = Vec2.Dot(d, right);
            if (side > 0.5) rightN++;
            else if (side < -0.5) left++;
        }

        if (rightN > left * 1.15 + 2) return 1;
        if (left > rightN * 1.15 + 2) return -1;
        return 0;
    }
}
