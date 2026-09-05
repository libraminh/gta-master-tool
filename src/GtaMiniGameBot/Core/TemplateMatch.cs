using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// Mau thang xam gan voi MOT O CO DINH tren man hinh, so khop bang
/// normalized cross-correlation (NCC).
///
/// Vi sao NCC chu khong phai dem pixel sang:
///   NCC bat bien voi phep bien doi  s -> a*s + b  (doi sang / doi tuong phan).
///   Cach dem pixel gan-trang truoc day sap vi dung ly do nay: hieu chuan luc
///   13:27 trong game ra "duoi dat = 0", den 15:37 doi anh sang thi cung trang
///   thai do doc ra 5100..6246 -> bot tuong dang ngoi trong xe.
///   NCC so CAU TRUC (tuong quan hinh dang) nen khong bi anh sang keo di.
///
/// Vi o la co dinh nen KHONG can tim kiem - chi so khop tai dung o do.
/// Chi phi: mot luot quet qua mau, vai nghin pixel.
/// </summary>
internal sealed class GrayTemplate
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }          // row-major, Width*Height

    private readonly double _mean;
    private readonly double _varSum;     // Sum (t - mean)^2

    private GrayTemplate(int w, int h, byte[] data)
    {
        Width = w; Height = h; Data = data;

        long sum = 0;
        foreach (byte v in data) sum += v;
        _mean = (double)sum / data.Length;

        double vs = 0;
        foreach (byte v in data) { double d = v - _mean; vs += d * d; }
        _varSum = vs;
    }

    public bool IsFlat => _varSum < 1e-6;

    // ---------------- tao ----------------

    private static byte[] ToGray(Bitmap bmp, out int w, out int h)
    {
        w = bmp.Width; h = bmp.Height;
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new byte[bd.Stride * h];
            Marshal.Copy(bd.Scan0, raw, 0, raw.Length);

            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * bd.Stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    gray[y * w + x] = (byte)((raw[i + 2] * 30 + raw[i + 1] * 59 + raw[i] * 11) / 100);
                }
            }
            return gray;
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>Tao truc tiep tu mang thang xam co san (khong qua file).</summary>
    public static GrayTemplate FromRaw(int width, int height, byte[] gray)
    {
        if (gray.Length != width * height)
            throw new ArgumentException($"Mang {gray.Length} khong khop {width}x{height}");
        return new GrayTemplate(width, height, gray);
    }

    public static GrayTemplate FromFile(string path)
    {
        using var bmp = new Bitmap(path);
        var gray = ToGray(bmp, out int w, out int h);
        return new GrayTemplate(w, h, gray);
    }

    /// <summary>Cat mot o con tu file PNG (dung khi chon o mau tu frame da ghi).</summary>
    public static GrayTemplate FromFileCrop(string path, Rectangle crop)
    {
        using var src = new Bitmap(path);
        return FromBitmapCrop(src, crop);
    }

    /// <summary>
    /// Cat mot o con tu anh da nam trong bo nho - dung khi mau la mot PHAN cua o nguoi dung vua
    /// khoanh (vi du chi lay phan chu, bo cai o phim ra ngoai).
    /// </summary>
    public static GrayTemplate FromBitmapCrop(Bitmap src, Rectangle crop)
    {
        var r = Rectangle.Intersect(crop, new Rectangle(0, 0, src.Width, src.Height));
        if (r.Width < 4 || r.Height < 4)
            throw new ArgumentException($"O cat nam ngoai anh: {crop} vs {src.Width}x{src.Height}");
        using var sub = src.Clone(r, PixelFormat.Format32bppArgb);
        var gray = ToGray(sub, out int w, out int h);
        return new GrayTemplate(w, h, gray);
    }

    /// <summary>Chup ngay tu man hinh - dung cho nut "Chup mau dong ho xe".</summary>
    public static GrayTemplate FromScreen(Rectangle rect)
    {
        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        var gray = ToGray(bmp, out int w, out int h);
        return new GrayTemplate(w, h, gray);
    }

    public void Save(string path)
    {
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new byte[bd.Stride * Height];
            for (int y = 0; y < Height; y++)
            {
                int row = y * bd.Stride;
                for (int x = 0; x < Width; x++)
                {
                    byte v = Data[y * Width + x];
                    int i = row + x * 4;
                    raw[i] = v; raw[i + 1] = v; raw[i + 2] = v; raw[i + 3] = 255;
                }
            }
            Marshal.Copy(raw, 0, bd.Scan0, raw.Length);
        }
        finally { bmp.UnlockBits(bd); }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
    }

    // ---------------- so khop ----------------

    /// <summary>
    /// NCC giua mau nay va mot mang thang xam cung kich thuoc.
    /// Tra ve -1..1;  1 = trung khop hoan toan,  0 = khong lien quan.
    /// </summary>
    public double Score(byte[] sample)
    {
        if (sample.Length != Data.Length || IsFlat) return 0;

        long sSum = 0, sSqSum = 0, cross = 0;
        for (int i = 0; i < Data.Length; i++)
        {
            int s = sample[i];
            sSum += s;
            sSqSum += (long)s * s;
            cross += (long)s * Data[i];
        }

        int n = Data.Length;
        // Sum (s-sMean)(t-tMean) = Sum s*t - tMean * Sum s
        double num = cross - _mean * sSum;
        double sVar = sSqSum - (double)sSum * sSum / n;
        double den = Math.Sqrt(Math.Max(0, sVar) * _varSum);
        return den < 1e-6 ? 0 : num / den;
    }
}
