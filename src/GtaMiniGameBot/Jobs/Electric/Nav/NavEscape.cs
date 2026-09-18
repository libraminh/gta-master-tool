namespace GtaMiniGameBot;

/// <summary>Tên các pha của thang thoát kẹt. Tiền tố <c>ESC_</c> là thứ lớp sàn phím nhận ra (xem <see cref="NavKey.NoAutoW"/>).</summary>
internal static class NavEscape
{
    public const string Strafe = "ESC_STRAFE";
    public const string StrafeW = "ESC_STRAFE_W";
    public const string Backoff = "ESC_BACKOFF";
    public const string Turn = "ESC_TURN";
    public const string Clear = "ESC_CLEAR";
    public const string StrafeFlip = "ESC_STRAFE_FLIP";
    public const string Legacy = "ESC_KET1";
    public const string Probe = "ESC_PROBE";
}

/// <summary>
/// Một tick của thang: tập phím muốn giữ, tên pha, và chuột muốn gì. KHÔNG chạm input — người gọi
/// (<see cref="NavController"/>) dịch sang <see cref="NavInput"/>.
/// </summary>
internal readonly struct EscapeAction
{
    /// <summary>Tập phím, có thể mang <see cref="NavKey.NoAutoW"/>.</summary>
    public readonly NavKey Keys;

    public readonly string State;
    public readonly int Rung;

    /// <summary>Bên đang dùng cho pha này (+1 phải, −1 trái) — bậc 3 đã đảo sẵn ở đây.</summary>
    public readonly int Side;

    /// <summary>Có lái theo đích trong pha này không.</summary>
    public readonly bool Servo;

    /// <summary>Trần yaw của servo (cps). 0 = không kẹp (pha thăm dò lái như bình thường).</summary>
    public readonly double YawCapCps;

    /// <summary>Pha QUAY đếm count: người gọi phát <see cref="TurnRateCps"/> × <see cref="Side"/> mỗi tick.</summary>
    public readonly bool WantTurn;

    public readonly double TurnRateCps;

    /// <summary>Bậc 4: giao lại cho KET1 cũ (<c>NavController.RecoveryStep</c>).</summary>
    public readonly bool Legacy;

    public EscapeAction(NavKey keys, string state, int rung, int side, bool servo, double yawCapCps,
                        bool wantTurn, double turnRateCps, bool legacy)
    {
        Keys = keys;
        State = state;
        Rung = rung;
        Side = side;
        Servo = servo;
        YawCapCps = yawCapCps;
        WantTurn = wantTurn;
        TurnRateCps = turnRateCps;
        Legacy = legacy;
    }
}

/// <summary>
/// THANG THOÁT KẸT — máy pha THUẦN (không chạm input, không đọc đồng hồ, không log) thay cho cú nhảy
/// một-bài KET1 ở hai nguồn kẹt MINIMAP và WORLD.
///
/// Vì sao thang chứ không phải một bài giỏi hơn: KET1 làm đúng một việc (quay đầu 168° → bẻ ngang 42°
/// → W 650 ms) tốn ~2,1 s và KHÔNG BAO GIỜ biết nó có thoát được không, nên khi kẹt lại nó lặp đúng
/// cú đó, chỉ đảo bên — thành ra lắc trái/phải tại chỗ mỗi 3,5–4 s (đọc được trong log thật). Người
/// chơi thật thì thử cái RẺ nhất trước: nhích ngang nửa thân người. Thang này xếp các cách thoát theo
/// giá (thời gian mất đi + hướng bị mất), và sau MỖI bậc có một pha THĂM DÒ 0,45 s để phán "thoát
/// chưa" — hỏng thì leo bậc kế NGAY, không nghỉ.
///
///   bậc 0  <c>ESC_STRAFE</c>       A/D thuần + NoAutoW, servo kẹp 300 cps   0,55 s
///   bậc 1  <c>ESC_STRAFE_W</c>     W + A/D cùng bên                          0,90 s
///   bậc 2  <c>ESC_BACKOFF</c> → <c>ESC_TURN</c> (45° đếm count) → <c>ESC_CLEAR</c> (W)   ~1,5 s
///   bậc 3  <c>ESC_STRAFE_FLIP</c>  như bậc 1 nhưng ĐẢO bên                   0,90 s
///   bậc 4  <c>ESC_KET1</c>         KET1 cũ nguyên vẹn                        ~2,1 s
///   rồi lặp 2 → 3 → 4, mỗi vòng đảo bên gốc; watchdog 30 s hiện có vẫn là lưới cuối.
///
/// ĐỢT (<see cref="Episode"/>) là khái niệm giữ cho thang không bị reset về bậc 0 mãi: kẹt lại ở CÙNG
/// CHỖ trong 4 s thì đó vẫn là cùng một vật cản → leo bậc, giữ bên. Chỗ khác thì về bậc 0, nhưng vẫn
/// GIỮ BÊN nếu đợt trước vừa xong dưới 8 s — đi vòng một phía thì phải vòng hết một phía.
///
/// Góc của bậc 2 đo bằng ĐẾM COUNT (<see cref="NavInput.XSentCounts"/>) chứ không bằng thời gian, vì
/// luồng chuột 240 Hz có ramp hai đầu: KET1 đặt đích 168° với cap 950 ms ở 420 cps mà thực tế chỉ quay
/// nổi ≈94°. Xem <see cref="NavRestartTurn"/>.
/// </summary>
internal sealed class NavEscapeLadder
{
    /// <summary>Một đợt kẹt — sống qua nhiều lần "kẹt lại" ở cùng chỗ.</summary>
    public sealed class Episode
    {
        public int Serial;
        public double StartT;

        /// <summary>Bán kính tới đích lúc mở đợt (px màn) — mốc để nhận ra "vẫn cùng chỗ".</summary>
        public double StartDist;

        public double StartRel;

        /// <summary>Bên thoát GỐC (+1 phải, −1 trái). Bậc 3 dùng bên ngược lại.</summary>
        public int Side;

        public int Rung;

        /// <summary>Số lần thang được mở lại trong đợt này (lần đầu = 1).</summary>
        public int Attempts;

        public string Source;

        /// <summary>Lúc đợt đóng (thoát được, hoặc bị huỷ). Còn 0 khi đợt đang chạy.</summary>
        public double LastEndT;
    }

    private readonly double _px;
    private readonly double _mouseMultiplier;

    private Episode _ep;
    private Episode _lastEp;
    private int _serial;

    private string _phase;
    private double _phaseStart;
    private double _lastDist = double.NaN;
    private double _probeStartDist = double.NaN;

    private long _turnCountsTarget, _turnBest;
    private double _turnLastGainT, _turnCapS;

    /// <param name="px">
    /// <see cref="NavScale.Px"/> — mọi ngưỡng pixel thô nhân với nó (ngưỡng của bản Python chỉnh ở 1080p).
    /// </param>
    /// <param name="mouseMultiplier">
    /// <see cref="NavSettings.MouseSpeedMultiplier"/> — chỉ dùng để SUY RA cap thời gian của pha quay.
    /// </param>
    public NavEscapeLadder(double px, double mouseMultiplier)
    {
        _px = px > 0 ? px : 1.0;
        _mouseMultiplier = mouseMultiplier > 0 ? mouseMultiplier : 1.0;
        _turnCountsTarget = NavRestartTurn.CountsForDegrees(NavTuning.EscapeTurnDeg);
        _turnCapS = NavRestartTurn.HardCapS(NavTuning.EscapeTurnDeg, NavTuning.Ket1UturnRateFarCps, _mouseMultiplier);
    }

    public bool Active => _ep is not null;

    public string Phase => _phase;

    public int Rung => _ep?.Rung ?? -1;

    public int Serial => _ep?.Serial ?? _lastEp?.Serial ?? 0;

    public int Attempts => _ep?.Attempts ?? 0;

    public string Source => _ep?.Source;

    /// <summary>Bên của pha đang chạy (bậc 3 đã đảo).</summary>
    public int Side => _ep is null ? 0 : (_ep.Rung == 3 ? -_ep.Side : _ep.Side);

    /// <summary>Đổi số mỗi lần vào pha quay — người gọi neo lại mốc đếm count khi thấy số này đổi.</summary>
    public int TurnSerial { get; private set; }

    /// <summary>Lần <see cref="Begin"/> gần nhất có NHẬP vào đợt cũ không (kẹt lại cùng chỗ).</summary>
    public bool LastBeginJoined { get; private set; }

    public long TurnCountsTarget => _turnCountsTarget;

    /// <summary>Kết quả pha quay vừa xong (để log): count đã gửi và lý do dừng.</summary>
    public long LastTurnCounts { get; private set; }

    public string LastTurnWhy { get; private set; } = "";

    /// <summary>Bán kính đầu/cuối pha thăm dò vừa xong (để log).</summary>
    public double LastProbeFromDist { get; private set; } = double.NaN;

    public double LastProbeToDist { get; private set; } = double.NaN;

    /// <summary>Đợt vừa đóng — người gọi đọc để in tổng kết sau khi <see cref="Step"/> trả null.</summary>
    public Episode ClosedEpisode => _lastEp;

    /// <summary>Số giây đợt đang chạy đã tốn (0 khi không có đợt).</summary>
    public double ElapsedS(double now) => _ep is null ? 0.0 : Math.Max(0.0, now - _ep.StartT);

    // ================================================================ may pha thuan

    /// <summary>Pha đầu của một bậc.</summary>
    public static string PhaseForRung(int rung) => rung switch
    {
        0 => NavEscape.Strafe,
        1 => NavEscape.StrafeW,
        2 => NavEscape.Backoff,
        3 => NavEscape.StrafeFlip,
        _ => NavEscape.Legacy
    };

    /// <summary>
    /// Bậc kế tiếp. Hết bậc 4 thì quay lại bậc 2 và ĐẢO bên gốc: ba bậc rẻ nhất đã thử cả hai bên rồi,
    /// cái còn lại chỉ có thể là "đi vòng xa hơn về phía kia".
    /// </summary>
    public static (int rung, bool flipSide) NextRung(int rung) => rung >= 4 ? (2, true) : (rung + 1, false);

    /// <summary>
    /// Pha kế tiếp TRONG một bậc, thuần theo thời gian/count — <see cref="NavRestartTurn.Advance"/> cùng
    /// khuôn. Trả về chính nó khi chưa tới lúc. Pha <see cref="NavEscape.Probe"/> và
    /// <see cref="NavEscape.Legacy"/> không kết thúc bằng thời gian nên không có ở đây.
    /// </summary>
    public static string Advance(string phase, double elapsed, long countsDone, long countsTarget,
                                bool stalled, double capS) => phase switch
    {
        NavEscape.Strafe when elapsed >= NavTuning.EscapeStrafeS0 => NavEscape.Probe,
        NavEscape.StrafeW when elapsed >= NavTuning.EscapeStrafeS1 => NavEscape.Probe,
        NavEscape.StrafeFlip when elapsed >= NavTuning.EscapeStrafeS1 => NavEscape.Probe,
        NavEscape.Backoff when elapsed >= NavTuning.EscapeBackoffS => NavEscape.Turn,
        NavEscape.Turn when countsDone >= countsTarget || stalled || elapsed >= capS => NavEscape.Clear,
        NavEscape.Clear when elapsed >= NavTuning.EscapeClearS => NavEscape.Probe,
        _ => phase
    };

    /// <summary>
    /// Bên thoát lúc MỞ đợt: vật cản đo được &gt; góc tới đích &gt; ngược bên đợt trước.
    ///
    /// <paramref name="rel"/> ở nguồn WORLD là góc GIẢ (±25° / 0) do <c>WorldStep</c> dựng từ lệch ngang
    /// của marker so với <see cref="NavTuning.WorldDirectCenterAcquirePx"/>, nên cùng một ngưỡng 10° ở
    /// đây đúng cho cả hai nguồn.
    /// </summary>
    public static int ChooseSide(double rel, int obstacleSide, int lastSide)
    {
        if (obstacleSide > 0) return 1;
        if (obstacleSide < 0) return -1;
        if (double.IsFinite(rel) && Math.Abs(rel) >= 10.0) return rel > 0 ? 1 : -1;
        return lastSide != 0 ? -lastSide : 1;
    }

    // ================================================================ dot

    /// <summary>
    /// Mở thang. Trả false khi đang có đợt chạy (kẹt báo lại giữa chừng là chuyện thường — bậc đang
    /// chạy giữ quyền).
    /// </summary>
    public bool Begin(double now, double dist, double rel, int obstacleSide, string source)
    {
        if (_ep is not null) return false;

        bool sameSpot = _lastEp is not null
                        && now - _lastEp.LastEndT < NavTuning.EscapeEpisodeJoinS
                        && double.IsFinite(dist) && double.IsFinite(_lastEp.StartDist)
                        && Math.Abs(dist - _lastEp.StartDist) < NavTuning.EscapeSameSpotPx * _px;
        LastBeginJoined = sameSpot;

        if (sameSpot)
        {
            // Cung vat can → leo bac, giu ben, giu ca StartT (do "dot dai nhat" phai tinh ca cho nay).
            _ep = _lastEp;
            var (rung, flip) = NextRung(_ep.Rung);
            _ep.Rung = rung;
            if (flip) _ep.Side = -_ep.Side;
            _ep.Attempts++;
            _ep.Source = source;
            _ep.LastEndT = 0;
        }
        else
        {
            bool keepSide = _lastEp is not null && _lastEp.Side != 0
                            && now - _lastEp.LastEndT < NavTuning.EscapeKeepSideS;
            _ep = new Episode
            {
                Serial = ++_serial,
                StartT = now,
                StartDist = dist,
                StartRel = rel,
                Side = keepSide ? _lastEp.Side : ChooseSide(rel, obstacleSide, _lastEp?.Side ?? 0),
                Rung = 0,
                Attempts = 1,
                Source = source
            };
        }

        _lastEp = null;
        _lastDist = dist;
        EnterPhase(now, PhaseForRung(_ep.Rung));
        return true;
    }

    /// <summary>
    /// Một tick. Trả <c>null</c> khi đợt vừa ĐÓNG (thoát được) — người gọi đọc
    /// <see cref="ClosedEpisode"/> để in tổng kết rồi quay về lái bình thường.
    /// </summary>
    /// <param name="progressing">
    /// Bằng chứng tiến bộ NGOÀI bán kính minimap — hiện là <c>worldProgressing</c> của world drive.
    /// (PR3 thêm: pose dịch ≥ 1 ref px.)
    /// </param>
    /// <param name="countsDone">|count chuột đã gửi| kể từ lúc <see cref="TurnSerial"/> đổi.</param>
    public EscapeAction? Step(double now, double dist, bool progressing, long countsDone)
    {
        if (_ep is null) return null;
        if (double.IsFinite(dist)) _lastDist = dist;

        // Bac 4 do KET1 cu lai; no bao xong qua FinishRung.
        if (_phase == NavEscape.Legacy) return Action();

        double elapsed = now - _phaseStart;

        if (_phase == NavEscape.Probe)
        {
            bool unstuck = ProbeUnstuck(dist, progressing);
            if (unstuck || elapsed >= NavTuning.EscapeProbeS)
            {
                MarkProbeResult(now, unstuck);
                if (_ep is null) return null;
            }
            return Action();
        }

        if (_phase == NavEscape.Turn && countsDone > _turnBest)
        {
            _turnBest = countsDone;
            _turnLastGainT = now;
        }
        bool stalled = _phase == NavEscape.Turn && now - _turnLastGainT >= NavTuning.EscapeTurnStallS;

        string next = Advance(_phase, elapsed, countsDone, _turnCountsTarget, stalled, _turnCapS);
        if (next != _phase)
        {
            if (_phase == NavEscape.Turn)
            {
                LastTurnCounts = countsDone;
                LastTurnWhy = countsDone >= _turnCountsTarget ? "đủ count" : stalled ? "TẮC" : "hết cap";
            }
            EnterPhase(now, next);
        }
        return Action();
    }

    /// <summary>Đã thoát chưa: bán kính tới đích giảm đủ, hoặc người gọi có bằng chứng khác.</summary>
    public bool ProbeUnstuck(double dist, bool progressing)
    {
        if (progressing) return true;
        if (!double.IsFinite(dist) || !double.IsFinite(_probeStartDist)) return false;
        return _probeStartDist - dist >= NavTuning.EscapeProgressPx * _px;
    }

    /// <summary>
    /// Chốt pha thăm dò: thoát thì ĐÓNG đợt, không thoát thì sang bậc kế NGAY (không nghỉ — nghỉ chính
    /// là thứ làm bản cũ mất 3,5 s mỗi vòng).
    /// </summary>
    public void MarkProbeResult(double now, bool unstuck)
    {
        if (_ep is null || _phase != NavEscape.Probe) return;
        LastProbeFromDist = _probeStartDist;
        LastProbeToDist = _lastDist;

        if (unstuck) { Close(now); return; }

        var (rung, flip) = NextRung(_ep.Rung);
        _ep.Rung = rung;
        if (flip) _ep.Side = -_ep.Side;
        _ep.Attempts++;
        EnterPhase(now, PhaseForRung(_ep.Rung));
    }

    /// <summary>Bậc 4 (KET1 cũ) báo đã chạy xong → sang thăm dò. Không dùng cho bậc khác.</summary>
    public void FinishRung(double now)
    {
        if (_ep is null || _phase != NavEscape.Legacy) return;
        EnterPhase(now, NavEscape.Probe);
    }

    /// <summary>Huỷ đợt (world marker hiện lại và ĐANG TIẾN, reset nghề, reset camera…).</summary>
    public void Cancel(double now)
    {
        if (_ep is null) return;
        Close(now);
    }

    /// <summary>Xoá sạch mọi trí nhớ đợt — dùng ở <c>ResetTransient</c> (đổi cảnh, đổi chuyến).</summary>
    public void Reset()
    {
        _ep = null;
        _lastEp = null;
        _phase = null;
        _phaseStart = 0;
        _probeStartDist = double.NaN;
        _lastDist = double.NaN;
    }

    private void Close(double now)
    {
        _ep.LastEndT = now;
        _lastEp = _ep;
        _ep = null;
        _phase = null;
    }

    private void EnterPhase(double now, string phase)
    {
        _phase = phase;
        _phaseStart = now;
        if (phase == NavEscape.Turn)
        {
            _turnBest = 0;
            _turnLastGainT = now;
            TurnSerial++;
        }
        if (phase == NavEscape.Probe) _probeStartDist = _lastDist;
    }

    private EscapeAction Action()
    {
        int rung = _ep.Rung;
        int side = Side;
        NavKey strafe = side > 0 ? NavKey.D : NavKey.A;
        double cap = NavTuning.EscapeStrafeYawCapCps;

        return _phase switch
        {
            // Truot ngang thuan: NoAutoW vi lop san phim mac dinh ep W vao moi tap.
            NavEscape.Strafe => new EscapeAction(strafe | NavKey.NoAutoW, _phase, rung, side, true, cap, false, 0, false),
            NavEscape.StrafeW => new EscapeAction(NavKey.W | strafe, _phase, rung, side, true, cap, false, 0, false),
            NavEscape.StrafeFlip => new EscapeAction(NavKey.W | strafe, _phase, rung, side, true, cap, false, 0, false),
            NavEscape.Backoff => new EscapeAction(NavKey.S, _phase, rung, side, false, 0, false, 0, false),
            NavEscape.Turn => new EscapeAction(NavKey.NoAutoW, _phase, rung, side, false, 0, true,
                                               NavTuning.Ket1UturnRateFarCps, false),
            NavEscape.Clear => new EscapeAction(NavKey.W, _phase, rung, side, false, 0, false, 0, false),
            NavEscape.Legacy => new EscapeAction(NavKey.None, _phase, rung, side, false, 0, false, 0, true),
            // Tham do: lai nhu binh thuong, khong kep tran — phai thay duoc no CO tien that.
            _ => new EscapeAction(NavKey.W, NavEscape.Probe, rung, side, true, 0, false, 0, false)
        };
    }
}
