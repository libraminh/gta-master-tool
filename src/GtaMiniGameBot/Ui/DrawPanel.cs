namespace GtaMiniGameBot;

/// <summary>
/// Panel tu ve. Truoc day class nay bi copy y nguyen ba lan (StillCropForm,
/// LearnDigitsForm, FishSlotForm) va ca ba deu thieu AllPaintingInWmPaint —
/// tuc con mot luot WM_ERASEBKGND truoc moi lan ve, dung nguon nhay trang
/// tren nen toi.
/// </summary>
internal class DrawPanel : Panel
{
    public DrawPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    /// <summary>Cho widget bam duoc: nhan focus ban phim va vao duoc vong Tab.</summary>
    protected void MakeFocusable()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }
}

/// <summary>Khung co tieu de — thay GroupBox, vi GroupBox bo qua ForeColor o vien.</summary>
internal sealed class DarkGroup : DrawPanel
{
    private string _title = "";

    public DarkGroup()
    {
        BackColor = Theme.Surface;
        Padding = new Padding(Theme.Px(12), Theme.Px(26), Theme.Px(12), Theme.Px(12));
    }

    public string Title
    {
        get => _title;
        set { _title = value ?? ""; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);

        var box = new Rectangle(0, Theme.Px(8), Width - 1, Height - Theme.Px(8) - 1);
        Theme.Frame(g, box, Theme.Line);

        if (_title.Length == 0) return;

        string t = _title.ToUpperInvariant();
        var size = TextRenderer.MeasureText(g, t, Theme.Section, new Size(int.MaxValue, int.MaxValue),
                                            Theme.Left);
        int pad = Theme.Px(6);
        var slot = new Rectangle(Theme.Px(10) - pad, 0, size.Width + pad * 2, Theme.Px(17));

        // Xoa mot khoang vien de chu khong dam vao net ke.
        Theme.Fill(g, new Rectangle(slot.X, Theme.Px(8), slot.Width, Theme.Px(1)), BackColor);
        TextRenderer.DrawText(g, t, Theme.Section,
            new Rectangle(Theme.Px(10), 0, size.Width + Theme.Px(2), Theme.Px(17)),
            Theme.Dim, Theme.Left);
    }
}
