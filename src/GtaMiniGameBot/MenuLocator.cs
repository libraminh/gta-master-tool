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
/// Mẫu chữ của một nút menu, kèm mặt nạ bỏ phần không phải nút.
///
/// Vì sao phải có mặt nạ: viên thuốc BO GÓC, nên bốn góc của ảnh mẫu không phải nút mà là nền
/// cảnh game phía sau. Mẫu được chụp lúc nút nằm trên viền kính trắng của xe, còn lúc chạy nút
/// có thể nằm trên nắp xe tối — góc đảo từ sáng sang tối là NCC tụt hẳn. Đo được trong log
/// 19/08: cùng một khối, cùng toạ độ 136×40@1081,700, cùng độ đặc 0.83, mà chấm 0.32 lần này
/// và 0.92 lần khác, tức trượt/đạt chỉ do nền sau góc bo.
///
/// Cùng công thức với <see cref="GrayTemplate.Score"/>, chỉ khác phạm vi cộng — giống cách
/// <see cref="IconTemplate"/> làm cho icon kho đồ.
/// </summary>
internal sealed class PillTemplate
{
    public int Width { get; }
    public int Height { get; }

    private readonly byte[] _gray;
    private readonly bool[] _mask;
    private readonly int _n;
    private readonly double _mean;
    private readonly double _varSum;

    public bool IsFlat => _varSum < 1e-6 || _n < 32;

    private PillTemplate(int w, int h, byte[] gray, bool[] mask)
    {
        Width = w; Height = h;
        _gray = gray; _mask = mask;

        long sum = 0;
        int n = 0;
        for (int i = 0; i < gray.Length; i++)
            if (mask[i]) { sum += gray[i]; n++; }

        _n = n;
        _mean = n == 0 ? 0 : (double)sum / n;

        double vs = 0;
        for (int i = 0; i < gray.Length; i++)
            if (mask[i]) { double d = gray[i] - _mean; vs += d * d; }
        _varSum = vs;
    }

    /// <summary>
    /// Nạp mẫu và tự suy mặt nạ.
    ///
    /// Mặt nạ dựng theo TỪNG HÀNG chứ không phải theo ngưỡng độ trắng: chữ và icon trong nút là
    /// màu TỐI, nên mặt nạ kiểu "chỉ lấy pixel gần trắng" sẽ bỏ mất đúng phần mang thông tin
    /// phân biệt ba nhãn. Mỗi hàng lấy khoảng GIỮA pixel màu nút ngoài cùng trái và phải, nên
    /// thân trắng lẫn chữ tối bên trong đều vào, còn góc bo rơi ra ngoài.
    ///
    /// Thêm một lớp chặn cứng: khoét bốn ô vuông ở bốn góc. Cần vì nếu nền ngay sau góc lại
    /// gần trắng (đúng cảnh mẫu chụp trên viền kính) thì phép quét hàng sẽ kéo dài tới sát mép
    /// ảnh và góc lại lọt vào mặt nạ.
    /// </summary>
    public static PillTemplate FromFile(string path, int tol)
    {
        using var bmp = new Bitmap(path);
        var target = KeepLocator.DominantColor(bmp);
        var whole = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var gray = GlyphSeg.GrayOf(bmp, whole, out int w, out int h);
        if (w < 4 || h < 4) throw new ArgumentException($"mẫu quá nhỏ: {w}×{h}");

        int tg = (target.R * 30 + target.G * 59 + target.B * 11) / 100;
        var mask = new bool[w * h];

        const int edgePad = 2;                       // tranh vien rang cua
        int corner = Math.Max(3, (int)Math.Round(h * 0.28));

        for (int y = 0; y < h; y++)
        {
            int left = -1, right = -1;
            for (int x = 0; x < w; x++)
                if (Math.Abs(gray[y * w + x] - tg) <= tol) { if (left < 0) left = x; right = x; }

            if (left < 0 || right - left + 1 < 8) continue;

            bool nearTop = y < corner, nearBot = y >= h - corner;
            for (int x = left + edgePad; x <= right - edgePad; x++)
            {
                if ((nearTop || nearBot) && (x < corner || x >= w - corner)) continue;
                mask[y * w + x] = true;
            }
        }

        return new PillTemplate(w, h, gray, mask);
    }

    /// <summary>NCC trên vùng mặt nạ.</summary>
    public double Score(byte[] sample)
    {
        if (IsFlat || sample.Length != _gray.Length) return 0;

        long sSum = 0, sSqSum = 0, cross = 0;
        for (int i = 0; i < _gray.Length; i++)
        {
            if (!_mask[i]) continue;
            int s = sample[i];
            sSum += s;
            sSqSum += (long)s * s;
            cross += (long)s * _gray[i];
        }

        double num = cross - _mean * sSum;
        double sVar = sSqSum - (double)sSum * sSum / _n;
        double den = Math.Sqrt(Math.Max(0, sVar) * _varSum);
        return den < 1e-6 ? 0 : num / den;
    }

    /// <summary>Ảnh để soi bằng mắt: xám là phần được tính điểm, hồng là phần bị mặt nạ bỏ.</summary>
    public Bitmap ToBitmap()
    {
        var bmp = new Bitmap(Width, Height);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            byte v = _gray[i];
            bmp.SetPixel(x, y, _mask[i] ? Color.FromArgb(v, v, v) : Color.FromArgb(255, 0, 150));
        }
        return bmp;
    }

    public int MaskedPixels => _n;
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
    private readonly int _hLo, _hHi, _wLo, _wHi;
    private readonly Dictionary<string, PillTemplate> _labels = new();

    public Rectangle BandRegion => _band.Region;
    public Color Target => Color.FromArgb(_tr, _tg, _tb);
    public IReadOnlyCollection<string> Labels => _labels.Keys;

    /// <summary>
    /// Chuyện đáng nói của lần quét gần nhất, null nếu không có. Có nó thì lúc dò trượt mới
    /// biết là băng hàng bị nhập lại — <see cref="MenuLocator"/> không giữ logger nên phải
    /// treo ra ngoài cho <c>TrunkOpener</c> ghi.
    /// </summary>
    public string ScanNote { get; private set; }

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

        // Khoang co chap nhan duoc cua MOT nut, suy tu co nut mau. Noi rong hon mot chut ve moi
        // phia vi nut dai ngan khac nhau theo do dai chu ("Cốp xe" ngan, "Bơm nhiên liệu" dai).
        _hLo = Math.Max(6, (int)(_refH * 0.45));
        _hHi = (int)Math.Ceiling(_refH * 2.2);
        _wLo = Math.Max(16, (int)(_refW * 0.30));
        _wHi = (int)Math.Ceiling(_refW * 4.0);
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
                var t = PillTemplate.FromFile(path, loc._tol);
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
        ScanNote = null;
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
        int tooBig = 0, tooSmall = 0, tooThin = 0;

        // KHOI LIEN THONG 2-D, khong phai phep chieu theo hang/cot.
        //
        // Phep chieu da thu hai lan va sai cả hai:
        //  - dem TONG pixel tung hang: chi can mot vat sang CAO nam trong bang (xe trang do dung
        //    canh, toa nha) la moi hang deu vuot nguong, ca bang thanh MOT dai cao 570px, diem
        //    cat tro thanh vo nghia va ba cai nut khong bao gio duoc tim ra.
        //  - dem DAI LIEN dai nhat: chu trong nut mau toi nen no xe doi vien thuoc thanh hai lat
        //    mong; log 19/08 do duoc 0 khoi.
        // Khoi lien thong thi mien nhiem ca hai: moi vien thuoc la mot khoi, xe trang la khoi
        // khac, va loc theo CO thi xe bi bo vi qua cao. Chu toi nam trong long nut nen khong cat
        // roi khoi — vanh trang quanh chu van lien mach.
        var seen = new bool[mask.Length];
        var stack = new int[mask.Length];

        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || seen[start]) continue;

            int sp = 0;
            stack[sp++] = start;
            seen[start] = true;

            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1, count = 0;
            while (sp > 0)
            {
                int i = stack[--sp];
                int x = i % w, y = i / w;
                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                if (x > 0 && mask[i - 1] != 0 && !seen[i - 1]) { seen[i - 1] = true; stack[sp++] = i - 1; }
                if (x < w - 1 && mask[i + 1] != 0 && !seen[i + 1]) { seen[i + 1] = true; stack[sp++] = i + 1; }
                if (y > 0 && mask[i - w] != 0 && !seen[i - w]) { seen[i - w] = true; stack[sp++] = i - w; }
                if (y < h - 1 && mask[i + w] != 0 && !seen[i + w]) { seen[i + w] = true; stack[sp++] = i + w; }
            }

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (bh > _hHi || bw > _wHi) { tooBig++; continue; }
            if (bh < _hLo || bw < _wLo) { tooSmall++; continue; }

            double density = count / (double)(bw * bh);
            if (density < _cfg.MenuDensityMin) { tooThin++; continue; }

            hits.Add(MakeHit(mask, w, origin, minX, maxX, minY, maxY, density));
        }

        if (hits.Count == 0 && tooBig + tooSmall + tooThin > 0)
            ScanNote = $"bỏ {tooBig} khối quá lớn, {tooSmall} khối quá nhỏ, {tooThin} khối quá thưa " +
                       $"(cỡ nút mẫu {_refW}×{_refH}, nhận cao {_hLo}–{_hHi}, rộng {_wLo}–{_wHi})";

        hits.Sort((a, b) => b.Density.CompareTo(a.Density));
        return hits;
    }

    /// <summary>Dựng khối từ hộp bao một khối liên thông, kèm điểm click đáng tin.</summary>
    private static MenuHit MakeHit(byte[] mask, int w, Point origin,
                                   int x0, int x1, int y0, int y1, double density)
    {
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        int cx = x0 + bw / 2, cy = y0 + bh / 2;

        // Tam co the roi dung vao chu (chu mau toi nen khong thuoc mat na) hoac vao cho bi vat
        // khac de len — keo diem click ve pixel dung mau gan nhat.
        if (mask[cy * w + cx] == 0)
        {
            int best = int.MaxValue;
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (mask[y * w + x] == 0) continue;
                int d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d < best) { best = d; cx = x; cy = y; }
            }
        }

        return new MenuHit
        {
            Rect = new Rectangle(origin.X + x0, origin.Y + y0, bw, bh),
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

    /// <summary>
    /// Lưu ảnh mặt nạ từng nhãn ra PNG để soi bằng mắt — hồng là phần bị bỏ khỏi phép chấm.
    /// Bốn góc bo phải hồng, thân trắng và chữ/icon phải xám.
    /// </summary>
    public string DumpMasks(string key)
    {
        string dir = Path.Combine(FishingConfig.ProfileDir(key), "debug-menu");
        Directory.CreateDirectory(dir);
        var parts = new List<string>();
        foreach (var (name, tpl) in _labels)
        {
            using var bmp = tpl.ToBitmap();
            StillPicker.Save(bmp, Path.Combine(dir, "mask-" + Slug(name) + ".png"));
            int all = tpl.Width * tpl.Height;
            parts.Add($"{name} {tpl.Width}×{tpl.Height} chấm {tpl.MaskedPixels}/{all}" +
                      $" ({tpl.MaskedPixels * 100 / Math.Max(1, all)}%)");
        }
        return dir + "  [" + string.Join("; ", parts) + "]";
    }

    private static string Slug(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' ? c : '-');
        return sb.ToString();
    }

    /// <summary>Điểm của MỌI nhãn tại một ô — để tinh chỉnh ngưỡng bằng số thật, không đoán.</summary>
    public string DescribeScores(MenuHit hit) =>
        string.Join("  ", _labels.Select(kv => $"{kv.Key}={ScoreAt(hit, kv.Value):F2}"));

    /// <summary>
    /// Điểm cao nhất của một mẫu quanh ô đã dò: cắt cửa sổ ĐÚNG cỡ mẫu, canh giữa ô, rồi thử
    /// lệch ±<see cref="ScoreSearchPx"/> pixel mỗi chiều và lấy điểm cao nhất.
    ///
    /// Phải quét lệch chứ không chấm cứng một chỗ, vì đỉnh NCC hẹp đúng một pixel. Đo trên
    /// alt2.png: đúng chỗ 1.000, lệch 1px còn 0.63, lệch 2px còn 0.24 — mà biên khối do
    /// <see cref="Measure"/> đo được xê dịch một hai pixel giữa các khung theo viền răng cưa
    /// và ánh sáng. Chấm cứng một chỗ nghĩa là điểm sụp ngẫu nhiên xuống dưới sàn, đúng cảnh
    /// log 19/08 có cùng một khối cùng toạ độ chấm 0.32 lần này và 0.92 lần khác.
    ///
    /// Cách biệt vẫn giữ: đo trên hai ảnh hiệu chỉnh, nút đúng được 1.000 còn nút sai cao nhất
    /// 0.245. Cùng lối với dải Offsets bên <see cref="ItemCatalog"/>.
    /// </summary>
    private double ScoreAt(MenuHit hit, PillTemplate tpl)
    {
        var band = _band.Region;
        int w = tpl.Width, h = tpl.Height;
        if (w > band.Width || h > band.Height) return -1;

        int cx = hit.Rect.Left + (hit.Rect.Width - w) / 2;
        int cy = hit.Rect.Top + (hit.Rect.Height - h) / 2;

        var scratch = new byte[w * h];
        double best = -1;
        for (int dy = -ScoreSearchPx; dy <= ScoreSearchPx; dy++)
        for (int dx = -ScoreSearchPx; dx <= ScoreSearchPx; dx++)
        {
            int x = Math.Clamp(cx + dx, band.Left, band.Right - w);
            int y = Math.Clamp(cy + dy, band.Top, band.Bottom - h);
            double s = tpl.Score(_band.GrayBuffer(new Rectangle(x, y, w, h), scratch));
            if (s > best) best = s;
        }
        return best;
    }

    /// <summary>Bán kính quét lệch khi chấm điểm, tính theo pixel.</summary>
    private const int ScoreSearchPx = 2;

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
