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

    /// <summary>Bộ điều hướng đo xong count/độ — panel lưu lại để lần chạy sau khỏi đo lại.</summary>
    public event Action<double, int> Calibrated;
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
            Emit($"chế độ {TenCheDo(_cfg.Mode)} — " +
                 (wantNav
                     ? $"tự đi tới điểm làm việc, {(_cfg.AutoLoop ? "chạy liên tục" : "dừng sau một lượt")}."
                     : "đang chờ minigame hiện ra."));

            // Cho bo dieu huong muon lai dung hai bo tham do nay: no chi biet "da bam E xong roi",
            // con "minigame da mo chua" thi o day moi tra loi duoc.
            bool PanelVisible() =>
                (wantWire && !wireProbe.FindPanel().IsEmpty) ||
                (wantBoard && boardProbe.TryRead(out _) is not null);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                WaitWindow(ct);

                if (wantWire && !wireProbe.FindPanel().IsEmpty)
                {
                    Emit("thấy panel đi dây — giao cho bộ giải dây.");
                    if (!RunWire(ct, out message)) return;
                    if (StopAfterOneRound(ref message)) return;
                    continue;
                }

                if (wantBoard && boardProbe.TryRead(out _) is not null)
                {
                    Emit("thấy bảng nước/điện — giao cho bộ giải bảng.");
                    if (!RunBoard(ct, out message)) return;
                    if (StopAfterOneRound(ref message)) return;
                    continue;
                }

                if (wantNav)
                {
                    if (!RunNav(ct, PanelVisible, out message)) return;
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

    /// <summary>
    /// Cho bộ điều hướng đi tới điểm làm việc rồi bấm E. Trả false nếu nó dừng vì một lý do mà
    /// chạy tiếp cũng vô ích — chưa hiệu chuẩn, không gửi được input, hoặc thử hết lượt vẫn không
    /// tới. Tới nơi rồi thì quay lại vòng điều phối: chính hai bộ thăm dò ở trên sẽ thấy minigame.
    /// </summary>
    private bool RunNav(CancellationToken ct, Func<bool> panelVisible, out string message)
    {
        var done = new ManualResetEventSlim(false);
        NavStopReason reason = NavStopReason.UserStopped;
        string detail = "";

        _navBot = new NavBot(_cfg, _screen, _profile) { PanelVisible = panelVisible };
        _navBot.Log += Emit;
        _navBot.Calibrated += (cpd, sign) => Calibrated?.Invoke(cpd, sign);
        _navBot.Stopped += (r, m) => { reason = r; detail = m; done.Set(); };
        _navBot.Start();

        WaitNav(ct, done);
        _navBot = null;

        if (reason == NavStopReason.Arrived) { message = ""; return true; }

        message = $"bộ điều hướng dừng — {NavBot.TenLyDo(reason)}: {detail}";
        Emit("dừng: " + message);
        return false;
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
    private bool RunWire(CancellationToken ct, out string message)
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

        bool keepGoing = reason is WireStopReason.Solved or WireStopReason.NoPanel;
        message = keepGoing ? "" : $"bộ giải dây dừng — {WireBot.TenLyDo(reason)}: {detail}";
        if (!keepGoing) Emit("dừng: " + message);
        return keepGoing;
    }

    private bool RunBoard(CancellationToken ct, out string message)
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

        bool keepGoing = reason is BoardStopReason.Solved or BoardStopReason.NoBoard;
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
