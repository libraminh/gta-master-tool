using System.Drawing.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Bang mau + font dung chung. Truoc day moi mau/font duoc khai o ngay cho dung
/// (~90 cho), nen doi mot mau la phai lan het file. Gio moi thu o day.
///
/// Mau lay tu phuong an "Tram dieu khien", nhung giu lai dung ho mau da co trong
/// repo (StatusOverlay, StillCropForm, LearnDigitsForm) de overlay va cac dialog
/// chup anh khong lac ra khoi bo.
/// </summary>
internal static class Theme
{
    // ---------------- nen ----------------
    public static readonly Color Ground = Hex(0x0B0E13);
    public static readonly Color Surface = Hex(0x141A23);
    public static readonly Color Sunk = Hex(0x0E131A);
    public static readonly Color Well = Hex(0x080B10);
    public static readonly Color Line = Hex(0x232E3B);
    public static readonly Color Line2 = Hex(0x2E3B4B);

    // ---------------- chu ----------------
    public static readonly Color Text = Hex(0xC9D4E1);
    public static readonly Color Head = Hex(0xEAF1F8);
    public static readonly Color Dim = Hex(0x6E7E92);
    public static readonly Color Dimmer = Hex(0x4C5A6B);

    // ---------------- nhan ----------------
    public static readonly Color Accent = Hex(0x4BD4FF);
    public static readonly Color AccentDim = Hex(0x1E5E75);
    public static readonly Color AccentWash = Hex(0x10222B);
    public static readonly Color Good = Hex(0x3ADB6D);
    public static readonly Color Warn = Hex(0xFFC44D);
    public static readonly Color Bad = Hex(0xFF6B6B);
    public static readonly Color WarnText = Hex(0xF0DDB0);
    public static readonly Color GoodText = Hex(0xB9EBC9);

    /// <summary>Mau nen nut CẤT VÀO trong game — do duoc tu anh that, dung cho thumbnail gia.</summary>
    public static readonly Color GameKeep = Hex(0x1E3D41);

    // ---------------- font ----------------
    // Bahnschrift co san tu Windows 10; neu may thieu thi new Font(...) am tham
    // roi ve Microsoft Sans Serif, nen phai do truoc bang FontFamily.
    private static readonly string DisplayFamily =
        Family("Bahnschrift SemiCondensed", "Bahnschrift", "Segoe UI Semibold", "Segoe UI");
    private static readonly string BodyFamily = Family("Segoe UI", "Tahoma");
    private static readonly string DataFamily = Family("Consolas", "Cascadia Mono", "Courier New");

    public static readonly Font Title = new(DisplayFamily, 13F, FontStyle.Bold);
    public static readonly Font StateBig = new(DisplayFamily, 15F, FontStyle.Bold);
    public static readonly Font PhaseBig = new(DisplayFamily, 11.5F, FontStyle.Bold);
    public static readonly Font Section = new(DisplayFamily, 9F, FontStyle.Bold);
    public static readonly Font Nav = new(DisplayFamily, 7.5F, FontStyle.Bold);

    public static readonly Font Body = new(BodyFamily, 9F);
    public static readonly Font BodySm = new(BodyFamily, 8.25F);

    public static readonly Font Data = new(DataFamily, 9F);
    public static readonly Font DataSm = new(DataFamily, 8.25F);
    public static readonly Font DataMd = new(DataFamily, 10F);
    public static readonly Font DataBig = new(DataFamily, 17F);

    private static string Family(params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                using var f = new FontFamily(n);
                return n;
            }
            catch { /* may khong co font nay - thu ten ke tiep */ }
        }
        return FontFamily.GenericSansSerif.Name;
    }

    // ---------------- DPI ----------------
    // App bat PerMonitorV2 (Program.cs) nen moi toa do la pixel vat ly. Bo cuc cu
    // dung so nguyen 96-dpi, nen o 125/150% chu phinh ra khoi hop. Moi thu ve moi
    // di qua Px() de con co duong sua.
    //
    // Chi doc DPI cua man hinh chinh: dung mot he so cho ca app thi don gian hon
    // nhieu, va cua so nay khong duoc phep nam tren vung game doc anyway.
    private static float _scale;

    public static float Scale
    {
        get
        {
            if (_scale > 0) return _scale;
            try
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                _scale = g.DpiX / 96f;
            }
            catch { _scale = 1f; }
            if (_scale <= 0) _scale = 1f;
            return _scale;
        }
    }

    public static int Px(int v) => (int)Math.Round(v * Scale);

    // ---------------- ve ----------------

    /// <summary>
    /// Hint chung cho moi OnPaint. Chu ve bang TextRenderer (GDI) vi
    /// Program.cs goi SetCompatibleTextRenderingDefault(false) — tron GDI+ vao
    /// se lech net so voi control con lai.
    /// </summary>
    public static void Prep(Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    public const TextFormatFlags Left =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
        | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    public const TextFormatFlags Right =
        TextFormatFlags.Right | TextFormatFlags.VerticalCenter
        | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    public const TextFormatFlags Centre =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
        | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    /// <summary>Cho dong log: xuong dong theo tu, khong an dau &amp; thanh gach chan.</summary>
    public const TextFormatFlags Wrap =
        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak
        | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    public static void Fill(Graphics g, Rectangle r, Color c)
    {
        using var b = new SolidBrush(c);
        g.FillRectangle(b, r);
    }

    public static void Frame(Graphics g, Rectangle r, Color c, int w = 1)
    {
        using var p = new Pen(c, w);
        g.DrawRectangle(p, r.X, r.Y, r.Width - w, r.Height - w);
    }

    /// <summary>
    /// Thanh do: nen lom, phan da day, va mot vach nguong. Dung o ca meter HUD lan
    /// thanh kg — de mot cho cho no giong nhau that.
    /// </summary>
    public static void Bar(Graphics g, Rectangle r, double fill01, Color fill,
                           double thr01 = -1, Color thrColor = default)
    {
        Fill(g, r, Well);
        Frame(g, r, Line);

        if (fill01 > 0)
        {
            int w = (int)Math.Round(Math.Clamp(fill01, 0, 1) * (r.Width - 2));
            if (w > 0) Fill(g, new Rectangle(r.X + 1, r.Y + 1, w, r.Height - 2), fill);
        }

        if (thr01 < 0 || thr01 > 1) return;
        int x = r.X + 1 + (int)Math.Round(thr01 * (r.Width - 2));
        x = Math.Min(x, r.Right - 2);
        Fill(g, new Rectangle(x, r.Y - Px(2), Math.Max(1, Px(1)), r.Height + Px(4)),
             thrColor == default ? Dim : thrColor);
    }

    /// <summary>Gach cheo — phan "cho ca" chua biet chac, ve khac phan da do duoc.</summary>
    public static void Hatch(Graphics g, Rectangle r, Color c)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var b = new System.Drawing.Drawing2D.HatchBrush(
            System.Drawing.Drawing2D.HatchStyle.ForwardDiagonal, c, Color.Transparent);
        var old = g.Clip;
        g.SetClip(r);
        g.FillRectangle(b, r);
        g.Clip = old;
    }

    /// <summary>
    /// Nhuom toi thanh tieu de. Phai goi sau khi handle da tao, khong thi
    /// DwmSetWindowAttribute khong co hwnd de nham vao.
    /// </summary>
    public static void DarkTitleBar(Form f)
    {
        if (f is null || !f.IsHandleCreated) return;
        try
        {
            int on = 1;
            if (Native.DwmSetWindowAttribute(f.Handle,
                    Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(f.Handle,
                    Native.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

            int caption = Native.ColorRef(Sunk);
            Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            int border = Native.ColorRef(Line2);
            Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
            int ink = Native.ColorRef(Text);
            Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_TEXT_COLOR, ref ink, sizeof(int));
        }
        catch { /* Windows cu hon 1809 khong co dwmapi attribute nay - de nguyen thanh sang */ }
    }

    private static Color Hex(int rgb) =>
        Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
