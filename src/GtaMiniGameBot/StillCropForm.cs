namespace GtaMiniGameBot;

internal sealed class StillCropResult
{
    /// <summary>Toạ độ trong ảnh = toạ độ tương đối góc màn hình.</summary>
    public Rectangle Rect { get; init; }
    public int Cols { get; init; }
    public int Rows { get; init; }
}

/// <summary>
/// Khoanh một vùng trên ảnh tĩnh đã chụp. Kéo chuột để khoanh thô, rồi chỉnh từng pixel bằng
/// 4 ô số — ô "27.4/30 KG" chỉ cao hơn hai chục pixel, kéo tay không đủ chính xác.
/// Ở chế độ lưới còn có số cột/hàng và vẽ đè luôn từng ô lên ảnh, sai số cột là thấy ngay.
/// </summary>
internal sealed class StillCropForm : Form
{
    private readonly Bitmap _still;
    private readonly bool _gridMode;

    private readonly Canvas _canvas = new();
    private readonly PictureBox _zoom = new();
    private readonly NumericUpDown _x = new();
    private readonly NumericUpDown _y = new();
    private readonly NumericUpDown _w = new();
    private readonly NumericUpDown _h = new();
    private readonly NumericUpDown _cols = new();
    private readonly NumericUpDown _rows = new();
    private readonly Label _info = new();

    private Rectangle _sel;
    private double _scale = 1;
    private bool _dragging;
    private Point _dragStart;
    private bool _syncing;

    private StillCropForm(Bitmap still, string title, string hint, Rectangle initial,
                          bool gridMode, int cols, int rows)
    {
        _still = still;
        _gridMode = gridMode;
        _sel = initial;

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ClientSize = new Size(1200, 830);
        MinimumSize = new Size(900, 640);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi(hint, cols, rows);
        SyncFromSel();
    }

    /// <summary>Null = người dùng huỷ.</summary>
    public static StillCropResult Run(IWin32Window owner, Bitmap still, string title, string hint,
                                      Rectangle initial, bool gridMode = false, int cols = 5, int rows = 5)
    {
        using var f = new StillCropForm(still, title, hint, initial, gridMode, cols, rows);
        if (f.ShowDialog(owner) != DialogResult.OK) return null;

        var r = f.Result;
        return r.Rect.Width < 4 || r.Rect.Height < 4 ? null : r;
    }

    private StillCropResult Result => new()
    {
        Rect = _sel,
        Cols = (int)_cols.Value,
        Rows = (int)_rows.Value
    };

    private void BuildUi(string hint, int cols, int rows)
    {
        var lblHint = new Label
        {
            Text = hint,
            AutoSize = false,
            ForeColor = Color.DimGray
        };
        lblHint.SetBounds(12, 8, 1176, 34);
        lblHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(lblHint);

        _canvas.SetBounds(12, 46, 1176, 610);
        _canvas.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _canvas.BorderStyle = BorderStyle.FixedSingle;
        _canvas.BackColor = Color.FromArgb(30, 32, 36);
        _canvas.Paint += (_, e) => PaintCanvas(e.Graphics);
        _canvas.MouseDown += OnCanvasDown;
        _canvas.MouseMove += OnCanvasMove;
        _canvas.MouseUp += OnCanvasUp;
        _canvas.Resize += (_, _) => _canvas.Invalidate();
        Controls.Add(_canvas);

        int y = 666;
        AddNum(_x, "X", 12, y, _still.Width);
        AddNum(_y, "Y", 152, y, _still.Height);
        AddNum(_w, "Rộng", 292, y, _still.Width);
        AddNum(_h, "Cao", 432, y, _still.Height);

        if (_gridMode)
        {
            AddNum(_cols, "Cột", 592, y, 20);
            AddNum(_rows, "Hàng", 712, y, 20);
            _cols.Minimum = 1;
            _rows.Minimum = 1;
            _cols.Value = Math.Clamp(cols, 1, 20);
            _rows.Value = Math.Clamp(rows, 1, 20);
            _cols.ValueChanged += (_, _) => _canvas.Invalidate();
            _rows.ValueChanged += (_, _) => _canvas.Invalidate();
        }
        else
        {
            _cols.Value = 1;
            _rows.Value = 1;
        }

        _info.SetBounds(12, y + 54, 700, 40);
        _info.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _info.ForeColor = Color.DimGray;
        Controls.Add(_info);

        Controls.Add(new Label
        {
            Text = "Xem gần:",
            Location = new Point(860, y - 20),
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        });
        _zoom.SetBounds(860, y, 328, 96);
        _zoom.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _zoom.BorderStyle = BorderStyle.FixedSingle;
        _zoom.BackColor = Color.FromArgb(30, 32, 36);
        _zoom.SizeMode = PictureBoxSizeMode.Normal;
        _zoom.Paint += (_, e) => PaintZoom(e.Graphics);
        Controls.Add(_zoom);

        var btnOk = new Button { Text = "Dùng vùng này", DialogResult = DialogResult.OK };
        btnOk.SetBounds(940, y + 110, 130, 32);
        btnOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Controls.Add(btnOk);
        AcceptButton = btnOk;

        var btnCancel = new Button { Text = "Huỷ", DialogResult = DialogResult.Cancel };
        btnCancel.SetBounds(1080, y + 110, 108, 32);
        btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void AddNum(NumericUpDown n, string caption, int x, int y, int max)
    {
        var lbl = new Label { Text = caption, Location = new Point(x, y - 20), AutoSize = true };
        lbl.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(lbl);

        n.SetBounds(x, y, 110, 26);
        n.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        n.Minimum = 0;
        n.Maximum = max;
        n.Font = new Font("Consolas", 10F);
        n.ValueChanged += (_, _) => SyncFromBoxes();
        Controls.Add(n);
    }

    // ---------------------------------------------------------------- vẽ

    private Rectangle ImageRectOnCanvas()
    {
        int cw = Math.Max(1, _canvas.ClientSize.Width);
        int ch = Math.Max(1, _canvas.ClientSize.Height);
        _scale = Math.Min(1.0, Math.Min(cw / (double)_still.Width, ch / (double)_still.Height));
        return new Rectangle(0, 0,
            (int)Math.Round(_still.Width * _scale),
            (int)Math.Round(_still.Height * _scale));
    }

    private void PaintCanvas(Graphics g)
    {
        var dest = ImageRectOnCanvas();
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(_still, dest);

        if (_sel.Width < 1 || _sel.Height < 1) return;

        var r = ToCanvas(_sel);
        using var pen = new Pen(Color.FromArgb(255, 205, 70), 2);
        g.DrawRectangle(pen, r);

        if (!_gridMode) return;

        int cols = (int)_cols.Value, rows = (int)_rows.Value;
        using var cellPen = new Pen(Color.FromArgb(120, 90, 220, 255), 1);
        for (int i = 1; i < cols; i++)
        {
            int x = r.Left + i * r.Width / cols;
            g.DrawLine(cellPen, x, r.Top, x, r.Bottom);
        }
        for (int i = 1; i < rows; i++)
        {
            int yy = r.Top + i * r.Height / rows;
            g.DrawLine(cellPen, r.Left, yy, r.Right, yy);
        }
    }

    private void PaintZoom(Graphics g)
    {
        if (_sel.Width < 1 || _sel.Height < 1) return;

        var src = Rectangle.Intersect(_sel, new Rectangle(0, 0, _still.Width, _still.Height));
        if (src.Width < 1 || src.Height < 1) return;

        int bw = _zoom.ClientSize.Width, bh = _zoom.ClientSize.Height;
        double k = Math.Min(bw / (double)src.Width, bh / (double)src.Height);
        k = Math.Min(k, 8.0);
        int dw = Math.Max(1, (int)(src.Width * k));
        int dh = Math.Max(1, (int)(src.Height * k));

        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(_still, new Rectangle((bw - dw) / 2, (bh - dh) / 2, dw, dh),
                    src, GraphicsUnit.Pixel);
    }

    private Rectangle ToCanvas(Rectangle img) => new(
        (int)Math.Round(img.X * _scale),
        (int)Math.Round(img.Y * _scale),
        Math.Max(1, (int)Math.Round(img.Width * _scale)),
        Math.Max(1, (int)Math.Round(img.Height * _scale)));

    private Point ToImage(Point canvas) => new(
        Math.Clamp((int)Math.Round(canvas.X / _scale), 0, _still.Width),
        Math.Clamp((int)Math.Round(canvas.Y / _scale), 0, _still.Height));

    // ---------------------------------------------------------------- chuột

    private void OnCanvasDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ImageRectOnCanvas();
        _dragging = true;
        _dragStart = e.Location;
    }

    private void OnCanvasMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        SetSel(RectFromDrag(_dragStart, e.Location));
    }

    private void OnCanvasUp(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;

        // Keo qua ngan la click nham - giu nguyen vung dang co thay vi xoa sach cong chinh tay.
        if (Math.Abs(e.X - _dragStart.X) < 3 && Math.Abs(e.Y - _dragStart.Y) < 3)
        {
            _canvas.Invalidate();
            return;
        }
        SetSel(RectFromDrag(_dragStart, e.Location));
    }

    private Rectangle RectFromDrag(Point a, Point b)
    {
        var p1 = ToImage(a);
        var p2 = ToImage(b);
        return Rectangle.FromLTRB(
            Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y),
            Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y));
    }

    // ---------------------------------------------------------------- đồng bộ

    private void SetSel(Rectangle r)
    {
        _sel = Rectangle.Intersect(r, new Rectangle(0, 0, _still.Width, _still.Height));
        SyncFromSel();
    }

    private void SyncFromSel()
    {
        _syncing = true;
        try
        {
            _x.Value = Math.Clamp(_sel.X, 0, (int)_x.Maximum);
            _y.Value = Math.Clamp(_sel.Y, 0, (int)_y.Maximum);
            _w.Value = Math.Clamp(_sel.Width, 0, (int)_w.Maximum);
            _h.Value = Math.Clamp(_sel.Height, 0, (int)_h.Maximum);
        }
        finally { _syncing = false; }
        Refresh2();
    }

    private void SyncFromBoxes()
    {
        if (_syncing) return;
        _sel = Rectangle.Intersect(
            new Rectangle((int)_x.Value, (int)_y.Value, (int)_w.Value, (int)_h.Value),
            new Rectangle(0, 0, _still.Width, _still.Height));
        Refresh2();
    }

    private void Refresh2()
    {
        _info.Text = _sel.Width < 4 || _sel.Height < 4
            ? "vùng quá nhỏ — kéo lại (tối thiểu 8×8)"
            : $"vùng {_sel.Width}×{_sel.Height} @ {_sel.X},{_sel.Y}" +
              (_gridMode
                  ? $"   ô ≈ {_sel.Width / Math.Max(1, (int)_cols.Value)}×{_sel.Height / Math.Max(1, (int)_rows.Value)}"
                  : "");
        _canvas.Invalidate();
        _zoom.Invalidate();
    }

    private sealed class Canvas : Panel
    {
        public Canvas()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
    }
}
