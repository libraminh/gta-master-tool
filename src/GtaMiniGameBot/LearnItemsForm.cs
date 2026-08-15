using System.Drawing.Imaging;

namespace GtaMiniGameBot;

/// <summary>
/// Dạy bot biết từng icon trong kho đồ, chia hai nhóm: GIỮ LẠI và CÁ.
///
/// Người dùng cho biết trong ba lô thỉnh thoảng có thêm đồ khác, nên bot không thể suy "cái gì
/// không phải đồ giữ lại thì là cá". Ô có đồ mà không khớp mẫu nào sẽ được xếp là "ô lạ" và
/// KHÔNG bị kéo đi đâu cả — chỉ ghi log để người dùng vào đây gán nhãn.
/// </summary>
internal sealed class LearnItemsForm : Form
{
    private sealed record Source(string Label, string Shot, Func<FishingProfile, GridSpec> Grid);

    private static readonly Source[] Sources =
    {
        new("Phím nhanh (ảnh kho đồ)", "bag", p => p.Hotbar),
        new("Ba lô (ảnh kho đồ)", "bag", p => p.Bag),
        new("Cốp xe (ảnh cốp)", "trunk", p => p.Trunk)
    };

    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;
    private readonly string _key;

    private readonly ComboBox _source = new();
    private readonly Canvas _canvas = new();
    private readonly PictureBox _zoom = new();
    private readonly TextBox _name = new();
    private readonly Label _cellInfo = new();
    private readonly Label _summary = new();
    private readonly ListBox _known = new();
    private readonly TextBox _log = new();

    private Bitmap _still;
    private GridScanner _scanner;
    private List<CellInfo> _cells = new();
    private int _selected = -1;
    private Size _cellSize;

    public LearnItemsForm(FishingConfig cfg, Screen screen, FishingProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _key = profile.Key;

        Text = "Học vật phẩm — " + _key;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(960, 700);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi();
        LoadSource();
    }

    private void BuildUi()
    {
        int y = 12;

        Controls.Add(new Label { Text = "Lưới:", Location = new Point(12, y + 4), AutoSize = true });
        _source.SetBounds(56, y, 280, 24);
        _source.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var s in Sources) _source.Items.Add(s.Label);
        _source.SelectedIndex = 0;
        _source.SelectedIndexChanged += (_, _) => LoadSource();
        Controls.Add(_source);

        _summary.SetBounds(348, y + 4, 600, 20);
        _summary.Font = new Font("Consolas", 9F);
        Controls.Add(_summary);
        y += 34;

        _canvas.SetBounds(12, y, 600, 460);
        _canvas.BorderStyle = BorderStyle.FixedSingle;
        _canvas.BackColor = Color.FromArgb(30, 32, 36);
        _canvas.Paint += (_, e) => PaintCanvas(e.Graphics);
        _canvas.MouseDown += OnCanvasClick;
        Controls.Add(_canvas);

        int rx = 626;
        Controls.Add(new Label { Text = "Ô đang chọn:", Location = new Point(rx, y), AutoSize = true });
        _zoom.SetBounds(rx, y + 20, 200, 200);
        _zoom.BorderStyle = BorderStyle.FixedSingle;
        _zoom.BackColor = Color.FromArgb(30, 32, 36);
        _zoom.Paint += (_, e) => PaintZoom(e.Graphics);
        Controls.Add(_zoom);

        _cellInfo.SetBounds(rx, y + 226, 320, 40);
        _cellInfo.Font = new Font("Consolas", 8.5F);
        _cellInfo.Text = "chưa chọn ô nào";
        Controls.Add(_cellInfo);

        Controls.Add(new Label { Text = "Tên vật phẩm:", Location = new Point(rx, y + 272), AutoSize = true });
        _name.SetBounds(rx, y + 292, 320, 26);
        Controls.Add(_name);

        var bKeep = new Button { Text = "Lưu: GIỮ LẠI" };
        bKeep.SetBounds(rx, y + 326, 155, 32);
        bKeep.Click += (_, _) => Save(fish: false);
        Controls.Add(bKeep);

        var bFish = new Button { Text = "Lưu: CÁ" };
        bFish.SetBounds(rx + 165, y + 326, 155, 32);
        bFish.Click += (_, _) => Save(fish: true);
        Controls.Add(bFish);

        Controls.Add(new Label { Text = "Mẫu đã có:", Location = new Point(rx, y + 366), AutoSize = true });
        _known.SetBounds(rx, y + 386, 320, 74);
        _known.Font = new Font("Consolas", 8.5F);
        Controls.Add(_known);

        var bDel = new Button { Text = "Xoá mẫu đang chọn" };
        bDel.SetBounds(rx, y + 466, 320, 26);
        bDel.Click += (_, _) => DeleteSelected();
        Controls.Add(bDel);
        y += 500;

        _log.SetBounds(12, y, 936, 700 - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        Controls.Add(_log);
    }

    // ---------------------------------------------------------------- nạp

    private void LoadSource()
    {
        var src = Sources[_source.SelectedIndex];
        var grid = src.Grid(_profile);

        _scanner?.Dispose();
        _scanner = null;
        _still?.Dispose();
        _still = null;
        _cells = new List<CellInfo>();
        _selected = -1;

        if (!grid.IsSet) { Append("lưới này chưa khoanh"); Refresh2(); return; }

        _still = StillPicker.Load(FishingConfig.ShotPath(_key, src.Shot));
        if (_still is null) { Append($"chưa có ảnh “{src.Shot}”"); Refresh2(); return; }

        var probe = new GridScanner(_cfg, _screen, grid, new ItemAtlas());
        _cellSize = probe.CellSize;
        probe.Dispose();

        var notes = new List<string>();
        var atlas = ItemAtlas.Load(_key, _cellSize, _cfg.BadgeFrac, notes);
        foreach (string n in notes) Append(n);

        _scanner = new GridScanner(_cfg, _screen, grid, atlas);
        _cells = _scanner.ScanStill(_still);
        Append($"{src.Label}: {grid.Cols}×{grid.Rows} ô, mỗi ô {_cellSize.Width}×{_cellSize.Height}" +
               $" — mẫu giữ lại {atlas.KeepCount}, mẫu cá {atlas.FishCount}");
        Refresh2();
    }

    // ---------------------------------------------------------------- vẽ

    private Rectangle GridInImage()
    {
        var src = Sources[_source.SelectedIndex];
        var a = src.Grid(_profile).Area;
        return new Rectangle(a.X, a.Y, a.W, a.H);
    }

    private double CanvasScale(out Rectangle dest)
    {
        var g = GridInImage();
        int cw = _canvas.ClientSize.Width - 8, ch = _canvas.ClientSize.Height - 8;
        double k = Math.Min(cw / (double)Math.Max(1, g.Width), ch / (double)Math.Max(1, g.Height));
        dest = new Rectangle(4, 4, (int)(g.Width * k), (int)(g.Height * k));
        return k;
    }

    private void PaintCanvas(Graphics g)
    {
        if (_still is null || _cells.Count == 0) return;

        var area = GridInImage();
        double k = CanvasScale(out var dest);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(_still, dest, area, GraphicsUnit.Pixel);

        var origin = _screen.Bounds.Location;
        using var font = new Font("Segoe UI", 7.5F, FontStyle.Bold);
        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i];
            var r = new Rectangle(
                dest.X + (int)((c.Rect.X - origin.X - area.X) * k),
                dest.Y + (int)((c.Rect.Y - origin.Y - area.Y) * k),
                Math.Max(2, (int)(c.Rect.Width * k)),
                Math.Max(2, (int)(c.Rect.Height * k)));

            var col = c.State switch
            {
                CellState.Empty => Color.FromArgb(90, 100, 110),
                CellState.Keep => Color.FromArgb(90, 200, 255),
                CellState.Fish => Color.FromArgb(80, 230, 120),
                _ => Color.FromArgb(255, 150, 60)
            };
            using var pen = new Pen(col, i == _selected ? 3 : 1);
            g.DrawRectangle(pen, r);

            if (c.State != CellState.Empty)
            {
                using var br = new SolidBrush(col);
                g.DrawString(c.State == CellState.Unknown ? "?" : c.Name, font, br, r.X + 2, r.Y + 2);
            }
        }
    }

    private void OnCanvasClick(object sender, MouseEventArgs e)
    {
        if (_cells.Count == 0) return;

        var area = GridInImage();
        double k = CanvasScale(out var dest);
        var origin = _screen.Bounds.Location;

        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i];
            var r = new Rectangle(
                dest.X + (int)((c.Rect.X - origin.X - area.X) * k),
                dest.Y + (int)((c.Rect.Y - origin.Y - area.Y) * k),
                Math.Max(2, (int)(c.Rect.Width * k)),
                Math.Max(2, (int)(c.Rect.Height * k)));
            if (!r.Contains(e.Location)) continue;

            _selected = i;
            if (c.Name is not null) _name.Text = c.Name;
            Refresh2();
            return;
        }
    }

    private void PaintZoom(Graphics g)
    {
        if (_still is null || _selected < 0 || _selected >= _cells.Count) return;

        var origin = _screen.Bounds.Location;
        var c = _cells[_selected].Rect;
        var src = new Rectangle(c.X - origin.X, c.Y - origin.Y, c.Width, c.Height);

        int bw = _zoom.ClientSize.Width, bh = _zoom.ClientSize.Height;
        double k = Math.Min(bw / (double)src.Width, bh / (double)src.Height);
        int dw = (int)(src.Width * k), dh = (int)(src.Height * k);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.DrawImage(_still, new Rectangle((bw - dw) / 2, (bh - dh) / 2, dw, dh), src, GraphicsUnit.Pixel);
    }

    // ---------------------------------------------------------------- lưu / xoá

    private void Save(bool fish)
    {
        if (_selected < 0) { Append("chọn một ô trên ảnh trước"); return; }
        string name = _name.Text.Trim();
        if (name.Length == 0) { Append("đặt tên cho vật phẩm đã"); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Append("tên có ký tự không đặt file được"); return; }

        var c = _cells[_selected];
        if (c.State == CellState.Empty)
        {
            Append($"ô #{c.Index} đang là ô trống (màu={c.Chroma:F3} lệch={c.Std:F1}) — " +
                   "nếu thật ra có đồ thì chỉnh lại ngưỡng ô trống");
            return;
        }

        try
        {
            var origin = _screen.Bounds.Location;
            var src = new Rectangle(c.Rect.X - origin.X, c.Rect.Y - origin.Y, c.Rect.Width, c.Rect.Height);
            using var crop = _still.Clone(src, PixelFormat.Format32bppArgb);
            string path = Path.Combine(FishingConfig.ItemDir(_key, fish, _cellSize), name + ".png");
            StillPicker.Save(crop, path);
            Append($"ô #{c.Index} → {(fish ? "CÁ" : "GIỮ LẠI")} “{name}” ({crop.Width}×{crop.Height})");
        }
        catch (Exception ex) { Append("lưu lỗi: " + ex.Message); return; }

        LoadSource();
    }

    private void DeleteSelected()
    {
        if (_known.SelectedItem is not string line) { Append("chọn một mẫu trong danh sách"); return; }
        bool fish = line.StartsWith("cá:", StringComparison.Ordinal);
        string name = line[(line.IndexOf(':') + 1)..].Trim();
        string path = Path.Combine(FishingConfig.ItemDir(_key, fish, _cellSize), name + ".png");

        try
        {
            if (File.Exists(path)) File.Delete(path);
            Append("đã xoá mẫu " + name);
        }
        catch (Exception ex) { Append("xoá lỗi: " + ex.Message); }
        LoadSource();
    }

    // ---------------------------------------------------------------- trạng thái

    private void Refresh2()
    {
        int empty = _cells.Count(c => c.State == CellState.Empty);
        int keep = _cells.Count(c => c.State == CellState.Keep);
        int fish = _cells.Count(c => c.State == CellState.Fish);
        int unknown = _cells.Count(c => c.State == CellState.Unknown);
        _summary.Text = $"trống {empty}   giữ lại {keep}   cá {fish}   lạ {unknown}";
        _summary.ForeColor = unknown > 0 ? Color.DarkOrange : Color.DarkGreen;

        _cellInfo.Text = _selected >= 0 && _selected < _cells.Count
            ? _cells[_selected].ToString()
            : "chưa chọn ô nào";

        _known.Items.Clear();
        if (_cellSize.Width > 0)
        {
            var notes = new List<string>();
            var atlas = ItemAtlas.Load(_key, _cellSize, _cfg.BadgeFrac, notes);
            foreach (string n in atlas.Names(false)) _known.Items.Add("giữ: " + n);
            foreach (string n in atlas.Names(true)) _known.Items.Add("cá: " + n);
        }

        _canvas.Invalidate();
        _zoom.Invalidate();
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
