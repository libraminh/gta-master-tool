namespace GtaMiniGameBot;

/// <summary>
/// Điều phối hai minigame của nghề Thợ điện: thăm dò xem màn nào đang hiện rồi giao cho bot tương
/// ứng, xong lại quay về thăm dò.
///
/// Vì sao cần lớp này thay vì để người dùng tự chọn: một phiên làm nghề gặp cả panel đi dây lẫn
/// bảng nước/điện, và người chơi đang ở trong game thì không bấm đổi tab được. Nhưng hai bot KHÔNG
/// được chạy cùng lúc — cả hai đều gửi chuột/phím vào game — nên phải có một chỗ quyết định lượt
/// của ai.
///
/// Việc thăm dò cố tình MỎNG: chỉ hỏi "panel/bảng có đang hiện không", còn mọi logic giải nằm
/// trong <see cref="WireBot"/> / <see cref="BoardBot"/>. Lớp này không biết gì về màu hay hoán vị.
/// </summary>
internal sealed class ElectricBot
{
    private readonly ElectricConfig _cfg;
    private readonly Screen _screen;
    private readonly ElectricProfile _profile;

    private CancellationTokenSource _cts;
    private Thread _thread;
    private bool _windowWarned;

    private WireBot _wire;
    private BoardBot _board;
    private NavBot _navBot;

    /// <summary>
    /// Cấu hình Discord (webhook, ID để ping) nằm trong <see cref="FishingConfig"/> vì job Câu cá
    /// dùng trước — nhưng nó là cấu hình chung của app, không phải của riêng nghề đó. Nạp MỘT LẦN
    /// mỗi phiên chạy thay vì mỗi chặng đi, và null khi đọc hỏng thì <see cref="DiscordNotifier"/>
    /// tự im lặng.
    /// </summary>
    private FishingConfig _discordCfg;

    /// <summary>
    /// Trạng thái ăn uống của cả lượt bật job. Phải sống ở đây chứ không trong <see cref="NavBot"/>:
    /// <see cref="RunNav"/> dựng NavBot MỚI sau mỗi minigame, nên "đã kết luận hết bánh" hay "vành
    /// cung nằm ở bán kính này" mà nhớ trong NavBot thì cứ giải xong một bảng là mất sạch.
    /// </summary>
    private SurvivalState _survival;

    public ElectricBot(ElectricConfig cfg, Screen screen, ElectricProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
    }

    public bool Running => _thread is { IsAlive: true };

    /// <summary>Số lượt đã giải, cộng cả hai minigame.</summary>
    public int Rounds { get; private set; }

    public event Action<string> Log;
    public event Action<int> RoundsChanged;
    public event Action<string> Stopped;

    public void Start()
    {
        if (Running) return;
        Rounds = 0;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "ElectricBot" };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _wire?.Stop();
        _board?.Stop();
        _navBot?.Stop();
    }

    public void StopAndWait(int ms = 4000)
    {
        _cts?.Cancel();
        _wire?.StopAndWait(ms);
        _board?.StopAndWait(ms);
        _navBot?.StopAndWait(ms);

        var t = _thread;
        if (t is null || !t.IsAlive) return;
        try { t.Join(ms); } catch { }
    }

    // ---------------------------------------------------------------- vong dieu phoi

    private void Run(CancellationToken ct)
    {
        string message = "người dùng bấm dừng";
        WireReader wireProbe = null;
        BoardReader boardProbe = null;

        bool wantWire = _cfg.Mode is ElectricMode.Wire or ElectricMode.Both;
        bool wantBoard = _cfg.Mode is ElectricMode.Board or ElectricMode.Both;

        try
        {
            if (wantWire)
            {
                wireProbe = WireReader.Open(_cfg, _screen, _profile);
                if (!wireProbe.Configured)
                {
                    Emit("không thăm dò được panel dây: " + wireProbe.Problem);
                    wantWire = false;
                }
            }

            if (wantBoard)
            {
                boardProbe = BoardReader.Open(_cfg, _screen, _profile);
                if (!boardProbe.Configured)
                {
                    Emit("không thăm dò được bảng: " + boardProbe.Problem);
                    wantBoard = false;
                }
            }

            if (!wantWire && !wantBoard)
            {
                message = "không mở được vùng đọc nào";
                Emit("dừng: " + message);
                return;
            }

            bool wantNav = _cfg.AutoWalk;
            _survival = new SurvivalState();
            if (wantNav && _cfg.Survival.Enabled)
                try { _discordCfg = FishingConfig.Load(); } catch { _discordCfg = null; }

            Emit($"chế độ {TenCheDo(_cfg.Mode)} — " +
                 (wantNav
                     ? $"tự đi tới điểm làm việc, {(_cfg.AutoLoop ? "chạy liên tục" : "dừng sau một lượt")}."
                     : "đang chờ minigame hiện ra."));

            // Cho bo dieu huong muon lai dung hai bo tham do nay: no chi biet "da bam E xong roi",
            // con "minigame da mo chua" thi o day moi tra loi duoc.
            bool PanelVisible() =>
                (wantWire && !wireProbe.FindPanel().IsEmpty) ||
                (wantBoard && boardProbe.TryRead(out _) is not null);

            // Bo dieu huong can biet no vua duoc goi lai SAU mot minigame (de reset camera, lay lai W)
            // va bang da bien mat bao lau: WireBot bao Solved sau PanelGoneMs, BoardBot sau 3 s.
            bool justSolved = false;
            bool justLost = false;
            int panelGoneMs = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                WaitWindow(ct);

                if (wantWire && !wireProbe.FindPanel().IsEmpty)
                {
                    Emit("thấy panel đi dây — giao cho bộ giải dây.");
                    if (!RunWire(ct, out message, out bool solved)) return;
                    if (StopAfterOneRound(ref message)) return;
                    if (solved) { justSolved = true; panelGoneMs = _cfg.Wire.PanelGoneMs; }
                    continue;
                }

                if (wantBoard && boardProbe.TryRead(out _) is not null)
                {
                    Emit("thấy bảng nước/điện — giao cho bộ giải bảng.");
                    if (!RunBoard(ct, out message, out bool cameFromBoard, out bool lost)) return;
                    if (StopAfterOneRound(ref message)) return;
                    if (cameFromBoard)
                    {
                        justLost = lost;
                        // Hỏng GIỮA TUYẾN thì bảng vẫn còn mở lúc BoardBot trả về. Không chờ nó
                        // đóng thì vòng này thấy bảng ngay và giao lại — dựng ra một tuyến "sạch"
                        // trên một bảng đã chết, rồi chết oan lần nữa vì dây không còn phóng.
                        WaitBoardGone(ct, () => boardProbe.TryRead(out _) is not null);
                        justSolved = true;
                        panelGoneMs = BoardGoneMsAfterSolved;
                    }
                    continue;
                }

                if (wantNav)
                {
                    if (!RunNav(ct, PanelVisible, justSolved, panelGoneMs, justLost, out message)) return;
                    justSolved = false;
                    justLost = false;
                    WaitPanelAfterArrival(ct, PanelVisible);
                    continue;
                }

                Sleep(ct, 300);
            }
        }
        catch (OperationCanceledException)
        {
            message = "người dùng bấm dừng";
        }
        catch (Exception ex)
        {
            message = ex.Message;
            Emit("lỗi điều phối: " + message);
        }
        finally
        {
            wireProbe?.Dispose();
            boardProbe?.Dispose();
            HeldKeys.ReleaseAll();
            Stopped?.Invoke(message);
        }
    }

    /// <summary>
    /// Giải xong một lượt mà chưa bật "chạy liên tục" thì dừng ở đây.
    ///
    /// Mặc định TẮT là cố ý: lượt thử đầu tiên của bộ điều hướng cần dừng đúng chỗ để còn đọc log,
    /// chứ bot chạy tiếp là log trôi mất.
    /// </summary>
    private bool StopAfterOneRound(ref string message)
    {
        if (_cfg.AutoLoop || Rounds <= 0) return false;

        message = $"xong {Rounds} lượt — chưa bật “chạy liên tục”";
        Emit("dừng: " + message);
        return true;
    }

    /// <summary>BoardBot chỉ báo Solved sau khi bảng đã mất liên tục 3 s (BoardBot.cs, nhánh <c>sinceSeen</c>).</summary>
    private const int BoardGoneMsAfterSolved = 3_000;

    /// <summary>
    /// Cho bộ điều hướng đi tới điểm làm việc rồi bấm E. Nó không bao giờ tự bỏ cuộc (mất điểm thì
    /// quét, lâu quá thì reset nghề, không tiến thì khởi động lại mềm — đúng bản Python), nên chỉ
    /// trả false khi không gửi được input hoặc lỗi. Tới nơi rồi thì quay lại vòng điều phối: chính
    /// hai bộ thăm dò ở trên sẽ thấy minigame.
    /// </summary>
    private bool RunNav(CancellationToken ct, Func<bool> panelVisible, bool afterMinigame, int panelGoneMs,
                        bool afterLoss, out string message)
    {
        var done = new ManualResetEventSlim(false);
        NavStopReason reason = NavStopReason.UserStopped;
        string detail = "";

        _navBot = new NavBot(_cfg, _screen, _profile)
        {
            PanelVisible = panelVisible,
            AfterMinigame = afterMinigame,
            AfterLoss = afterLoss,
            PanelGoneAgoMs = panelGoneMs,
            SurvivalState = _survival ??= new SurvivalState()
        };
        _navBot.Log += Emit;
        _navBot.Alert += (title, body) =>
            DiscordNotifier.NotifyAlert(_discordCfg, title, body, m => Emit("Discord: " + m));
        _navBot.Stopped += (r, m) => { reason = r; detail = m; done.Set(); };
        _navBot.Start();

        WaitNav(ct, done);
        _navBot = null;

        if (reason == NavStopReason.Arrived) { message = ""; return true; }
        if (reason == NavStopReason.UserStopped) throw new OperationCanceledException();

        message = $"bộ điều hướng dừng — {NavBot.TenLyDo(reason)}: {detail}";
        Emit("dừng: " + message);
        return false;
    }

    /// <summary>
    /// Sau khi bộ điều hướng báo tới nơi, panel vừa hiện lên; một khung thăm dò lỡ (hiệu ứng mở
    /// bảng) mà quay ngay về nav là NavBot mới thấy prompt còn đó và bấm E lần hai. Chờ tới 1 s.
    /// </summary>
    private void WaitPanelAfterArrival(CancellationToken ct, Func<bool> panelVisible)
    {
        for (int i = 0; i < 8; i++)
        {
            if (panelVisible()) return;
            Sleep(ct, 125);
        }
        Emit("bộ điều hướng báo có minigame nhưng thăm dò không thấy — quay lại điều hướng.");
    }

    /// <summary>
    /// Chờ bảng biến mất hẳn sau một lượt hỏng giữa tuyến, tối đa 12 s (game tự đóng bảng thua
    /// nhanh hơn thế nhiều). Hết giờ mà bảng vẫn còn thì cứ đi tiếp — thà giao lại một lần thừa
    /// còn hơn đứng chờ mãi.
    /// </summary>
    private void WaitBoardGone(CancellationToken ct, Func<bool> boardVisible)
    {
        if (!boardVisible()) return;

        Emit("bảng còn mở sau lượt hỏng — chờ nó đóng rồi mới đi tiếp");
        for (int i = 0; i < 60; i++)
        {
            Sleep(ct, 200);
            if (!boardVisible()) return;
        }
        Emit("chờ 12s mà bảng chưa đóng — đi tiếp");
    }

    private void WaitNav(CancellationToken ct, ManualResetEventSlim done)
    {
        while (!done.IsSet)
        {
            if (ct.IsCancellationRequested)
            {
                _navBot?.StopAndWait();
                done.Wait(1500);
                throw new OperationCanceledException();
            }
            done.Wait(80);
        }
    }

    /// <summary>
    /// Chạy bot dây tới khi nó dừng. Trả false nếu nó dừng vì một lý do mà điều phối KHÔNG nên
    /// chạy tiếp — kéo mãi không dính, đọc phản hồi không chắc, hay mâu thuẫn dữ liệu. Những lý do
    /// đó là dấu hiệu có gì sai thật, và thử lại chỉ là giật điện người chơi thêm lần nữa.
    /// </summary>
    private bool RunWire(CancellationToken ct, out string message, out bool solved)
    {
        var done = new ManualResetEventSlim(false);
        WireStopReason reason = WireStopReason.UserStopped;
        string detail = "";

        _wire = new WireBot(_cfg, _screen, _profile);
        _wire.Log += Emit;
        _wire.RoundsChanged += _ => Bump();
        _wire.Stopped += (r, m) => { reason = r; detail = m; done.Set(); };
        _wire.Start();

        Wait(ct, done);
        _wire = null;

        solved = reason == WireStopReason.Solved;
        bool keepGoing = reason is WireStopReason.Solved or WireStopReason.NoPanel;
        message = keepGoing ? "" : $"bộ giải dây dừng — {WireBot.TenLyDo(reason)}: {detail}";
        if (!keepGoing) Emit("dừng: " + message);
        return keepGoing;
    }

    /// <summary>
    /// <paramref name="cameFromBoard"/> nghĩa là "vừa có một bảng trên màn và nó đã đóng", KHÔNG
    /// phải "đã thắng". Bộ điều hướng dùng cờ này để chạy pha hậu minigame (reset camera, lấy lại
    /// W) — mà pha đó cần thiết y như nhau dù lượt vừa rồi thắng hay thua: nhân vật vẫn đang đứng
    /// ở đầu nối, camera vẫn ở góc bảng, W vẫn đang bị nhả. Đếm số lượt thắng đi đường khác
    /// (<see cref="BoardBot.RoundsChanged"/>), nên gộp ở đây không làm sai thống kê.
    /// </summary>
    private bool RunBoard(CancellationToken ct, out string message, out bool cameFromBoard, out bool lost)
    {
        var done = new ManualResetEventSlim(false);
        BoardStopReason reason = BoardStopReason.UserStopped;
        string detail = "";

        _board = new BoardBot(_cfg, _screen, _profile);
        _board.Log += Emit;
        _board.RoundsChanged += _ => Bump();
        _board.Stopped += (r, m) => { reason = r; detail = m; done.Set(); };
        _board.Start();

        Wait(ct, done);
        _board = null;

        cameFromBoard = reason is BoardStopReason.Solved
                               or BoardStopReason.BoardFailed
                               or BoardStopReason.NoProgress
                               or BoardStopReason.TurnNotConfirmed
                               or BoardStopReason.LateStart
                               or BoardStopReason.WireDidNotStart;

        // THUA: diem lam viec van o dau noi nay, bo dieu huong phai dung tai cho E lai thay vi di
        // tim diem ke (xem NavBot.AfterLoss).
        lost = reason is BoardStopReason.BoardFailed
                      or BoardStopReason.NoProgress
                      or BoardStopReason.TurnNotConfirmed
                      or BoardStopReason.LateStart
                      or BoardStopReason.WireDidNotStart;

        // MẤT MỘT BẢNG KHÔNG ĐƯỢC PHÉP THÀNH MẤT CẢ PHIÊN.
        //
        // Trước đây danh sách này chỉ có Solved/NoBoard, nên mọi lý do khác đều giết job — và đó
        // đúng là thứ khớp với "chạy được ~20 lượt rồi tự dừng". Ba lý do dưới đây đều là "lượt
        // này hỏng", không phải "có gì đó sai về cấu hình":
        //   - BoardFailed      : bảng đóng mà chưa giải được lượt nào (planner giữ tới hết giờ);
        //   - NoProgress       : dây va tường giữa tuyến;
        //   - TurnNotConfirmed : gõ phím rẽ mà dây không rẽ;
        //   - LateStart        : dựng tuyến xong thì dây đã vượt ngã rẽ đầu — bản đồ này rắc rối
        //                        hơn nên dựng lâu hơn, KHÔNG phải cấu hình sai;
        //   - WireDidNotStart  : dây không phóng khỏi đầu nối (bảng đã chết trước khi ta vào).
        // Bảng sau là một bản đồ khác, nên thử lại là hợp lý.
        //
        // LateStart nằm đây là bài học đắt ngày 04/09: hai bảng 17:05 chết vì nó và mỗi cái GIẾT CẢ
        // PHIÊN — người dùng thấy "fail liên tục, tệ hơn cả bản cũ" trong khi 6/8 bảng vẫn thắng.
        // Chỉ InputFailed (mất focus / SendInput hỏng) và Error mới là dấu hiệu sai thật.
        bool keepGoing = reason is BoardStopReason.Solved
                                or BoardStopReason.NoBoard
                                or BoardStopReason.BoardFailed
                                or BoardStopReason.NoProgress
                                or BoardStopReason.TurnNotConfirmed
                                or BoardStopReason.LateStart
                                or BoardStopReason.WireDidNotStart;

        if (keepGoing && reason != BoardStopReason.Solved && reason != BoardStopReason.NoBoard)
            Emit($"lượt bảng hỏng — {BoardBot.TenLyDo(reason)}: {detail} → đi tìm bảng kế, không dừng job");

        message = keepGoing ? "" : $"bộ giải bảng dừng — {BoardBot.TenLyDo(reason)}: {detail}";
        if (!keepGoing) Emit("dừng: " + message);
        return keepGoing;
    }

    /// <summary>
    /// Chờ bot con xong, nhưng vẫn phản ứng ngay khi người dùng bấm dừng: huỷ thì báo cho bot con
    /// rồi chờ nó chết hẳn, KHÔNG bỏ nó chạy tiếp — bot con đang giữ phím trong game.
    /// </summary>
    private void Wait(CancellationToken ct, ManualResetEventSlim done)
    {
        while (!done.IsSet)
        {
            if (ct.IsCancellationRequested)
            {
                _wire?.StopAndWait();
                _board?.StopAndWait();
                _navBot?.StopAndWait();
                done.Wait(1500);
                throw new OperationCanceledException();
            }
            done.Wait(80);
        }
    }

    private void Bump()
    {
        Rounds++;
        RoundsChanged?.Invoke(Rounds);
    }

    public static string TenCheDo(ElectricMode m) => m switch
    {
        ElectricMode.Wire => "chỉ panel đi dây",
        ElectricMode.Board => "chỉ bảng nước/điện",
        _ => "cả hai minigame"
    };

    private void WaitWindow(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) return;

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
                return;
            }

            if (!_windowWarned)
            {
                Emit($"tạm dừng: chưa focus “{_cfg.WindowMatch}” (đang focus: “{title}”)");
                _windowWarned = true;
            }
            Sleep(ct, 250);
        }
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
    }

    private void Emit(string line) => Log?.Invoke(line);
}
