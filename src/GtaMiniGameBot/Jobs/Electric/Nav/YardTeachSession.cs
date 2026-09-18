using System.Diagnostics;
using System.Globalization;

namespace GtaMiniGameBot;

/// <summary>
/// Một phiên DẠY bản đồ: người chơi đi tay, bot chỉ nhìn và ghi.
///
/// <b>Bảo đảm không gửi phím.</b> Lớp này cố tình KHÔNG có field <see cref="NavInput"/> và không nhắc
/// tới <c>InputSender</c> / <c>HeldKeys</c> / <see cref="NavController"/> ở bất kỳ đâu — đó là lý do
/// nó là một lớp riêng chứ không phải một cờ trong <see cref="NavBot"/>. Đọc hết file này là kiểm
/// xong lời hứa đó, không phải lần theo mười nhánh <c>if</c>.
///
/// Nó vẫn dùng chung mọi bộ dò với <see cref="NavBot"/> (cùng <see cref="NavCapture"/>, cùng
/// <see cref="DotTracker"/>, cùng ROI), nên số ghi được mô tả đúng cái mà bot sẽ thấy khi chạy thật.
///
/// Vòng lặp nhịp 25 ms như <c>NavBot.Loop</c>, và trả quyền ngay khi bộ thăm dò của
/// <see cref="ElectricBot"/> thấy minigame mở — người chỉ cần đi tới nơi và bấm E, bộ giải bảng/dây
/// lo phần còn lại.
/// </summary>
internal sealed class YardTeachSession : IDisposable
{
    private readonly ElectricConfig _cfg;
    private readonly Screen _screen;
    private readonly ElectricProfile _profile;
    private readonly YardRecorder _rec;

    private NavScale _s;
    private NavCapture _capture;
    private DotTracker _dot;
    private YardPoseTracker _pose;
    private BlipTemplates _tpl;
    private ElectricLocator _locator;
    private double _originX, _originY;

    private readonly Stopwatch _tickSw = new();
    private double _tickMsEma;
    private int _grabFails;

    private double _lastStatus, _lastLogLine;
    private int _panelHits;
    private int _nFix2, _nFix1, _nNone, _nTicks;
    private double _lastLightningT;
    private bool _lightningLost;
    private bool _promptOn;
    private string _lastQuality = YardQuality.None;
    private Vec2? _lastT;

    /// <summary>Số chuyến đã ghi trong phiên này (mỗi lần bảng mở là hết một chuyến).</summary>
    public int Trips { get; private set; }

    public event Action<string> Log;

    /// <summary>Dòng ngắn cho ô trạng thái trên UI — không phải log.</summary>
    public event Action<string> Status;

    public YardTeachSession(ElectricConfig cfg, Screen screen, ElectricProfile profile, YardRecorder rec)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _rec = rec;
    }

    private void Emit(string line) => Log?.Invoke(line);

    /// <summary>
    /// Chạy tới khi <paramref name="panelVisible"/> đúng hai lần liên tiếp (125 ms — cùng luật
    /// <see cref="NavTuning.PanelInterruptHits"/> mà NavBot dùng) hoặc tới khi bị huỷ.
    /// Trả true = minigame đã mở, giao lại cho bộ điều phối.
    /// </summary>
    public bool Run(CancellationToken ct, Func<bool> panelVisible, bool afterMinigame)
    {
        var b = _screen.Bounds;
        _s = new NavScale(b.Width, b.Height, _cfg.Nav.ScreenPxScale);
        _originX = (_cfg.Nav.PlayerOriginXRef > 0 ? _cfg.Nav.PlayerOriginXRef : NavTuning.PlayerOriginXRef) * _s.Sx;
        _originY = (_cfg.Nav.PlayerOriginYRef > 0 ? _cfg.Nav.PlayerOriginYRef : NavTuning.PlayerOriginYRef) * _s.Sy;

        _capture = new NavCapture(_screen, _s, _cfg.Survival, _profile.SurvivalHud);
        _dot = new DotTracker(_s);
        _pose = new YardPoseTracker(_s, _originX, _originY);
        _tpl = BlipTemplates.Load(_profile.Key);

        // Prompt chi de GHI LAI (cot prompt), khong dieu khien gi — nen thieu khoanh khong phai loi.
        _locator = ElectricLocator.Create(_screen, _profile, out string locProblem);

        double now0 = NavClock.Now;
        _lastLightningT = now0;

        if (Trips == 0)
        {
            _rec.Event(now0, "SESSION_START",
                $"{_profile.Key} sx={_s.Sx.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"dRef={NavTuning.YardDRef.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"tpl={(_tpl is null ? "KHÔNG" : _tpl.Describe())}");
            _rec.Event(now0, _tpl is null ? "TEMPLATES_MISSING" : "TEMPLATES_LEARNED", _tpl?.Describe() ?? "");
            Emit($"[GHI BẢN ĐỒ] bắt đầu — {System.IO.Path.GetFileName(_rec.Path)}; " +
                 $"mẫu blip: {(_tpl is null ? "CHƯA HỌC (nhận theo hình dạng, kém chính xác hơn)" : _tpl.Describe())}" +
                 (_locator is null ? $"; chưa khoanh [E] TƯƠNG TÁC ({locProblem}) — cột prompt để trống" : ""));
            Emit("[GHI BẢN ĐỒ] bot KHÔNG bấm phím nào trong chế độ này — cứ đi tay như thường.");
        }
        if (afterMinigame) _rec.Event(now0, "PANEL_CLOSED", "vừa giải xong một minigame");

        try
        {
            _capture.StartScanner();
            return Loop(ct, panelVisible);
        }
        finally
        {
            try { _locator?.Dispose(); } catch { }
            try { _capture?.Dispose(); } catch { }
            _locator = null;
            _capture = null;
        }
    }

    private bool Loop(CancellationToken ct, Func<bool> panelVisible)
    {
        double period = NavTuning.TickMs / 1000.0;
        double next = NavClock.Now;
        double lastPanelPoll = 0;
        bool panel = false;

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

            if (_capture.Fault is not null) throw new Exception("luồng quét màn hình dừng: " + _capture.Fault.Message);

            NavFrame mini;
            try
            {
                mini = _capture.GrabMinimap(now);
                _grabFails = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_grabFails++ == 0) Emit("[GHI BẢN ĐỒ] không chụp được minimap: " + ex.Message);
                if (_grabFails >= 20) throw new Exception("không chụp được màn hình 5 giây liên tục: " + ex.Message);
                Thread.Sleep(250);
                continue;
            }

            // Tham do panel theo nhip, giong NavBot: goi moi tick la doc ca man hai lan mot nhip.
            if (now - lastPanelPoll >= NavTuning.PanelInterruptPollS)
            {
                lastPanelPoll = now;
                bool hit = false;
                try { hit = panelVisible(); } catch { }
                if (hit)
                {
                    _panelHits++;
                    if (_panelHits >= NavTuning.PanelInterruptHits)
                    {
                        Trips++;
                        _rec.Event(now, "PANEL_OPEN", $"chuyến {Trips}");
                        _rec.DumpRing(now, "open", 0.5, 0.1);
                        Emit($"[GHI BẢN ĐỒ] minigame mở — hết chuyến {Trips}, giao cho bộ giải.");
                        return true;
                    }
                }
                else _panelHits = 0;
                panel = hit;
            }

            Step(mini, now, panel);
        }
    }

    /// <summary>Một tick: một lượt mặt nạ chung cho cả chấm vàng lẫn blip, rồi ghi.</summary>
    private void Step(NavFrame mini, double now, bool panel)
    {
        var masks = BlipMasks.Build(mini, _s);
        var candidates = masks is null
            ? YellowDotDetector.Detect(mini, _s, _originX, _originY)
            : YellowDotDetector.Detect(mini, _s, _originX, _originY, masks.Yellow, masks.Local);
        var fragments = YellowDotDetector.DetectNearFragments(mini, _s, _originX, _originY);
        var dot = _dot.Update(candidates, _originX, _originY, now, fragments);

        var predicted = _pose.Predict(now);
        var hits = BlipDetector.Detect(mini, _s, masks, _tpl, predicted);

        // Buoi day: bot khong cam chuot nen KHONG co count de suy huong — truyen null, tracker tu
        // chuyen sang luat "giu θ cua FIX2 cuoi".
        var pose = _pose.Update(hits, dot, now, null);

        int ny = masks is null ? 0 : ImageOps.Blobs(masks.Yellow).Count;
        int nr = masks is null ? 0 : ImageOps.Blobs(masks.Red).Count;

        bool prompt = false;
        try { prompt = _locator is not null && _locator.Visible(); } catch { }

        var snap = _capture.Latest;
        _rec.PushFrame(mini, now);
        _rec.Tick(now, pose, hits, dot, ny, nr, snap.Marker, _originX, _originY, prompt, panel, _tickMsEma);

        Events(now, pose, hits, prompt);

        _nTicks++;
        if (pose.Quality is YardQuality.Fix2 or YardQuality.Fix2Lm) _nFix2++;
        else if (pose.Quality == YardQuality.Fix1) _nFix1++;
        else _nNone++;

        _tickMsEma = _tickMsEma <= 0
            ? _tickSw.Elapsed.TotalMilliseconds
            : 0.9 * _tickMsEma + 0.1 * _tickSw.Elapsed.TotalMilliseconds;

        if (now - _lastStatus >= NavTuning.YardStatusS)
        {
            _lastStatus = now;
            StatusLine(pose, hits, dot);
        }
    }

    private void Events(double now, YardPose pose, IReadOnlyList<BlipHit> hits, bool prompt)
    {
        bool lightning = hits.Any(h => h.Id == BlipId.Lightning);
        if (lightning) { _lastLightningT = now; if (_lightningLost) { _lightningLost = false; } }
        else if (!_lightningLost && now - _lastLightningT > 5.0)
        {
            _lightningLost = true;
            _rec.Event(now, "POSE_LOST", "mất ⚡ quá 5 s");
            _rec.DumpRing(now, "nolm", 0.1);
        }

        if (prompt != _promptOn)
        {
            _promptOn = prompt;
            _rec.Event(now, prompt ? "PROMPT_ON" : "PROMPT_OFF");
        }

        if (pose.ScaleReinit)
            _rec.Event(now, "ZOOM_CHANGE", $"s={pose.S.ToString("F3", CultureInfo.InvariantCulture)}");
        if (pose.Reinit)
            _rec.Event(now, "POSE_REINIT", $"P={pose.P}");

        bool wasUsable = _lastQuality is YardQuality.Fix2 or YardQuality.Fix2Lm or YardQuality.Fix1;
        if (!wasUsable && pose.Usable) _rec.Event(now, "POSE_REGAINED", pose.Quality);
        else if (wasUsable && pose.Quality == YardQuality.Lost) _rec.Event(now, "POSE_LOST", "không còn mốc");
        _lastQuality = pose.Quality;

        if (pose.T is not null)
        {
            if (_lastT is not null && (pose.T.Value - _lastT.Value).Len > 10.0)
                _rec.Event(now, "TRIP_T_CHANGED", $"T={pose.T.Value}");
            _lastT = pose.T;
        }
    }

    private void StatusLine(YardPose pose, IReadOnlyList<BlipHit> hits, TargetOutput dot)
    {
        string Mark(BlipId id)
        {
            var h = hits.FirstOrDefault(x => x.Id == id);
            return BlipTemplates.Name(id) + (h is null ? "–" : h.Clipped ? "!" : "✓");
        }

        double n = Math.Max(1, _nTicks);
        string line =
            $"[GHI BẢN ĐỒ] fix={pose.Quality} c={pose.Conf.ToString("F2", CultureInfo.InvariantCulture)} " +
            $"s={pose.S.ToString("F2", CultureInfo.InvariantCulture)} " +
            $"θ={pose.ThetaDeg.ToString("F0", CultureInfo.InvariantCulture)}° P={pose.P} " +
            $"T={(pose.T is null ? "–" : pose.T.Value.ToString())} dot={dot.Quality} " +
            $"{Mark(BlipId.Lightning)} {Mark(BlipId.Cross)} {Mark(BlipId.Pizza)} | chuyến {Trips} | " +
            $"2mốc {(100 * _nFix2 / n):F0}% 1mốc {(100 * _nFix1 / n):F0}% mất {(100 * _nNone / n):F0}% | " +
            $"tick {_tickMsEma.ToString("F1", CultureInfo.InvariantCulture)}ms";

        // O trang thai nhan moi 0,5 s; khung Dien bien (va bot-log.txt) chi nhan moi 5 s — hai lan mot
        // giay suot 20-30 chuyen thi moi dong khac bi troi mat truoc khi doc kip.
        Status?.Invoke(line);
        if (_lastLogLine <= 0 || _lastStatus - _lastLogLine >= 5.0)
        {
            _lastLogLine = _lastStatus;
            Emit(line);
        }
    }

    public void Dispose()
    {
        try { _locator?.Dispose(); } catch { }
        try { _capture?.Dispose(); } catch { }
    }
}
