using System.Drawing.Imaging;

namespace GtaMiniGameBot;

internal sealed class RegionPickResult
{
    public Rectangle Relative { get; init; }
    public Rectangle Absolute { get; init; }
    public Bitmap Preview { get; init; }
}

/// <summary>
/// Overlay một màn: kéo hình chữ nhật, ẩn overlay rồi chụp crop (tránh dính lớp mờ).
/// </summary>
internal static class RegionPicker
{
    public static RegionPickResult Run(IWin32Window owner, Screen screen, string title, string hint)
    {
        var host = owner as Form;
        bool hid = false;
        void RestoreHost()
        {
            if (hid && host is { IsDisposed: false })
            {
                host.Show();
                host.Activate();
                hid = false;
            }
        }

        if (host is { Visible: true })
        {
            host.Hide();
            hid = true;
            Application.DoEvents();
        }

        Rectangle picked;
        try
        {
            using var overlay = new OverlayForm(screen, hint);
            if (overlay.ShowDialog() != DialogResult.OK || overlay.Picked.Width < 8)
            {
                RestoreHost();
                return null;
            }
            picked = overlay.Picked;
        }
        catch
        {
            RestoreHost();
            throw;
        }

        var abs = new Rectangle(
            screen.Bounds.X + picked.X,
            screen.Bounds.Y + picked.Y,
            picked.Width,
            picked.Height);

        Application.DoEvents();
        Thread.Sleep(80);

        Bitmap crop;
        try { crop = Capture(abs); }
        catch (Exception ex)
        {
            RestoreHost();
            MessageBox.Show(owner, "Không chụp được vùng vừa khoanh: " + ex.Message,
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        RestoreHost();

        using var preview = new PreviewForm(title, hint, crop);
        if (preview.ShowDialog(owner) != DialogResult.OK)
        {
            crop.Dispose();
            return null;
        }

        return new RegionPickResult
        {
            Relative = picked,
            Absolute = abs,
            Preview = crop
        };
    }

    public static Bitmap Capture(Rectangle abs)
    {
        var bmp = new Bitmap(abs.Width, abs.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(abs.Left, abs.Top, 0, 0, abs.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static void SavePng(Bitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
    }

    private sealed class OverlayForm : Form
    {
        private Point _a, _b;
        private bool _drag;
        public Rectangle Picked { get; private set; }

        public OverlayForm(Screen screen, string hint)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen.Bounds;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            Opacity = 0.28;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;

            var tip = new Label
            {
                Text = hint + "   ·   Esc hủy",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(180, 0, 0, 0),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter
            };
            tip.SetBounds(0, 24, screen.Bounds.Width, 36);
            Controls.Add(tip);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _drag = true;
            _a = _b = e.Location;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_drag) return;
            _b = e.Location;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_drag || e.Button != MouseButtons.Left) return;
            _drag = false;
            _b = e.Location;
            var r = Norm(_a, _b);
            if (r.Width < 8 || r.Height < 8)
            {
                Picked = Rectangle.Empty;
                DialogResult = DialogResult.Cancel;
            }
            else
            {
                Picked = r;
                DialogResult = DialogResult.OK;
            }
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!_drag && Picked.IsEmpty) return;
            var r = _drag ? Norm(_a, _b) : Picked;
            if (r.Width < 1 || r.Height < 1) return;
            using var fill = new SolidBrush(Color.FromArgb(60, 80, 180, 255));
            using var pen = new Pen(Color.FromArgb(255, 80, 220, 255), 2);
            e.Graphics.FillRectangle(fill, r);
            e.Graphics.DrawRectangle(pen, r);
        }

        private static Rectangle Norm(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Rectangle(x, y, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }
    }

    private sealed class PreviewForm : Form
    {
        public PreviewForm(string title, string hint, Bitmap crop)
        {
            Text = title;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(Math.Clamp(crop.Width + 48, 360, 720),
                                  Math.Clamp(crop.Height + 140, 240, 560));

            var lbl = new Label
            {
                Text = hint,
                AutoSize = false,
                Location = new Point(16, 12),
                Size = new Size(ClientSize.Width - 32, 36)
            };
            Controls.Add(lbl);

            var box = new PictureBox
            {
                Location = new Point(16, 52),
                Size = new Size(ClientSize.Width - 32, ClientSize.Height - 110),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
                Image = crop
            };
            Controls.Add(box);

            var ok = new Button
            {
                Text = "Dùng vùng này",
                DialogResult = DialogResult.OK
            };
            ok.SetBounds(ClientSize.Width - 220, ClientSize.Height - 44, 120, 28);
            Controls.Add(ok);

            var cancel = new Button
            {
                Text = "Hủy",
                DialogResult = DialogResult.Cancel
            };
            cancel.SetBounds(ClientSize.Width - 92, ClientSize.Height - 44, 76, 28);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
