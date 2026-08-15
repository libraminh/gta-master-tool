using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

internal sealed record GlyphBox(Rectangle Box, int Ink);

internal sealed record GlyphGuess(char Ch, double Best, double Second, int W, int H)
{
    public double Margin => Second <= -1 ? 1.0 : Best - Second;
}

/// <summary>
/// Tách chữ số ra khỏi một ô ảnh nhỏ. Chữ sáng trên nền tối nên nhị phân hoá bằng Otsu rồi
/// chiếu theo cột là đủ — nhưng KHÔNG dùng lại được <see cref="Calibrator.Find"/>: sức phân
/// biệt của hàm đó nằm ở ràng buộc các cụm cách đều nhau, mà chữ số tỉ lệ thì không cách đều,
/// và ngưỡng bề rộng tối thiểu 4 px của nó xoá luôn dấu chấm thập phân (chỉ 2–3 px).
/// </summary>
internal static class GlyphSeg
{
    /// <summary>
    /// Otsu, có sàn cứng: nền panel tối nên nếu ô gần như trống thì Otsu sẽ chọn một ngưỡng
    /// rất thấp và biến nhiễu thành chữ.
    /// </summary>
    public static byte[] Binarize(byte[] gray, int floorGray, out int threshold)
    {
        var hist = new int[256];
        foreach (var g in gray) hist[g]++;

        int total = gray.Length;
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (double)hist[i];

        double sumB = 0;
        int wB = 0;
        double maxVar = -1;
        int best = floorGray;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;

            sumB += t * (double)hist[t];
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;
            double v = (double)wB * wF * (mB - mF) * (mB - mF);
            if (v > maxVar) { maxVar = v; best = t; }
        }

        threshold = Math.Clamp(best, floorGray, 240);
        var bin = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
            bin[i] = gray[i] > threshold ? (byte)1 : (byte)0;
        return bin;
    }

    /// <summary>Nhị phân hoá theo ngưỡng cho sẵn — dùng khi người dùng tự kéo thanh ngưỡng.</summary>
    public static byte[] BinarizeAt(byte[] gray, int threshold)
    {
        var bin = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
            bin[i] = gray[i] > threshold ? (byte)1 : (byte)0;
        return bin;
    }

    /// <summary>Các khối chữ, trái→phải, đã bó sát cả chiều ngang lẫn chiều dọc.</summary>
    public static List<GlyphBox> Segment(byte[] bin, int w, int h, int minW, int minInk, int mergeGap)
    {
        var outp = new List<GlyphBox>();
        if (w < 1 || h < 1 || bin.Length < w * h) return outp;

        var col = new int[w];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
                if (bin[row + x] != 0) col[x]++;
        }

        var runs = new List<(int Lo, int Hi)>();
        int start = -1;
        for (int x = 0; x < w; x++)
        {
            if (col[x] > 0) { if (start < 0) start = x; }
            else if (start >= 0) { runs.Add((start, x - 1)); start = -1; }
        }
        if (start >= 0) runs.Add((start, w - 1));

        if (mergeGap > 0)
        {
            var merged = new List<(int Lo, int Hi)>();
            foreach (var r in runs)
            {
                if (merged.Count > 0 && r.Lo - merged[^1].Hi - 1 <= mergeGap)
                    merged[^1] = (merged[^1].Lo, r.Hi);
                else
                    merged.Add(r);
            }
            runs = merged;
        }

        foreach (var (lo, hi) in runs)
        {
            int top = -1, bot = -1, ink = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                bool any = false;
                for (int x = lo; x <= hi; x++)
                    if (bin[row + x] != 0) { any = true; ink++; }
                if (any) { if (top < 0) top = y; bot = y; }
            }
            if (top < 0) continue;

            int bw = hi - lo + 1, bh = bot - top + 1;
            if (bw < minW || ink < minInk) continue;
            outp.Add(new GlyphBox(new Rectangle(lo, top, bw, bh), ink));
        }
        return outp;
    }

    /// <summary>Số pixel chữ của từng cột trong khoảng [lo..hi]. Chỉ số 0 ứng với cột lo.</summary>
    public static int[] ColumnInk(byte[] bin, int w, int h, int lo, int hi)
    {
        var col = new int[hi - lo + 1];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = lo; x <= hi; x++)
                if (bin[row + x] != 0) col[x - lo]++;
        }
        return col;
    }

    /// <summary>Khối bó sát cho một khoảng cột. Null nếu khoảng đó không có chữ nào.</summary>
    public static GlyphBox TightBox(byte[] bin, int w, int h, int lo, int hi)
    {
        if (lo > hi || lo < 0 || hi >= w) return null;

        int top = -1, bot = -1, ink = 0, left = -1, right = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            bool any = false;
            for (int x = lo; x <= hi; x++)
            {
                if (bin[row + x] == 0) continue;
                any = true;
                ink++;
                if (left < 0 || x < left) left = x;
                if (x > right) right = x;
            }
            if (any) { if (top < 0) top = y; bot = y; }
        }
        if (top < 0) return null;
        return new GlyphBox(new Rectangle(left, top, right - left + 1, bot - top + 1), ink);
    }

    /// <summary>Cắt một ô con ra khỏi mảng xám. Pixel ngoài biên đọc ra 0, giống RegionReader.</summary>
    public static byte[] Crop(byte[] gray, int w, int h, int x0, int y0, int cw, int ch)
    {
        var outp = new byte[cw * ch];
        int k = 0;
        for (int y = y0; y < y0 + ch; y++)
        for (int x = x0; x < x0 + cw; x++)
            outp[k++] = x >= 0 && y >= 0 && x < w && y < h ? gray[y * w + x] : (byte)0;
        return outp;
    }

    /// <summary>Mảng xám của một ô con trong Bitmap — để đọc từ ảnh tĩnh, không qua màn hình.</summary>
    public static byte[] GrayOf(Bitmap bmp, Rectangle roi, out int w, out int h)
    {
        var r = Rectangle.Intersect(roi, new Rectangle(0, 0, bmp.Width, bmp.Height));
        w = r.Width;
        h = r.Height;
        if (w < 1 || h < 1) return Array.Empty<byte>();

        var outp = new byte[w * h];
        var bd = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4;
                    outp[y * w + x] = (byte)((row[i + 2] * 30 + row[i + 1] * 59 + row[i] * 11) / 100);
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return outp;
    }
}

/// <summary>
/// Bộ mẫu chữ số người dùng dạy dần. Mỗi ký tự một PNG bó sát, lưu theo độ phân giải.
///
/// Ghép theo BỀ RỘNG TỰ NHIÊN chứ không co giãn về một khung chung: NCC sống bằng cấu trúc
/// nét, kéo một chữ "1" rộng 6 px thành 12 px biến nét mảnh thành mảng dày và mất đúng cái
/// thông tin đang cần. Đổi lại, cổng bề rộng cho không một bộ lọc: "1" và "8" không bao giờ
/// nằm chung danh sách ứng viên.
///
/// Vì NCC đòi hai mảng bằng đúng kích thước, việc canh lề làm ở phía MẪU CHỤP: thử vài offset
/// quanh vị trí glyph rồi lấy điểm cao nhất — không phải sửa gì trong GrayTemplate.
/// </summary>
internal sealed class DigitAtlas
{
    private sealed record Entry(char Ch, GrayTemplate Tpl);

    public const string Classes = "0123456789./";

    private readonly List<Entry> _entries = new();

    public IReadOnlyCollection<char> Known => _entries.Select(e => e.Ch).Distinct().ToList();

    public int Count => _entries.Count;

    /// <summary>Bề rộng mẫu hẹp nhất / rộng nhất — dùng để biết một khối có đang dính hai chữ không.</summary>
    public int MinWidth => _entries.Count == 0 ? 0 : _entries.Min(e => e.Tpl.Width);
    public int MaxWidth => _entries.Count == 0 ? 0 : _entries.Max(e => e.Tpl.Width);

    /// <summary>Kích thước các mẫu đã có của một ký tự — để phát hiện mẫu mới bị gán nhầm nhãn.</summary>
    public IReadOnlyList<(int W, int H)> SizesOf(char ch) =>
        _entries.Where(e => e.Ch == ch).Select(e => (e.Tpl.Width, e.Tpl.Height)).ToList();

    public static DigitAtlas Load(string profileKey)
    {
        var atlas = new DigitAtlas();
        string dir = FishingConfig.DigitDir(profileKey);
        if (!Directory.Exists(dir)) return atlas;

        foreach (string path in Directory.GetFiles(dir, "*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            // "d7-2.png" = mau thu hai cua chu so 7.
            int dash = name.IndexOf('-');
            string cls = dash > 0 ? name[..dash] : name;
            char ch = FishingConfig.DigitClassChar(cls);
            if (ch == '\0') continue;

            try
            {
                var tpl = GrayTemplate.FromFile(path);
                if (!tpl.IsFlat) atlas._entries.Add(new Entry(ch, tpl));
            }
            catch { /* mau hong thi bo qua, con lai van dung duoc */ }
        }
        return atlas;
    }

    public string MissingText()
    {
        var have = Known.ToHashSet();
        var missing = Classes.Where(c => !have.Contains(c)).ToArray();
        return missing.Length == 0 ? "" : string.Join(" ", missing);
    }

    /// <summary>
    /// Đoán một glyph. Ch = '?' nghĩa là không đủ tự tin — cố ý, vì đọc bừa "12.0" khi thật ra
    /// là "29.0" còn tệ hơn nhiều so với báo không đọc được.
    /// </summary>
    public GlyphGuess Classify(byte[] roiGray, int roiW, int roiH, Rectangle box, FishingConfig cfg)
    {
        var perChar = new Dictionary<char, double>();

        foreach (var e in _entries)
        {
            if (Math.Abs(e.Tpl.Width - box.Width) > cfg.DigitWidthTolPx) continue;
            if (Math.Abs(e.Tpl.Height - box.Height) > cfg.DigitWidthTolPx) continue;

            int x0 = box.X + (box.Width - e.Tpl.Width) / 2;
            int y0 = box.Y + (box.Height - e.Tpl.Height) / 2;

            double s = -2;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var sample = GlyphSeg.Crop(roiGray, roiW, roiH, x0 + dx, y0 + dy, e.Tpl.Width, e.Tpl.Height);
                double v = e.Tpl.Score(sample);
                if (v > s) s = v;
            }

            // Mot ky tu co the co nhieu mau — giu ban tot nhat cua tung ky tu roi moi xep hang,
            // khong thi hai mau cua cung chu so lai tu dong vai "nhat" va "nhi" cho nhau.
            if (!perChar.TryGetValue(e.Ch, out double cur) || s > cur) perChar[e.Ch] = s;
        }

        if (perChar.Count == 0) return new GlyphGuess('?', -2, -2, box.Width, box.Height);

        var ranked = perChar.OrderByDescending(kv => kv.Value).ToList();
        double best = ranked[0].Value;
        char bestCh = ranked[0].Key;
        double second = ranked.Count > 1 ? ranked[1].Value : -2;

        if (best < cfg.DigitNccMin) return new GlyphGuess('?', best, second, box.Width, box.Height);
        if (second > -1 && best - second < cfg.DigitMarginMin)
            return new GlyphGuess('?', best, second, box.Width, box.Height);
        return new GlyphGuess(bestCh, best, second, box.Width, box.Height);
    }

    /// <summary>Lưu một mẫu mới. Đã có mẫu cùng ký tự thì thêm bản phụ, không đè.</summary>
    public static string SaveGlyph(string profileKey, char ch, byte[] gray, int w, int h, bool overwrite)
    {
        string cls = FishingConfig.DigitClassName(ch);
        if (cls is null) throw new ArgumentException($"ký tự '{ch}' không lưu được");

        string dir = FishingConfig.DigitDir(profileKey);
        Directory.CreateDirectory(dir);

        string path = FishingConfig.DigitPath(profileKey, cls);
        if (File.Exists(path) && !overwrite)
        {
            int n = 2;
            while (File.Exists(FishingConfig.DigitPath(profileKey, $"{cls}-{n}"))) n++;
            path = FishingConfig.DigitPath(profileKey, $"{cls}-{n}");
        }

        GrayTemplate.FromRaw(w, h, gray).Save(path);
        return path;
    }
}
