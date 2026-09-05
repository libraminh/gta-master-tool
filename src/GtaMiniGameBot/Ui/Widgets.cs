using System.Drawing.Drawing2D;

namespace GtaMiniGameBot;

// ------------------------------------------------------------------ so do vong cau

/// <summary>
/// Vong cau ve thanh so do trang thai: nam tram + hai nhanh quay lai.
///
/// Ly do no dang o day chu khong phai mot dong chu "Đang câu": vong lap that co
/// ba duong quay lai (che moi / tha truot / het thoi gian cho can) va chung la
/// thu quyet dinh hieu suat. Thay nhanh nao dang sang la biet dang mat thoi gian
/// o dau.
/// </summary>
internal sealed class PhaseTrack : DrawPanel
{
    private static readonly string[] Names = { "THẢ CÂU", "CHỜ CẮN", "GIỮ S", "CẤT / THẢ", "ĐỔ CỐP" };

    private readonly string[] _subs = { "4 + Space", "", "", "", "" };
    private int _station = -1;
    private string _note = "";
    private bool _loopWarm;

    public PhaseTrack()
    {
        BackColor = Theme.Sunk;
        Height = Theme.Px(150);
    }

    public void Update(FishingState st, FishingConfig cfg)
    {
        _station = st.Station;
        _note = st.Running ? $"{st.PhaseMs / 1000.0:0.0} s" : "";

        _subs[1] = $"tối đa {cfg.WaitBiteMs / 1000} s";
        _subs[2] = st.Phase == FishingPhase.Fighting
            ? $"{st.PhaseMs / 1000.0:0.0} s"
            : $"tối đa {cfg.FightTimeoutMs / 1000} s";
        _subs[3] = st.Phase == FishingPhase.ClickingRelease ? "đang thả"
            : st.Phase == FishingPhase.ClickingSell ? "đang bán"
            : st.Phase == FishingPhase.ClickingKeep ? "đang click"
            : "chờ nút hiện";
        _subs[4] = st.DumpOn
            ? st.TrunkFull ? "cốp đầy" : $"{st.CatchesSinceDump} con chưa đổ"
            : "tắt";

        // Nhanh quay lai sang len khi vua di qua no.
        _loopWarm = st.Phase == FishingPhase.Casting &&
                    st.Casts > st.Bites + st.Catches + st.Released + st.Sold;

        Invalidate();
    }

    public void Clear()
    {
        _station = -1;
        _note = "";
        _loopWarm = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);

        int pad = Theme.Px(10);
        int gap = Theme.Px(24);
        int boxH = Theme.Px(32);
        int boxY = Theme.Px(20);
        int usable = Width - pad * 2 - gap * 4;
        if (usable < 5) return;
        int boxW = usable / 5;

        using var dim = new Pen(Theme.Line2, 1.4f);
        using var hot = new Pen(Theme.Accent, 1.6f);

        var boxes = new Rectangle[5];
        for (int i = 0; i < 5; i++)
        {
            int x = pad + i * (boxW + gap);
            bool on = i == _station;
            boxes[i] = on
                ? new Rectangle(x, boxY - Theme.Px(4), boxW, boxH + Theme.Px(8))
                : new Rectangle(x, boxY, boxW, boxH);
        }

        // Mui noi giua cac tram — mui dan vao tram dang sang thi to mau accent.
        for (int i = 0; i < 4; i++)
        {
            int y = boxY + boxH / 2;
            int x1 = boxes[i].Right + Theme.Px(4);
            int x2 = boxes[i + 1].Left - Theme.Px(4);
            bool lit = i + 1 == _station;
            g.DrawLine(lit ? hot : dim, x1, y, x2 - Theme.Px(5), y);
            Tri(g, x2, y, Theme.Px(6), lit ? Theme.Accent : Theme.Dimmer);
        }

        for (int i = 0; i < 5; i++)
        {
            bool on = i == _station;
            Theme.Fill(g, boxes[i], on ? Theme.AccentWash : Theme.Surface);
            Theme.Frame(g, boxes[i], on ? Theme.Accent : Theme.Line2);

            TextRenderer.DrawText(g, Names[i], on ? Theme.PhaseBig : Theme.Section, boxes[i],
                on ? Theme.Accent : Theme.Dim, Theme.Centre);

            if (on)
            {
                using var b = new SolidBrush(Theme.Accent);
                g.FillEllipse(b, boxes[i].X + Theme.Px(5), boxes[i].Y + Theme.Px(5), Theme.Px(6), Theme.Px(6));
            }

            var sub = new Rectangle(boxes[i].X, boxY + boxH + Theme.Px(8), boxW, Theme.Px(16));
            TextRenderer.DrawText(g, on && _note.Length > 0 ? _note : _subs[i], Theme.DataSm, sub,
                on ? Theme.Text : Theme.Dimmer, Theme.Centre);
        }

        // Nhanh quay lai: cho can -> tha cau.
        int arcTop = boxY + boxH + Theme.Px(28);
        int arcBottom = arcTop + Theme.Px(16);
        Arc(g, boxes[1], boxes[0], arcTop, arcBottom,
            _loopWarm ? Theme.Warn : Theme.Line2, true);
        TextRenderer.DrawText(g, "chê mồi · thả trượt · hết giờ chờ", Theme.DataSm,
            new Rectangle(boxes[0].X, arcBottom + Theme.Px(2), boxes[1].Right - boxes[0].X, Theme.Px(16)),
            _loopWarm ? Theme.Warn : Theme.Dimmer, Theme.Centre);

        // Nhanh quay lai: do cop -> tha cau (xong mot con).
        Arc(g, boxes[4], boxes[2], arcTop, arcBottom, Theme.Line2, false);
        TextRenderer.DrawText(g, "xong một con → thả lại", Theme.DataSm,
            new Rectangle(boxes[2].X, arcBottom + Theme.Px(2), boxes[4].Right - boxes[2].X, Theme.Px(16)),
            Theme.Dimmer, Theme.Centre);
    }

    private static void Tri(Graphics g, int tipX, int cy, int s, Color c)
    {
        using var b = new SolidBrush(c);
        g.FillPolygon(b, new[]
        {
            new Point(tipX, cy),
            new Point(tipX - s, cy - s / 2),
            new Point(tipX - s, cy + s / 2)
        });
    }

    /// <summary>Duong cong tu day tram A ve day tram B, ve net dut.</summary>
    private static void Arc(Graphics g, Rectangle from, Rectangle to, int top, int bottom,
                            Color c, bool arrow)
    {
        using var p = new Pen(c, 1.3f) { DashStyle = DashStyle.Dash };
        int ax = from.X + from.Width / 2;
        int bx = to.X + to.Width / 2;
        using var path = new GraphicsPath();
        path.AddBezier(ax, top, ax, bottom, bx, bottom, bx, top);
        g.DrawPath(p, path);
        if (arrow) Tri2(g, bx, top, Theme.Px(6), c);
    }

    private static void Tri2(Graphics g, int cx, int tipY, int s, Color c)
    {
        using var b = new SolidBrush(c);
        g.FillPolygon(b, new[]
        {
            new Point(cx, tipY),
            new Point(cx - s / 2, tipY + s),
            new Point(cx + s / 2, tipY + s)
        });
    }
}

// ------------------------------------------------------------------ o chi so

internal enum SparkKind { None, Line, Stack }

/// <summary>Mot o chi so: nhan nho, so to, va mot dai nho ben duoi.</summary>
internal sealed class MetricTile : DrawPanel
{
    private const int Cap = 64;
    private readonly double[] _ring = new double[Cap];
    private int _count;
    private (double frac, Color colour)[] _stack = Array.Empty<(double, Color)>();

    public MetricTile()
    {
        BackColor = Theme.Surface;
        Height = Theme.Px(74);
    }

    public string Caption { get; set; } = "";
    public string Value { get; set; } = "--";
    public string Unit { get; set; } = "";
    public string Foot { get; set; } = "";
    public SparkKind Kind { get; set; } = SparkKind.None;
    public Color Ink { get; set; } = Theme.Accent;

    /// <summary>Vach ke phai — o cuoi hang thi tat.</summary>
    public bool Divider { get; set; } = true;

    public void Push(double v)
    {
        if (_count < Cap) { _ring[_count++] = v; }
        else
        {
            Array.Copy(_ring, 1, _ring, 0, Cap - 1);
            _ring[Cap - 1] = v;
        }
    }

    public void SetStack(params (double frac, Color colour)[] parts)
    {
        _stack = parts ?? Array.Empty<(double, Color)>();
    }

    public void Reset()
    {
        _count = 0;
        _stack = Array.Empty<(double, Color)>();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);

        if (Divider)
            Theme.Fill(g, new Rectangle(Width - 1, 0, 1, Height), Theme.Line);

        int pad = Theme.Px(12);
        int y = Theme.Px(8);

        TextRenderer.DrawText(g, Caption.ToUpperInvariant(), Theme.Section,
            new Rectangle(pad, y, Width - pad * 2, Theme.Px(14)), Theme.Dim, Theme.Left);
        y += Theme.Px(15);

        var vSize = TextRenderer.MeasureText(g, Value, Theme.DataBig, new Size(int.MaxValue, int.MaxValue),
                                             Theme.Left);
        TextRenderer.DrawText(g, Value, Theme.DataBig,
            new Rectangle(pad, y, Width - pad * 2, Theme.Px(26)), Theme.Head, Theme.Left);

        if (Unit.Length > 0)
            TextRenderer.DrawText(g, Unit, Theme.Data,
                new Rectangle(pad + vSize.Width + Theme.Px(3), y + Theme.Px(9),
                              Width - pad * 2 - vSize.Width, Theme.Px(16)),
                Theme.Dim, Theme.Left);
        y += Theme.Px(28);

        var strip = new Rectangle(pad, y, Width - pad * 2, Theme.Px(14));
        if (Kind == SparkKind.Line) Line(g, strip);
        else if (Kind == SparkKind.Stack) Stack(g, strip);
        else if (Foot.Length > 0)
            TextRenderer.DrawText(g, Foot, Theme.DataSm, strip, Theme.Dimmer, Theme.Left);
    }

    private void Line(Graphics g, Rectangle r)
    {
        if (_count < 2) return;

        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < _count; i++) { lo = Math.Min(lo, _ring[i]); hi = Math.Max(hi, _ring[i]); }
        double span = hi - lo;
        if (span < 1e-9) { lo -= 0.5; span = 1; }

        var pts = new PointF[_count];
        for (int i = 0; i < _count; i++)
        {
            float fx = r.X + r.Width * (_count == 1 ? 0 : i / (float)(_count - 1));
            float fy = r.Bottom - (float)((_ring[i] - lo) / span) * (r.Height - 2) - 1;
            pts[i] = new PointF(fx, fy);
        }

        using var p = new Pen(Ink, 1.5f);
        g.DrawLines(p, pts);
        using var b = new SolidBrush(Ink);
        g.FillEllipse(b, pts[^1].X - 2f, pts[^1].Y - 2f, 4f, 4f);
    }

    private void Stack(Graphics g, Rectangle r)
    {
        if (_stack.Length == 0) return;
        int x = r.X;
        int h = Theme.Px(9);
        int top = r.Y + (r.Height - h) / 2;
        foreach (var (frac, colour) in _stack)
        {
            int w = (int)Math.Round(Math.Clamp(frac, 0, 1) * r.Width);
            if (w <= 0) continue;
            Theme.Fill(g, new Rectangle(x, top, Math.Min(w, r.Right - x), h), colour);
            x += w + 1;
            if (x >= r.Right) break;
        }
    }
}

// ------------------------------------------------------------------ hang do HUD

/// <summary>
/// Cac hang "nhan · thanh do · vach nguong · gia tri". Thay nam nhan Consolas cua
/// khung Đọc HUD: cung con so do, nhung co vach nguong nen doc duoc ngay la dang
/// tren hay duoi muc quyet dinh.
/// </summary>
internal sealed class MeterList : DrawPanel
{
    internal sealed class Row
    {
        public string Label = "";
        public double Fill01 = -1;
        public double Thr01 = -1;
        public string Value = "--";
        public Color Ink = Theme.Dimmer;
        public Color ValueInk = Theme.Text;
    }

    private readonly List<Row> _rows = new();

    public MeterList()
    {
        BackColor = Theme.Surface;
    }

    public string Footer { get; set; } = "";

    public Row Add(string label)
    {
        var r = new Row { Label = label };
        _rows.Add(r);
        return r;
    }

    public int Preferred =>
        _rows.Count * Theme.Px(21) + (Footer.Length > 0 ? Theme.Px(18) : 0);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);

        int labW = Theme.Px(66);
        int valW = Theme.Px(112);
        int rowH = Theme.Px(21);
        int y = 0;

        foreach (var r in _rows)
        {
            TextRenderer.DrawText(g, r.Label, Theme.Data,
                new Rectangle(0, y, labW, rowH), Theme.Dim, Theme.Left);

            int barX = labW + Theme.Px(8);
            int barW = Width - barX - valW - Theme.Px(8);
            if (barW > Theme.Px(20))
            {
                var bar = new Rectangle(barX, y + rowH / 2 - Theme.Px(4), barW, Theme.Px(7));
                Theme.Bar(g, bar, r.Fill01, r.Ink, r.Thr01);
            }

            TextRenderer.DrawText(g, r.Value, Theme.Data,
                new Rectangle(Width - valW, y, valW, rowH), r.ValueInk, Theme.Right);

            y += rowH;
        }

        if (Footer.Length == 0) return;
        TextRenderer.DrawText(g, Footer, Theme.DataSm,
            new Rectangle(0, y + Theme.Px(2), Width, Theme.Px(16)), Theme.Dimmer, Theme.Left);
    }
}

// ------------------------------------------------------------------ thanh kg

/// <summary>
/// Thanh dung luong: phan da dung, phan gach cheo (cho ca — uoc luong, chua chac),
/// va mot vach nguong. Ba thu nay truoc day chi nam trong log duoi dang so.
/// </summary>
internal sealed class CapacityBar : DrawPanel
{
    public CapacityBar()
    {
        BackColor = Theme.Surface;
        Height = Theme.Px(56);
    }

    public string Label { get; set; } = "";
    public string ValueText { get; set; } = "--";
    public string Note { get; set; } = "";

    /// <summary>0..1, -1 = chua biet.</summary>
    public double Fill01 { get; set; } = -1;
    /// <summary>Phan uoc luong nam tiep sau Fill01. -1 = chua biet.</summary>
    public double Pending01 { get; set; } = -1;
    public double Thr01 { get; set; } = -1;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);

        TextRenderer.DrawText(g, Label, Theme.Data,
            new Rectangle(0, 0, Width / 2, Theme.Px(17)), Theme.Text, Theme.Left);
        TextRenderer.DrawText(g, ValueText, Theme.Data,
            new Rectangle(Width / 2, 0, Width / 2, Theme.Px(17)), Theme.Dim, Theme.Right);

        var bar = new Rectangle(0, Theme.Px(20), Width, Theme.Px(16));
        Theme.Bar(g, bar, Fill01, Theme.AccentDim, Thr01, Theme.Warn);

        if (Pending01 > 0 && Fill01 >= 0)
        {
            int x0 = bar.X + 1 + (int)Math.Round(Math.Clamp(Fill01, 0, 1) * (bar.Width - 2));
            int w = (int)Math.Round(Math.Clamp(Pending01, 0, 1) * (bar.Width - 2));
            w = Math.Min(w, bar.Right - 1 - x0);
            if (w > 0)
                Theme.Hatch(g, new Rectangle(x0, bar.Y + 1, w, bar.Height - 2), Theme.Accent);
        }

        if (Note.Length == 0) return;
        TextRenderer.DrawText(g, Note, Theme.DataSm,
            new Rectangle(0, Theme.Px(38), Width, Theme.Px(16)), Theme.Dimmer, Theme.Left);
    }
}
