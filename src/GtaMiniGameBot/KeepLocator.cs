using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

internal sealed class KeepHit
{
    /// <summary>O nut do duoc, toa do man hinh that.</summary>
    public Rectangle Rect { get; init; }

    /// <summary>Diem nen click - tam o, keo ve pixel dung mau neu tam bi vat khac de len.</summary>
    public Point Click { get; init; }

    /// <summary>Ti le pixel dung mau trong o, 0..1.</summary>
    public double Density { get; init; }
}

/// <summary>
/// Do nut CAT VAO bang KHOI MAU trong mot vung quet cao, thay cho so khop anh tai
/// mot o co dinh.
///
/// Vi sao khong dung o co dinh: panel nhan ca neo dinh, nen ten ca dai (xuong 2 dong)
/// va mo ta dai day ca hang nut xuong 1-2 dong chu. O co dinh vua tut diem khop vua
/// click vao cho trong.
///
/// Vi sao mau chu khong phai NCC: nen nut la khoi dac mot mau, con canh cau / day cau
/// ve de len nut chi la vet manh - no lam diem NCC tut manh nhung gan nhu khong doi
/// ti le pixel dung mau tren mot o 80x44.
///
/// Dinh vi bang phep chieu hang/cot, khong can connected-components: hang nao cat qua
/// nut se co gan du W pixel dung mau, hang nen panel thi ~0.
/// </summary>
internal sealed class KeepLocator
{
    private readonly int _tb, _tg, _tr;      // mau dich, thu tu BGR nhu buffer
    private readonly int _tol;
    private readonly double _densityMin;
    private readonly int _refW, _refH;       // kich thuoc nut mau = o Keep da khoanh

    private KeepLocator(Color target, int tol, double densityMin, Size refSize)
    {
        _tb = target.B; _tg = target.G; _tr = target.R;
        _tol = Math.Max(4, tol);
        _densityMin = Math.Clamp(densityMin, 0.05, 1.0);
        _refW = Math.Max(8, refSize.Width);
        _refH = Math.Max(8, refSize.Height);
    }

    /// <summary>Mau nen nut suy ra tu anh mau da khoanh - log ra de nguoi dung doi chieu.</summary>
    public Color Target => Color.FromArgb(_tr, _tg, _tb);

    /// <summary>
    /// Lay mau dich tu chinh anh mau nguoi dung da khoanh (keep.png luu ban MAU,
    /// chi GrayTemplate moi doi sang xam luc doc). Mode cua histogram luong tu hoa
    /// >>3: nen nut ap dao chu trang va vien.
    /// </summary>
    public static Color DominantColor(string pngPath)
    {
        using var bmp = new Bitmap(pngPath);
        return DominantColor(bmp);
    }

    /// <summary>Cung phep do nhung tren anh dang co san trong bo nho.</summary>
    public static Color DominantColor(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new byte[bd.Stride * h];
            Marshal.Copy(bd.Scan0, raw, 0, raw.Length);

            const int Levels = 32;               // 256 >> 3
            var count = new int[Levels * Levels * Levels];
            var sumB = new long[count.Length];
            var sumG = new long[count.Length];
            var sumR = new long[count.Length];

            for (int y = 0; y < h; y++)
            {
                int row = y * bd.Stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    int b = raw[i], g = raw[i + 1], r = raw[i + 2];
                    int bin = ((b >> 3) * Levels + (g >> 3)) * Levels + (r >> 3);
                    count[bin]++;
                    sumB[bin] += b; sumG[bin] += g; sumR[bin] += r;
                }
            }

            int bestBin = 0;
            for (int i = 1; i < count.Length; i++)
                if (count[i] > count[bestBin]) bestBin = i;

            int n = Math.Max(1, count[bestBin]);
            return Color.FromArgb((int)(sumR[bestBin] / n), (int)(sumG[bestBin] / n), (int)(sumB[bestBin] / n));
        }
        finally { bmp.UnlockBits(bd); }
    }

    public static KeepLocator FromTemplateFile(string pngPath, Size refSize, int tol, double densityMin)
        => new(DominantColor(pngPath), tol, densityMin, refSize);

    private bool IsTarget(int b, int g, int r)
        => Math.Abs(b - _tb) <= _tol && Math.Abs(g - _tg) <= _tol && Math.Abs(r - _tr) <= _tol;

    /// <summary>
    /// Quet vung <paramref name="band"/> (da Refresh) tim o nut. Null = khong thay.
    /// </summary>
    public KeepHit Find(RegionReader band)
        => Find(band.MaskBuffer(IsTarget), band.Region.Width, band.Region.Height, band.Region.Location);

    /// <summary>Mask 1/0 tu mot anh co san - de doi chieu nguong mau bang anh chup, khong can man hinh.</summary>
    public byte[] MaskFromBitmap(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new byte[bd.Stride * h];
            Marshal.Copy(bd.Scan0, raw, 0, raw.Length);

            var mask = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * bd.Stride, k = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    if (IsTarget(raw[i], raw[i + 1], raw[i + 2])) mask[k + x] = 1;
                }
            }
            return mask;
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>
    /// Ruot thuat toan tren mask 1/0 row-major. Nhieu o ung vien thi lay o dac mau nhat.
    /// <paramref name="origin"/> la goc tren-trai cua mask trong toa do man hinh.
    /// </summary>
    public KeepHit Find(byte[] mask, int w, int h, Point origin)
    {
        if (w < 4 || h < 4 || mask.Length < w * h) return null;

        var rowCount = new int[h];
        for (int y = 0; y < h; y++)
        {
            int k = y * w, n = 0;
            for (int x = 0; x < w; x++) n += mask[k + x];
            rowCount[y] = n;
        }

        int minRow = Math.Max(4, _refW / 2);
        int hLo = Math.Max(4, (int)(_refH * 0.55));
        int hHi = (int)Math.Ceiling(_refH * 1.8);

        KeepHit best = null;
        int y0 = 0;
        while (y0 < h)
        {
            if (rowCount[y0] < minRow) { y0++; continue; }

            int y1 = y0;
            while (y1 + 1 < h && rowCount[y1 + 1] >= minRow) y1++;

            int runH = y1 - y0 + 1;
            if (runH >= hLo && runH <= hHi)
            {
                var hit = Measure(mask, w, origin, y0, y1);
                if (hit is not null && (best is null || hit.Density > best.Density))
                    best = hit;
            }

            y0 = y1 + 1;
        }

        return best;
    }

    /// <summary>Trong dai hang [y0..y1], lay khoang cot lien tuc dai nhat du dac mau.</summary>
    private KeepHit Measure(byte[] mask, int w, Point origin, int y0, int y1)
    {
        int runH = y1 - y0 + 1;
        int minCol = Math.Max(2, runH / 2);

        int bestX = -1, bestLen = 0, curX = -1, curLen = 0;
        for (int x = 0; x < w; x++)
        {
            int n = 0;
            for (int y = y0; y <= y1; y++) n += mask[y * w + x];

            if (n >= minCol)
            {
                if (curX < 0) { curX = x; curLen = 0; }
                curLen++;
                if (curLen > bestLen) { bestLen = curLen; bestX = curX; }
            }
            else
            {
                curX = -1;
                curLen = 0;
            }
        }

        int wLo = Math.Max(4, (int)(_refW * 0.55));
        int wHi = (int)Math.Ceiling(_refW * 1.8);
        if (bestLen < wLo || bestLen > wHi) return null;

        int on = 0;
        for (int y = y0; y <= y1; y++)
        {
            int k = y * w;
            for (int x = bestX; x < bestX + bestLen; x++) on += mask[k + x];
        }
        double dens = on / (double)(bestLen * runH);
        if (dens < _densityMin) return null;

        // Tam o co the bi canh cau de len -> keo ve pixel dung mau gan tam nhat,
        // nhu vay diem click chac chan nam tren nut.
        double cx = bestX + (bestLen - 1) / 2.0;
        double cy = y0 + (runH - 1) / 2.0;
        int pickX = (int)Math.Round(cx), pickY = (int)Math.Round(cy);
        if (mask[pickY * w + pickX] == 0)
        {
            double bestD = double.MaxValue;
            for (int y = y0; y <= y1; y++)
            {
                int k = y * w;
                for (int x = bestX; x < bestX + bestLen; x++)
                {
                    if (mask[k + x] == 0) continue;
                    double dx = x - cx, dy = y - cy, d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; pickX = x; pickY = y; }
                }
            }
        }

        return new KeepHit
        {
            Rect = new Rectangle(origin.X + bestX, origin.Y + y0, bestLen, runH),
            Click = new Point(origin.X + pickX, origin.Y + pickY),
            Density = dens
        };
    }
}
