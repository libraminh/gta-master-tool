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

    /// <summary>
    /// Ba kênh B,G,R của CẢ vùng, row-major, 3 byte mỗi pixel.
    ///
    /// Có mặt bên cạnh <see cref="MaskBuffer"/> vì hai job điện phải so vùng đó với NHIỀU màu
    /// (5 màu đầu dây + 5 màu ổ cắm) và phải đọc giá trị V của HSV, chứ không chỉ hỏi một câu
    /// đúng/sai. Đi qua MaskBuffer thì thành 10 lượt quét cả vùng cho mỗi khung; lấy đệm một lần
    /// rồi tính tại chỗ là một lượt.
    /// </summary>
    byte[] BgrBuffer();

    /// <summary>
    /// Như trên nhưng ghi vào mảng CÓ SẴN — cùng lý do với
    /// <see cref="RegionReader.GrayBuffer(Rectangle, byte[])"/>.
    ///
    /// Vòng closed-loop của bảng Water &amp; Power đọc lại ROI mỗi ~2 ms; ở 2560×1440 mỗi lượt là
    /// 5.7 MB, tức cấp phát vài GB mỗi giây nếu lượt nào cũng xin mảng mới. Truyền
    /// <paramref name="into"/> cỡ đúng thì nó dùng lại; sai cỡ hoặc null thì cấp mảng mới.
    ///
    /// CẢNH BÁO: bản dùng lại trả về CHÍNH mảng của người gọi, nên đừng đưa mảng đang giữ làm
    /// khung tham chiếu vào đây — khung tham chiếu phải là bản chụp riêng, không thì phép so
    /// "đã đổi chưa" luôn ra 0.
    /// </summary>
    byte[] BgrBuffer(byte[] into);
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
        Copy(src);
    }

    /// <summary>
    /// Nạp ẢNH KHÁC vào cùng vùng đọc, giữ nguyên đối tượng.
    ///
    /// Có mặt vì <see cref="MarkerReader"/> mang trạng thái LIÊN KHUNG (phép kiểm thị sai so vị
    /// trí khung này với khung trước). Muốn kiểm nó ngoài game thì phải đưa được nhiều khung liên
    /// tiếp qua CÙNG một bộ dò — dựng bộ dò mới cho mỗi ảnh là xoá sạch cái đang cần kiểm.
    /// </summary>
    public void Retarget(Bitmap src)
    {
        if (src.Width < Region.Right || src.Height < Region.Bottom)
            throw new ArgumentException(
                $"Anh {src.Width}x{src.Height} nho hon vung dang doc {Region}", nameof(src));
        Copy(src);
    }

    private void Copy(Bitmap src)
    {
        var bd = src.LockBits(Region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Region.Height; y++)
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

    public byte[] BgrBuffer() => BgrBuffer(null);

    public byte[] BgrBuffer(byte[] into)
    {
        int w = Region.Width, h = Region.Height;
        int need = w * h * 3;
        var outp = into is not null && into.Length == need ? into : new byte[need];
        for (int y = 0; y < h; y++)
        {
            int row = y * _stride, k = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                outp[k + x * 3] = _buf[i];
                outp[k + x * 3 + 1] = _buf[i + 1];
                outp[k + x * 3 + 2] = _buf[i + 2];
            }
        }
        return outp;
    }

    public void Dispose() { }
}
