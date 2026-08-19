using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// Nguồn pixel của một vùng. Có hai bản cài: <see cref="RegionReader"/> đọc từ MÀN HÌNH THẬT, và
/// <see cref="BitmapRegion"/> đọc từ một ảnh tĩnh đã chụp.
///
/// Tồn tại để bộ dò chạy y NGUYÊN MỘT ĐOẠN CODE ở cả hai đường: trong game, và lúc thử lại trên
/// ảnh tĩnh (form hiệu chuẩn, <c>--verify-wood</c>). Viết bản sao cho đường offline thì phép thử
/// hết còn chứng minh được gì về đường thật.
/// </summary>
internal interface IPixelSource : IDisposable
{
    /// <summary>Vùng đang đọc. Toạ độ MÀN HÌNH thật với RegionReader, toạ độ ẢNH với BitmapRegion.</summary>
    Rectangle Region { get; }

    void Refresh();
    byte[] MaskBuffer(Func<int, int, int, bool> match);
    byte[] GrayBuffer(Rectangle rect);
}

/// <summary>
/// Đọc pixel một vùng của ảnh tĩnh, cùng giao diện với <see cref="RegionReader"/>.
///
/// Toạ độ trong ảnh chụp cả màn trùng đúng toạ độ TƯƠNG ĐỐI góc màn (xem
/// <see cref="StillPicker"/>), nên mọi phép tính hình học của bộ dò dùng chung được, không phải
/// quy đổi gì.
/// </summary>
internal sealed class BitmapRegion : IPixelSource
{
    public Rectangle Region { get; }

    private readonly byte[] _buf;   // BGRA, chi vung Region
    private readonly int _stride;

    public BitmapRegion(Bitmap src, Rectangle region)
    {
        var clip = Rectangle.Intersect(region, new Rectangle(0, 0, src.Width, src.Height));
        if (clip.Width < 1 || clip.Height < 1)
            throw new ArgumentException($"Vung {region} nam ngoai anh {src.Width}x{src.Height}", nameof(region));

        Region = clip;
        _stride = clip.Width * 4;
        _buf = new byte[_stride * clip.Height];

        var bd = src.LockBits(clip, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < clip.Height; y++)
                Marshal.Copy(bd.Scan0 + y * bd.Stride, _buf, y * _stride, _stride);
        }
        finally { src.UnlockBits(bd); }
    }

    /// <summary>Ảnh tĩnh không đổi — không có gì để chụp lại.</summary>
    public void Refresh() { }

    private bool TryIndex(int x, int y, out int i)
    {
        int lx = x - Region.Left, ly = y - Region.Top;
        if (lx < 0 || ly < 0 || lx >= Region.Width || ly >= Region.Height) { i = 0; return false; }
        i = ly * _stride + lx * 4;
        return true;
    }

    public byte[] MaskBuffer(Func<int, int, int, bool> match)
    {
        int w = Region.Width, h = Region.Height;
        var outp = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * _stride, k = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                if (match(_buf[i], _buf[i + 1], _buf[i + 2])) outp[k + x] = 1;
            }
        }
        return outp;
    }

    public byte[] GrayBuffer(Rectangle rect)
    {
        var outp = new byte[rect.Width * rect.Height];
        int k = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
        for (int x = rect.Left; x < rect.Right; x++)
        {
            outp[k++] = TryIndex(x, y, out int i)
                ? (byte)((_buf[i + 2] * 30 + _buf[i + 1] * 59 + _buf[i] * 11) / 100)
                : (byte)0;
        }
        return outp;
    }

    public void Dispose() { }
}
