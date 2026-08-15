using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

internal enum CellState
{
    Empty,
    Keep,
    Fish,
    Unknown
}

internal sealed class CellInfo
{
    public int Index { get; init; }
    /// <summary>Ô đã co vào, toạ độ màn hình thật — cũng là ô dùng để so mẫu.</summary>
    public Rectangle Rect { get; init; }
    public Point Centre { get; init; }
    public CellState State { get; init; }
    public string Name { get; init; }
    public double Score { get; init; } = -1;
    public double Chroma { get; init; }
    public double Std { get; init; }

    public override string ToString()
    {
        string s = State switch
        {
            CellState.Empty => "trống",
            CellState.Keep => "giữ lại: " + Name,
            CellState.Fish => "CÁ: " + Name,
            _ => "ô lạ"
        };
        return $"#{Index,-2} {s,-22} màu={Chroma:F3} lệch={Std:F1}" +
               (Score >= 0 ? $" ncc={Score:F2}" : "");
    }
}

/// <summary>
/// Nhận diện vật phẩm trong lưới kho đồ.
///
/// Cách làm là PHÂN LOẠI TUYỆT ĐỐI, không so với ảnh chụp lúc bắt đầu phiên. So với ảnh nền
/// nghe thì gọn nhưng thủng nhiều chỗ: cá còn sót từ phiên trước đã nằm sẵn trong ảnh nền nên
/// vĩnh viễn vô hình; cá mới chồng vào ô cũ thì không có gì thay đổi để mà thấy; số lượng mồi
/// tụt làm badge dịch chỗ; vệt sáng dưới con trỏ làm một ô luôn khác.
///
/// Thay vào đó mỗi ô được so với hai bộ mẫu người dùng dạy: "giữ lại" (cần câu, katana, mồi)
/// và "cá". Ô có đồ mà không khớp bộ nào thì để nguyên và ghi log — người dùng nói trong ba lô
/// thỉnh thoảng có đồ khác, nên KHÔNG kéo là lựa chọn duy nhất đúng.
/// </summary>
internal static class CellSignature
{
    /// <summary>
    /// Mảng xám của ô, đã CẮT BỎ góc dưới-phải nơi game vẽ số lượng.
    ///
    /// Cắt bỏ chứ không tô đen: tô đen là thêm một vùng hằng số giống hệt nhau vào cả mẫu lẫn
    /// ảnh chụp, làm điểm NCC bị đẩy lên cao giả tạo ở mọi cặp.
    /// </summary>
    public static byte[] Build(byte[] gray, int w, int h, double badgeFrac)
    {
        int bw = Math.Clamp((int)Math.Round(w * badgeFrac), 0, w - 1);
        int bh = Math.Clamp((int)Math.Round(h * badgeFrac), 0, h - 1);
        int x0 = w - bw, y0 = h - bh;

        var outp = new byte[w * h - bw * bh];
        int k = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (x >= x0 && y >= y0) continue;
            outp[k++] = gray[y * w + x];
        }
        return outp;
    }

    public static double StdDev(byte[] buf)
    {
        if (buf.Length == 0) return 0;
        double mean = 0;
        foreach (byte b in buf) mean += b;
        mean /= buf.Length;

        double var = 0;
        foreach (byte b in buf) var += (b - mean) * (b - mean);
        return Math.Sqrt(var / buf.Length);
    }

    /// <summary>Ô trống là mảng xám trung tính và gần phẳng; icon thì bão hoà và lắm chi tiết.</summary>
    public static bool IsChromatic(int b, int g, int r)
    {
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        return max - min > 25;
    }
}

/// <summary>Bộ mẫu icon vật phẩm, tách hai nhóm: giữ lại và cá.</summary>
internal sealed class ItemAtlas
{
    private sealed record Entry(string Name, bool Fish, GrayTemplate Tpl);

    private readonly List<Entry> _entries = new();

    public int KeepCount => _entries.Count(e => !e.Fish);
    public int FishCount => _entries.Count(e => e.Fish);
    public IEnumerable<string> Names(bool fish) => _entries.Where(e => e.Fish == fish).Select(e => e.Name);

    /// <summary>
    /// Mẫu lưu dạng ảnh ô đầy đủ; lúc nạp mới cắt góc badge, đúng như lúc so.
    /// Mẫu lệch kích thước ô hiện tại thì bỏ và báo — lưới bị khoanh lại thì mẫu cũ vô nghĩa.
    /// </summary>
    public static ItemAtlas Load(string key, Size cellSize, double badgeFrac, List<string> notes)
    {
        var atlas = new ItemAtlas();
        foreach (bool fish in new[] { false, true })
        {
            string dir = FishingConfig.ItemDir(key, fish, cellSize);
            if (!Directory.Exists(dir)) continue;

            foreach (string path in Directory.GetFiles(dir, "*.png"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    using var bmp = StillPicker.Load(path);
                    if (bmp is null) continue;
                    if (bmp.Width != cellSize.Width || bmp.Height != cellSize.Height)
                    {
                        notes?.Add($"mẫu “{name}” {bmp.Width}×{bmp.Height} lệch ô {cellSize.Width}×{cellSize.Height} — dạy lại");
                        continue;
                    }

                    var gray = GlyphSeg.GrayOf(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), out int w, out int h);
                    var sig = CellSignature.Build(gray, w, h, badgeFrac);
                    var tpl = GrayTemplate.FromRaw(sig.Length, 1, sig);
                    if (!tpl.IsFlat) atlas._entries.Add(new Entry(name, fish, tpl));
                }
                catch (Exception ex) { notes?.Add($"mẫu “{name}”: {ex.Message}"); }
            }
        }
        return atlas;
    }

    public (string Name, bool Fish, double Score) Best(byte[] signature)
    {
        string name = null;
        bool fish = false;
        double best = -2;
        foreach (var e in _entries)
        {
            double s = e.Tpl.Score(signature);
            if (s <= best) continue;
            best = s;
            name = e.Name;
            fish = e.Fish;
        }
        return (name, fish, best);
    }
}

/// <summary>Quét một lưới ô, từ màn hình thật hoặc từ ảnh tĩnh.</summary>
internal sealed class GridScanner : IDisposable
{
    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly GridSpec _grid;
    private readonly ItemAtlas _atlas;
    private RegionReader _reader;

    public GridScanner(FishingConfig cfg, Screen screen, GridSpec grid, ItemAtlas atlas)
    {
        _cfg = cfg;
        _screen = screen;
        _grid = grid;
        _atlas = atlas;
    }

    public Size CellSize => _grid.CellInset(_screen, 0, _cfg.CellInsetFrac).Size;

    /// <summary>Ô thứ index đã co vào — cũng chính là ô dùng để cắt mẫu.</summary>
    public Rectangle Cell(int index) => _grid.CellInset(_screen, index, _cfg.CellInsetFrac);

    public List<CellInfo> ScanScreen()
    {
        var area = FishingConfig.ToAbsolute(_screen, _grid.Area);
        _reader ??= new RegionReader(area);
        _reader.Refresh();

        var outp = new List<CellInfo>(_grid.Count);
        for (int i = 0; i < _grid.Count; i++)
        {
            var cell = Cell(i);
            var gray = _reader.GrayBuffer(cell);
            int chroma = _reader.CountMatch(cell, CellSignature.IsChromatic);
            outp.Add(Classify(i, cell, gray, cell.Width, cell.Height,
                              chroma / (double)Math.Max(1, cell.Width * cell.Height)));
        }
        return outp;
    }

    /// <summary>Quét trên ảnh tĩnh — dạy mẫu và tinh chỉnh ngưỡng không cần đứng trong game.</summary>
    public List<CellInfo> ScanStill(Bitmap still)
    {
        var outp = new List<CellInfo>(_grid.Count);
        var origin = _screen.Bounds.Location;

        for (int i = 0; i < _grid.Count; i++)
        {
            var cell = Cell(i);
            // Toa do trong anh la toa do TUONG DOI goc man, bo phan offset man hinh di.
            var inImage = new Rectangle(cell.X - origin.X, cell.Y - origin.Y, cell.Width, cell.Height);
            var gray = GlyphSeg.GrayOf(still, inImage, out int w, out int h);
            double chroma = ChromaOf(still, inImage);
            outp.Add(Classify(i, cell, gray, w, h, chroma));
        }
        return outp;
    }

    private CellInfo Classify(int index, Rectangle cell, byte[] gray, int w, int h, double chroma)
    {
        if (gray.Length < w * h || w < 4 || h < 4)
            return new CellInfo { Index = index, Rect = cell, Centre = Mid(cell), State = CellState.Unknown };

        var sig = CellSignature.Build(gray, w, h, _cfg.BadgeFrac);
        double std = CellSignature.StdDev(sig);

        if (chroma < _cfg.CellEmptyChroma01 && std < _cfg.CellEmptyStdMax)
            return new CellInfo
            {
                Index = index, Rect = cell, Centre = Mid(cell),
                State = CellState.Empty, Chroma = chroma, Std = std
            };

        var (name, fish, score) = _atlas.Best(sig);
        var state = score >= _cfg.ItemNccMin
            ? fish ? CellState.Fish : CellState.Keep
            : CellState.Unknown;

        return new CellInfo
        {
            Index = index, Rect = cell, Centre = Mid(cell),
            State = state,
            Name = state == CellState.Unknown ? null : name,
            Score = score,
            Chroma = chroma,
            Std = std
        };
    }

    private static Point Mid(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static double ChromaOf(Bitmap bmp, Rectangle roi)
    {
        var r = Rectangle.Intersect(roi, new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (r.Width < 1 || r.Height < 1) return 0;

        int n = 0;
        var bd = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[r.Width * 4];
            for (int y = 0; y < r.Height; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                for (int x = 0; x < r.Width; x++)
                {
                    int i = x * 4;
                    if (CellSignature.IsChromatic(row[i], row[i + 1], row[i + 2])) n++;
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return n / (double)(r.Width * r.Height);
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
    }
}
