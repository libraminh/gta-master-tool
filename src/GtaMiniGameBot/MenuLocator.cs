namespace GtaMiniGameBot;

internal sealed class MenuHit
{
    public Rectangle Rect { get; init; }
    public Point Click { get; init; }
    public double Density { get; init; }

    public override string ToString() =>
        $"{Rect.Width}×{Rect.Height}@{Rect.X},{Rect.Y} dens={Density:F2}";
}

internal sealed class MenuPick
{
    public MenuHit Hit { get; init; }
    public string Name { get; init; }
    public double Score { get; init; }
    /// <summary>Điểm của nhãn về nhì trên cùng ô đó — cách biệt mới là thứ dám tin.</summary>
    public double Rival { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// Dò các nút hình viên thuốc trắng của menu radial xe.
///
/// Khác <see cref="KeepLocator"/> ở bốn chỗ, và cả bốn đều bắt buộc ở đây:
///  1. KHOÉT LỖ TÂM — nút ✕ nằm đúng tâm màn, trắng, và là khối đặc nhất nếu không bỏ đi.
///  2. TRẢ VỀ MỌI DẢI CỘT trong một băng hàng, không chỉ dải dài nhất: "Cốp xe" và
///     "Bơm nhiên liệu" nằm CÙNG một hàng nên KeepLocator chỉ thấy được một trong hai.
///  3. Siết lại biên hàng cho từng dải cột riêng.
///  4. Phân biệt bằng NCC với mẫu chữ, và bằng phép SO SÁNH giữa các nhãn chứ không phải
///     một cái ngưỡng — đo được thực tế là vị trí nút xê dịch theo số lượng nút đang hiện.
/// </summary>
internal sealed class MenuLocator : IDisposable
{
    private readonly FishingConfig _cfg;
    private readonly RegionReader _band;
    private readonly Point _holeCentre;
    private readonly int _holeRadius;
    private readonly int _refW, _refH;
    private readonly int _tb, _tg, _tr, _tol;
    private readonly Dictionary<string, GrayTemplate> _labels = new();

    public Rectangle BandRegion => _band.Region;
    public Color Target => Color.FromArgb(_tr, _tg, _tb);
    public IReadOnlyCollection<string> Labels => _labels.Keys;

    private MenuLocator(FishingConfig cfg, Rectangle band, Point hole, Size refSize, Color target)
    {
        _cfg = cfg;
        _band = new RegionReader(band);
        _holeCentre = hole;
        _refW = Math.Max(20, refSize.Width);
        _refH = Math.Max(10, refSize.Height);
        _holeRadius = (int)Math.Round(_refH * 0.85);
        _tb = target.B; _tg = target.G; _tr = target.R;
        _tol = Math.Max(4, cfg.MenuColorTol);
    }

    /// <summary>Null + lý do nếu chưa đủ mẫu/vùng để dò.</summary>
    public static MenuLocator Create(FishingConfig cfg, Screen screen, FishingProfile p, out string problem)
    {
        problem = null;
        if (!p.AltInteract.IsSet) { problem = "chưa khoanh nút Tương tác"; return null; }

        var bandRect = p.AltSearchBand();
        if (!bandRect.IsSet) { problem = "vùng quét menu quá nhỏ"; return null; }

        string interactPng = FishingConfig.TrunkTemplatePath(p.Key, "menu-interact");
        if (!File.Exists(interactPng)) { problem = "thiếu mẫu menu-interact.png — khoanh lại nút Tương tác"; return null; }

        Color target;
        try { target = KeepLocator.DominantColor(interactPng); }
        catch (Exception ex) { problem = "màu nút: " + ex.Message; return null; }

        var band = FishingConfig.ToAbsolute(screen, bandRect);
        var b = screen.Bounds;
        var hole = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        var refSize = p.AltInteract.ToRectangle().Size;

        var loc = new MenuLocator(cfg, band, hole, refSize, target);
        foreach (var (name, file) in new[]
                 {
                     ("Tương tác", "menu-interact"),
                     ("Cốp xe", "menu-trunk"),
                     ("Bơm nhiên liệu", "menu-fuel")
                 })
        {
            string path = FishingConfig.TrunkTemplatePath(p.Key, file);
            if (!File.Exists(path)) continue;
            try
            {
                var t = GrayTemplate.FromFile(path);
                if (!t.IsFlat) loc._labels[name] = t;
            }
            catch { /* mau hong thi bo, cac nhan con lai van dung duoc */ }
        }

        if (!loc._labels.ContainsKey("Tương tác"))
        {
            loc.Dispose();
            problem = "mẫu nút Tương tác hỏng — khoanh lại";
            return null;
        }
        return loc;
    }

    private bool IsPill(int b, int g, int r)
        => Math.Abs(b - _tb) <= _tol && Math.Abs(g - _tg) <= _tol && Math.Abs(r - _tr) <= _tol;

    /// <summary>Chụp lại vùng quét rồi trả về mọi khối viên thuốc thấy được, đặc nhất trước.</summary>
    public List<MenuHit> FindAll()
    {
        _band.Refresh();
        var mask = _band.MaskBuffer(IsPill);
        int w = _band.Region.Width, h = _band.Region.Height;
        var origin = _band.Region.Location;

        // Nut X o tam man: trang, dac, va nam giua — khong khoet thi no thang moi khoi khac.
        int hx = _holeCentre.X - origin.X, hy = _holeCentre.Y - origin.Y;
        int r2 = _holeRadius * _holeRadius;
        for (int y = Math.Max(0, hy - _holeRadius); y < Math.Min(h, hy + _holeRadius); y++)
        for (int x = Math.Max(0, hx - _holeRadius); x < Math.Min(w, hx + _holeRadius); x++)
        {
            int dx = x - hx, dy = y - hy;
            if (dx * dx + dy * dy <= r2) mask[y * w + x] = 0;
        }

        var hits = new List<MenuHit>();
        int hLo = Math.Max(6, (int)(_refH * 0.45));
        int hHi = (int)Math.Ceiling(_refH * 2.2);
        int minRow = Math.Max(8, (int)(_refW * 0.25));

        var rowCount = new int[h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int n = 0;
            for (int x = 0; x < w; x++) if (mask[row + x] != 0) n++;
            rowCount[y] = n;
        }

        int y0 = -1;
        for (int y = 0; y <= h; y++)
        {
            bool on = y < h && rowCount[y] >= minRow;
            if (on) { if (y0 < 0) y0 = y; continue; }
            if (y0 < 0) continue;

            int runH = y - y0;
            if (runH >= hLo && runH <= hHi) CollectRow(mask, w, origin, y0, y - 1, hits);
            y0 = -1;
        }

        hits.Sort((a, b) => b.Density.CompareTo(a.Density));
        return hits;
    }

    /// <summary>Mọi dải cột trong một băng hàng — chỗ KeepLocator chỉ lấy dải dài nhất.</summary>
    private void CollectRow(byte[] mask, int w, Point origin, int y0, int y1, List<MenuHit> hits)
    {
        int runH = y1 - y0 + 1;
        int minCol = Math.Max(2, runH / 2);
        int wLo = Math.Max(16, (int)(_refW * 0.30));
        int wHi = (int)Math.Ceiling(_refW * 4.0);

        var colOn = new bool[w];
        for (int x = 0; x < w; x++)
        {
            int n = 0;
            for (int y = y0; y <= y1; y++) if (mask[y * w + x] != 0) n++;
            colOn[x] = n >= minCol;
        }

        int x0 = -1;
        for (int x = 0; x <= w; x++)
        {
            bool on = x < w && colOn[x];
            if (on) { if (x0 < 0) x0 = x; continue; }
            if (x0 < 0) continue;

            int runW = x - x0;
            if (runW >= wLo && runW <= wHi)
            {
                var hit = Measure(mask, w, origin, x0, x - 1, y0, y1);
                if (hit is not null) hits.Add(hit);
            }
            x0 = -1;
        }
    }

    private MenuHit Measure(byte[] mask, int w, Point origin, int x0, int x1, int y0, int y1)
    {
        // Siet lai bien hang RIENG cho dai cot nay: hai nut cung hang van co the lech nhau
        // vai pixel, lay chung bien thi diem click roi lech ra ngoai vien thuoc.
        int top = -1, bot = -1, on = 0;
        for (int y = y0; y <= y1; y++)
        {
            bool any = false;
            for (int x = x0; x <= x1; x++)
                if (mask[y * w + x] != 0) { any = true; on++; }
            if (any) { if (top < 0) top = y; bot = y; }
        }
        if (top < 0) return null;

        int bw = x1 - x0 + 1, bh = bot - top + 1;
        double density = on / (double)(bw * bh);
        if (density < _cfg.MenuDensityMin) return null;

        int cx = x0 + bw / 2, cy = top + bh / 2;
        if (mask[cy * w + cx] == 0)
        {
            // Tam bi vat khac de len — keo diem click ve pixel dung mau gan nhat.
            int best = int.MaxValue;
            for (int y = top; y <= bot; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (mask[y * w + x] == 0) continue;
                int d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d < best) { best = d; cx = x; cy = y; }
            }
        }

        return new MenuHit
        {
            Rect = new Rectangle(origin.X + x0, origin.Y + top, bw, bh),
            Click = new Point(origin.X + cx, origin.Y + cy),
            Density = density
        };
    }

    /// <summary>
    /// Chọn ô mang nhãn <paramref name="wanted"/>. Null = không đủ tự tin.
    /// Điều kiện là SO SÁNH: nhãn muốn tìm phải hơn nhãn về nhì trên cùng ô một khoảng —
    /// "Cốp xe" và "Bơm nhiên liệu" cùng hàng nên một cái ngưỡng đơn lẻ không phân biệt nổi.
    /// </summary>
    public MenuPick Best(string wanted, IReadOnlyList<MenuHit> hits)
    {
        if (!_labels.TryGetValue(wanted, out var want)) return null;

        MenuPick best = null;
        foreach (var hit in hits)
        {
            double score = ScoreAt(hit, want);
            double rival = -2;
            string rivalName = "–";
            foreach (var (name, tpl) in _labels)
            {
                if (name == wanted) continue;
                double s = ScoreAt(hit, tpl);
                if (s > rival) { rival = s; rivalName = name; }
            }

            if (score < _cfg.MenuNccMin) continue;
            if (rival > -1 && score - rival < _cfg.MenuNccMargin) continue;
            if (best is not null && score <= best.Score) continue;

            best = new MenuPick
            {
                Hit = hit,
                Name = wanted,
                Score = score,
                Rival = rival,
                Detail = $"{hit}  ncc={score:F2} (nhì “{rivalName}”={rival:F2})"
            };
        }
        return best;
    }

    /// <summary>Điểm của MỌI nhãn tại một ô — để tinh chỉnh ngưỡng bằng số thật, không đoán.</summary>
    public string DescribeScores(MenuHit hit) =>
        string.Join("  ", _labels.Select(kv => $"{kv.Key}={ScoreAt(hit, kv.Value):F2}"));

    /// <summary>
    /// NCC tại một ô đã dò: cắt cửa sổ ĐÚNG cỡ mẫu, canh giữa ô, kẹp trong vùng quét.
    /// Cùng thủ thuật với FishingReader.ScoreAt — GrayTemplate chỉ so được hai mảng bằng cỡ.
    /// </summary>
    private double ScoreAt(MenuHit hit, GrayTemplate tpl)
    {
        var band = _band.Region;
        int w = tpl.Width, h = tpl.Height;
        if (w > band.Width || h > band.Height) return -1;

        int x = Math.Clamp(hit.Rect.Left + (hit.Rect.Width - w) / 2, band.Left, band.Right - w);
        int y = Math.Clamp(hit.Rect.Top + (hit.Rect.Height - h) / 2, band.Top, band.Bottom - h);
        return tpl.Score(_band.GrayBuffer(new Rectangle(x, y, w, h)));
    }

    public void Dispose() => _band?.Dispose();

    /// <summary>
    /// Bó ô đã khoanh lại sát viên thuốc trắng.
    ///
    /// Cần thiết vì người dùng khoanh rộng hẹp khác nhau — đo được 166×59 cho nút này và
    /// 232×106 cho nút kia, tức quá nửa ô là nền. Mà nền ở đây là cảnh game: xe, cỏ, bóng
    /// nắng, đổi theo từng lần chụp. Để nền vào mẫu NCC là tự dìm điểm khớp của chính mình.
    /// Trả về ô gốc nếu không dò được khối nào — thà mẫu rộng còn hơn mẫu cắt bậy.
    /// </summary>
    public static Rectangle TightenPill(Bitmap crop, int tol, out string note)
    {
        var whole = new Rectangle(0, 0, crop.Width, crop.Height);
        var target = KeepLocator.DominantColor(crop);
        var gray = GlyphSeg.GrayOf(crop, whole, out int w, out int h);
        if (w < 4 || h < 4) { note = "ô quá nhỏ"; return whole; }

        // Vien thuoc gan nhu trang; do tren xam la du va re hon doc lai 3 kenh mau.
        int tg = (target.R * 30 + target.G * 59 + target.B * 11) / 100;
        var on = new bool[w * h];
        for (int i = 0; i < gray.Length; i++) on[i] = Math.Abs(gray[i] - tg) <= tol;

        int left = -1, right = -1, top = -1, bot = -1;
        for (int y = 0; y < h; y++)
        {
            int n = 0;
            for (int x = 0; x < w; x++) if (on[y * w + x]) n++;
            if (n < w * 0.25) continue;
            if (top < 0) top = y;
            bot = y;
        }
        for (int x = 0; x < w; x++)
        {
            int n = 0;
            for (int y = 0; y < h; y++) if (on[y * w + x]) n++;
            if (n < h * 0.25) continue;
            if (left < 0) left = x;
            right = x;
        }

        if (top < 0 || left < 0 || right - left < 8 || bot - top < 6)
        {
            note = "không dò được viên thuốc — giữ nguyên ô đã khoanh";
            return whole;
        }

        var tight = Rectangle.Intersect(
            Rectangle.Inflate(Rectangle.FromLTRB(left, top, right + 1, bot + 1), 2, 2), whole);
        note = $"bó sát {whole.Width}×{whole.Height} → {tight.Width}×{tight.Height}";
        return tight;
    }
}
