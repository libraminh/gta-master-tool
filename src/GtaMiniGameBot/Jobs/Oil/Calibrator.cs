using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// Do 4 thanh tien trinh cua minigame.
///
/// Y tuong: thanh la vach DOC, nen neu lay trung binh do sang theo chieu doc
/// suot ca than thanh thi thanh se noi len ro, con nen cat/thang/nha xuong
/// se bi dap di (vi chung khong lien tuc theo chieu doc).
///
/// Roi loc bang mot rang buoc rat manh: 4 cum phai CACH DEU NHAU.
/// Nen dat trong game khong bao gio tinh co tao 4 cum cach deu ~138px.
/// </summary>
internal static class Calibrator
{
    public sealed record Cluster(int Lo, int Hi, int Center, double Peak, double Prominence);

    public sealed class Result
    {
        public bool Ok => Centers is not null;
        public int[] Centers;
        public double Spacing;
        public double Deviation;
        public string Note = "";
        public List<Cluster> Clusters = [];
        public double Median, Max, Threshold;
    }

    // ---------------- lay profile ----------------

    /// <summary>Profile tu file PNG - dung cho kiem chung offline tren ban ghi.</summary>
    public static double[] ProfileFromFile(string path, int x0, int x1, int y0, int y1)
    {
        using var bmp = new Bitmap(path);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                              ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[bd.Stride * bmp.Height];
            Marshal.Copy(bd.Scan0, buf, 0, buf.Length);

            x1 = Math.Min(x1, bmp.Width - 1);
            y1 = Math.Min(y1, bmp.Height - 1);

            var prof = new double[x1 - x0 + 1];
            int rows = y1 - y0 + 1;
            for (int x = x0; x <= x1; x++)
            {
                long s = 0;
                for (int y = y0; y <= y1; y++)
                {
                    int i = y * bd.Stride + x * 4;
                    s += (buf[i + 2] * 30 + buf[i + 1] * 59 + buf[i] * 11) / 100;
                }
                prof[x - x0] = (double)s / rows;
            }
            return prof;
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>Profile tu vung dang doc tren man hinh - dung luc chay that.</summary>
    public static double[] ProfileFromReader(RegionReader r, int x0, int x1, int y0, int y1)
    {
        var prof = new double[x1 - x0 + 1];
        int rows = y1 - y0 + 1;
        for (int x = x0; x <= x1; x++)
        {
            long s = 0;
            for (int y = y0; y <= y1; y++)
            {
                int v = r.Gray(x, y);
                if (v >= 0) s += v;
            }
            prof[x - x0] = (double)s / rows;
        }
        return prof;
    }

    // ---------------- tim cum + rang buoc cach deu ----------------

    public static Result Find(double[] prof, int x0,
                              int expected = 4,
                              double minSpacing = 80, double maxSpacing = 260,
                              double tolerance = 12,
                              double thresholdOverMedian = 15,
                              double minProminence = 10,
                              int minClusterWidth = 4)
    {
        var res = new Result();
        if (prof.Length < 16) { res.Note = "Profile qua ngan."; return res; }

        var sorted = (double[])prof.Clone();
        Array.Sort(sorted);
        res.Median = sorted[sorted.Length / 2];
        res.Max = sorted[^1];

        // NGUONG TINH THEO MEDIAN, KHONG THEO MAX.
        // Neu tinh theo max: frame nao co thanh da chay xong (255) se day nguong
        // len ~156 va loai mat cac thanh con xam (~110) -> chi tim duoc 3/4 thanh.
        res.Threshold = res.Median + thresholdOverMedian;

        // gom cac doan lien tuc vuot nguong
        int start = -1;
        for (int k = 0; k <= prof.Length; k++)
        {
            bool over = k < prof.Length && prof[k] >= res.Threshold;
            if (over) { if (start < 0) start = k; continue; }
            if (start < 0) continue;

            int lo = start, hi = k - 1;
            start = -1;
            if (hi - lo + 1 < minClusterWidth) continue;

            double sw = 0, sx = 0, peak = 0;
            for (int j = lo; j <= hi; j++)
            {
                double w = Math.Max(0, prof[j] - res.Median);   // trong so theo phan noi tren nen
                sw += w; sx += (j + x0) * w;
                peak = Math.Max(peak, prof[j]);
            }
            if (sw <= 0) continue;

            double prom = peak - res.Median;
            if (prom < minProminence) continue;

            res.Clusters.Add(new Cluster(lo + x0, hi + x0, (int)Math.Round(sx / sw), peak, prom));
        }

        if (res.Clusters.Count < expected)
        {
            res.Note = $"THAT BAI: chi tim duoc {res.Clusters.Count} cum, can {expected}.";
            return res;
        }

        // thu moi cap lam 2 diem dau -> suy ra khoang cach -> tim cac diem con lai
        double best = double.MaxValue;
        int[] bestSet = null;
        double bestSpacing = 0;

        for (int i = 0; i < res.Clusters.Count; i++)
        for (int j = i + 1; j < res.Clusters.Count; j++)
        {
            double d = res.Clusters[j].Center - res.Clusters[i].Center;
            if (d < minSpacing || d > maxSpacing) continue;

            var pick = new List<int> { res.Clusters[i].Center };
            double dev = 0;
            bool ok = true;
            for (int n = 1; n < expected; n++)
            {
                double target = res.Clusters[i].Center + n * d;
                Cluster nearest = null;
                double bestErr = double.MaxValue;
                foreach (var c in res.Clusters)
                {
                    double e = Math.Abs(c.Center - target);
                    if (e < bestErr) { bestErr = e; nearest = c; }
                }
                if (nearest is null || bestErr > tolerance) { ok = false; break; }
                dev += bestErr;
                pick.Add(nearest.Center);
            }
            if (!ok || pick.Distinct().Count() != expected) continue;
            if (dev < best) { best = dev; bestSet = [.. pick]; bestSpacing = d; }
        }

        if (bestSet is null)
        {
            res.Note = $"THAT BAI: co {res.Clusters.Count} cum nhung khong nhom nao cach deu nhau.";
            return res;
        }

        Array.Sort(bestSet);
        res.Centers = bestSet;
        res.Spacing = bestSpacing;
        res.Deviation = best;
        res.Note = "OK";
        return res;
    }

    /// <summary>Do truc tiep tu man hinh, dung cau hinh hien tai.</summary>
    public static Result FromScreen(BotConfig cfg, int searchX0 = 280, int searchX1 = 880)
    {
        var region = new Rectangle(searchX0, cfg.BarYTop, searchX1 - searchX0 + 1,
                                   cfg.BarYBottom - cfg.BarYTop + 1);
        using var reader = new RegionReader(region);
        reader.Refresh();
        var prof = ProfileFromReader(reader, searchX0, searchX1, cfg.BarYTop, cfg.BarYBottom);
        return Find(prof, searchX0);
    }
}
