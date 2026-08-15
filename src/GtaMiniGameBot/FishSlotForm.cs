namespace GtaMiniGameBot;

/// <summary>
/// Chọn ô nào luôn chứa cá, bằng cách click thẳng lên ảnh kho đồ đã chụp.
///
/// Thay cho bản đầu bắt gán nhãn từng icon rồi so mẫu: người dùng luôn để cá ở một ô cố định
/// nên khai báo ô đó nhanh hơn hẳn, và bot không cần biết icon là con cá gì.
///
/// Đánh đổi phải nói rõ ngay trên hộp thoại: bot kéo BẤT KỲ thứ gì nằm trong ô đã chọn.
/// </summary>
internal sealed class FishSlotForm : Form
{
    private sealed record Source(string Label, string Shot, string GridName, Func<FishingProfile, GridSpec> Grid);

    private static readonly Source[] Sources =
    {
        new("Phím nhanh", "bag", FishSlot.GridHotbar, p => p.Hotbar),
        new("Ba lô", "bag", FishSlot.GridBag, p => p.Bag)
    };

    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;
    private readonly string _key;

    private readonly ComboBox _source = new();
    private readonly Canvas _canvas = new();
    private readonly Label _summary = new();
    private readonly TextBox _log = new();

    private Bitmap _still;
    private GridScanner _scanner;
    private List<CellInfo> _cells = new();

    public FishSlotForm(FishingConfig cfg, Screen screen, FishingProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _key = profile.Key;

        Text = "Chọn ô chứa cá — " + _key;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(760, 700);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi();
        LoadSource();
    }

    private void BuildUi()
    {
        int y = 12;

        var warn = new Label
        {
            Text = "Bot sẽ kéo BẤT KỲ thứ gì nằm trong ô đã chọn, không kiểm tra đó có phải cá không. " +
                   "Chỉ chọn ô mà bạn chắc chắn luôn để cá.",
            ForeColor = Color.Firebrick,
            AutoSize = false
        };
        warn.SetBounds(12, y, 736, 34);
        Controls.Add(warn);
        y += 40;

        Controls.Add(new Label { Text = "Lưới:", Location = new Point(12, y + 4), AutoSize = true });
        _source.SetBounds(56, y, 200, 24);
        _source.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var s in Sources) _source.Items.Add(s.Label);
        _source.SelectedIndex = 0;
        _source.SelectedIndexChanged += (_, _) => LoadSource();
        Controls.Add(_source);

        Controls.Add(new Label
        {
            Text = "Click vào ô để bật/tắt.",
            Location = new Point(268, y + 4),
            AutoSize = true,
            ForeColor = Color.DimGray
        });
        y += 34;

        _canvas.SetBounds(12, y, 736, 420);
        _canvas.BorderStyle = BorderStyle.FixedSingle;
        _canvas.BackColor = Color.FromArgb(30, 32, 36);
        _canvas.Paint += (_, e) => PaintCanvas(e.Graphics);
        _canvas.MouseDown += OnCanvasClick;
        Controls.Add(_canvas);
        y += 430;

        _summary.SetBounds(12, y, 736, 22);
        _summary.Font = new Font("Consolas", 10F);
        Controls.Add(_summary);
        y += 28;

        var clear = new Button { Text = "Xoá hết ô đã chọn" };
        clear.SetBounds(12, y, 170, 30);
        clear.Click += (_, _) => ClearAll();
        Controls.Add(clear);

        var close = new Button { Text = "Xong", DialogResult = DialogResult.OK };
        close.SetBounds(658, y, 90, 30);
        Controls.Add(close);
        AcceptButton = close;
        y += 38;

        _log.SetBounds(12, y, 736, 700 - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        Controls.Add(_log);
    }

    // ---------------------------------------------------------------- nạp

    private Source Current => Sources[_source.SelectedIndex];

    private void LoadSource()
    {
        var src = Current;
        var grid = src.Grid(_profile);

        _scanner?.Dispose();
        _scanner = null;
        _still?.Dispose();
        _still = null;
        _cells = new List<CellInfo>();

        if (!grid.IsSet) { Append($"lưới {src.Label} chưa khoanh"); Refresh2(); return; }

        _still = StillPicker.Load(FishingConfig.ShotPath(_key, src.Shot));
        if (_still is null) { Append($"chưa có ảnh “{src.Shot}”"); Refresh2(); return; }

        _scanner = new GridScanner(_cfg, _screen, grid);
        _cells = _scanner.ScanStill(_still);
        Append($"{src.Label}: {grid.Cols}×{grid.Rows} ô, " +
               $"{_cells.Count(c => c.IsEmpty)} trống / {_cells.Count} ô");
        Refresh2();
    }

    // ---------------------------------------------------------------- vẽ

    private Rectangle GridInImage()
    {
        var a = Current.Grid(_profile).Area;
        return new Rectangle(a.X, a.Y, a.W, a.H);
    }

    private double CanvasScale(out Rectangle dest)
    {
        var g = GridInImage();
        int cw = _canvas.ClientSize.Width - 8, ch = _canvas.ClientSize.Height - 8;
        double k = Math.Min(cw / (double)Math.Max(1, g.Width), ch / (double)Math.Max(1, g.Height));
        dest = new Rectangle(
            4 + (cw - (int)(g.Width * k)) / 2, 4,
            (int)(g.Width * k), (int)(g.Height * k));
        return k;
    }

    private Rectangle CellOnCanvas(CellInfo c, Rectangle area, Rectangle dest, double k)
    {
        var origin = _screen.Bounds.Location;
        return new Rectangle(
            dest.X + (int)((c.Rect.X - origin.X - area.X) * k),
            dest.Y + (int)((c.Rect.Y - origin.Y - area.Y) * k),
            Math.Max(2, (int)(c.Rect.Width * k)),
            Math.Max(2, (int)(c.Rect.Height * k)));
    }

    private void PaintCanvas(Graphics g)
    {
        if (_still is null || _cells.Count == 0) return;

        var area = GridInImage();
        double k = CanvasScale(out var dest);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(_still, dest, area, GraphicsUnit.Pixel);

        using var font = new Font("Segoe UI", 9F, FontStyle.Bold);
        foreach (var c in _cells)
        {
            var r = CellOnCanvas(c, area, dest, k);
            bool chosen = IsChosen(c.Index);

            var col = chosen
                ? Color.FromArgb(60, 220, 100)
                : c.IsEmpty ? Color.FromArgb(90, 100, 110) : Color.FromArgb(220, 220, 220);
            using var pen = new Pen(col, chosen ? 4 : 1);
            g.DrawRectangle(pen, r);

            if (!chosen) continue;
            using var br = new SolidBrush(col);
            g.DrawString("CÁ", font, br, r.X + 4, r.Y + 4);
        }
    }

    private void OnCanvasClick(object sender, MouseEventArgs e)
    {
        if (_cells.Count == 0) return;

        var area = GridInImage();
        double k = CanvasScale(out var dest);

        foreach (var c in _cells)
        {
            if (!CellOnCanvas(c, area, dest, k).Contains(e.Location)) continue;
            Toggle(c);
            return;
        }
    }

    // ---------------------------------------------------------------- chọn

    private bool IsChosen(int index) =>
        _profile.FishSlots.Any(s => s.Grid == Current.GridName && s.Index == index);

    private void Toggle(CellInfo c)
    {
        var existing = _profile.FishSlots.FirstOrDefault(s => s.Grid == Current.GridName && s.Index == c.Index);
        if (existing is not null)
        {
            _profile.FishSlots.Remove(existing);
            Append($"bỏ ô {Current.Label} #{c.Index}");
        }
        else
        {
            _profile.FishSlots.Add(new FishSlot { Grid = Current.GridName, Index = c.Index });
            Append($"chọn ô {Current.Label} #{c.Index}" +
                   (c.IsEmpty ? "  (ô đang trống — nhớ ô này phải là chỗ cá rơi vào)" : ""));
        }
        Save();
        Refresh2();
    }

    private void ClearAll()
    {
        if (_profile.FishSlots.Count == 0) return;
        _profile.FishSlots.Clear();
        Append("đã xoá hết ô chứa cá");
        Save();
        Refresh2();
    }

    private void Save()
    {
        try { _cfg.Save(); }
        catch (Exception ex) { Append("lưu cấu hình lỗi: " + ex.Message); }
    }

    private void Refresh2()
    {
        _summary.Text = _profile.FishSlots.Count == 0
            ? "chưa chọn ô nào"
            : "ô cá: " + string.Join(", ", _profile.FishSlots.Select(s => s.Label));
        _summary.ForeColor = _profile.FishSlots.Count == 0 ? Color.Firebrick : Color.DarkGreen;
        _canvas.Invalidate();
    }

    private void Append(string line) =>
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _scanner?.Dispose();
        _still?.Dispose();
        _still = null;
        base.OnFormClosed(e);
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
