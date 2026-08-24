namespace GtaMiniGameBot;

internal enum LogLevel { Info, Good, Warn, Bad, Mark }

/// <summary>
/// Khung dien bien tu ve.
///
/// Vi sao khong dung TextBox nua: dong log dai 100-160 ky tu, khong mac do, khong
/// loc, va cai trim cu (`_log.Lines = _log.Lines.Skip(150).ToArray()`) viet lai
/// TOAN BO noi dung moi lan tran 400 dong. Ban nay giu danh sach, ve theo tam nhin,
/// va gop dong lap lai thanh ×N — dong "chờ cửa sổ PlayXGTA" truoc day nhan chim
/// het moi thu khac.
/// </summary>
internal sealed class LogView : DrawPanel
{
    private const int Keep = 1500;

    private sealed class Entry
    {
        public string Stamp = "";
        public string Text = "";
        public string Key = "";
        public LogLevel Level;
        public int Count = 1;

        /// <summary>Chieu cao da do, cung be rong da dung de do no. -1 = phai do lai.</summary>
        public int Height = -1;
        public int ForWidth = -1;
    }

    private readonly List<Entry> _all = new();
    private LogLevel? _filter;
    private int _scroll;
    private bool _stick = true;
    private int _contentH;
    private int _lastBodyW = -1;

    private Rectangle[] _chips = Array.Empty<Rectangle>();
    private int _hotChip = -1;
    private bool _dragging;
    private int _dragFrom;
    private int _dragScroll;

    public LogView()
    {
        BackColor = Theme.Surface;
        MakeFocusable();
    }

    private int HeaderH => Theme.Px(36);
    private int StampW => Theme.Px(54);
    private int StripeW => Theme.Px(3);
    private int PadX => Theme.Px(12);
    private int ScrollW => Theme.Px(8);

    private int BodyTextWidth =>
        Math.Max(Theme.Px(60), Width - PadX - StripeW - Theme.Px(8) - StampW - Theme.Px(8) - ScrollW - Theme.Px(4));

    // ---------------------------------------------------------------- them dong

    public void Append(string line)
    {
        line = (line ?? "").TrimEnd();
        if (line.Length == 0) return;

        var level = Classify(line);
        string key = CollapseKey(line);

        // Gop dong lap lien tiep. Chi so lien tiep — gop cach quang se lam dong cu
        // nhay len lam doi thu tu thoi gian.
        if (_all.Count > 0)
        {
            var last = _all[^1];
            if (last.Level == level && last.Key == key)
            {
                last.Count++;
                last.Stamp = DateTime.Now.ToString("HH:mm:ss");
                last.Text = line;
                last.Height = -1;
                Bump();
                return;
            }
        }

        _all.Add(new Entry
        {
            Stamp = DateTime.Now.ToString("HH:mm:ss"),
            Text = line,
            Key = key,
            Level = level
        });

        if (_all.Count > Keep) _all.RemoveRange(0, _all.Count - Keep);
        Bump();
    }

    public void Clear()
    {
        _all.Clear();
        _scroll = 0;
        _stick = true;
        _contentH = 0;
        Invalidate();
    }

    private void Bump()
    {
        _lastBodyW = -1;      // buoc do lai chieu cao
        if (_stick) _scroll = int.MaxValue;
        Invalidate();
    }

    /// <summary>
    /// Phan loai theo chinh chuoi bot phat ra. Bot khong gan muc do, va them tham so
    /// muc do vao ~40 cho goi Emit thi vua on ao vua de sot — doc chuoi o day la du.
    /// </summary>
    private static LogLevel Classify(string s)
    {
        if (s.StartsWith("---", StringComparison.Ordinal)) return LogLevel.Mark;

        if (Has(s, "lỗi") || Has(s, "thất bại") || Has(s, "KHÔNG chạy")
            || Has(s, "ĐANG KẸT") || Has(s, "hỏng"))
            return LogLevel.Bad;

        if (Has(s, "cảnh báo") || Has(s, "không cắn") || Has(s, "thả câu trượt")
            || Has(s, "chê mồi") || Has(s, "chờ cửa sổ") || Has(s, "không dò được")
            || Has(s, "vẫn còn sau") || Has(s, "không nhận") || Has(s, "bỏ qua")
            || Has(s, "quá ") || Has(s, "huỷ") || Has(s, "hủy"))
            return LogLevel.Warn;

        if (Has(s, "cá cắn") || Has(s, "xong —") || Has(s, "đã kéo")
            || Has(s, "CẤT VÀO @") || Has(s, "THẢ RA @") || Has(s, "đã mở") || Has(s, "thấy nút"))
            return LogLevel.Good;

        return LogLevel.Info;
    }

    private static bool Has(string s, string needle) =>
        s.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Khoa de gop. Cat phan trong ngoac dau tien: dong "chờ cửa sổ … (đang focus: …)"
    /// doi ten cua so moi lan, nhung no la cung mot su kien.
    /// </summary>
    private static string CollapseKey(string s)
    {
        int i = s.IndexOf(" (", StringComparison.Ordinal);
        return i > 8 ? s[..i] : s;
    }

    // ---------------------------------------------------------------- bo cuc

    private IEnumerable<Entry> Shown()
    {
        if (_filter is null) return _all;
        var f = _filter.Value;
        return _all.Where(x => x.Level == f);
    }

    private void Measure(Graphics g)
    {
        int w = BodyTextWidth;
        if (_lastBodyW == w) return;
        _lastBodyW = w;

        _contentH = 0;
        foreach (var en in Shown())
        {
            if (en.Height < 0 || en.ForWidth != w)
            {
                var sz = TextRenderer.MeasureText(g, en.Text, Theme.Data,
                    new Size(w, int.MaxValue), Theme.Wrap);
                en.Height = Math.Max(Theme.Px(16), sz.Height) + Theme.Px(4);
                en.ForWidth = w;
            }
            _contentH += en.Height;
        }
    }

    private Rectangle Body => new(0, HeaderH, Width, Math.Max(0, Height - HeaderH));

    private void LayoutChips(Graphics g)
    {
        var labels = ChipLabels();
        var rects = new Rectangle[labels.Length];
        int x = PadX;
        int h = Theme.Px(18);
        int y = (HeaderH - h) / 2 + Theme.Px(1);

        // "DIỄN BIẾN" nam truoc cac chip.
        var head = TextRenderer.MeasureText(g, "DIỄN BIẾN", Theme.Section,
            new Size(int.MaxValue, int.MaxValue), Theme.Left);
        x += head.Width + Theme.Px(14);

        for (int i = 0; i < labels.Length; i++)
        {
            var sz = TextRenderer.MeasureText(g, labels[i], Theme.DataSm,
                new Size(int.MaxValue, int.MaxValue), Theme.Left);
            rects[i] = new Rectangle(x, y, sz.Width + Theme.Px(16), h);
            x += rects[i].Width + Theme.Px(6);
        }
        _chips = rects;
    }

    private string[] ChipLabels()
    {
        int warn = _all.Count(x => x.Level == LogLevel.Warn);
        int bad = _all.Count(x => x.Level == LogLevel.Bad);
        return new[] { $"tất cả {_all.Count}", $"cảnh báo {warn}", $"lỗi {bad}" };
    }

    private static LogLevel? ChipFilter(int i) => i switch
    {
        1 => LogLevel.Warn,
        2 => LogLevel.Bad,
        _ => null
    };

    // ---------------------------------------------------------------- chuot

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();

        for (int i = 0; i < _chips.Length; i++)
        {
            if (!_chips[i].Contains(e.Location)) continue;
            _filter = ChipFilter(i);
            _lastBodyW = -1;
            _scroll = int.MaxValue;
            _stick = true;
            Invalidate();
            base.OnMouseDown(e);
            return;
        }

        var track = ScrollTrack();
        if (track.Contains(e.Location))
        {
            _dragging = true;
            _dragFrom = e.Y;
            _dragScroll = _scroll;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hot = -1;
        for (int i = 0; i < _chips.Length; i++)
            if (_chips[i].Contains(e.Location)) { hot = i; break; }
        if (hot != _hotChip) { _hotChip = hot; Invalidate(); }

        Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;

        if (_dragging)
        {
            var body = Body;
            int over = Math.Max(0, _contentH - body.Height);
            if (over > 0 && body.Height > 0)
            {
                double k = over / (double)body.Height;
                ScrollTo(_dragScroll + (int)((e.Y - _dragFrom) * (1 + k)));
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hotChip >= 0) { _hotChip = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollTo(_scroll - e.Delta / 120 * Theme.Px(48));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var body = Body;
        switch (e.KeyCode)
        {
            case Keys.Home: ScrollTo(0); e.Handled = true; break;
            case Keys.End: ScrollTo(int.MaxValue); e.Handled = true; break;
            case Keys.PageUp: ScrollTo(_scroll - body.Height); e.Handled = true; break;
            case Keys.PageDown: ScrollTo(_scroll + body.Height); e.Handled = true; break;
            case Keys.Up: ScrollTo(_scroll - Theme.Px(24)); e.Handled = true; break;
            case Keys.Down: ScrollTo(_scroll + Theme.Px(24)); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Up or Keys.Down
        || base.IsInputKey(keyData);

    private void ScrollTo(int to)
    {
        int max = Math.Max(0, _contentH - Body.Height);
        int v = Math.Clamp(to, 0, max);
        // Dinh day: dang o cuoi thi dong moi tu keo theo. Roi khoi cuoi thi thoi,
        // khong thi khong the doc lai dong cu luc bot dang chay.
        _stick = v >= max - Theme.Px(4);
        if (v == _scroll) return;
        _scroll = v;
        Invalidate();
    }

    private Rectangle ScrollTrack()
    {
        var body = Body;
        return new Rectangle(Width - ScrollW - Theme.Px(2), body.Y, ScrollW, body.Height);
    }

    // ---------------------------------------------------------------- ve

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);

        Measure(g);
        LayoutChips(g);

        var body = Body;
        int max = Math.Max(0, _contentH - body.Height);
        if (_scroll > max) _scroll = max;
        if (_scroll < 0) _scroll = 0;

        // --- dau khung ---
        TextRenderer.DrawText(g, "DIỄN BIẾN", Theme.Section,
            new Rectangle(PadX, 0, Width, HeaderH), Theme.Dim, Theme.Left);

        var labels = ChipLabels();
        for (int i = 0; i < _chips.Length && i < labels.Length; i++)
        {
            bool on = _filter == ChipFilter(i);
            Color edge = i switch
            {
                1 => on ? Theme.Warn : Theme.Line2,
                2 => on ? Theme.Bad : Theme.Line2,
                _ => on ? Theme.AccentDim : Theme.Line2
            };
            Color ink = i switch
            {
                1 => Theme.Warn,
                2 => Theme.Bad,
                _ => on ? Theme.Accent : Theme.Dim
            };
            if (on) Theme.Fill(g, _chips[i], Theme.AccentWash);
            else if (i == _hotChip) Theme.Fill(g, _chips[i], Theme.Sunk);
            Theme.Frame(g, _chips[i], edge);
            TextRenderer.DrawText(g, labels[i], Theme.DataSm, _chips[i], ink, Theme.Centre);
        }

        Theme.Fill(g, new Rectangle(0, HeaderH - 1, Width, 1), Theme.Line);

        // --- than khung ---
        var clip = g.Clip;
        g.SetClip(body);

        int textW = BodyTextWidth;
        int y = body.Y - _scroll;
        foreach (var en in Shown())
        {
            int h = en.Height < 0 ? Theme.Px(20) : en.Height;
            if (y + h < body.Y) { y += h; continue; }
            if (y > body.Bottom) break;

            Theme.Fill(g, new Rectangle(PadX, y + Theme.Px(2), StripeW, h - Theme.Px(5)),
                       Stripe(en.Level));

            int tx = PadX + StripeW + Theme.Px(8);
            TextRenderer.DrawText(g, en.Stamp, Theme.DataSm,
                new Rectangle(tx, y + Theme.Px(2), StampW, Theme.Px(16)), Theme.Dimmer, Theme.Left);

            string text = en.Count > 1 ? $"{en.Text}   ×{en.Count}" : en.Text;
            TextRenderer.DrawText(g, text, Theme.Data,
                new Rectangle(tx + StampW + Theme.Px(8), y + Theme.Px(2), textW, h),
                Ink(en.Level), Theme.Wrap);

            y += h;
        }

        g.Clip = clip;

        // --- thanh cuon ---
        if (max <= 0) return;
        var track = ScrollTrack();
        Theme.Fill(g, track, Theme.Well);
        int thumbH = Math.Max(Theme.Px(24),
            (int)(body.Height * (body.Height / (double)_contentH)));
        int thumbY = track.Y + (int)((track.Height - thumbH) * (_scroll / (double)max));
        Theme.Fill(g, new Rectangle(track.X, thumbY, track.Width, thumbH),
                   _dragging ? Theme.Accent : Theme.Line2);
    }

    private static Color Stripe(LogLevel l) => l switch
    {
        LogLevel.Good => Theme.Good,
        LogLevel.Warn => Theme.Warn,
        LogLevel.Bad => Theme.Bad,
        LogLevel.Mark => Theme.Dim,
        _ => Theme.AccentDim
    };

    private static Color Ink(LogLevel l) => l switch
    {
        LogLevel.Good => Theme.GoodText,
        LogLevel.Warn => Theme.WarnText,
        LogLevel.Bad => Theme.Bad,
        LogLevel.Mark => Theme.Dim,
        _ => Theme.Text
    };
}
