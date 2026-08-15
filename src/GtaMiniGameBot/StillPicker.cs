using System.Diagnostics;
using System.Drawing.Imaging;

namespace GtaMiniGameBot;

/// <summary>
/// Chụp một ảnh TĨNH của cả màn game rồi khoanh vùng trên ảnh đó.
///
/// Vì sao không dùng <see cref="RegionPicker"/> như các ROI cũ: nó ẩn app rồi phủ overlay lên
/// game để kéo chuột, tức là game mất focus. Menu radial của xe chỉ tồn tại khi đang GIỮ Alt
/// trong game, tắt ngay khi mất focus — không thể khoanh trực tiếp. Kho đồ cũng vậy.
/// Nên: đếm ngược cho người dùng bày sẵn màn hình trong game → chụp → khoanh nguội trên ảnh.
///
/// Toạ độ trong ảnh trùng đúng toạ độ TƯƠNG ĐỐI góc màn, nên cắm thẳng vào
/// <see cref="FishingRect.FromRelative"/> được, không phải quy đổi.
/// </summary>
internal static class StillPicker
{
    /// <summary>
    /// Đếm ngược rồi chụp cả màn <paramref name="screen"/>.
    /// Trả null nếu người dùng huỷ hoặc lúc chụp game không còn là cửa sổ đang focus —
    /// ảnh chụp nhầm desktop mà vẫn cho khoanh thì mọi ROI sau đó đều sai một cách âm thầm.
    /// </summary>
    public static Bitmap CaptureWithCountdown(
        Form owner, Screen screen, string instruction, int seconds, string windowMatch, out string problem)
    {
        problem = null;

        var ok = MessageBox.Show(owner,
            instruction + "\r\n\r\n" +
            $"Bấm OK rồi có {seconds} giây để click vào game và bày đúng màn hình cần chụp.",
            "Chụp ảnh màn hình game", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (ok != DialogResult.OK) { problem = "đã huỷ"; return null; }

        bool wasVisible = owner is { Visible: true };
        if (wasVisible) owner.Hide();

        var overlay = new CountdownOverlay(screen);
        try
        {
            overlay.ShowNoActivate();
            var sw = Stopwatch.StartNew();
            int total = Math.Max(1, seconds) * 1000;
            while (sw.ElapsedMilliseconds < total)
            {
                overlay.SetSeconds((int)Math.Ceiling((total - sw.ElapsedMilliseconds) / 1000.0));
                Application.DoEvents();
                Thread.Sleep(60);
            }
        }
        finally
        {
            overlay.Close();
            overlay.Dispose();
        }

        // Doi overlay bien mat that su khoi man hinh truoc khi chup, khong thi no nam trong anh.
        Application.DoEvents();
        Thread.Sleep(150);

        string title = Native.ForegroundTitle();
        Bitmap shot = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(windowMatch)
                && !title.Contains(windowMatch, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"lúc chụp cửa sổ đang focus là “{title}”, không phải “{windowMatch}” — chụp lại";
            }
            else
            {
                shot = RegionPicker.Capture(screen.Bounds);
            }
        }
        catch (Exception ex)
        {
            problem = "chụp lỗi: " + ex.Message;
        }
        finally
        {
            if (wasVisible)
            {
                owner.Show();
                owner.Activate();
            }
        }

        return shot;
    }

    public static Bitmap Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // Doc qua stream roi copy: Image.FromFile giu khoa file, chup de lai la hong.
            using var fs = File.OpenRead(path);
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    public static void Save(Bitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Số đếm ngược to, luôn nổi trên cùng, KHÔNG cướp focus — nếu cướp thì game mất focus và
    /// menu radial tắt mất, đúng cái mình đang cần chụp. Cùng thủ thuật với StatusOverlay.
    /// </summary>
    private sealed class CountdownOverlay : Form
    {
        private const int W = 150;
        private const int H = 90;

        private readonly Label _label = new();
        private readonly Rectangle _bounds;

        public CountdownOverlay(Screen screen)
        {
            _bounds = screen.Bounds;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(W, H);
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(16, 18, 22);

            _label.Dock = DockStyle.Fill;
            _label.TextAlign = ContentAlignment.MiddleCenter;
            _label.Font = new Font("Segoe UI", 34F, FontStyle.Bold);
            _label.ForeColor = Color.FromArgb(255, 205, 70);
            Controls.Add(_label);
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

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST) { m.Result = (IntPtr)Native.HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }

        public void ShowNoActivate()
        {
            Show();
            // Goc tren-PHAI: cac ROI can chup (so KG, luoi o, menu radial) deu nam giua hoac ben trai.
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST,
                _bounds.Right - W - 24, _bounds.Top + 24, W, H,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        public void SetSeconds(int s) => _label.Text = Math.Max(0, s).ToString();
    }
}
