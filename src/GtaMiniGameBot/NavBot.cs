namespace GtaMiniGameBot;

internal enum NavStopReason
{
    /// <summary>Đã tới nơi và bấm E, minigame mở ra.</summary>
    Arrived,

    UserStopped,

    /// <summary>Hết số lượt thử mà không tới được.</summary>
    Unreachable,

    /// <summary>Thiếu mẫu chữ hoặc vùng đọc.</summary>
    NotConfigured,

    InputFailed,
    Error
}

/// <summary>
/// Tự đi tới điểm làm việc rồi bấm E. Không giải minigame — bảng hiện ra là xong việc của lớp này,
/// <see cref="ElectricBot"/> nhận tiếp.
///
/// LUẬT LÁI, và vì sao nó ngắn hơn bản Python nhiều: trong game này chuột chỉ xoay CAMERA, nhân
/// vật không xoay theo, mà giữ W thì nhân vật đi THEO HƯỚNG CAMERA. Nên chỉ cần "xoay camera cho
/// mục tiêu về giữa rồi giữ W" — không cần biết thân nhân vật đang hướng nào. Bản Python phải nuôi
/// cả <c>PlayerDetector</c> đo hướng mũi tên trắng bằng <c>minEnclosingTriangle</c> chỉ để trả lời
/// câu hỏi đó.
///
/// Hệ quả thứ hai, quan trọng hơn: W+A trượt chéo trái THEO HỆ CAMERA, tức né vật cản mà KHÔNG mất
/// hướng. Hết vật cản là đã đúng hướng sẵn. Bản Python né bằng cách xoay camera nên mỗi lần né là
/// một lần mất hướng, và nó phải đẻ ra <c>VectorStuckWatchdog</c> (287 dòng) + <c>KET1/KET2</c> để
/// gỡ lại.
///
/// BA TÍN HIỆU, dùng theo cự ly:
///   xa  → chấm vàng trên minimap cho GÓC (<see cref="MinimapReader"/>);
///   gần → mốc vàng 3D cho lệch ngang, to hàng nghìn pixel thay vì một chấm 10 px
///         (<see cref="MarkerReader"/>);
///   tới → prompt "[E] TƯƠNG TÁC" (<see cref="PromptReader"/>). Đây là tín hiệu TỚI NƠI duy nhất
///         được tin: chính game trả lời, không phải mình đoán theo bán kính pixel.
///
/// Còn "có đang tiến tới đích không" thì trọng tài là CỰ LY chấm minimap
/// (<see cref="ProgressTracker"/>), vì xoay camera đổi góc chứ không đổi cự ly. Sai phân khung
/// trên hai dải đất (<see cref="GroundFlow"/>) chỉ là tín hiệu nhanh phụ trợ — đo trong game nó
/// sai cả hai chiều.
/// </summary>
internal sealed class NavBot
{
    private const ushort VK_W = 0x57;
    private const ushort VK_A = 0x41;
    private const ushort VK_D = 0x44;
    private const ushort VK_S = 0x53;
    private const ushort VK_E = 0x45;
    private const ushort VK_SPACE = 0x20;

    private readonly ElectricConfig _cfg;
    private readonly NavSettings _nav;
    private readonly Screen _screen;
    private readonly ElectricProfile _p;

    private CancellationTokenSource _cts;
    private Thread _thread;
    private bool _windowWarned;

    private MinimapReader _mini;
    private MarkerReader _marker;
    private PromptReader _prompt;
    private GroundFlow _ground;
    private readonly ProgressTracker _progress;
    private readonly EscapeLadder _escape;

    /// <summary>Mốc chấm điểm quãng đi lại sau một bậc thoát kẹt. 0 = không trong quãng đó.</summary>
    private long _judgeAt;

    /// <summary>Đi lệch về phía vừa trượt cho tới mốc này, để khỏi chui lại vào khe.</summary>
    private long _detourUntil;

    private bool _detourSide;

    // ---- trang thai giu phim, de khong ban lai SendInput moi vong ----
    private bool _wDown, _aDown, _dDown, _sDown, _shiftDown;

    // ---- hieu chuan chuot ----
    private double _countsPerDeg;
    private int _yawSign = 1;
    private bool _calibrated;
    private int _calibTries;
    private int _wrongWayStreak;

    /// <summary>Số count ngang đã bắn kể từ lần đọc mốc gần nhất — đầu vào của phép kiểm thị sai.</summary>
    private int _yawSinceHeavy;

    public NavBot(ElectricConfig cfg, Screen screen, ElectricProfile profile)
    {
        _cfg = cfg;
        _nav = cfg.Nav;
        _screen = screen;
        _p = profile;
        _progress = new ProgressTracker(cfg.Nav);
        _escape = new EscapeLadder(cfg.Nav);

        // Nap ti le da do lan truoc de quet theo bac chay duoc NGAY, khong phai cho toi luc thay
        // cham moi hieu chuan duoc. Nhung KHONG dat _calibrated = true: thay cham roi thi van do
        // lai tu te, nen doi do nhay chuot trong game cung tu sua duoc.
        if (_nav.CountsPerDegSaved > 0)
        {
            _countsPerDeg = _nav.CountsPerDegSaved;
            _yawSign = _nav.YawSignSaved;
        }
    }

    public bool Running => _thread is { IsAlive: true };

    public event Action<string> Log;

    public event Action<NavStopReason, string> Stopped;

    /// <summary>Đo xong count/độ và dấu xoay — bên ngoài lưu lại để lần chạy sau khỏi đo lại.</summary>
    public event Action<double, int> Calibrated;

    /// <summary>
    /// Hỏi bên ngoài "minigame đã hiện chưa" sau khi bấm E. <see cref="ElectricBot"/> cắm hai bộ
    /// thăm dò sẵn có của nó vào đây — NavBot không tự dò panel/bảng, đó là việc của lớp kia.
    /// </summary>
    public Func<bool> PanelVisible { get; set; }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "NavBot" };
        _thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    public void StopAndWait(int ms = 2500)
    {
        _cts?.Cancel();
        var t = _thread;
        if (t is null || !t.IsAlive) return;
        try { t.Join(ms); } catch { }
    }

    public static string TenLyDo(NavStopReason r) => r switch
    {
        NavStopReason.Arrived => "đã tới điểm làm việc",
        NavStopReason.UserStopped => "người dùng bấm dừng",
        NavStopReason.Unreachable => "không tới được điểm làm việc",
        NavStopReason.NotConfigured => "chưa hiệu chuẩn đủ",
        NavStopReason.InputFailed => "không gửi được phím/chuột vào game",
        _ => "lỗi"
    };

    // ================================================================ vong doi

    private void Run(CancellationToken ct)
    {
        var reason = NavStopReason.UserStopped;
        string message = "người dùng bấm dừng";

        try
        {
            if (!OpenReaders(out string problem))
            {
                reason = NavStopReason.NotConfigured;
                message = problem;
                Emit("dừng: " + message);
                return;
            }

            Emit($"bắt đầu. minimap {_mini.Region.Width}×{_mini.Region.Height}, " +
                 $"băng prompt {_prompt.Region.Width}×{_prompt.Region.Height}, " +
                 $"hộp bóng nhân vật {_marker.SilhouetteBox.Width}×{_marker.SilhouetteBox.Height}.");

            WaitWindow(ct);
            NormalizePitch(ct);

            for (int attempt = 1; attempt <= _nav.MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Emit($"— lượt {attempt}/{_nav.MaxAttempts} —");

                var outcome = Approach(ct, out string detail);
                if (outcome == NavStopReason.Arrived)
                {
                    reason = NavStopReason.Arrived;
                    message = detail;
                    Emit("tới nơi: " + detail);
                    return;
                }

                Emit($"lượt {attempt} hỏng: {detail}");
                if (attempt < _nav.MaxAttempts) Recover(ct);
            }

            reason = NavStopReason.Unreachable;
            message = $"thử {_nav.MaxAttempts} lượt đều không tới";
            Emit("dừng: " + message);
        }
        catch (OperationCanceledException)
        {
            reason = NavStopReason.UserStopped;
            message = "người dùng bấm dừng";
        }
        catch (InvalidOperationException ex)
        {
            // InputSender nem cai nay khi SendInput khong lot — thuong la game chay quyen Admin ma
            // app thi khong. Thong diep cua no da noi ro cach sua.
            reason = NavStopReason.InputFailed;
            message = ex.Message;
            Emit(message);
        }
        catch (Exception ex)
        {
            reason = NavStopReason.Error;
            message = ex.Message;
            Emit("lỗi: " + message);
        }
        finally
        {
            ReleaseAll();
            _mini?.Dispose();
            _marker?.Dispose();
            _prompt?.Dispose();
            _ground?.Dispose();
            _mini = null; _marker = null; _prompt = null; _ground = null;
            Stopped?.Invoke(reason, message);
        }
    }

    private bool OpenReaders(out string problem)
    {
        _mini = MinimapReader.Open(_cfg, _screen, _p, out problem);
        if (_mini is null) return false;

        _marker = MarkerReader.Open(_cfg, _screen, _p, out problem);
        if (_marker is null) return false;

        _prompt = PromptReader.Open(_cfg, _screen, _p, out problem);
        if (_prompt is null)
        {
            // Khong ha xuong "go mu" nhu WoodBot: khong co prompt thi khong biet luc nao toi noi,
            // ma doan bang ban kinh pixel chinh la thu bo di co y thuc.
            problem = problem + " — mở tab Thợ điện, bấm “Khoanh mẫu TƯƠNG TÁC…” rồi chạy lại";
            return false;
        }

        try { _ground = new GroundFlow(_screen, _p, _nav); }
        catch (Exception ex)
        {
            problem = "không mở được dải đo tiến độ: " + ex.Message;
            return false;
        }

        return true;
    }

    // ================================================================ mot luot tiep can

    private enum Phase { Scan, Far, Near }

    private NavStopReason Approach(CancellationToken ct, out string detail)
    {
        _mini.Forget();
        _marker.Forget();
        _ground.Reset();
        _progress.Reset();
        ResetScan();
        _escape.Close();
        _judgeAt = 0;
        _detourUntil = 0;

        var phase = Phase.Scan;
        long t0 = Now;
        long lastHeavy = 0, lastLog = 0, stillSince = 0, scanSince = Now, closeSince = 0;
        long bestAt = Now;
        double bestDist = double.MaxValue;
        int promptStreak = 0;
        bool wasClose = false;
        MarkerFix marker = new() { Locked = false };
        PromptHit prompt = new() { Visible = false };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Sleep(ct, _nav.TickMs);

            if (!GameForeground())
            {
                ReleaseAll();
                WaitWindow(ct);
                _ground.Reset();
                continue;
            }

            long now = Now;
            if (now - t0 > _nav.ApproachTimeoutMs)
            {
                detail = $"quá {_nav.ApproachTimeoutMs / 1000}s chưa tới (pha {phase})";
                ReleaseAll();
                return NavStopReason.Unreachable;
            }

            var dot = _mini.Read(now);

            // Chi nap so ĐO ĐƯỢC vao bo theo doi tien do: luc dang dung lai vi tri nho thi khong
            // co thong tin moi, nap vao chi lam nhieu cua so.
            if (dot.Found && !dot.Held) _progress.Push(now, dot.DistRef);

            if (dot.Found && dot.DistRef < bestDist - 0.5)
            {
                bestDist = dot.DistRef;
                bestAt = now;
            }

            if (now - bestAt > _nav.NoProgressAbortMs)
            {
                detail = $"{_nav.NoProgressAbortMs / 1000}s không rút ngắn được cự ly " +
                         $"(gần nhất {(bestDist < double.MaxValue ? bestDist.ToString("F0") : "?")})";
                ReleaseAll();
                return NavStopReason.Unreachable;
            }

            bool close = dot.Found && dot.DistRef <= _nav.NearDistRef;
            if (close) { wasClose = true; closeSince = now; }

            bool heavy = now - lastHeavy >= _nav.HeavyReadEveryMs;
            if (heavy)
            {
                lastHeavy = now;
                marker = _marker.Update(now, _yawSinceHeavy);
                _yawSinceHeavy = 0;

                // Chi doc prompt khi da tam gan: bang quet la 60%x50% man hinh, doc no moi vong tu
                // xa la tra tien cho khong. Nhung mot khi DA tung gan thi doc tiep den het luot —
                // sat noi la cham dich chui xuong duoi mui ten nguoi choi va bien mat.
                if (marker.Locked || close || wasClose)
                {
                    prompt = _prompt.Read();
                    promptStreak = prompt.Visible ? promptStreak + 1 : 0;
                }
                else
                {
                    prompt = new PromptHit { Visible = false };
                    promptStreak = 0;
                }
            }

            // ---------------- toi noi ----------------
            if (promptStreak >= _nav.PromptConfirmFrames)
            {
                ReleaseAll();
                Emit($"thấy prompt (ncc={prompt.Score:F2}) — bấm E");
                InputSender.TapKey(VK_E, _nav.EHoldMs);

                if (WaitPanel(ct))
                {
                    detail = $"minigame đã mở sau {(Now - t0) / 1000.0:F1}s";
                    return NavStopReason.Arrived;
                }

                Emit("bấm E mà minigame không mở — thử thêm một lần");
                InputSender.TapKey(VK_E, _nav.EHoldMs);
                if (WaitPanel(ct))
                {
                    detail = $"minigame đã mở sau {(Now - t0) / 1000.0:F1}s (lần E thứ hai)";
                    return NavStopReason.Arrived;
                }

                promptStreak = 0;
                detail = "bấm E hai lần mà minigame không mở";
                return NavStopReason.Unreachable;
            }

            // ---------------- chon tin hieu lai ----------------
            double errDeg;
            string source;

            if (marker.Locked)
            {
                phase = Phase.Near;
                // marker.Cx la toa do TRONG KHUNG game, nen tam khung la Width/2 — khong dinh dang
                // gi toi goc man hinh ao (xem MarkerCandidate.Cx).
                errDeg = PixelToDeg(marker.Cx - (_p.Width / 2.0));
                source = "mốc";
            }
            else if (dot.Found)
            {
                phase = Phase.Far;
                errDeg = dot.BearingDeg;
                source = dot.Held ? "chấm(nhớ)" : "chấm";
            }
            else
            {
                phase = Phase.Scan;
                errDeg = 0;
                source = "quét";
            }

            if (phase == Phase.Scan)
            {
                long lost = now - closeSince;

                // Vua nay con gan ma gio mat dau chấm: ĐI TIẾP theo huong cuoi da biet. O cu ly do
                // nhan vat con cach moc vai met — dung im thi khong bao gio toi (log 23/08: toi
                // xa=7 roi dung im 2.5 s, khong co prompt, roi bo luot).
                if (wasClose && lost <= _nav.NearPushMs)
                {
                    Hold(w: true, a: false, d: false, s: false, shift: false);
                    Trace(ref lastLog, now, $"mất dấu chấm khi đã gần — đi tiếp theo hướng cuối " +
                                            $"({lost / 1000.0:F1}s) {PromptText(prompt)}");
                    scanSince = now;
                    continue;
                }

                // Roi moi dung yen cho prompt, TUYET DOI khong xoay camera — xoay la prompt troi ra
                // khoi bang quet dung luc dang dung tren moc.
                if (wasClose && lost <= _nav.NearPushMs + _nav.NearHoldMs)
                {
                    Hold(w: false, a: false, d: false, s: false, shift: false);
                    Trace(ref lastLog, now, $"đứng yên chờ prompt " +
                                            $"({(lost - _nav.NearPushMs) / 1000.0:F1}s) {PromptText(prompt)}");
                    scanSince = now;
                    continue;
                }

                // Dem tu luc VAO pha quet, khong tu dau luot: di duoc mot phut roi moi mat dau cham
                // ma tinh la het gio quet thi luot nao cung chet ngay giay dau.
                if (now - scanSince > ScanBudgetMs())
                {
                    detail = $"quét {(now - scanSince) / 1000.0:F0}s không thấy chấm vàng nào";
                    ReleaseAll();
                    return NavStopReason.Unreachable;
                }

                Hold(w: false, a: false, d: false, s: false, shift: false);

                if (Stepped)
                {
                    if (!StepScan(now, ref lastLog))
                    {
                        detail = $"quét {_nav.ScanSweeps} vòng ({_nav.ScanSteps} bậc) không thấy chấm vàng nào";
                        ReleaseAll();
                        return NavStopReason.Unreachable;
                    }
                }
                else
                {
                    Yaw(_nav.ScanYawCounts);
                    Trace(ref lastLog, now, $"quét mượt ({(now - scanSince) / 1000.0:F1}s" +
                                            $"/{ScanBudgetMs() / 1000.0:F0}s) — chưa biết count/độ");
                }
                continue;
            }

            ResetScan();

            scanSince = now;

            // Chi hieu chuan khi dang lai theo CHAM: phep do can doc lai goc cham sau khi xoay.
            if (!_calibrated && dot.Found && !marker.Locked) CalibrateYaw(ct);

            // ---------------- cham diem quang di lai sau mot bac thoat ket ----------------
            if (_judgeAt != 0 && now >= _judgeAt)
            {
                _judgeAt = 0;

                // Mat dau cham dung luc cham diem (dang bam moc 3D, hoac cham bi che): khong co cu
                // ly de so, roi ve tin hieu nhanh. Coi "khong co cu ly" la "chua thoat" thi bot se
                // leo thang oan trong khi no dang di ngon lanh theo moc.
                //
                // So voi StartDist cua CA DOT, khong phai vi tri truoc bac vua roi: thang co the
                // day nhan vat ra xa dan (log 25/08: 7 → 8 → 7 → 10), va lay moc theo tung bac thi
                // mot cu keo ve 9 cung tinh la "thoat" — dong dot o cho xa hon luc mo.
                bool better = dot.Found
                    ? dot.DistRef <= _escape.StartDist - _nav.MinProgressRef
                    : stillSince == 0;

                if (better)
                {
                    Emit($"thoát: xa {_escape.StartDist:F0} → " +
                         $"{(dot.Found ? dot.DistRef.ToString("F0") : "? (theo đất trôi)")} " +
                         $"sau bậc {_escape.Rung} bên {SideName(_escape.Right)} — đóng đợt");
                    _escape.Close();
                    _progress.Reset();
                }
                else
                {
                    // Da co bang chung: di binh thuong ca quang ma cu ly khong nhuc nhich. Leo bac
                    // NGAY, khong cho bo theo doi gom lai du 3 s lich su.
                    Emit($"chưa thoát (xa {(dot.Found ? dot.DistRef.ToString("F0") : "?")} " +
                         $"so với đầu đợt {_escape.StartDist:F0}, trước bậc {_escape.LastDist:F0}) " +
                         "— leo bậc");

                    if (!RunStep(ct, dot))
                    {
                        detail = $"kẹt ở xa={_escape.StartDist:F0}, đã thử hết cả hai bên";
                        ReleaseAll();
                        return NavStopReason.Unreachable;
                    }

                    stillSince = 0;
                    continue;
                }
            }

            // ---------------- lai ----------------
            // Vua thoat khe thi nham LECH ve phia da truot mot luc, roi thu ve 0 — nham thang lai
            // muc tieu ngay la cam thang vao dung cai khe vua ra.
            double bias = DetourBias(now);
            double steer = errDeg + bias;

            int counts = YawCountsFor(steer);
            Yaw(counts);
            if (bias == 0) TrackWrongWay(errDeg, source);

            bool aligned = Math.Abs(steer) <= _nav.TurnOnlyDeg;

            // Chay cho toi khi THAY VONG TRON VANG — khong con cua cu ly toi thieu, va goc cho phep
            // rong hon nhieu. Theo ban Python (sprint_angle_deg 52, world_sprint_area_max 2600).
            // Ban cu tat chay tu xa=26 nen di bo gan het quang duong: log 25/08 chi chay duoc
            // 32→29 roi di bo suot 26→7. Luat nam trong ShouldSprint/NearRing de kiem duoc offline.
            bool sprint = ShouldSprint(_nav, steer, bias, marker.SeenAreaRef, dot.Found, dot.DistRef);

            Hold(w: aligned, a: false, d: false, s: false, shift: sprint);

            // ---------------- ket chua ----------------
            double flow = _ground.Sample(now);
            if (!aligned || flow < 0 || flow >= _nav.GroundFlowMin) stillSince = 0;
            else if (stillSince == 0) stillSince = now;

            bool flowStill = stillSince != 0 && now - stillSince >= _nav.StuckMs;
            bool stuck;
            string why;

            if (_progress.Ready(now))
            {
                // Cu ly la trong tai: xoay camera doi GOC chu khong doi CU LY, nen no mien nhiem
                // voi chinh thu bo lai dang lam suot.
                stuck = _progress.Stalled(now);
                why = $"Δxa={_progress.Delta(now):+0.0;-0.0;0.0}/{_nav.ProgressWindowMs / 1000}s, flow={flow:F1}";
            }
            else
            {
                // Chua du lich su (vua vao, vua thoat bac, hoac dang bam moc va mat cham) → tam
                // dung tin hieu nhanh.
                stuck = flowStill;
                why = $"flow={flow:F1}, chưa đủ lịch sử cự ly";
            }

            if (stuck && aligned && _judgeAt == 0)
            {
                double dist = dot.Found ? dot.DistRef : _escape.StartDist;

                if (_escape.Open(dist, preferRight: steer >= 0))
                    Emit($"kẹt ({why}) — đợt mới, bên {SideName(_escape.Right)}, xa đầu đợt={dist:F0}");
                else
                    Emit($"vẫn kẹt ({why}) — cùng đợt, bên {SideName(_escape.Right)} bậc {_escape.Rung}");

                if (!RunStep(ct, dot))
                {
                    detail = $"kẹt ở xa={_escape.StartDist:F0}, đã thử hết cả hai bên";
                    ReleaseAll();
                    return NavStopReason.Unreachable;
                }

                stillSince = 0;
                continue;
            }

            Trace(ref lastLog, now,
                $"{source} lệch={errDeg:F1}°{(bias == 0 ? "" : $" (lệch né {bias:+0;-0}°)")} " +
                $"chuột={counts:+#;-#;0} " +
                $"{(sprint ? "chạy" : aligned ? $"đi({WhyWalk(marker, dot)})" : "xoay tại chỗ")} " +
                $"xa={(dot.Found ? dot.DistRef.ToString("F0") : "?")} " +
                $"Δxa={(_progress.Ready(now) ? _progress.Delta(now).ToString("+0.0;-0.0;0.0") : "?")} " +
                $"đất={flow:F1} {PromptText(prompt)} {(marker.Locked ? "mốc✓" : marker.Note)}");
        }
    }

    // ================================================================ quet theo bac

    /// <summary>Chặng trong một bậc quét.</summary>
    private enum ScanLeg
    {
        /// <summary>Vừa giật xong, đang chờ camera dừng hẳn.</summary>
        Settle,

        /// <summary>Đã đọc; vừa né nhẹ, đang chờ để đọc lại cho phép kiểm thị sai.</summary>
        Nudge
    }

    private ScanLeg _scanLeg;
    private long _scanLegUntil;
    private int _scanStep, _scanSweep;
    private bool _scanArmed;

    /// <summary>
    /// Có đổi được độ ra count không. Không thì phải quét mượt — bước góc tính sai thì bốn cú giật
    /// chẳng đi tới đâu, mà lại còn tưởng là đã quét đủ vòng.
    /// </summary>
    private bool Stepped => _countsPerDeg > 0;

    private void ResetScan()
    {
        _scanArmed = false;
        _scanStep = 0;
        _scanSweep = 0;
        _scanLeg = ScanLeg.Settle;
        _scanLegUntil = 0;
    }

    /// <summary>
    /// Một nhịp của vòng quét theo bậc. Trả false khi đã quét đủ <c>ScanSweeps</c> vòng mà không
    /// thấy gì — lúc đó bỏ lượt.
    ///
    /// Mỗi bậc đi qua hai chặng: giật → chờ ổn định → ĐỌC → né nhẹ → chờ → ĐỌC lại. Lần đọc thứ
    /// hai là lần duy nhất phép kiểm thị sai có đầu vào, vì nó có <c>_yawSinceHeavy</c> khác 0.
    /// Bỏ chặng né đi thì mốc 3D không bao giờ khoá được trong lúc quét — xem
    /// <see cref="NavSettings.ScanNudgeCounts"/>.
    ///
    /// Không tự đọc gì ở đây: vòng chạy chính đã đọc chấm mỗi tick và đọc mốc mỗi
    /// <c>HeavyReadEveryMs</c>. Việc của hàm này chỉ là XOAY và ĐỢI đúng nhịp.
    /// </summary>
    private bool StepScan(long now, ref long lastLog)
    {
        double stepDeg = 360.0 / Math.Max(2, _nav.ScanSteps);

        if (!_scanArmed)
        {
            _scanArmed = true;
            _scanStep = 0;
            _scanSweep = 0;
            // Bac dau tien doc NGAY tai cho dang dung, chua giat — huong hien tai cung la mot
            // trong cac huong can soi, giat truoc la bo phi no.
            EnterLeg(now, ScanLeg.Settle, _nav.ScanStepSettleMs);
            Emit($"quét theo bậc: {_nav.ScanSteps} bậc × {stepDeg:F0}°, tối đa {_nav.ScanSweeps} vòng");
            return true;
        }

        if (now < _scanLegUntil) return true;

        if (_scanLeg == ScanLeg.Settle)
        {
            // Da doc xong o huong nay. Ne nhe de lan doc sau cham duoc thi sai.
            Yaw(_nav.ScanNudgeCounts);
            EnterLeg(now, ScanLeg.Nudge, _nav.ScanNudgeSettleMs);
            return true;
        }

        // Xong ca hai lan doc cua bac nay — sang bac sau.
        _scanStep++;
        if (_scanStep >= _nav.ScanSteps)
        {
            _scanStep = 0;
            _scanSweep++;
            if (_scanSweep >= _nav.ScanSweeps) return false;
        }

        // Tru phan da ne di, khong thi moi bac lech them mot chut va sau mot vong lech han.
        int counts = (int)Math.Round(stepDeg * _countsPerDeg) - _nav.ScanNudgeCounts;
        Yaw(counts * _yawSign);
        EnterLeg(now, ScanLeg.Settle, _nav.ScanStepSettleMs);

        Trace(ref lastLog, now,
            $"quét bậc {_scanStep + 1}/{_nav.ScanSteps} vòng {_scanSweep + 1}/{_nav.ScanSweeps} " +
            $"({stepDeg:F0}°/bậc, {counts:+#;-#;0} count)");
        return true;
    }

    private void EnterLeg(long now, ScanLeg leg, int ms)
    {
        _scanLeg = leg;
        _scanLegUntil = now + ms;
    }

    /// <summary>
    /// Hạn giờ quét, tính đủ cho ÍT NHẤT một vòng 360° cộng biên.
    ///
    /// Vì sao không để hằng số: tốc độ quay khi quét là <c>ScanYawCounts</c> count mỗi
    /// <c>TickMs</c>, đổi ra độ thì phải chia cho <see cref="_countsPerDeg"/> — con số chỉ biết
    /// được SAU khi hiệu chuẩn, và nó phụ thuộc độ nhạy chuột của từng máy.
    ///
    /// Log 25/08 cho thấy hậu quả khi bỏ qua: đo được 16.89 count/độ → 18 count mỗi 50 ms là
    /// 21.3 °/s → một vòng cần 16.9 s, trong khi <c>ScanTimeoutMs</c> là 12 s. Bot không bao giờ
    /// quét hết một vòng, chỉ tới ~256° rồi bỏ lượt — mục tiêu nằm trong 100° còn lại thì vĩnh
    /// viễn không thấy. Cả ba lượt của phiên đó đều chết đúng kiểu này.
    ///
    /// Vẫn tôn trọng <c>ScanTimeoutMs</c> làm sàn: người dùng chỉnh nó lên thì phải có tác dụng.
    /// </summary>
    private long ScanBudgetMs()
    {
        double cpd = _countsPerDeg > 0 ? _countsPerDeg : _nav.FallbackCountsPerDeg;
        double degPerTick = _nav.ScanYawCounts / Math.Max(0.2, cpd);
        if (degPerTick <= 0) return _nav.ScanTimeoutMs;

        double ticks = 360.0 / degPerTick;
        double fullTurnMs = ticks * Math.Max(1, _nav.TickMs) * _nav.ScanTurnMargin;

        return (long)Math.Max(_nav.ScanTimeoutMs, Math.Min(fullTurnMs, _nav.ScanMaxMs));
    }

    /// <summary>Bấm E xong thì chờ minigame hiện ra.</summary>
    private bool WaitPanel(CancellationToken ct)
    {
        long until = Now + _nav.WaitPanelMs;
        while (Now < until)
        {
            ct.ThrowIfCancellationRequested();
            if (PanelVisible is null) { Sleep(ct, 250); continue; }
            if (PanelVisible()) return true;
            Sleep(ct, 120);
        }
        // Khong co ham tham do thi khong khang dinh duoc gi — coi nhu da mo, de ElectricBot tu xu.
        return PanelVisible is null;
    }

    // ================================================================ thoat ket

    /// <summary>
    /// Thi hành một bậc của thang rồi hẹn giờ chấm điểm. false = hết thang.
    ///
    /// Chấm điểm bằng CỰ LY sau khi đã đi lại một quãng, chứ KHÔNG hỏi sai phân khung ngay tại chỗ
    /// như bản đầu — cú trượt làm nhân vật cựa quậy sát tường nên khung nào cũng "có đổi", và bản
    /// đầu vì thế lần nào cũng tự báo "thoát được" rồi quay lại húc tường.
    /// </summary>
    private bool RunStep(CancellationToken ct, DotFix dot)
    {
        var step = _escape.Next();
        Emit(step.ToString());

        switch (step.Action)
        {
            case EscapeAction.Strafe:
                // Truot ngang THUAN: khong kem W (W+A la di cheo 45°, van huc vao tuong), khong
                // dung toi camera nen thoat xong huong van con nguyen.
                Hold(w: false, a: !step.Right, d: step.Right, s: false, shift: false);
                Sleep(ct, step.DurationMs);
                Hold(w: false, a: false, d: false, s: false, shift: false);
                break;

            case EscapeAction.BackupAndFlip:
                Hold(w: false, a: false, d: false, s: true, shift: false);
                Sleep(ct, step.DurationMs);
                Hold(w: false, a: false, d: false, s: false, shift: false);
                break;

            case EscapeAction.Jump:
                Hold(w: true, a: false, d: false, s: false, shift: false);
                InputSender.TapKey(VK_SPACE, 80);
                Sleep(ct, 700);
                break;

            default:
                ReleaseAll();
                return false;
        }

        if (dot.Found) _escape.MarkDistance(dot.DistRef);

        _judgeAt = Now + _nav.ResumeCheckMs;
        _detourUntil = Now + _nav.DetourBiasMs;
        _detourSide = _escape.Right;

        _progress.Reset();
        _ground.Reset();
        return true;
    }

    /// <summary>Góc lệch né còn lại, suy giảm tuyến tính về 0. Dương = lệch sang phải.</summary>
    private double DetourBias(long now)
    {
        if (_detourUntil <= now || _nav.DetourBiasMs <= 0) return 0;

        double k = (_detourUntil - now) / (double)_nav.DetourBiasMs;
        return _nav.DetourBiasDeg * Math.Clamp(k, 0, 1) * (_detourSide ? +1 : -1);
    }

    /// <summary>
    /// "Đã thấy vòng tròn vàng dưới đất chưa" — mốc để chuyển từ CHẠY sang ĐI BỘ.
    ///
    /// Tách ra thành hàm THUẦN và static để <c>--verify-nav</c> chấm được cả bảng quyết định mà
    /// không cần dựng khung hình nào. Vòng lái là vòng kín: mỗi lần chỉnh ngưỡng ở đây mà phải vào
    /// game mới biết đúng sai thì rất đắt.
    /// </summary>
    internal static bool NearRing(NavSettings nav, double seenAreaRef, bool dotFound, double distRef) =>
        seenAreaRef >= nav.WalkMarkerAreaRef || (dotFound && distRef <= nav.WalkMinDistRef);

    /// <summary>
    /// Có được phép chạy không. Cũng thuần, cùng lý do trên.
    /// </summary>
    internal static bool ShouldSprint(NavSettings nav, double steerDeg, double bias,
                                      double seenAreaRef, bool dotFound, double distRef)
    {
        bool aligned = Math.Abs(steerDeg) <= nav.TurnOnlyDeg;
        return aligned
               && bias == 0
               && Math.Abs(steerDeg) <= nav.SprintMaxDeg
               && !NearRing(nav, seenAreaRef, dotFound, distRef);
    }

    /// <summary>
    /// Vì sao đang đi bộ chứ không chạy. Chỉ để log — nhưng là dòng log quan trọng nhất khi cần
    /// biết bot có tắt chạy quá sớm không, đúng thứ đã phải mò bằng tay từ log 25/08.
    /// </summary>
    private string WhyWalk(MarkerFix marker, DotFix dot)
    {
        if (marker.SeenAreaRef >= _nav.WalkMarkerAreaRef) return $"thấy vòng dt={marker.SeenAreaRef:F0}";
        if (dot.Found && dot.DistRef <= _nav.WalkMinDistRef) return $"sát đích xa={dot.DistRef:F0}";
        return "lệch quá";
    }

    private static string SideName(bool right) => right ? "PHẢI" : "TRÁI";

    private static string PromptText(PromptHit p) =>
        p is null || p.Rows.Count == 0 ? "prompt=?" : $"prompt={p.Score:F2}";

    /// <summary>Giữa hai lượt: nhả phím, lùi một chút, chuẩn hoá lại góc nhìn.</summary>
    private void Recover(CancellationToken ct)
    {
        ReleaseAll();
        Hold(w: false, a: false, d: false, s: true, shift: false);
        Sleep(ct, _nav.BackupMs);
        ReleaseAll();
        NormalizePitch(ct);
    }

    // ================================================================ camera

    /// <summary>
    /// Dí camera xuống hết chốt rồi ngẩng lên một lượng cố định.
    ///
    /// Pitch trong GTA có chốt cứng hai đầu, nên "dí quá tay" là vô hại và cho ra một mốc BIẾT
    /// TRƯỚC. Nhờ vậy không phải bắt người dùng tự canh góc camera như bản Python.
    /// </summary>
    private void NormalizePitch(CancellationToken ct)
    {
        Emit("chuẩn hoá góc nhìn: dí xuống hết chốt rồi ngẩng lên");
        Nudge(ct, 0, +1, _nav.PitchDownCounts);
        Nudge(ct, 0, -1, _nav.PitchUpCounts);
    }

    private void Nudge(CancellationToken ct, int dirX, int dirY, int total)
    {
        int step = _nav.PitchStepCounts;
        for (int done = 0; done < total; done += step)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(step, total - done);
            InputSender.MoveRelative(dirX * n, dirY * n);
            Sleep(ct, 12);
        }
    }

    /// <summary>
    /// Đo "bao nhiêu count chuột được một độ" bằng chính chấm vàng: xoay một lượng đã biết rồi xem
    /// góc chấm đổi bao nhiêu.
    ///
    /// Phép này trả lời luôn câu hỏi mà ảnh tĩnh không trả lời được — minimap xoay theo CAMERA hay
    /// theo NHÂN VẬT. Đổi góc rõ ràng nghĩa là theo camera, lái thẳng theo góc chấm được. Không
    /// đổi thì rơi về tỉ lệ dự phòng và để <see cref="TrackWrongWay"/> tự sửa dấu bằng quan sát.
    /// </summary>
    private void CalibrateYaw(CancellationToken ct)
    {
        // Thu lai duoc vai lan (cham co the vua bi che), nhung khong thu mai: moi lan la mot lan
        // dung khung giua duong.
        if (++_calibTries >= 3)
        {
            _calibrated = true;
            _countsPerDeg = _nav.FallbackCountsPerDeg;
            Emit("hiệu chuẩn thất bại 3 lần — dùng tỉ lệ dự phòng");
        }

        Hold(w: false, a: false, d: false, s: false, shift: false);
        Sleep(ct, 80);

        var a = _mini.Read(Now);
        if (!a.Found) return;

        InputSender.MoveRelative(_nav.CalibrateCounts, 0);
        Sleep(ct, _nav.CalibrateSettleMs);

        var b = _mini.Read(Now);
        if (!b.Found) return;

        _calibrated = true;

        double delta = Wrap(b.BearingDeg - a.BearingDeg);
        if (Math.Abs(delta) < _nav.CalibrateMinDeltaDeg)
        {
            _countsPerDeg = _nav.FallbackCountsPerDeg;
            _yawSign = 1;
            Emit($"hiệu chuẩn: xoay {_nav.CalibrateCounts} count mà góc chấm chỉ đổi {delta:F1}° → " +
                 "minimap KHÔNG theo camera, dùng tỉ lệ dự phòng và tự sửa dấu khi chạy");
            return;
        }

        // Xoay phai (+count) ma goc chấm GIAM nghia la ban than goc do da tinh nguoc — dau am.
        _yawSign = delta < 0 ? +1 : -1;
        _countsPerDeg = Math.Clamp(Math.Abs(_nav.CalibrateCounts / delta), 0.2, 60.0);
        Emit($"hiệu chuẩn: {_nav.CalibrateCounts} count → {delta:F1}° " +
             $"(={_countsPerDeg:F2} count/độ, dấu {_yawSign:+#;-#}), minimap XOAY THEO CAMERA");

        // Bao ra ngoai de LUU lai — lan chay sau quet theo bac duoc ngay tu dau, khoi phai quet
        // muot cho toi luc thay cham. KHONG tu goi _cfg.Save() o day: ca repo chi ghi config tu
        // luong UI, ghi tu luong bot la mo ra dua ghi file.
        Calibrated?.Invoke(_countsPerDeg, _yawSign);
    }

    /// <summary>Số count cần bắn để bù <paramref name="errDeg"/>, đã kẹp và có vùng chết.</summary>
    private int YawCountsFor(double errDeg)
    {
        if (Math.Abs(errDeg) <= _nav.YawDeadzoneDeg) return 0;

        double cpd = _countsPerDeg > 0 ? _countsPerDeg : _nav.FallbackCountsPerDeg;
        double want = errDeg * cpd * _nav.YawKp * _yawSign;
        int counts = (int)Math.Round(Math.Clamp(want, -_nav.YawMaxCounts, _nav.YawMaxCounts));

        // Duoi 1 count thi SendInput lam tron ve 0 va bot dung nhin mai — day len 1.
        if (counts == 0) counts = want > 0 ? 1 : -1;
        return counts;
    }

    /// <summary>
    /// Lưới an toàn cho DẤU xoay: nếu sai số cứ to lên sau mỗi lần bù thì dấu đang ngược — đảo.
    /// Rẻ hơn nhiều so với việc bắt người dùng khai báo <c>invert_mouse_x</c> như bản Python.
    /// </summary>
    private void TrackWrongWay(double errDeg, string source)
    {
        // Doi tin hieu (cham minimap -> moc 3D) la doi ca don vi lan goc quy chieu, nen sai so
        // nhay mot buoc ma khong phai vi lai sai. Bat dau dem lai tu dau.
        if (source != _lastSource) { _lastSource = source; _wrongWayStreak = 0; _lastAbsErr = 0; return; }

        double abs = Math.Abs(errDeg);
        if (abs <= _nav.YawDeadzoneDeg) { _wrongWayStreak = 0; _lastAbsErr = abs; return; }

        if (_lastAbsErr > 0 && abs > _lastAbsErr + 1.0) _wrongWayStreak++;
        else if (abs < _lastAbsErr) _wrongWayStreak = 0;

        _lastAbsErr = abs;

        if (_wrongWayStreak < 6) return;

        _yawSign = -_yawSign;
        _wrongWayStreak = 0;
        Emit($"sai số cứ to lên — đảo dấu xoay thành {_yawSign:+#;-#}");
    }

    private double _lastAbsErr;
    private string _lastSource = "";

    private void Yaw(int counts)
    {
        if (counts == 0) return;
        InputSender.MoveRelative(counts, 0);
        _yawSinceHeavy += counts;
    }

    /// <summary>
    /// Đổi lệch ngang trên màn ra độ. Nửa màn ứng với <see cref="NavSettings.HalfFovDeg"/> —
    /// xấp xỉ tuyến tính, đủ dùng vì đây là vòng kín: sai một chút thì vòng sau bù nốt.
    /// </summary>
    private double PixelToDeg(double px) => px / Math.Max(1.0, _p.Width / 2.0) * _nav.HalfFovDeg;

    // ================================================================ phim

    private void Hold(bool w, bool a, bool d, bool s, bool shift)
    {
        Set(ref _wDown, w, VK_W);
        Set(ref _aDown, a, VK_A);
        Set(ref _dDown, d, VK_D);
        Set(ref _sDown, s, VK_S);

        if (shift != _shiftDown)
        {
            if (shift) InputSender.ShiftDown(); else InputSender.ShiftUp();
            _shiftDown = shift;
        }
    }

    private static void Set(ref bool state, bool want, ushort vk)
    {
        if (state == want) return;
        if (want) InputSender.KeyDown(vk); else InputSender.KeyUp(vk);
        state = want;
    }

    private void ReleaseAll()
    {
        Hold(w: false, a: false, d: false, s: false, shift: false);
        HeldKeys.ReleaseAll();
        _wDown = _aDown = _dDown = _sDown = _shiftDown = false;
    }

    // ================================================================ vat

    private static long Now => Environment.TickCount64;

    private bool GameForeground()
    {
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) return true;
        return Native.ForegroundTitle().Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase);
    }

    private void WaitWindow(CancellationToken ct)
    {
        while (!GameForeground())
        {
            if (!_windowWarned)
            {
                Emit($"tạm dừng: chưa focus “{_cfg.WindowMatch}” (đang focus: “{Native.ForegroundTitle()}”)");
                _windowWarned = true;
            }
            Sleep(ct, 250);
        }

        if (_windowWarned)
        {
            Emit("game đã focus lại — chạy tiếp");
            _windowWarned = false;
        }
    }

    private void Trace(ref long last, long now, string line)
    {
        if (now - last < _nav.LogEveryMs) return;
        last = now;
        Emit(line);
    }

    private static double Wrap(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
    }

    private void Emit(string line) => Log?.Invoke(line);

    /// <summary>
    /// "Đất có trôi không" — sai phân khung trên hai dải hai bên bóng nhân vật.
    ///
    /// Vì sao hai dải hai bên chứ không một dải giữa: nhân vật đứng chính giữa, và bóng của nhân
    /// vật cũng nhúc nhích theo, nên dải giữa báo có chuyển động cả khi đang đứng im húc tường.
    /// </summary>
    private sealed class GroundFlow : IDisposable
    {
        private readonly NavSettings _nav;
        private readonly RegionReader _left, _right;
        private byte[] _prevL, _prevR, _curL, _curR;
        private bool _has;

        public GroundFlow(Screen screen, ElectricProfile p, NavSettings nav)
        {
            _nav = nav;

            int y0 = (int)(p.Height * nav.GroundBandTopFrac);
            int y1 = (int)(p.Height * nav.GroundBandBottomFrac);
            int bw = (int)(p.Width * nav.GroundBandWidthFrac);
            int bh = Math.Max(8, y1 - y0);

            var l = new FishingRect { X = 0, Y = y0, W = bw, H = bh };
            var r = new FishingRect { X = p.Width - bw, Y = y0, W = bw, H = bh };

            _left = new RegionReader(FishingConfig.ToAbsolute(screen, l));
            _right = new RegionReader(FishingConfig.ToAbsolute(screen, r));
        }

        public void Reset() => _has = false;

        /// <summary>Sai khác trung bình mỗi pixel so với lần gọi trước; −1 nếu chưa có khung trước.</summary>
        public double Sample(long nowMs)
        {
            _left.Refresh();
            _right.Refresh();
            _curL = _left.GrayBuffer(_left.Region, _curL);
            _curR = _right.GrayBuffer(_right.Region, _curR);

            if (!_has || _prevL is null || _prevL.Length != _curL.Length)
            {
                _prevL = (byte[])_curL.Clone();
                _prevR = (byte[])_curR.Clone();
                _has = true;
                return -1;
            }

            double flow = (Diff(_prevL, _curL) + Diff(_prevR, _curR)) / 2.0;

            // Chup rieng ban sao: giu chinh _curL lam khung truoc thi lan sau GrayBuffer ghi de len
            // no va phep so luon ra 0 — dung canh bao o IPixelSource.BgrBuffer(into).
            Array.Copy(_curL, _prevL, _curL.Length);
            Array.Copy(_curR, _prevR, _curR.Length);
            return flow;
        }

        private static double Diff(byte[] a, byte[] b)
        {
            long sum = 0;
            for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
            return (double)sum / a.Length;
        }

        public void Dispose()
        {
            _left?.Dispose();
            _right?.Dispose();
        }
    }
}
