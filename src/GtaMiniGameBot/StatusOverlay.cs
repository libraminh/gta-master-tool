using System.Drawing.Drawing2D;
using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Thẻ trạng thái nhỏ trong game, luôn nổi trên cùng, click xuyên xuống game.
///
/// Ve bang GDI thuong tren nen DAC. UpdateLayeredWindow va WS_EX_TRANSPARENT deu
/// cho ra cua so trong khi lop duoi la DirectX — da thu ba lan, deu chet. Nghia la
/// khong co per-pixel alpha o day, mockup ve the hoi trong suot la khong lam duoc.
/// </summary>
internal sealed class StatusOverlay : Form
{
    private const int MarginPx = 12;

    // Pixel VAT LY, co tinh khong scale theo DPI: the nay duoc dat theo toa do vat ly
    // cua cua so game, khong theo he toa do cua app.
    private const int CardW = 224;
    private const int CardH = 66;

    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "overlay-log.txt");

    private readonly System.Windows.Forms.Timer _timer = new();
    private string _windowMatch = "PlayXGTA";
    private int _ticks;
    private FishingState _state = FishingState.Idle;

    public StatusOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(CardW, CardH);
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(13, 19, 25);
        DoubleBuffered = true;

        _timer.Interval = 200;
        _timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>
    /// Nhan trang thai moi tu bot. Chi ve lai khi dang hien — bot van phat state
    /// ngay ca luc badge dang an (job khac dang chay chang han).
    /// </summary>
    public void Update(FishingState s)
    {
        if (IsDisposed || s is null) return;
        _state = s;
        if (Visible) Invalidate();
    }

    public void ShowOn(string windowMatch)
    {
        if (IsDisposed) return;
        _windowMatch = string.IsNullOrWhiteSpace(windowMatch) ? "PlayXGTA" : windowMatch;
        _ticks = 0;

        StartLog();
        Show();
        Invalidate();
        Place(log: true);
        _timer.Start();
    }

    public new void Hide()
    {
        if (IsDisposed) return;
        bool wasShown = Visible;
        _timer.Stop();
        base.Hide();
        if (wasShown) Log("ẩn thẻ");
    }

    /// <summary>Click di xuyen xuong cua so ben duoi (game) thay vi vao the.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCHITTEST)
        {
            m.Result = (IntPtr)Native.HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    private void Tick()
    {
        _ticks++;
        Place(log: _ticks % 25 == 0);
        // Dong ho trong pha phai nhich du bot chua doi pha.
        if (_state.Running) Invalidate();
    }

    private void Place(bool log)
    {
        if (IsDisposed || !IsHandleCreated) return;

        var (x, y, source) = AnchorPoint();
        Native.SetWindowPos(
            Handle, Native.HWND_TOPMOST,
            x, y, CardW, CardH,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        if (log) LogState(x, y, source);
    }

    /// <summary>
    /// Goc tren-trai cua so game, nhung chi nhan neu cua so do nam tren man hinh
    /// game — khong thi the de bay sang man phu roi khong ai thay.
    /// </summary>
    private (int x, int y, string source) AnchorPoint()
    {
        var screen = FishingConfig.Prefer2kOrPrimary();
        var bounds = screen.Bounds;

        var game = Native.FindWindowByTitleContains(_windowMatch);
        if (game != IntPtr.Zero
            && !Native.IsIconic(game)
            && Native.GetWindowRect(game, out var r)
            && r.Width > 50 && r.Height > 50)
        {
            var rect = new Rectangle(r.Left, r.Top, r.Width, r.Height);
            if (bounds.IntersectsWith(rect))
                return (
                    Math.Clamp(rect.Left + MarginPx, bounds.Left, bounds.Right - CardW),
                    Math.Clamp(rect.Top + MarginPx, bounds.Top, bounds.Bottom - CardH),
                    $"cửa sổ “{TitleOf(game)}” tại ({rect.Left},{rect.Top}) {rect.Width}x{rect.Height}");
        }

        return (bounds.Left + MarginPx, bounds.Top + MarginPx,
            $"màn hình {screen.DeviceName} ({bounds.Left},{bounds.Top}) {bounds.Width}x{bounds.Height}");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var accent = Color.FromArgb(75, 212, 255);
        var good = Color.FromArgb(58, 219, 109);
        var dim = Color.FromArgb(110, 126, 146);
        var head = Color.FromArgb(234, 241, 248);

        // Vien trai la dai mau duy nhat — the nay nam tren canh game nen cang it
        // net ke cang do che tam nhin.
        using (var b = new SolidBrush(accent))
            g.FillRectangle(b, 0, 0, 2, CardH);
        using (var p = new Pen(Color.FromArgb(46, 59, 75)))
            g.DrawRectangle(p, 0, 0, CardW - 1, CardH - 1);

        var s = _state;
        bool live = s.Running;

        // Hang 1: pha + dong ho + fill.
        string phase = live ? s.PhaseName.ToUpperInvariant() : "ON";
        Draw(g, phase, BoldFont(11f), new Rectangle(10, 6, 120, 18), live ? accent : good);

        if (live)
        {
            Draw(g, $"{s.PhaseMs / 1000.0:0.0}s", MonoFont(8f),
                 new Rectangle(10, 24, 60, 14), dim);
        }

        string big = s.Catches >= 0 && live ? s.Catches.ToString() : "";
        if (big.Length > 0)
        {
            Draw(g, big, MonoFont(14f), new Rectangle(CardW - 74, 4, 64, 20), head,
                 TextFormatFlags.Right);
            Draw(g, "con", MonoFont(8f), new Rectangle(CardW - 74, 24, 64, 14), dim,
                 TextFormatFlags.Right);
        }

        // Hang 2: thanh cau. Chi ve khi dang keo — luc khac con so nay la rac.
        double fill = s.Phase == FishingPhase.Fighting ? s.Fill01 : -1;
        using (var b = new SolidBrush(Color.FromArgb(8, 11, 16)))
            g.FillRectangle(b, 10, 42, CardW - 20, 4);
        if (fill > 0)
        {
            int w = (int)Math.Round(Math.Clamp(fill, 0, 1) * (CardW - 20));
            using var b = new SolidBrush(accent);
            g.FillRectangle(b, 10, 42, w, 4);
        }

        if (fill > 0)
            Draw(g, $"{fill * 100:0.0}%", MonoFont(8f), new Rectangle(74, 24, 60, 14), accent);

        // Hang 3: ba lo / cop.
        string line = live
            ? Kg(s)
            : "chưa chạy";
        Draw(g, line, MonoFont(8f), new Rectangle(10, 48, CardW - 20, 14), dim);
    }

    private static string Kg(FishingState s)
    {
        var sb = new StringBuilder();
        if (s.BagKg >= 0) sb.Append($"ba lô {s.BagKg:F1}/{s.BagCapKg:F0}");
        else sb.Append("ba lô --");
        if (s.TrunkFreeKg >= 0) sb.Append($"   cốp còn {s.TrunkFreeKg:F1}");
        else if (s.TrunkFull) sb.Append("   cốp đầy");
        return sb.ToString();
    }

    // Font tao moi moi lan ve la 3 allocation moi 200 ms — giu san.
    private static Font _bold, _mono;
    private static float _boldSize, _monoSize;

    private static Font BoldFont(float size)
    {
        if (_bold is null || Math.Abs(_boldSize - size) > 0.01f)
        {
            _bold?.Dispose();
            _bold = new Font("Segoe UI", size, FontStyle.Bold);
            _boldSize = size;
        }
        return _bold;
    }

    private static Font MonoFont(float size)
    {
        if (_mono is null || Math.Abs(_monoSize - size) > 0.01f)
        {
            _mono?.Dispose();
            _mono = new Font("Consolas", size);
            _monoSize = size;
        }
        return _mono;
    }

    private static void Draw(Graphics g, string text, Font f, Rectangle r, Color c,
                             TextFormatFlags extra = TextFormatFlags.Left)
    {
        TextRenderer.DrawText(g, text, f, r, c,
            extra | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    // ---------------- chan doan ----------------
    // Ba lan sua truoc deu "khong thay gi" ma khong biet chet o khau nao,
    // nen ghi lai hwnd + rect thuc te de doc duoc bang mat.

    private void LogState(int x, int y, string source)
    {
        string self = Native.GetWindowRect(Handle, out var r)
            ? $"({r.Left},{r.Top}) {r.Width}x{r.Height}"
            : "GetWindowRect thất bại";

        Log($"đặt ({x},{y}) {CardW}x{CardH} theo {source} | " +
            $"hwnd=0x{Handle.ToInt64():X} visible={Native.IsWindowVisible(Handle)} rect={self}");
    }

    private void StartLog()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# overlay-log — {DateTime.Now:HH:mm:ss dd/MM/yyyy}");
            foreach (var s in Screen.AllScreens)
                sb.AppendLine($"# màn hình {s.DeviceName} ({s.Bounds.Left},{s.Bounds.Top}) " +
                              $"{s.Bounds.Width}x{s.Bounds.Height}{(s.Primary ? " chính" : "")}");
            File.WriteAllText(LogPath, sb.ToString(), new UTF8Encoding(true));
        }
        catch { }
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {line}\r\n"); }
        catch { }
    }

    private static string TitleOf(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        Native.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
