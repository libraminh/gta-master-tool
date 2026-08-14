using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Badge "● ON" nhỏ, luôn nổi trên cùng, click xuyên xuống game.
/// Ve bang GDI thuong: UpdateLayeredWindow va WS_EX_TRANSPARENT deu cho ra
/// cua so trong khi lop duoi la DirectX.
/// </summary>
internal sealed class StatusOverlay : Form
{
    private const int MarginPx = 12;
    private const int BadgeW = 70;
    private const int BadgeH = 24;

    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "overlay-log.txt");

    private readonly System.Windows.Forms.Timer _timer = new();
    private string _windowMatch = "PlayXGTA";
    private int _ticks;

    public StatusOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(BadgeW, BadgeH);
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(16, 18, 22);
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
        if (wasShown) Log("ẩn badge");
    }

    /// <summary>Click di xuyen xuong cua so ben duoi (game) thay vi vao badge.</summary>
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
    }

    private void Place(bool log)
    {
        if (IsDisposed || !IsHandleCreated) return;

        var (x, y, source) = AnchorPoint();
        Native.SetWindowPos(
            Handle, Native.HWND_TOPMOST,
            x, y, BadgeW, BadgeH,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        if (log) LogState(x, y, source);
    }

    /// <summary>
    /// Goc tren-trai cua so game, nhung chi nhan neu cua so do nam tren man hinh
    /// game — khong thi badge de bay sang man phu roi khong ai thay.
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
                    Math.Clamp(rect.Left + MarginPx, bounds.Left, bounds.Right - BadgeW),
                    Math.Clamp(rect.Top + MarginPx, bounds.Top, bounds.Bottom - BadgeH),
                    $"cửa sổ “{TitleOf(game)}” tại ({rect.Left},{rect.Top}) {rect.Width}x{rect.Height}");
        }

        return (bounds.Left + MarginPx, bounds.Top + MarginPx,
            $"màn hình {screen.DeviceName} ({bounds.Left},{bounds.Top}) {bounds.Width}x{bounds.Height}");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var on = Color.FromArgb(50, 255, 90);
        using var pen = new Pen(Color.FromArgb(70, 80, 90));
        g.DrawRectangle(pen, 0, 0, BadgeW - 1, BadgeH - 1);

        using var brush = new SolidBrush(on);
        g.FillEllipse(brush, 8, 7, 10, 10);

        using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
        TextRenderer.DrawText(g, "ON", font, new Rectangle(22, 0, 44, BadgeH), on,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left
            | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    // ---------------- chan doan ----------------
    // Ba lan sua truoc deu "khong thay gi" ma khong biet chet o khau nao,
    // nen ghi lai hwnd + rect thuc te de doc duoc bang mat.

    private void LogState(int x, int y, string source)
    {
        string self = Native.GetWindowRect(Handle, out var r)
            ? $"({r.Left},{r.Top}) {r.Width}x{r.Height}"
            : "GetWindowRect thất bại";

        Log($"đặt ({x},{y}) {BadgeW}x{BadgeH} theo {source} | " +
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
