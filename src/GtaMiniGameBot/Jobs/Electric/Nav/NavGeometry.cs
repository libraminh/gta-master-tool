namespace GtaMiniGameBot;

/// <summary>
/// Một đường biên ngoài (contour) của một khối trong mặt nạ, theo ĐÚNG nghĩa của
/// <c>cv2.findContours(RETR_EXTERNAL, CHAIN_APPROX_SIMPLE)</c>: chuỗi toạ độ pixel biên, đã nén
/// các điểm thẳng hàng.
///
/// Mọi số đo ở đây là số đo TRÊN ĐA GIÁC biên, không phải trên tập pixel — đó là điểm khác quan
/// trọng so với <see cref="Blob"/> của <see cref="ImageOps.Label"/>:
///   - <see cref="Area"/> là diện tích shoelace của đa giác (<c>cv2.contourArea</c>), luôn NHỎ HƠN số
///     pixel: một ô vuông N×N pixel cho (N−1)². Toàn bộ ngưỡng <c>dot_area_*</c> của bản Python đo
///     theo số này, đem số pixel vào là lệch cả bộ lọc.
///   - <see cref="Perimeter"/> là tổng độ dài các bước chuỗi biên (<c>cv2.arcLength(closed)</c>).
///   - Trọng tâm lấy từ moment đa giác (định lý Green), như <c>cv2.moments(cnt)</c>.
/// </summary>
internal sealed class Contour
{
    /// <summary>Điểm biên đã nén kiểu CHAIN_APPROX_SIMPLE, toạ độ trong mặt nạ.</summary>
    public List<Point> Points { get; init; }

    /// <summary><c>cv2.boundingRect</c>: rộng = maxX − minX + 1.</summary>
    public Rectangle Box { get; init; }

    /// <summary><c>cv2.contourArea</c> — shoelace, không dấu.</summary>
    public double Area { get; init; }

    /// <summary><c>cv2.arcLength(cnt, True)</c>.</summary>
    public double Perimeter { get; init; }

    /// <summary>Diện tích bao lồi — <c>cv2.contourArea(cv2.convexHull(cnt))</c>.</summary>
    public double HullArea { get; init; }

    /// <summary>
    /// Có trọng tâm hay không: bản Python bỏ contour có <c>|m00| &lt; 1e-9</c> (điểm đơn, đoạn thẳng).
    /// </summary>
    public bool HasCentroid { get; init; }

    /// <summary>Trọng tâm đa giác (m10/m00, m01/m00), toạ độ trong mặt nạ.</summary>
    public double Cx { get; init; }

    public double Cy { get; init; }

    /// <summary>Số pixel thật của khối — để tiện đối chiếu khi debug, KHÔNG dùng để lọc.</summary>
    public int PixelCount { get; init; }

    public double Circularity => Perimeter > 1e-6 ? 4 * Math.PI * Area / (Perimeter * Perimeter) : 0.0;

    /// <summary><c>area / max(1, bw*bh)</c> — bbox tính bằng PIXEL, như bản Python.</summary>
    public double Fill => Area / Math.Max(1, Box.Width * Box.Height);

    public double Solidity => HullArea > 1e-6 ? Area / HullArea : 0.0;

    /// <summary>
    /// <c>_radial_cv</c> của bản Python: độ lệch chuẩn / trung bình của khoảng cách từ các ĐỈNH đa
    /// giác nén tới tâm. 0 = tròn hoàn hảo. Dưới 5 đỉnh trả 1.0 (coi như xấu).
    /// </summary>
    public double RadialCv(double cx, double cy)
    {
        if (Points.Count < 5) return 1.0;
        double sum = 0;
        var rr = new double[Points.Count];
        for (int i = 0; i < Points.Count; i++)
        {
            double dx = Points[i].X - cx, dy = Points[i].Y - cy;
            rr[i] = Math.Sqrt(dx * dx + dy * dy);
            sum += rr[i];
        }
        double mean = sum / rr.Length;
        if (mean < 1e-4) return 1.0;
        double var = 0;
        foreach (double r in rr) var += (r - mean) * (r - mean);
        return Math.Sqrt(var / rr.Length) / mean;   // numpy.std mặc định ddof=0
    }

    public override string ToString() =>
        $"{Box.Width}×{Box.Height}@{Box.X},{Box.Y} area={Area:F1} per={Perimeter:F1} " +
        $"circ={Circularity:F2} fill={Fill:F2} solid={Solidity:F2}";
}

/// <summary>
/// Phần hình học mà bản Python lấy từ OpenCV còn <see cref="ImageOps"/> chưa có: dò biên contour,
/// bao lồi, moment, và Canny. Viết tay cùng lý do đã ghi ở <see cref="ImageOps"/> — repo này không
/// mang thư viện ảnh native.
/// </summary>
internal static class NavGeometry
{
    // Tam huong lang gieng theo CHIEU KIM DONG HO tren man hinh (y huong xuong):
    // W, NW, N, NE, E, SE, S, SW.
    private static readonly int[] Dx = { -1, -1, 0, 1, 1, 1, 0, -1 };
    private static readonly int[] Dy = { 0, -1, -1, -1, 0, 1, 1, 1 };

    /// <summary>
    /// Đường biên ngoài của MỌI khối 8-liên thông trong mặt nạ — tương đương
    /// <c>cv2.findContours(mask, RETR_EXTERNAL, CHAIN_APPROX_SIMPLE)</c>.
    ///
    /// Cách làm: <see cref="ImageOps.Label"/> tách khối, rồi với mỗi khối dò biên bằng lân cận
    /// Moore từ pixel trên-trái nhất (đúng pixel mà quét raster của OpenCV gặp đầu tiên), dừng theo
    /// tiêu chí Jacob. Lỗ bên trong khối bị bỏ qua đúng như RETR_EXTERNAL. Chuỗi biên có thể đi
    /// qua một pixel hai lần ở chỗ nhánh mỏng — OpenCV cũng vậy, và chu vi/diện tích vì thế trùng.
    /// </summary>
    public static List<Contour> FindContours(Mask m)
    {
        var lab = ImageOps.Label(m);
        var outp = new List<Contour>(lab.Blobs.Count);
        for (int i = 0; i < lab.Blobs.Count; i++)
        {
            var c = Trace(lab, i + 1, lab.Blobs[i]);
            if (c is not null) outp.Add(c);
        }
        return outp;
    }

    private static Contour Trace(Labeled lab, int id, Blob blob)
    {
        int w = lab.Width, h = lab.Height;
        var label = lab.Label;

        // Pixel dau: tren-trai nhat theo thu tu raster trong hop bao.
        int sx = -1, sy = -1;
        for (int y = blob.Box.Top; y < blob.Box.Bottom && sx < 0; y++)
        {
            int row = y * w;
            for (int x = blob.Box.Left; x < blob.Box.Right; x++)
            {
                if (label[row + x] == id) { sx = x; sy = y; break; }
            }
        }
        if (sx < 0) return null;

        bool On(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && label[y * w + x] == id;

        var pts = new List<Point>(Math.Max(8, blob.Box.Width * 2 + blob.Box.Height * 2));
        pts.Add(new Point(sx, sy));

        int px = sx, py = sy;
        int back = 0;                     // huong cua pixel lui (b) nhin tu p: bat dau la W
        int firstStep = -1;
        int guard = 8 * (blob.Box.Width + blob.Box.Height) + 64;

        while (guard-- > 0)
        {
            int found = -1;
            for (int k = 1; k <= 8; k++)
            {
                int d = (back + k) & 7;
                if (On(px + Dx[d], py + Dy[d])) { found = d; break; }
            }
            if (found < 0) break;         // khoi mot pixel

            // Tieu chi dung Jacob: da quay ve diem dau va buoc tiep theo trung buoc dau tien.
            if (px == sx && py == sy && pts.Count > 1 && found == firstStep) break;
            if (firstStep < 0) firstStep = found;

            // Pixel lui moi = lang gieng dung truoc pixel tim duoc (theo chieu kim dong ho quanh p).
            int prevDir = (found + 7) & 7;
            int bx = px + Dx[prevDir], by = py + Dy[prevDir];
            px += Dx[found];
            py += Dy[found];
            pts.Add(new Point(px, py));

            back = DirIndex(bx - px, by - py);
        }

        // Diem cuoi trung diem dau (vong kin) thi bo di.
        if (pts.Count > 1 && pts[pts.Count - 1] == pts[0]) pts.RemoveAt(pts.Count - 1);

        var simple = Simplify(pts);
        double area = Shoelace(simple, out double m00, out double m10, out double m01);
        bool hasC = Math.Abs(m00) >= 1e-9;

        return new Contour
        {
            Points = simple,
            Box = blob.Box,
            Area = area,
            Perimeter = ArcLength(simple),
            HullArea = Shoelace(ConvexHull(simple), out _, out _, out _),
            HasCentroid = hasC,
            Cx = hasC ? m10 / m00 : blob.Cx,
            Cy = hasC ? m01 / m00 : blob.Cy,
            PixelCount = blob.Area
        };
    }

    private static int DirIndex(int dx, int dy)
    {
        for (int d = 0; d < 8; d++) if (Dx[d] == dx && Dy[d] == dy) return d;
        return 0;
    }

    /// <summary>CHAIN_APPROX_SIMPLE: bỏ điểm nằm giữa hai bước cùng hướng.</summary>
    private static List<Point> Simplify(List<Point> pts)
    {
        int n = pts.Count;
        if (n < 3) return new List<Point>(pts);

        var outp = new List<Point>(n);
        for (int i = 0; i < n; i++)
        {
            var a = pts[(i + n - 1) % n];
            var b = pts[i];
            var c = pts[(i + 1) % n];
            int d1x = Math.Sign(b.X - a.X), d1y = Math.Sign(b.Y - a.Y);
            int d2x = Math.Sign(c.X - b.X), d2y = Math.Sign(c.Y - b.Y);
            if (d1x == d2x && d1y == d2y) continue;
            outp.Add(b);
        }
        return outp.Count >= 1 ? outp : new List<Point> { pts[0] };
    }

    /// <summary>
    /// Diện tích shoelace không dấu, kèm ba moment đa giác (định lý Green) — đúng công thức
    /// <c>cv2.moments</c> dùng cho contour: <c>m00 = Σ cross/2</c>, <c>m10 = Σ (x_i+x_{i+1})·cross/6</c>.
    /// </summary>
    public static double Shoelace(List<Point> pts, out double m00, out double m10, out double m01)
    {
        m00 = m10 = m01 = 0;
        int n = pts.Count;
        if (n < 3) return 0.0;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            double cross = (double)a.X * b.Y - (double)b.X * a.Y;
            m00 += cross;
            m10 += (a.X + b.X) * cross;
            m01 += (a.Y + b.Y) * cross;
        }
        m00 /= 2.0;
        m10 /= 6.0;
        m01 /= 6.0;
        return Math.Abs(m00);
    }

    public static double ArcLength(List<Point> pts)
    {
        int n = pts.Count;
        if (n < 2) return 0.0;
        double len = 0;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            len += Math.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }

    /// <summary>Bao lồi (Andrew monotone chain).</summary>
    public static List<Point> ConvexHull(List<Point> pts)
    {
        var p = pts.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        int n = p.Count;
        if (n < 3) return p;

        var hull = new List<Point>(2 * n);
        for (int i = 0; i < n; i++)
        {
            while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p[i]) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p[i]);
        }
        int lower = hull.Count + 1;
        for (int i = n - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p[i]) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static long Cross(Point o, Point a, Point b) =>
        (long)(a.X - o.X) * (b.Y - o.Y) - (long)(a.Y - o.Y) * (b.X - o.X);

    // ---------------------------------------------------------------- thong ke

    /// <summary><c>numpy.percentile(a, q)</c> mặc định — nội suy tuyến tính. <paramref name="sorted"/> phải tăng dần.</summary>
    public static double Percentile(double[] sorted, double q)
    {
        int n = sorted.Length;
        if (n == 0) return double.NaN;
        if (n == 1) return sorted[0];
        double pos = (n - 1) * q / 100.0;
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(n - 1, lo + 1);
        double frac = pos - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }

    public static double Median(IEnumerable<double> values)
    {
        var a = values.ToArray();
        Array.Sort(a);
        return Percentile(a, 50.0);
    }

    // ---------------------------------------------------------------- Canny

    /// <summary>
    /// Ảnh xám của một ô con trong đệm BGRA — trọng số OpenCV <c>0.299/0.587/0.114</c> vì đây là
    /// đầu vào của Canny bản Python (<c>cv2.cvtColor(BGR2GRAY)</c>), khác trọng số 30/59/11 dùng cho
    /// mẫu NCC ở nơi khác trong repo.
    /// </summary>
    public static byte[] GrayOf(byte[] bgra, int stride, Rectangle roi)
    {
        var g = new byte[roi.Width * roi.Height];
        int k = 0;
        for (int y = roi.Top; y < roi.Bottom; y++)
        {
            int row = y * stride;
            for (int x = roi.Left; x < roi.Right; x++)
            {
                int i = row + x * 4;
                g[k++] = (byte)((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114 + 500) / 1000);
            }
        }
        return g;
    }

    /// <summary>
    /// <c>cv2.GaussianBlur(gray, (5,5), 0)</c> rồi <c>cv2.Canny(low, high)</c> (khẩu độ 3, chuẩn L1).
    /// Trả mặt nạ biên 0/1. Viền 2 pixel để 0.
    ///
    /// Chỉ dùng cho <c>ObstacleClassifier</c> — thứ duy nhất nó nuôi là "nửa trái hay nửa phải màn
    /// nhiều biên hơn" để chọn bên thoát kẹt, nên sai khác vài pixel so với OpenCV không đổi kết quả.
    /// </summary>
    public static Mask Canny(byte[] gray, int w, int h, int low, int high)
    {
        var outp = new Mask(w, h);
        if (w < 5 || h < 5) return outp;

        // Gaussian 5x5 sigma=0 cua OpenCV = [1 4 6 4 1]/16, tach hai luot.
        var tmp = new float[w * h];
        var blur = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - 2), x1 = Math.Max(0, x - 1);
                int x3 = Math.Min(w - 1, x + 1), x4 = Math.Min(w - 1, x + 2);
                tmp[row + x] = (gray[row + x0] + 4 * gray[row + x1] + 6 * gray[row + x]
                                + 4 * gray[row + x3] + gray[row + x4]) / 16f;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - 2), y1 = Math.Max(0, y - 1);
            int y3 = Math.Min(h - 1, y + 1), y4 = Math.Min(h - 1, y + 2);
            for (int x = 0; x < w; x++)
                blur[y * w + x] = (tmp[y0 * w + x] + 4 * tmp[y1 * w + x] + 6 * tmp[y * w + x]
                                   + 4 * tmp[y3 * w + x] + tmp[y4 * w + x]) / 16f;
        }

        // Sobel 3x3, do lon L1 (|gx|+|gy|) nhu Canny mac dinh cua OpenCV.
        var mag = new float[w * h];
        var dir = new byte[w * h];   // 0: ngang, 1: cheo /, 2: doc, 3: cheo \
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float gx = (blur[i - w + 1] + 2 * blur[i + 1] + blur[i + w + 1])
                           - (blur[i - w - 1] + 2 * blur[i - 1] + blur[i + w - 1]);
                float gy = (blur[i + w - 1] + 2 * blur[i + w] + blur[i + w + 1])
                           - (blur[i - w - 1] + 2 * blur[i - w] + blur[i - w + 1]);
                mag[i] = Math.Abs(gx) + Math.Abs(gy);
                float ax = Math.Abs(gx), ay = Math.Abs(gy);
                if (ay <= ax * 0.4142f) dir[i] = 0;
                else if (ay >= ax * 2.4142f) dir[i] = 2;
                else dir[i] = (byte)((gx * gy) > 0 ? 3 : 1);
            }
        }

        // Non-maximum suppression + phan loai manh/yeu.
        var strong = new byte[w * h];   // 2 = manh, 1 = yeu
        for (int y = 2; y < h - 2; y++)
        {
            for (int x = 2; x < w - 2; x++)
            {
                int i = y * w + x;
                float m = mag[i];
                if (m < low) continue;
                float n1, n2;
                switch (dir[i])
                {
                    case 0: n1 = mag[i - 1]; n2 = mag[i + 1]; break;
                    case 2: n1 = mag[i - w]; n2 = mag[i + w]; break;
                    case 1: n1 = mag[i - w + 1]; n2 = mag[i + w - 1]; break;
                    default: n1 = mag[i - w - 1]; n2 = mag[i + w + 1]; break;
                }
                if (m < n1 || m < n2) continue;
                strong[i] = (byte)(m >= high ? 2 : 1);
            }
        }

        // Hysteresis: yeu chi giu khi noi (8 huong) toi manh.
        var stack = new Stack<int>();
        for (int i = 0; i < strong.Length; i++)
        {
            if (strong[i] != 2 || outp.Data[i] != 0) continue;
            outp.Data[i] = 1;
            stack.Push(i);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                int cx = c % w, cy = c / w;
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + Dx[d], ny = cy + Dy[d];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (strong[ni] != 0 && outp.Data[ni] == 0)
                    {
                        outp.Data[ni] = 1;
                        stack.Push(ni);
                    }
                }
            }
        }
        return outp;
    }
}
