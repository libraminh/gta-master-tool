namespace GtaMiniGameBot;

/// <summary>
/// Control tu ve cho nen toi. Phai tu ve vi Button/CheckBox/NumericUpDown/ComboBox
/// cua WinForms khong theo mau nen, va GroupBox thi bo qua ca ForeColor o vien.
///
/// Hover state dung MouseEnter/MouseLeave cua Control — khong can TrackMouseEvent,
/// WinForms da bat san cho Control roi.
/// </summary>
internal abstract class DarkBase : Control
{
    protected bool Hot;
    protected bool Down;

    protected DarkBase()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.Selectable, true);
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Body;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        Hot = true; Invalidate(); base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        Hot = false; Down = false; Invalidate(); base.OnMouseLeave(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { Hot = false; Down = false; }
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    /// <summary>Vien focus — ban phim phai thay duoc minh dang o dau.</summary>
    protected void DrawFocus(Graphics g)
    {
        if (!Focused) return;
        Theme.Frame(g, new Rectangle(1, 1, Width - 2, Height - 2), Theme.AccentDim);
    }
}

// ------------------------------------------------------------------ nut

internal sealed class DarkButton : DarkBase
{
    /// <summary>Nut chinh (nen accent) thay vi nut vien.</summary>
    public bool Primary { get; set; }

    public DarkButton()
    {
        Cursor = Cursors.Hand;
        Height = Theme.Px(30);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { Down = true; Focus(); Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        Down = false; Invalidate(); base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);
        var r = new Rectangle(0, 0, Width, Height);

        Color face, edge, ink;
        if (!Enabled)
        {
            face = Theme.Sunk; edge = Theme.Line; ink = Theme.Dimmer;
        }
        else if (Primary)
        {
            face = Down ? Theme.AccentDim : Hot ? Theme.Head : Theme.Accent;
            edge = face;
            ink = Theme.Ground;
        }
        else
        {
            face = Down ? Theme.Well : Hot ? Theme.AccentWash : Theme.Surface;
            edge = Hot ? Theme.AccentDim : Theme.Line2;
            ink = Hot ? Theme.Accent : Theme.Text;
        }

        Theme.Fill(g, r, face);
        Theme.Frame(g, r, edge);
        TextRenderer.DrawText(g, Text, Font, r, ink, Theme.Centre);
        DrawFocus(g);
    }
}

// ------------------------------------------------------------------ tick

internal sealed class DarkCheck : DarkBase
{
    private bool _checked;

    public event Action CheckedChanged;

    public DarkCheck()
    {
        Cursor = Cursors.Hand;
        Height = Theme.Px(22);
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke();
        }
    }

    /// <summary>
    /// Doi trang thai ma KHONG ban CheckedChanged. Can cho nhanh veto: khi cau hinh
    /// chua du, handler phai go tick nguoc lai ma khong tu goi lai chinh no.
    /// </summary>
    public void SetCheckedQuiet(bool value)
    {
        _checked = value;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { Focus(); Checked = !Checked; }
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);

        int s = Theme.Px(14);
        int top = (Height - s) / 2;
        var box = new Rectangle(0, top, s, s);

        Color edge = !Enabled ? Theme.Line : Hot ? Theme.Accent : _checked ? Theme.AccentDim : Theme.Line2;
        Theme.Fill(g, box, _checked && Enabled ? Theme.AccentWash : Theme.Well);
        Theme.Frame(g, box, edge);

        if (_checked)
        {
            using var p = new Pen(Enabled ? Theme.Accent : Theme.Dimmer, Math.Max(1.6f, Theme.Px(2)));
            float x = box.X, y = box.Y, w = box.Width, h = box.Height;
            g.DrawLines(p, new[]
            {
                new PointF(x + w * 0.22f, y + h * 0.52f),
                new PointF(x + w * 0.43f, y + h * 0.73f),
                new PointF(x + w * 0.79f, y + h * 0.27f)
            });
        }

        var lab = new Rectangle(s + Theme.Px(8), 0, Width - s - Theme.Px(8), Height);
        TextRenderer.DrawText(g, Text, Font, lab,
            !Enabled ? Theme.Dimmer : Hot ? Theme.Head : Theme.Text, Theme.Left);
        DrawFocus(g);
    }
}

// ------------------------------------------------------------------ so

/// <summary>
/// O nhap so. Chi lam viec voi int — moi cho dung trong app deu ep
/// (int)NumericUpDown.Value roi, nen decimal chi la buoc trung gian vo ich.
/// </summary>
internal sealed class DarkSpin : DarkBase
{
    private int _value;
    private int _min;
    private int _max = 100;
    private Rectangle _up, _down;

    public event Action ValueChanged;

    public DarkSpin()
    {
        Height = Theme.Px(24);
        Width = Theme.Px(70);
        Font = Theme.Data;
    }

    public int Step { get; set; } = 1;

    // Min/Max duoc dat lan luot luc dung UI, nen co luc ho van con cheo nhau
    // (dat Min = 500 khi Max con la 100 mac dinh). Phai tu go, khong duoc nem —
    // day la duong chay luc khoi tao cua so.
    public int Min
    {
        get => _min;
        set
        {
            _min = value;
            if (_max < _min) _max = _min;
            Reclamp();
        }
    }

    public int Max
    {
        get => _max;
        set
        {
            _max = value;
            if (_min > _max) _min = _max;
            Reclamp();
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Clamp(value);
            if (v == _value) { Invalidate(); return; }
            _value = v;
            Invalidate();
            ValueChanged?.Invoke();
        }
    }

    public void SetValueQuiet(int value)
    {
        _value = Clamp(value);
        Invalidate();
    }

    private int Clamp(int v) => _max < _min ? _min : Math.Clamp(v, _min, _max);

    /// <summary>
    /// Keo Value ve trong khoang moi, KHONG ban ValueChanged: doi khoang la sua rang
    /// buoc, khong phai nguoi dung sua so. Ban event o day se lam handler luu config
    /// ngay giua luc dang dung UI.
    /// </summary>
    private void Reclamp()
    {
        _value = Clamp(_value);
        Invalidate();
    }

    private void LayoutButtons()
    {
        int bw = Theme.Px(16);
        int half = Height / 2;
        _up = new Rectangle(Width - bw - 1, 1, bw, half - 1);
        _down = new Rectangle(Width - bw - 1, half, bw, Height - half - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        LayoutButtons();
        Focus();
        if (_up.Contains(e.Location)) Value += Step;
        else if (_down.Contains(e.Location)) Value -= Step;
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!Focused) return;
        Value += e.Delta > 0 ? Step : -Step;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up) { Value += Step; e.Handled = true; }
        else if (e.KeyCode == Keys.Down) { Value -= Step; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);
        LayoutButtons();

        var r = new Rectangle(0, 0, Width, Height);
        Theme.Fill(g, r, Enabled ? Theme.Well : Theme.Sunk);
        Theme.Frame(g, r, !Enabled ? Theme.Line : Hot || Focused ? Theme.AccentDim : Theme.Line2);

        TextRenderer.DrawText(g, _value.ToString(), Font,
            new Rectangle(Theme.Px(7), 0, Width - _up.Width - Theme.Px(10), Height),
            Enabled ? Theme.Head : Theme.Dimmer, Theme.Left);

        if (!Enabled) return;
        Arrow(g, _up, true);
        Arrow(g, _down, false);
    }

    private void Arrow(Graphics g, Rectangle box, bool up)
    {
        int w = Theme.Px(7), h = Theme.Px(4);
        int cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
        var pts = up
            ? new[] { new Point(cx - w / 2, cy + h / 2), new Point(cx + w / 2, cy + h / 2), new Point(cx, cy - h / 2) }
            : new[] { new Point(cx - w / 2, cy - h / 2), new Point(cx + w / 2, cy - h / 2), new Point(cx, cy + h / 2) };
        using var b = new SolidBrush(Hot ? Theme.Accent : Theme.Dim);
        g.FillPolygon(b, pts);
    }
}

// ------------------------------------------------------------------ chon

/// <summary>
/// O chon mot trong nhieu. Khong dung ComboBox: o che do FlatStyle.Flat no van
/// ve cai nut mui xuong bang mau he thong, nen tren nen toi luon co mot o sang
/// khong bo duoc. Day la mot hop tu ve + ContextMenuStrip da nhuom toi.
/// </summary>
internal sealed class DarkPick : DarkBase
{
    private readonly List<object> _items = new();
    private int _index = -1;

    public event Action SelectedIndexChanged;

    public DarkPick()
    {
        Cursor = Cursors.Hand;
        Height = Theme.Px(24);
        Font = Theme.Data;
    }

    public IList<object> Items => _items;

    public object SelectedItem => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    public int SelectedIndex
    {
        get => _index;
        set
        {
            int v = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
            if (v == _index) return;
            _index = v;
            Invalidate();
            SelectedIndexChanged?.Invoke();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left && Enabled && _items.Count > 0) Drop();
        base.OnMouseDown(e);
    }

    private void Drop()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Theme.Sunk,
            ForeColor = Theme.Text,
            Font = Theme.Data,
            ShowImageMargin = false
        };

        for (int i = 0; i < _items.Count; i++)
        {
            int at = i;
            var it = new ToolStripMenuItem(_items[i].ToString())
            {
                ForeColor = at == _index ? Theme.Accent : Theme.Text,
                BackColor = Theme.Sunk
            };
            it.Click += (_, _) => SelectedIndex = at;
            menu.Items.Add(it);
        }

        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(this, new Point(0, Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);

        var r = new Rectangle(0, 0, Width, Height);
        Theme.Fill(g, r, Enabled ? Theme.Well : Theme.Sunk);
        Theme.Frame(g, r, !Enabled ? Theme.Line : Hot || Focused ? Theme.AccentDim : Theme.Line2);

        int chev = Theme.Px(20);
        string label = SelectedItem?.ToString() ?? "";
        TextRenderer.DrawText(g, label, Font,
            new Rectangle(Theme.Px(8), 0, Width - chev - Theme.Px(10), Height),
            Enabled ? Theme.Text : Theme.Dimmer,
            Theme.Left | TextFormatFlags.EndEllipsis);

        int cx = Width - chev / 2 - Theme.Px(2), cy = Height / 2;
        int w = Theme.Px(8), h = Theme.Px(4);
        using var b = new SolidBrush(Hot && Enabled ? Theme.Accent : Theme.Dim);
        g.FillPolygon(b, new[]
        {
            new Point(cx - w / 2, cy - h / 2), new Point(cx + w / 2, cy - h / 2), new Point(cx, cy + h / 2)
        });
    }
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColours()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? Theme.Head : e.Item.ForeColor;
        base.OnRenderItemText(e);
    }

    private sealed class DarkColours : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Sunk;
        public override Color ImageMarginGradientBegin => Theme.Sunk;
        public override Color ImageMarginGradientMiddle => Theme.Sunk;
        public override Color ImageMarginGradientEnd => Theme.Sunk;
        public override Color MenuBorder => Theme.Line2;
        public override Color MenuItemBorder => Theme.AccentDim;
        public override Color MenuItemSelected => Theme.AccentWash;
        public override Color MenuItemSelectedGradientBegin => Theme.AccentWash;
        public override Color MenuItemSelectedGradientEnd => Theme.AccentWash;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Line;
    }
}
