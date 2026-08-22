using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

internal enum CellState
{
    Empty,
    Occupied
}

internal sealed class CellInfo
{
    public int Index { get; init; }
    /// <summary>Ô đã co vào, toạ độ màn hình thật.</summary>
    public Rectangle Rect { get; init; }
    public Point Centre { get; init; }
    public CellState State { get; init; }
    public double Chroma { get; init; }
    public double Std { get; init; }

    /// <summary>
    /// Đọc ra trống, nhưng lệch cao đáng ngờ — gần chắc là ô CÓ đồ mà icon chưa tải xong.
    /// Không đổi <see cref="IsEmpty"/>: đây chỉ là tín hiệu "quét lại đi", xem
    /// <see cref="FishingConfig.CellFaintStdMin"/>.
    /// </summary>
    public bool Faint { get; init; }

    public bool IsEmpty => State == CellState.Empty;

    public override string ToString() =>
        $"#{Index,-2} {(IsEmpty ? Faint ? "nhạt  " : "trống " : "có đồ")}   " +
        $"màu={Chroma:F3} lệch={Std:F1}";
}

/// <summary>
/// Phép đo "ô này có đồ hay không". Chỉ có vậy — bot KHÔNG nhận diện icon.
///
/// Bản đầu nhận diện từng icon theo mẫu người dùng gán nhãn, để phân biệt cá với cần câu và
/// mồi. An toàn nhưng tốn công: mỗi loại cá mới lại phải dạy, và phải dạy riêng cho từng lưới
/// vì icon co giãn theo kích thước ô. Người dùng chọn cách khác — luôn để cá ở một ô cố định và
/// khai báo ô đó — nên toàn bộ phần so mẫu biến mất, chỉ còn phép đo trống/có đồ.
/// </summary>
internal static class CellSignature
{
    /// <summary>Mảng xám của ô, cắt bỏ góc dưới-phải nơi game vẽ số lượng.</summary>
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

/// <summary>Quét một lưới ô, từ màn hình thật hoặc từ ảnh tĩnh.</summary>
internal sealed class GridScanner : IDisposable
{
    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly GridSpec _grid;
    private RegionReader _reader;

    public GridScanner(FishingConfig cfg, Screen screen, GridSpec grid)
    {
        _cfg = cfg;
        _screen = screen;
        _grid = grid;
    }

    public int Count => _grid.Count;
    public Size CellSize => _grid.CellInset(_screen, 0, _cfg.CellInsetFrac).Size;

    public Rectangle Cell(int index) => _grid.CellInset(_screen, index, _cfg.CellInsetFrac);

    public List<CellInfo> ScanScreen() => ScanScreenPixels().Select(p => p.Cell).ToList();

    /// <summary>
    /// Quét lưới và GIỮ LUÔN mảng xám từng ô. Phép đo trống/có đồ chỉ cần độ lệch chuẩn, nhưng
    /// nhận diện icon thì cần chính mảng pixel đó — chụp lại lần nữa vừa tốn vừa có nguy cơ ảnh
    /// đã đổi giữa hai lần chụp.
    /// </summary>
    public List<(CellInfo Cell, byte[] Gray)> ScanScreenPixels()
    {
        var area = FishingConfig.ToAbsolute(_screen, _grid.Area);
        _reader ??= new RegionReader(area);
        _reader.Refresh();

        var outp = new List<(CellInfo, byte[])>(_grid.Count);
        for (int i = 0; i < _grid.Count; i++)
        {
            var cell = Cell(i);
            var gray = _reader.GrayBuffer(cell);
            int chroma = _reader.CountMatch(cell, CellSignature.IsChromatic);
            outp.Add((Classify(i, cell, gray, cell.Width, cell.Height,
                               chroma / (double)Math.Max(1, cell.Width * cell.Height)), gray));
        }
        return outp;
    }

    /// <summary>Đọc một ô duy nhất từ màn hình — dùng khi chỉ cần kiểm tra lại sau cú kéo.</summary>
    public CellInfo ScanCell(int index)
    {
        var list = ScanScreen();
        return list.FirstOrDefault(c => c.Index == index);
    }

    /// <summary>Quét trên ảnh tĩnh — tinh chỉnh ngưỡng và chọn ô không cần đứng trong game.</summary>
    public List<CellInfo> ScanStill(Bitmap still) => ScanStillPixels(still).Select(p => p.Cell).ToList();

    /// <summary>Như <see cref="ScanScreenPixels"/> nhưng đọc từ ảnh tĩnh — thử nhận diện nguội.</summary>
    public List<(CellInfo Cell, byte[] Gray)> ScanStillPixels(Bitmap still)
    {
        var outp = new List<(CellInfo, byte[])>(_grid.Count);
        var origin = _screen.Bounds.Location;

        for (int i = 0; i < _grid.Count; i++)
        {
            var cell = Cell(i);
            // Toa do trong anh la toa do TUONG DOI goc man, bo phan offset man hinh di.
            var inImage = new Rectangle(cell.X - origin.X, cell.Y - origin.Y, cell.Width, cell.Height);
            var gray = GlyphSeg.GrayOf(still, inImage, out int w, out int h);
            double chroma = ChromaOf(still, inImage);
            outp.Add((Classify(i, cell, gray, w, h, chroma), gray));
        }
        return outp;
    }

    private CellInfo Classify(int index, Rectangle cell, byte[] gray, int w, int h, double chroma)
    {
        if (gray.Length < w * h || w < 4 || h < 4)
            return new CellInfo { Index = index, Rect = cell, Centre = Mid(cell), State = CellState.Occupied };

        var sig = CellSignature.Build(gray, w, h, _cfg.BadgeFrac);
        double std = CellSignature.StdDev(sig);

        // CHI dua vao do lech chuan. Do tren anh that: o trong 0.5-1.7, o co do 10.7-55.4 —
        // tach sach gap 6 lan. Ti le pixel co mau tung duoc dung kem nhung phai bo: icon can
        // cau gan nhu xam han, do duoc 0.008, sat ngay o trong 0.000, nen no khong phan biet
        // duoc gi ma chi lam nguong them mot con so de dat sai. Van do va ghi lai de chan doan.
        bool empty = std < _cfg.CellEmptyStdMax;

        return new CellInfo
        {
            Index = index,
            Rect = cell,
            Centre = Mid(cell),
            State = empty ? CellState.Empty : CellState.Occupied,
            // Trong ma lech cao la o dang tai icon, khong phai o rong. Chi co nghia khi o doc
            // ra trong — o da co do thi cau hoi "co dang tai khong" chuyen sang cho diem NCC.
            Faint = empty && std >= _cfg.CellFaintStdMin,
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
