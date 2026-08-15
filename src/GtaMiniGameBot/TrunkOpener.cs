using System.Diagnostics;

namespace GtaMiniGameBot;

internal sealed class TrunkStepException : Exception
{
    public TrunkStepException(string message) : base(message) { }
}

/// <summary>
/// Mở cốp xe: giữ Alt → click "Tương tác" → click "Cốp xe" → nhả Alt → chờ cốp mở.
///
/// Ba điều đã đo được trên máy thật và định hình toàn bộ lớp này:
///
///  1. Rê chuột bằng SendInput làm CAMERA XOAY và menu tắt theo — GTA đọc MOUSEEVENTF_MOVE
///     thành raw input. Nên mọi cú rê ở đây đi bằng <see cref="InputSender.MoveCursorOnly"/>.
///  2. Menu đổi từ 2 nút sang 4 nút mất HƠN MỘT GIÂY. Không ngủ cứng bước nào; mỗi bước chờ
///     tới khi dò thấy đúng nút cần, và phải thấy trên hai khung liên tiếp.
///  3. Trong lúc Alt còn xuống, phím tắt dừng bot (đăng ký không modifier) KHÔNG nổ. Nên có
///     đồng hồ tự nhả Alt, chạy độc lập với luồng bot — nó là thứ duy nhất còn tác dụng nếu
///     luồng bot treo.
/// </summary>
internal sealed class TrunkOpener : IDisposable
{
    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;
    private readonly Action<string> _log;
    private readonly MenuLocator _menu;
    private readonly InventoryReader _state;
    private readonly System.Threading.Timer _altWatchdog;

    private readonly object _altGate = new();
    private bool _altDown;
    private bool _watchdogFired;

    private TrunkOpener(FishingConfig cfg, Screen screen, FishingProfile profile,
                        MenuLocator menu, InventoryReader state, Action<string> log)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _menu = menu;
        _state = state;
        _log = log;
        _altWatchdog = new System.Threading.Timer(_ => OnWatchdog(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static TrunkOpener Create(FishingConfig cfg, Screen screen, FishingProfile profile,
                                     Action<string> log, out string problem)
    {
        var menu = MenuLocator.Create(cfg, screen, profile, out problem);
        if (menu is null) return null;

        var state = InventoryReader.Create(cfg, screen, profile, out problem);
        if (state is null) { menu.Dispose(); return null; }

        return new TrunkOpener(cfg, screen, profile, menu, state, log);
    }

    public ScreenState ReadState() => _state.Read();
    public List<MenuHit> FindMenu() => _menu.FindAll();
    public MenuPick Pick(string label, IReadOnlyList<MenuHit> hits) => _menu.Best(label, hits);
    public Rectangle MenuBand => _menu.BandRegion;
    public Color PillColor => _menu.Target;

    // ---------------------------------------------------------------- mở

    /// <summary>Ném <see cref="TrunkStepException"/> nếu không đi hết được chuỗi.</summary>
    public void Open(CancellationToken ct)
    {
        Native.GetCursorPos(out var saved);
        try
        {
            RequireFocus();

            var pre = _state.Read();
            if (pre.AnyOpen)
                throw new TrunkStepException("đang có màn hình mở sẵn (" + pre + ") — lệch trạng thái, không bấm gì");

            if (Native.IsKeyDown(HeldKeys.VK_ALT))
                throw new TrunkStepException("Alt đang được giữ sẵn — nhả ra rồi thử lại");

            MenuPick interact = null;
            for (int attempt = 1; attempt <= _cfg.AltRetries + 1 && interact is null; attempt++)
            {
                AltDown();
                Sleep(ct, _cfg.AltMenuAppearMs);
                interact = WaitForLabel("Tương tác", ct);
                if (interact is not null) break;

                _log($"lần {attempt}: không thấy nút Tương tác — nhả Alt rồi thử lại");
                AltUp();
                Sleep(ct, _cfg.AltRetryGapMs);
            }

            if (interact is null)
                throw new TrunkStepException("không hiện menu Alt — xe có còn cạnh không, camera có hướng vào xe không?");

            _log("thấy Tương tác: " + interact.Detail);
            ClickAt(interact.Hit.Click, ct);

            // Cho menu chuyen sang 4 nut. Day la cho ngu cung 400 ms da that bai.
            var trunk = WaitForLabel("Cốp xe", ct, _cfg.MenuClickRetries, interact.Hit.Click);
            if (trunk is null)
                throw new TrunkStepException("menu không chuyển sang mục Cốp xe");

            _log("thấy Cốp xe: " + trunk.Detail);
            ClickAt(trunk.Hit.Click, ct);

            AltUp();
            if (Native.IsKeyDown(HeldKeys.VK_ALT))
            {
                _log("Alt vẫn còn xuống sau khi nhả — gửi lại");
                for (int i = 0; i < 3 && Native.IsKeyDown(HeldKeys.VK_ALT); i++)
                {
                    InputSender.AltUp();
                    Sleep(ct, 60);
                }
            }

            if (!WaitFor(() => _state.Read().TrunkOpen, _cfg.TrunkOpenMs, ct, out var last))
                throw new TrunkStepException($"cốp xe không mở ({last})");

            _log("cốp xe đã mở — " + last);
        }
        finally
        {
            AltUp();
            try { InputSender.LeftUp(); } catch { }
            try { InputSender.MoveCursorOnly(saved.x, saved.y); } catch { }
        }
    }

    /// <summary>
    /// Đóng hết bằng MỘT lần Esc. Không bấm lần hai: nếu màn hình vốn đã đóng thì cú Esc đó
    /// mở menu tạm dừng, và bấm tiếp chỉ càng lún sâu.
    /// </summary>
    public void CloseAll(CancellationToken ct)
    {
        RequireFocus();
        InputSender.TapKey(0x1B);

        if (!WaitFor(() => !_state.Read().AnyOpen, _cfg.EscCloseMs, ct, out var last))
            throw new TrunkStepException($"Esc không đóng được màn hình ({last})");

        Sleep(ct, _cfg.AfterEscMs);
        var after = _state.Read();
        if (after.PauseKnown && after.PauseOpen)
            throw new TrunkStepException("Esc mở menu tạm dừng — trạng thái lệch, dừng để không bấm bừa");
    }

    /// <summary>
    /// Giữ Alt, đọc ra mọi khối dò được kèm điểm của cả ba nhãn, rồi nhả — KHÔNG click.
    /// Đây là buổi tinh chỉnh ngưỡng: nhìn số thật rồi chỉnh, thay vì đoán.
    /// </summary>
    public void Diagnose(CancellationToken ct)
    {
        Native.GetCursorPos(out var saved);
        try
        {
            RequireFocus();
            _log($"vùng quét {MenuBand.Width}×{MenuBand.Height} @ {MenuBand.X},{MenuBand.Y}" +
                 $"  màu nút #{PillColor.R:X2}{PillColor.G:X2}{PillColor.B:X2} ±{_cfg.MenuColorTol}");
            _log("trạng thái trước: " + _state.Read());

            AltDown();
            Sleep(ct, _cfg.AltMenuAppearMs + 300);

            var hits = FindMenu();
            _log($"dò được {hits.Count} khối:");
            foreach (var h in hits)
                _log($"   {h}   {_menu.DescribeScores(h)}");

            foreach (string label in new[] { "Tương tác", "Cốp xe", "Bơm nhiên liệu" })
            {
                var pick = _menu.Best(label, hits);
                _log($"   chọn “{label}”: " + (pick is null ? "KHÔNG đủ tự tin" : pick.Detail));
            }
        }
        finally
        {
            AltUp();
            try { InputSender.MoveCursorOnly(saved.x, saved.y); } catch { }
        }
    }

    // ---------------------------------------------------------------- chờ / click

    /// <summary>
    /// Chờ tới khi dò thấy nhãn, phải thấy trên HAI khung liên tiếp. Một khung có thể trúng
    /// đúng lúc menu đang mờ dần vào/ra, lúc đó toạ độ chưa đứng yên để mà click.
    /// </summary>
    private MenuPick WaitForLabel(string label, CancellationToken ct,
                                  int reclicks = 0, Point? reclickAt = null)
    {
        for (int round = 0; round <= reclicks; round++)
        {
            var sw = Stopwatch.StartNew();
            MenuPick prev = null;

            while (sw.ElapsedMilliseconds < _cfg.AltMenuWaitMs)
            {
                ct.ThrowIfCancellationRequested();
                RequireFocus();

                var pick = _menu.Best(label, _menu.FindAll());
                if (pick is not null && prev is not null) return pick;
                prev = pick;
                Sleep(ct, _cfg.PollMs);
            }

            if (round < reclicks && reclickAt is not null)
            {
                _log($"chưa thấy “{label}” sau {_cfg.AltMenuWaitMs} ms — click lại (lần {round + 1})");
                ClickAt(reclickAt.Value, ct);
            }
        }
        return null;
    }

    private bool WaitFor(Func<bool> done, int timeoutMs, CancellationToken ct, out string last)
    {
        var sw = Stopwatch.StartNew();
        int hold = 0;
        last = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            last = _state.Read().ToString();
            hold = done() ? hold + 1 : 0;
            if (hold >= 2) return true;
            Sleep(ct, _cfg.PollMs);
        }
        return false;
    }

    /// <summary>
    /// Rê CHỈ bằng SetCursorPos rồi click tại chỗ. Không dùng MoveSmooth: cú SendInput kèm
    /// theo bị game đọc thành lệnh xoay camera, camera quay là menu tắt.
    /// </summary>
    private void ClickAt(Point p, CancellationToken ct)
    {
        RequireFocus();
        InputSender.MoveCursorOnlySmooth(p.X, p.Y, _cfg.MenuMoveSteps);
        Sleep(ct, _cfg.MenuHoverMs);
        InputSender.LeftDown();
        Sleep(ct, 60);
        InputSender.LeftUp();
        _log($"click @ {p.X},{p.Y}");
    }

    // ---------------------------------------------------------------- Alt

    private void AltDown()
    {
        lock (_altGate)
        {
            if (_altDown) return;
            _watchdogFired = false;
            InputSender.AltDown();
            _altDown = true;
            _altWatchdog.Change(_cfg.AltMaxHoldMs, Timeout.Infinite);
        }
    }

    private void AltUp()
    {
        lock (_altGate)
        {
            _altWatchdog.Change(Timeout.Infinite, Timeout.Infinite);
            if (!_altDown) return;
            try { InputSender.AltUp(); } catch { }
            _altDown = false;
        }
    }

    private void OnWatchdog()
    {
        bool fired = false;
        lock (_altGate)
        {
            if (!_altDown) return;
            try { InputSender.AltUp(); } catch { }
            _altDown = false;
            _watchdogFired = true;
            fired = true;
        }
        if (fired)
            _log($"ĐỒNG HỒ AN TOÀN: giữ Alt quá {_cfg.AltMaxHoldMs} ms — đã cưỡng bức nhả");
    }

    public bool WatchdogFired { get { lock (_altGate) return _watchdogFired; } }

    /// <summary>
    /// Mất focus là dừng ngay, KHÔNG chờ như FishingBot.WaitWindow: hàm đó lặp vô hạn, mà ở
    /// đây Alt đang xuống — chờ tức là giữ Alt vô thời hạn.
    /// </summary>
    private void RequireFocus()
    {
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) return;
        string title = Native.ForegroundTitle();
        if (!title.Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase))
            throw new TrunkStepException($"game mất focus (đang là “{title}”) — dừng để không bấm bừa");
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
    }

    public void Dispose()
    {
        AltUp();
        _altWatchdog.Dispose();
        _menu?.Dispose();
        _state?.Dispose();
    }
}
