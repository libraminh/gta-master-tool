using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

internal sealed class ItemGuess
{
    /// <summary>Vật phẩm chấm cao nhất. Luôn có, kể cả khi không đủ tự tin.</summary>
    public string Best { get; init; }
    public double Score { get; init; }
    /// <summary>Vật phẩm về nhì — cần cho cả việc từ chối lẫn việc bỏ qua cách biệt (xem dưới).</summary>
    public string Runner { get; init; }
    public double RunnerScore { get; init; }
    public double Scale { get; init; }
    public int Dx { get; init; }
    public int Dy { get; init; }

    /// <summary>Qua cả hai cổng: điểm sàn và cách biệt với người về nhì.</summary>
    public bool Sure { get; init; }

    /// <summary>Tên chốt được, null nếu chưa đủ tự tin.</summary>
    public string Name => Sure ? Best : null;

    public override string ToString() =>
        (Sure ? $"{Best} {Score:F2}" : $"không rõ (cao nhất {Best ?? "–"} {Score:F2})") +
        (Runner is null ? "" : $", nhì {Runner} {RunnerScore:F2}");
}

/// <summary>
/// Một icon đã thu về đúng cỡ ô, kèm mặt nạ. NCC chỉ chấm trên pixel THUỘC icon.
///
/// Mặt nạ là thứ làm phép so này dùng được: nền ô đổi màu theo độ hiếm (katana nền tím, đá
/// may mắn nền xanh) nên NCC toàn ô sẽ chấm cả cái nền đó và bị nó kéo đi. Icon gốc là RGBA
/// nên biết chính xác pixel nào là đồ, pixel nào là nền.
/// </summary>
internal sealed class IconTemplate
{
    public string Name { get; }
    public double Scale { get; }
    public int Width { get; }
    public int Height { get; }

    private readonly byte[] _gray;
    private readonly bool[] _mask;
    private readonly int _n;
    private readonly double _mean;
    private readonly double _varSum;

    public bool IsFlat => _varSum < 1e-6 || _n < 32;

    public IconTemplate(string name, double scale, int w, int h, byte[] gray, bool[] mask)
    {
        Name = name; Scale = scale; Width = w; Height = h;
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

    /// <summary>Ảnh mẫu để soi bằng mắt: xám là phần được tính điểm, hồng là phần bị mặt nạ bỏ.</summary>
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

    /// <summary>NCC trên vùng mặt nạ. Cùng công thức <see cref="GrayTemplate.Score"/>, khác phạm vi cộng.</summary>
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
}

/// <summary>
/// Nhận ra ô kho đồ đang chứa vật phẩm nào, bằng bộ icon moi từ cache game.
///
/// Game vẽ icon vào ô ở cỡ và vị trí không biết trước, nên phép so phải tự dò lấy cả hai. Làm
/// hai vòng cho khỏi tốn: vòng thô chấm mọi vật phẩm ở vài cỡ với mẫu đặt chính giữa, vòng
/// tinh chỉ lấy mấy ứng viên dẫn đầu rồi dò kỹ cỡ lẫn độ lệch.
/// </summary>
internal sealed class ItemCatalog
{
    /// <summary>
    /// Icon to bằng bao nhiêu lần khung LẤY MẪU (không phải bằng ô).
    ///
    /// Phải vượt 1.0, và đó là điểm dễ hiểu sai nhất ở đây: khung lấy mẫu đã bị
    /// <see cref="FishingConfig.CellInsetFrac"/> co vào 15% mỗi cạnh, chỉ còn 70% ô, trong khi
    /// game vẽ icon gần kín ô. Nghĩa là mẫu chụp được là icon ĐÃ BỊ CẮT VIỀN, nên mẫu đem so
    /// cũng phải vẽ to hơn khung rồi để nó cắt y như vậy — 1/0.7 ≈ 1.43. Dải chỉ tới 1.00 thì
    /// con cá dài nằm ngang mất cả đầu lẫn đuôi và không bao giờ khớp.
    /// </summary>
    private static readonly double[] Scales = { 1.60, 1.45, 1.30, 1.15, 1.00, 0.85, 0.70 };

    /// <summary>Vòng tinh: nấc cỡ dày hơn, quanh vùng thực tế đo được (cá trê thắng ở 1.1).</summary>
    private static readonly double[] FineScales = { 0.70, 0.85, 1.00, 1.10, 1.20, 1.30, 1.45 };

    /// <summary>Độ lệch thử theo cả hai trục. ±2 px là đủ: đo được cá trê chỉ cần đúng (-2,-2).</summary>
    private static readonly int[] Offsets = { -2, 0, 2 };

    /// <summary>Bao nhiêu ứng viên dẫn đầu vòng thô được dò kỹ. Cá trê xếp thứ 3 ở vòng thô.</summary>
    private const int FineCandidates = 8;

    private readonly FishingConfig _cfg;
    private readonly Dictionary<string, Bitmap> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mẫu đã dựng, khoá theo "rộngxcao" của ô — mỗi lưới một cỡ ô riêng.</summary>
    private readonly Dictionary<string, List<IconTemplate>> _built = new();

    /// <summary>
    /// Mẫu vòng tinh, khoá theo tên|cỡ|lệch|kích thước ô.
    ///
    /// Cần đệm vì <see cref="TrunkDumper"/> gọi lại sau MỖI cú kéo: dựng lại 504 mẫu cho từng ô
    /// mỗi lượt thì một lần đổ cốp mất hàng chục giây. Số vật phẩm thật sự lọt vào vòng tinh
    /// chỉ khoảng vài chục nên bộ đệm không phình được bao nhiêu.
    /// </summary>
    private readonly Dictionary<string, IconTemplate> _fine = new();

    public int Count => _icons.Count;
    public IEnumerable<string> Names => _icons.Keys;

    private ItemCatalog(FishingConfig cfg) => _cfg = cfg;

    /// <summary>Nạp mọi PNG trong thư mục icon. Thư mục trống thì trả về bộ rỗng, không ném.</summary>
    public static ItemCatalog Load(FishingConfig cfg)
    {
        var cat = new ItemCatalog(cfg);
        string dir = ItemIconExtractor.ItemDir;
        if (!Directory.Exists(dir)) return cat;

        foreach (string path in Directory.EnumerateFiles(dir, "*.png"))
        {
            try
            {
                // Doc qua bo nho roi dong file: Bitmap(path) giu khoa file suot doi doi tuong,
                // nen ban trich icon lan sau khong ghi de duoc.
                using var tmp = new Bitmap(path);
                var copy = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(copy))
                {
                    g.Clear(Color.Transparent);
                    g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
                }
                cat._icons[Path.GetFileNameWithoutExtension(path)] = copy;
            }
            catch { /* mot file hong khong duoc lam hong ca bo */ }
        }
        return cat;
    }

    // ---------------------------------------------------------------- nhận diện

    /// <summary>
    /// Ô này chứa gì. <paramref name="cellGray"/> là mảng xám của ô, đúng cỡ
    /// <paramref name="w"/>×<paramref name="h"/> mà <see cref="RegionReader.GrayBuffer"/> trả ra.
    /// </summary>
    public ItemGuess Classify(byte[] cellGray, int w, int h)
    {
        var ranked = Rank(cellGray, w, h);
        if (ranked.Count == 0) return new ItemGuess();

        var top = ranked[0];
        var second = ranked.Count > 1 ? ranked[1] : default;

        bool sure = top.Score >= _cfg.ItemNccMin
                    && (ranked.Count < 2 || top.Score - second.Score >= _cfg.ItemMarginMin);

        return new ItemGuess
        {
            Best = top.Name,
            Score = top.Score,
            Runner = ranked.Count > 1 ? second.Name : null,
            RunnerScore = ranked.Count > 1 ? second.Score : -1,
            Scale = top.Scale,
            Dx = top.Dx,
            Dy = top.Dy,
            Sure = sure
        };
    }

    /// <summary>Xếp hạng vật phẩm cho một ô, cao nhất trước.</summary>
    public List<(string Name, double Score, double Scale, int Dx, int Dy)> Top(
        byte[] cellGray, int w, int h, int take) => Rank(cellGray, w, h).Take(take).ToList();

    /// <summary>
    /// Hai vòng. Vòng thô chấm mọi vật phẩm ở vài cỡ, mẫu đặt chính giữa; vòng tinh chỉ lấy
    /// mấy ứng viên dẫn đầu rồi dò kỹ cả cỡ lẫn ĐỘ LỆCH vài pixel.
    ///
    /// Vòng tinh là thứ không thể thiếu, và đo được: trên ô cá trê, mẫu đặt đúng giữa chỉ chấm
    /// 0.45 và xếp sau cả cái máy câu, nhưng xê đi ĐÚNG 2 PIXEL thì lên 0.885 và bỏ xa mọi thứ
    /// khác. Icon mảnh và dài thì lệch một hai pixel là tương quan sụp, nên chỉ dò cỡ không bao
    /// giờ đủ.
    /// </summary>
    private List<(string Name, double Score, double Scale, int Dx, int Dy)> Rank(
        byte[] cellGray, int w, int h)
    {
        var outp = new List<(string, double, double, int, int)>();
        if (cellGray is null || cellGray.Length < w * h || w < 8 || h < 8) return Wrap(outp);

        // Vong tho: chi de chon ung vien, khong de ket luan.
        var coarse = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Build(w, h))
        {
            double s = t.Score(cellGray);
            if (!coarse.TryGetValue(t.Name, out double cur) || s > cur) coarse[t.Name] = s;
        }
        if (coarse.Count == 0) return Wrap(outp);

        foreach (var name in coarse.OrderByDescending(kv => kv.Value).Take(FineCandidates).Select(kv => kv.Key))
        {
            if (!_icons.TryGetValue(name, out var icon)) continue;

            double best = -2, bk = 0; int bdx = 0, bdy = 0;
            foreach (double k in FineScales)
            foreach (int dx in Offsets)
            foreach (int dy in Offsets)
            {
                string key = $"{name}|{k:F2}|{dx}|{dy}|{w}x{h}";
                if (!_fine.TryGetValue(key, out var t))
                {
                    t = Make(name, icon, w, h, k, dx, dy);
                    _fine[key] = t;
                }
                if (t.IsFlat) continue;

                double s = t.Score(cellGray);
                if (s > best) { best = s; bk = k; bdx = dx; bdy = dy; }
            }
            outp.Add((name, best, bk, bdx, bdy));
        }

        outp.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return Wrap(outp);
    }

    private static List<(string Name, double Score, double Scale, int Dx, int Dy)> Wrap(
        List<(string, double, double, int, int)> src) =>
        src.Select(t => (t.Item1, t.Item2, t.Item3, t.Item4, t.Item5)).ToList();

    /// <summary>
    /// Vẽ ra đúng cái mẫu đem so, để đặt cạnh ảnh ô thật mà nhìn. Pixel ngoài mặt nạ tô hồng —
    /// đó là phần KHÔNG được tính điểm, và nhìn thấy nó mới biết mẫu đang phủ lệch chỗ nào.
    /// </summary>
    public Bitmap Render(string name, double scale, int w, int h)
    {
        var t = Build(w, h).FirstOrDefault(
            x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                 && Math.Abs(x.Scale - scale) < 1e-6);
        return t?.ToBitmap();
    }

    // ---------------------------------------------------------------- dựng mẫu

    private static string Key(int w, int h) => $"{w}x{h}";

    private List<IconTemplate> Build(int w, int h)
    {
        string key = Key(w, h);
        if (_built.TryGetValue(key, out var cached)) return cached;

        var list = new List<IconTemplate>(_icons.Count * Scales.Length);
        if (w >= 8 && h >= 8)
            foreach (var kv in _icons)
            foreach (double k in Scales)
            {
                var t = Make(kv.Key, kv.Value, w, h, k);
                if (t is { IsFlat: false }) list.Add(t);
            }

        _built[key] = list;
        return list;
    }

    /// <summary>Dựng mẫu cho một vật phẩm ở cỡ và độ lệch cho trước — dùng khi dò vét bằng tay.</summary>
    public IconTemplate MakeAt(string name, int w, int h, double k, int dx, int dy) =>
        _icons.TryGetValue(name, out var icon) ? Make(name, icon, w, h, k, dx, dy) : null;

    /// <summary>Thu icon về k×cạnh ô, đặt giữa, cắt góc badge số lượng.</summary>
    private IconTemplate Make(string name, Bitmap icon, int w, int h, double k, int dx = 0, int dy = 0)
    {
        int iw = Math.Max(4, (int)Math.Round(w * k));
        int ih = Math.Max(4, (int)Math.Round(h * k));

        using var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(icon, new Rectangle((w - iw) / 2 + dx, (h - ih) / 2 + dy, iw, ih));
        }

        var bd = canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var gray = new byte[w * h];
        var mask = new bool[w * h];
        try
        {
            var row = new byte[bd.Stride];
            // Goc duoi-phai la cho game ve so luong de len icon — cat di y het CellSignature.Build.
            int bw = Math.Clamp((int)Math.Round(w * _cfg.BadgeFrac), 0, w - 1);
            int bh = Math.Clamp((int)Math.Round(h * _cfg.BadgeFrac), 0, h - 1);
            int bx = w - bw, by = h - bh;

            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, bd.Stride);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4;
                    int o = y * w + x;
                    gray[o] = (byte)((row[i + 2] * 30 + row[i + 1] * 59 + row[i] * 11) / 100);
                    mask[o] = row[i + 3] > 128 && !(x >= bx && y >= by);
                }
            }
        }
        finally { canvas.UnlockBits(bd); }

        return new IconTemplate(name, k, w, h, gray, mask);
    }
}
