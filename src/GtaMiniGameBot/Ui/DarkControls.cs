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
/// Go so truc tiep; Enter / mat focus chot, Escape huy. Mui ten va banh xe van dung.
/// </summary>
internal sealed class DarkSpin : DarkBase
{
    private int _value;
    private int _min;
    private int _max = 100;
    private Rectangle _up, _down;

    /// <summary>Chu dang go. Null = khong sua, ve Value.</summary>
    private string _edit;

    /// <summary>So dang duoc chon het — chu so dau thay ca chuoi.</summary>
    private bool _replace;

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
        DropEdit();
        _value = Clamp(value);
        Invalidate();
    }

    private int Clamp(int v) => _max < _min ? _min : Math.Clamp(v, _min, _max);

    private int MaxDigits => Math.Max(1, _max.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);

    /// <summary>
    /// Keo Value ve trong khoang moi, KHONG ban ValueChanged: doi khoang la sua rang
    /// buoc, khong phai nguoi dung sua so. Ban event o day se lam handler luu config
    /// ngay giua luc dang dung UI.
    /// </summary>
    private void Reclamp()
    {
        DropEdit();
        _value = Clamp(_value);
        Invalidate();
    }

    private void DropEdit()
    {
        _edit = null;
        _replace = false;
    }

    private void BeginReplace()
    {
        if (!Enabled) return;
        _edit ??= _value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _replace = true;
        Invalidate();
    }

    private void CommitEdit()
    {
        if (_edit == null)
        {
            _replace = false;
            return;
        }

        string s = _edit;
        DropEdit();
        if (s.Length > 0 && int.TryParse(s, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int v))
            Value = v;
        else
            Invalidate();
    }

    private void CancelEdit()
    {
        if (_edit == null) return;
        DropEdit();
        Invalidate();
    }

    private void StepBy(int delta)
    {
        CommitEdit();
        Value += delta;
    }

    private void TypeDigit(char d)
    {
        if (!Enabled) return;
        if (_edit == null || _replace)
        {
            _edit = d.ToString();
            _replace = false;
        }
        else if (_edit.Length < MaxDigits)
        {
            _edit += d;
        }
        Invalidate();
    }

    private void Backspace()
    {
        if (!Enabled) return;
        if (_edit == null || _replace)
        {
            _edit = "";
            _replace = false;
        }
        else if (_edit.Length > 0)
        {
            _edit = _edit[..^1];
        }
        Invalidate();
    }

    private void LayoutButtons()
    {
        int bw = Theme.Px(16);
        int half = Height / 2;
        _up = new Rectangle(Width - bw - 1, 1, bw, half - 1);
        _down = new Rectangle(Width - bw - 1, half, bw, Height - half - 1);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        BeginReplace();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        CommitEdit();
        base.OnLostFocus(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        LayoutButtons();
        Focus();
        if (_up.Contains(e.Location))
        {
            StepBy(Step);
            BeginReplace();
        }
        else if (_down.Contains(e.Location))
        {
            StepBy(-Step);
            BeginReplace();
        }
        else
        {
            BeginReplace();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!Focused) return;
        StepBy(e.Delta > 0 ? Step : -Step);
        BeginReplace();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (e.KeyChar is >= '0' and <= '9')
        {
            TypeDigit(e.KeyChar);
            e.Handled = true;
        }
        base.OnKeyPress(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up)
        {
            StepBy(Step);
            BeginReplace();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Down)
        {
            StepBy(-Step);
            BeginReplace();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Back)
        {
            Backspace();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            CommitEdit();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            CancelEdit();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        return k is Keys.Up or Keys.Down or Keys.Enter or Keys.Escape or Keys.Back
            || base.IsInputKey(keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);
        LayoutButtons();

        var r = new Rectangle(0, 0, Width, Height);
        Theme.Fill(g, r, Enabled ? Theme.Well : Theme.Sunk);
        Theme.Frame(g, r, !Enabled ? Theme.Line : Hot || Focused ? Theme.AccentDim : Theme.Line2);

        string shown = _edit ?? _value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var textBox = new Rectangle(Theme.Px(7), 0, Width - _up.Width - Theme.Px(10), Height);
        Color ink = Enabled ? Theme.Head : Theme.Dimmer;

        if (Enabled && Focused && _replace && shown.Length > 0)
        {
            var sz = TextRenderer.MeasureText(g, shown, Font, textBox.Size, Theme.Left);
            int top = textBox.Y + Math.Max(0, (textBox.Height - sz.Height) / 2);
            Theme.Fill(g, new Rectangle(textBox.X, top, sz.Width, sz.Height), Theme.AccentDim);
        }

        TextRenderer.DrawText(g, shown, Font, textBox, ink, Theme.Left);

        if (Enabled && Focused && _edit != null && !_replace)
        {
            int cx = textBox.X;
            if (shown.Length > 0)
                cx += TextRenderer.MeasureText(g, shown, Font, textBox.Size, Theme.Left).Width;
            int y1 = textBox.Y + Theme.Px(4);
            int y2 = textBox.Bottom - Theme.Px(4);
            using var p = new Pen(Theme.Accent, Math.Max(1, Theme.Px(1)));
            g.DrawLine(p, cx, y1, cx, y2);
        }

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

    /// <summary>
    /// Menu của control này, dựng một lần và sống hết đời control. Chỉ <c>Items</c> được dựng lại
    /// mỗi lần mở.
    ///
    /// Vì sao không tạo-rồi-huỷ mỗi lần mở: xem <see cref="Drop"/> — đã sai hai lần ở đúng chỗ đó.
    /// </summary>
    private ContextMenuStrip _menu;

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

    /// <summary>
    /// Mở danh sách chọn.
    ///
    /// MỘT menu duy nhất cho cả đời control, chỉ dựng lại phần <c>Items</c> mỗi lần mở. Nó KHÔNG
    /// bao giờ bị huỷ ở đây — chỉ ở <see cref="Dispose(bool)"/>.
    ///
    /// Hai bản trước đều sai và cùng một kiểu sai: huỷ menu ở chỗ WinForms còn đang dùng nó.
    ///
    /// Bản 1 huỷ ngay trong event <c>Closed</c> của chính menu. <c>Closed</c> nổ BÊN TRONG
    /// <c>SetVisibleCore(false)</c>, nên hàm đó chạy tiếp rồi gọi lại <c>get_Handle()</c> trên
    /// object vừa bị huỷ → ném ngay lần chọn đầu tiên.
    ///
    /// Bản 2 dời sang đầu lần mở SAU, tưởng là an toàn vì "menu cũ đã đóng rồi". Không an toàn:
    /// <c>ToolStripManager.ModalMenuFilter</c> nhả tham chiếu tới drop-down qua message ĐÃ POST,
    /// không phải tức thời. Bấm nhanh mở → chọn → mở là huỷ một menu mà filter còn đang giữ, và
    /// nó nổ ở lần dismiss kế tiếp với đúng stack cũ:
    ///
    ///   ToolStripItem.HandleMouseUp → HandleClick
    ///   → ToolStrip.HandleItemClick(dismissingItem)
    ///   → ContextMenuStrip.SetVisibleCore  ← trên instance ĐÃ bị huỷ
    ///   → Control.get_Handle() → CreateHandle() → ObjectDisposedException
    ///
    /// Đó cũng là lý do phép thử của bản 2 không bắt được: nó bơm message giữa mỗi bước, tức tặng
    /// cho filter đúng khoảng nghỉ mà người dùng thật không có.
    ///
    /// Vì sao <c>Items.Clear()</c> mà không dispose từng item: <see cref="ToolStripMenuItem"/> là
    /// Component chứ không phải Control — nó không giữ HWND nào, nên bỏ cho GC dọn là đủ. Và nếu
    /// dispose thì lại rơi vào đúng cái bẫy trên, vì filter có thể còn trỏ vào item vừa được bấm.
    /// </summary>
    private void Drop()
    {
        _menu ??= new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Theme.Sunk,
            ForeColor = Theme.Text,
            Font = Theme.Data,
            ShowImageMargin = false
        };

        // Dang mo ma bam lai vao hop: dong lai, dung hanh vi cua mot combo box that. Truoc day
        // no mo them mot menu nua va bo roi cai dang mo.
        if (_menu.Visible)
        {
            _menu.Close(ToolStripDropDownCloseReason.CloseCalled);
            return;
        }

        _menu.Items.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            int at = i;
            var it = new ToolStripMenuItem(_items[i].ToString())
            {
                ForeColor = at == _index ? Theme.Accent : Theme.Text,
                BackColor = Theme.Sunk
            };
            it.Click += (_, _) => SelectedIndex = at;
            _menu.Items.Add(it);
        }

        _menu.Show(this, new Point(0, Height));
    }

    /// <summary>
    /// Menu là của control này nên nó đi theo control — không để lại cửa sổ mồ côi.
    ///
    /// Đây là chỗ DUY NHẤT được phép huỷ nó: control đang bị huỷ thì không còn cú click nào đang
    /// chạy dở trên nó nữa.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menu?.Dispose();
            _menu = null;
        }
        base.Dispose(disposing);
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
