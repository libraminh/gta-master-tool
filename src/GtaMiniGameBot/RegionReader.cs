using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// Doc pixel cua MOT VUNG NHO tren man hinh.
/// Chu y: khong bao gio chup ca 2560x1440 - chi chup dung vung can (vai chuc KB),
/// nho vay poll duoc 20 lan/giay ma khong lam game giat.
/// </summary>
internal sealed class RegionReader : IPixelSource
{
    public Rectangle Region { get; private set; }

    private Bitmap _bmp;
    private Graphics _g;
    private byte[] _buf = Array.Empty<byte>();   // BGRA
    private int _stride;

    public RegionReader(Rectangle region) => Resize(region);

    /// <summary>
    /// Đệm BGRA thô của lần <see cref="Refresh"/> gần nhất, row-major với <see cref="Stride"/> byte
    /// mỗi hàng. Bộ điều hướng thợ điện đọc thẳng đệm này (qua <c>NavFrame</c>) thay vì xin
    /// <see cref="BgrBuffer()"/>: ở 2K vùng world là 3 triệu pixel mỗi khung, mà bước chép BGRA→BGR
    /// đo được ~4 ms và cấp một mảng 9 MB — cả hai đều vô ích khi detector chỉ cần đọc.
    /// </summary>
    public byte[] Raw => _buf;

    public int Stride => _stride;

    public void Resize(Rectangle region)
    {
        if (region.Width < 1 || region.Height < 1)
            throw new ArgumentException("Vung doc phai co kich thuoc > 0.", nameof(region));
        if (_bmp is not null && Region == region) return;

        _g?.Dispose();
        _bmp?.Dispose();

        Region = region;
        _bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        _g = Graphics.FromImage(_bmp);
        _stride = region.Width * 4;
        _buf = new byte[_stride * region.Height];
    }

    /// <summary>
    /// Doi VI TRI vung doc, giu nguyen kich thuoc va toan bo dem.
    ///
    /// Ton tai rieng thay vi goi <see cref="Resize"/> voi rect moi: Resize so ca Rectangle, nen
    /// chi doi goc thoi la no dispose roi cap lai Bitmap + Graphics + dem. Vong chay bang
    /// Water &amp; Power doi goc MOI KHUNG de cua so chup di theo dau day, nen cap lai moi khung la
    /// dung thu phai tranh. Do duoc: chup cua so 320x320 voi goc di dong het 3.35 ms/lan, con chup
    /// ca ROI 1814x1053 het 16.08 ms/lan — do la ca khoang cach giua 300 luot/giay va 62 luot/giay.
    /// </summary>
    public void MoveWindow(int left, int top)
    {
        if (Region.Left == left && Region.Top == top) return;
        Region = new Rectangle(left, top, Region.Width, Region.Height);
    }

    /// <summary>Chup lai vung nay tu man hinh.</summary>
    public void Refresh()
    {
        _g.CopyFromScreen(Region.Left, Region.Top, 0, 0, Region.Size, CopyPixelOperation.SourceCopy);

        var bd = _bmp.LockBits(new Rectangle(0, 0, Region.Width, Region.Height),
                               ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // LockBits co the tra stride khac (padding) - copy theo tung dong cho an toan
            for (int y = 0; y < Region.Height; y++)
                Marshal.Copy(bd.Scan0 + y * bd.Stride, _buf, y * _stride, _stride);
        }
        finally { _bmp.UnlockBits(bd); }
    }

    private bool TryIndex(int screenX, int screenY, out int i)
    {
        int lx = screenX - Region.Left, ly = screenY - Region.Top;
        if (lx < 0 || ly < 0 || lx >= Region.Width || ly >= Region.Height) { i = 0; return false; }
        i = ly * _stride + lx * 4;
        return true;
    }

    /// <summary>Do sang 0..255 tai 1 diem (toa do man hinh that).</summary>
    public int Gray(int screenX, int screenY)
    {
        if (!TryIndex(screenX, screenY, out int i)) return -1;
        return (_buf[i + 2] * 30 + _buf[i + 1] * 59 + _buf[i] * 11) / 100;
    }

    /// <summary>Do sang trung binh tren mot doan ngang (giam nhieu).</summary>
    public int GrayAvgH(int screenX, int screenY, int halfWidth)
    {
        int sum = 0, n = 0;
        for (int x = screenX - halfWidth; x <= screenX + halfWidth; x++)
        {
            int v = Gray(x, screenY);
            if (v >= 0) { sum += v; n++; }
        }
        return n == 0 ? -1 : sum / n;
    }

    /// <summary>
    /// Dem pixel mau XANH LA trong ca vung. Con so "san luong ca nhan" duoc game
    /// ve bang mau xanh, con "/50" mau trang - nen bo dem nay chi phan anh con so.
    /// </summary>
    public int CountGreen(int minG = 120, int marginOverRed = 30)
    {
        int n = 0;
        for (int i = 0; i + 3 < _buf.Length; i += 4)
        {
            int b = _buf[i], g = _buf[i + 1], r = _buf[i + 2];
            if (g > minG && g > r + marginOverRed && g > b + marginOverRed) n++;
        }
        return n;
    }

    /// <summary>
    /// Dem pixel TRANG (xam trung tinh, sang) trong mot o con.
    /// Dung de biet panel con mo hay khong: chuoi "/50" mau trang chi ton tai khi
    /// panel mo - do duoc 691 pixel luc mo, dung 0 luc dong.
    /// </summary>
    public int CountWhite(Rectangle screenRect, int minBright = 150, int maxChannelSpread = 30)
    {
        int n = 0;
        for (int y = screenRect.Top; y < screenRect.Bottom; y++)
        for (int x = screenRect.Left; x < screenRect.Right; x++)
        {
            if (!TryIndex(x, y, out int i)) continue;
            int b = _buf[i], g = _buf[i + 1], r = _buf[i + 2];
            if (r > minBright && Math.Abs(r - g) < maxChannelSpread && Math.Abs(g - b) < maxChannelSpread) n++;
        }
        return n;
    }

    /// <summary>
    /// Lay mang thang xam cua mot o con, xep row-major - dung de so khop NCC.
    /// Pixel nam ngoai vung dang doc tra ve 0.
    /// </summary>
    public byte[] GrayBuffer(Rectangle screenRect)
        => GrayBuffer(screenRect, new byte[screenRect.Width * screenRect.Height]);

    /// <summary>
    /// Như trên nhưng ghi vào mảng có sẵn — dùng khi phải chấm cùng một cỡ ô nhiều lần liền
    /// (quét lệch vài pixel), để không cấp phát lại mỗi lượt.
    /// </summary>
    public byte[] GrayBuffer(Rectangle screenRect, byte[] into)
    {
        int need = screenRect.Width * screenRect.Height;
        var outp = into is not null && into.Length == need ? into : new byte[need];
        int k = 0;
        for (int y = screenRect.Top; y < screenRect.Bottom; y++)
        for (int x = screenRect.Left; x < screenRect.Right; x++)
        {
            outp[k++] = TryIndex(x, y, out int i)
                ? (byte)((_buf[i + 2] * 30 + _buf[i + 1] * 59 + _buf[i] * 11) / 100)
                : (byte)0;
        }
        return outp;
    }

    /// <summary>
    /// Tỉ lệ hàng (từ dưới lên) có đủ pixel thỏa <paramref name="match"/>.
    /// Dừng khi gặp khoảng trống sau khi đã có fill. -1 nếu vùng rỗng.
    /// </summary>
    public double BottomUpFill01(Func<int, int, int, bool> match, double rowFrac = 0.12)
    {
        int w = Region.Width, h = Region.Height;
        if (w < 1 || h < 1 || _buf.Length == 0) return -1;

        int minPerRow = Math.Max(1, (int)Math.Ceiling(w * rowFrac));
        int filled = 0;
        bool started = false;
        for (int y = h - 1; y >= 0; y--)
        {
            int n = 0;
            int row = y * _stride;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                if (match(_buf[i], _buf[i + 1], _buf[i + 2])) n++;
            }
            if (n >= minPerRow)
            {
                filled++;
                started = true;
            }
            else if (started && n < Math.Max(1, minPerRow / 3))
                break;
        }
        return filled / (double)h;
    }

    /// <summary>
    /// Mask 1/0 cua ca vung theo <paramref name="match"/>, row-major - dung de do
    /// khoi mau (vi du nen nut CAT VAO) khi khong biet truoc no nam o dau.
    /// </summary>
    public byte[] MaskBuffer(Func<int, int, int, bool> match)
    {
        int w = Region.Width, h = Region.Height;
        var outp = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * _stride;
            int k = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                if (match(_buf[i], _buf[i + 1], _buf[i + 2])) outp[k + x] = 1;
            }
        }
        return outp;
    }

    /// <summary>Dem pixel thoa <paramref name="match"/> trong MOT o con (toa do man hinh that).</summary>
    public int CountMatch(Rectangle screenRect, Func<int, int, int, bool> match)
    {
        int n = 0;
        for (int y = screenRect.Top; y < screenRect.Bottom; y++)
        for (int x = screenRect.Left; x < screenRect.Right; x++)
        {
            if (!TryIndex(x, y, out int i)) continue;
            if (match(_buf[i], _buf[i + 1], _buf[i + 2])) n++;
        }
        return n;
    }

    public int CountMatch(Func<int, int, int, bool> match)
    {
        int n = 0;
        for (int i = 0; i + 3 < _buf.Length; i += 4)
        {
            if (match(_buf[i], _buf[i + 1], _buf[i + 2])) n++;
        }
        return n;
    }

    /// <summary>
    /// Ba kênh B,G,R của cả vùng, row-major, 3 byte mỗi pixel. Xem <see cref="IPixelSource"/> để
    /// biết vì sao job điện cần đệm này thay vì gọi <see cref="MaskBuffer"/> nhiều lần.
    /// </summary>
    public byte[] BgrBuffer() => BgrBuffer(null);

    /// <summary>Như trên nhưng ghi vào mảng có sẵn — xem <see cref="IPixelSource.BgrBuffer(byte[])"/>.</summary>
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

    /// <summary>Luu vung dang doc ra file - de debug / doi chieu bang mat.</summary>
    public void SaveDebug(string path) => _bmp.Save(path, ImageFormat.Png);

    public void Dispose()
    {
        _g?.Dispose();
        _bmp?.Dispose();
    }
}
