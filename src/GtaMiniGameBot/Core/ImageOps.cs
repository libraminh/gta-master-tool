namespace GtaMiniGameBot;

/// <summary>
/// Mặt nạ 0/1 phẳng, row-major — cùng khuôn dữ liệu mà
/// <see cref="IPixelSource.MaskBuffer"/> trả về, chỉ gắn thêm kích thước để biết đường nào là
/// hàng nào.
/// </summary>
internal sealed class Mask
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>0 hoặc 1, row-major. Dùng byte chứ không bool để cộng dồn được trực tiếp.</summary>
    public byte[] Data { get; }

    public Mask(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new byte[width * height];
    }

    public Mask(int width, int height, byte[] data)
    {
        if (data.Length != width * height)
            throw new ArgumentException($"Mang {data.Length} khong khop {width}x{height}", nameof(data));
        Width = width;
        Height = height;
        Data = data;
    }

    public byte this[int x, int y] => Data[y * Width + x];

    public int Count
    {
        get
        {
            int n = 0;
            foreach (byte v in Data) n += v;
            return n;
        }
    }

    /// <summary>Số pixel bật trong một ô con. Ô nằm ngoài mặt nạ bị cắt, không ném.</summary>
    public int CountIn(Rectangle r)
    {
        var c = Rectangle.Intersect(r, new Rectangle(0, 0, Width, Height));
        if (c.Width <= 0 || c.Height <= 0) return 0;

        int n = 0;
        for (int y = c.Top; y < c.Bottom; y++)
        {
            int row = y * Width;
            for (int x = c.Left; x < c.Right; x++) n += Data[row + x];
        }
        return n;
    }
}

/// <summary>
/// Ba kênh HSV của một vùng, theo ĐÚNG quy ước OpenCV: H 0–179, S và V 0–255.
///
/// Quy ước H nửa vòng là quan trọng chứ không phải tuỳ chọn: mọi ngưỡng màu trong hai bộ solver
/// (H 35–105 cho thân bảng, 65–100 cho đầu dây điện, ≤15 hoặc ≥170 cho đỏ báo lỗi) đều đo trong
/// hệ này. Đổi sang H 0–359 là phải nhân đôi tất cả, và một chỗ quên là một lỗi màu không ai dò ra.
/// </summary>
internal sealed class Hsv
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] H { get; init; }
    public byte[] S { get; init; }
    public byte[] V { get; init; }
}

/// <summary>Bản đồ nhãn của phép tách khối, giữ lại để lọc rồi vẽ khối được chọn trở lại.</summary>
internal sealed class Labeled
{
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>0 = nền; i+1 = khối <c>Blobs[i]</c>.</summary>
    public int[] Label { get; init; }

    public List<Blob> Blobs { get; init; }
}

/// <summary>Một khối liền nhau trong mặt nạ: diện tích, hộp bao, và trọng tâm.</summary>
internal readonly struct Blob
{
    public int Area { get; init; }
    public Rectangle Box { get; init; }
    public double Cx { get; init; }
    public double Cy { get; init; }

    public override string ToString() => $"{Box.Width}×{Box.Height}@{Box.X},{Box.Y} area={Area}";
}

/// <summary>
/// Phép hình thái học và tách khối trên mặt nạ nhị phân.
///
/// Vì sao viết trong nhà chứ không kéo OpenCvSharp về: cùng lý do đã ghi ở
/// <see cref="GrayTemplate"/> — repo này publish single-file self-contained, thêm một bộ native
/// DLL ~40 MB là mất tính chất "giải nén là chạy" của <c>tools/build-portable.ps1</c>. Và phần
/// OpenCV mà bản Python thật sự dùng đến thì đếm được: <c>morphologyEx</c>,
/// <c>connectedComponentsWithStats</c>, và (ở bảng Water &amp; Power) thêm <c>blur</c> /
/// <c>distanceTransform</c>. Ngần đó thì viết tay rẻ hơn là mang cả thư viện.
///
/// Một chỗ thay thế có ý thức: bản Python dò panel bằng <c>findContours</c> +
/// <c>boundingRect(RETR_EXTERNAL)</c>. Ở đây dùng <see cref="Blobs"/> (thành phần liên thông
/// 8-hướng) rồi lấy hộp bao. Với mục đích "tìm hộp bao của mảng viền panel" thì hai cách cho cùng
/// một kết quả, mà thành phần liên thông còn cho luôn diện tích và trọng tâm — hai thứ bản Python
/// phải gọi thêm hàm khác mới có.
/// </summary>
internal static class ImageOps
{
    // ---------------------------------------------------------------- hinh thai hoc

    /// <summary>
    /// Nở mặt nạ bằng nhân CHỮ NHẬT <paramref name="kw"/>×<paramref name="kh"/>.
    ///
    /// Tách làm hai lượt ngang rồi dọc: nhân chữ nhật phân tách được, nên O(w·h) thay vì
    /// O(w·h·kw·kh). Dùng tổng tiền tố từng hàng/cột — cửa sổ có ít nhất một pixel bật thì bật.
    /// </summary>
    public static Mask Dilate(Mask m, int kw, int kh)
    {
        var mid = SweepH(m, Math.Max(1, kw), dilate: true);
        return SweepV(mid, Math.Max(1, kh), dilate: true);
    }

    /// <summary>Co mặt nạ bằng nhân chữ nhật. Cửa sổ phải bật HẾT thì mới bật.</summary>
    public static Mask Erode(Mask m, int kw, int kh)
    {
        var mid = SweepH(m, Math.Max(1, kw), dilate: false);
        return SweepV(mid, Math.Max(1, kh), dilate: false);
    }

    /// <summary>
    /// Đóng (nở rồi co) bằng nhân vuông <paramref name="k"/>. Bản Python gọi
    /// <c>morphologyEx(MORPH_CLOSE, np.ones((5,5)))</c> — đúng phép này với k=5.
    /// </summary>
    public static Mask Close(Mask m, int k)
    {
        k = Math.Max(1, k);
        return Erode(Dilate(m, k, k), k, k);
    }

    /// <summary>Mở (co rồi nở) — dùng để bỏ hạt nhiễu lẻ.</summary>
    public static Mask Open(Mask m, int k)
    {
        k = Math.Max(1, k);
        return Dilate(Erode(m, k, k), k, k);
    }

    /// <summary>
    /// Một lượt ngang. Cửa sổ căn giữa như OpenCV: bán kính <c>k/2</c> mỗi bên với k lẻ; k chẵn
    /// thì lệch về bên phải một pixel, cũng đúng quy ước neo <c>(-1,-1)</c> của OpenCV.
    /// </summary>
    private static Mask SweepH(Mask m, int k, bool dilate)
    {
        int w = m.Width, h = m.Height;
        var outp = new Mask(w, h);
        int before = k / 2, after = k - 1 - before;
        var pre = new int[w + 1];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            pre[0] = 0;
            for (int x = 0; x < w; x++) pre[x + 1] = pre[x] + m.Data[row + x];

            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - before);
                int x1 = Math.Min(w - 1, x + after);
                int sum = pre[x1 + 1] - pre[x0];
                int span = x1 - x0 + 1;
                outp.Data[row + x] = (byte)(dilate ? (sum > 0 ? 1 : 0) : (sum == span ? 1 : 0));
            }
        }
        return outp;
    }

    /// <summary>
    /// Số cột xử lý cùng lúc trong các lượt quét THEO CỘT.
    ///
    /// Đây không phải con số tuỳ ý. Quét một cột nghĩa là mỗi bước nhảy <c>Width</c> byte; với ROI
    /// 2K rộng 1814 thì đó là một cache miss cho mỗi pixel, và đo được: chỉ riêng bước quét tường
    /// mất 642 ms, trong đó phần lớn nằm ở các lượt dọc. Làm 64 cột một lượt thì bảng tổng tiền tố
    /// là 64×(h+1) int ≈ 270 KB — vừa L2 — và cả hai vòng trong đều đi liền ô nhớ.
    /// </summary>
    private const int ColumnBlock = 64;

    private static Mask SweepV(Mask m, int k, bool dilate)
    {
        int w = m.Width, h = m.Height;
        var outp = new Mask(w, h);
        int before = k / 2, after = k - 1 - before;

        int b = Math.Min(ColumnBlock, w);
        var pre = new int[(h + 1) * b];      // pre[y * b + lx] — lx lien o nho

        for (int x0 = 0; x0 < w; x0 += b)
        {
            int bw = Math.Min(b, w - x0);

            for (int lx = 0; lx < bw; lx++) pre[lx] = 0;
            for (int y = 0; y < h; y++)
            {
                int src = y * w + x0;
                int cur = (y + 1) * b, prev = y * b;
                for (int lx = 0; lx < bw; lx++) pre[cur + lx] = pre[prev + lx] + m.Data[src + lx];
            }

            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - before);
                int y1 = Math.Min(h - 1, y + after);
                int span = y1 - y0 + 1;
                int hi = (y1 + 1) * b, lo = y0 * b, dst = y * w + x0;

                for (int lx = 0; lx < bw; lx++)
                {
                    int sum = pre[hi + lx] - pre[lo + lx];
                    outp.Data[dst + lx] = (byte)(dilate ? (sum > 0 ? 1 : 0) : (sum == span ? 1 : 0));
                }
            }
        }
        return outp;
    }

    // ---------------------------------------------------------------- tach khoi

    /// <summary>
    /// Thành phần liên thông 8-hướng, kèm diện tích / hộp bao / trọng tâm — tương đương
    /// <c>cv2.connectedComponentsWithStats(mask, 8)</c>, đã bỏ nhãn nền.
    ///
    /// Hai lượt + union-find theo đường: lượt một gán nhãn tạm và ghi các cặp tương đương, lượt
    /// hai gộp. Với ROI vài trăm nghìn pixel thì rẻ hơn hẳn quét lan (flood fill) đệ quy, và
    /// không có nguy cơ tràn ngăn xếp trên mặt nạ dài liền một dải.
    /// </summary>
    public static List<Blob> Blobs(Mask m, int minArea = 1)
    {
        var lab = Label(m);
        if (minArea <= 1) return lab.Blobs;

        var outp = new List<Blob>();
        foreach (var b in lab.Blobs)
            if (b.Area >= minArea) outp.Add(b);
        return outp;
    }

    /// <summary>
    /// Như <see cref="Blobs"/> nhưng giữ luôn BẢN ĐỒ NHÃN, để lọc khối rồi vẽ những khối được
    /// chọn trở lại thành mặt nạ mới — việc mà bộ dò tường của bảng Water &amp; Power làm liên tục
    /// (<c>clean[labels==i]=255</c> trong bản Python).
    /// </summary>
    /// <remarks>
    /// Toàn bộ phần gom số liệu dùng MẢNG chỉ số, không dùng <c>Dictionary</c>. Bản đầu tiên tra
    /// bảy tự điển cho từng pixel với lý do "nhãn sau khi gộp thưa thớt, mảng thì phí chỗ" — đo
    /// trên ROI 2K thật thì cái "phí chỗ" đó rẻ hơn nhiều so với ~13 triệu lượt băm: chỉ riêng
    /// bước quét tường mất 880 ms, mà nó gọi Label hai lần.
    /// </remarks>
    public static Labeled Label(Mask m)
    {
        int w = m.Width, h = m.Height;
        var label = new int[w * h];

        // Nhieu nhat mot nhan tam moi cho mot nua so pixel (mau ban co), nhung cap dan theo nhu
        // cau: bat dau nho roi nhan doi.
        var parent = new int[Math.Max(16, w + 1)];
        int labelCount = 1;                 // 0 = nen

        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }

        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (a > b) (a, b) = (b, a);
            parent[b] = a;                  // luon gop ve nhan NHO hon, giu thu tu on dinh
        }

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int prev = row - w;
            for (int x = 0; x < w; x++)
            {
                if (m.Data[row + x] == 0) continue;

                // Bon lang gieng da duyet: (x-1,y), (x-1,y-1), (x,y-1), (x+1,y-1).
                int best = 0;

                if (x > 0)
                {
                    int l = label[row + x - 1];
                    if (l != 0) best = l;
                }
                if (y > 0)
                {
                    if (x > 0)
                    {
                        int l = label[prev + x - 1];
                        if (l != 0) { if (best == 0) best = l; else Union(best, l); }
                    }
                    {
                        int l = label[prev + x];
                        if (l != 0) { if (best == 0) best = l; else Union(best, l); }
                    }
                    if (x + 1 < w)
                    {
                        int l = label[prev + x + 1];
                        if (l != 0) { if (best == 0) best = l; else Union(best, l); }
                    }
                }

                if (best == 0)
                {
                    if (labelCount >= parent.Length) Array.Resize(ref parent, parent.Length * 2);
                    best = labelCount++;
                    parent[best] = best;
                }
                label[row + x] = best;
            }
        }

        // Nen nhan goc thua thot thanh chi so lien tuc 1..n, roi gom so lieu vao mang.
        var slot = new int[labelCount];
        int n = 0;
        for (int l = 1; l < labelCount; l++)
            if (Find(l) == l) slot[l] = ++n;

        var area = new int[n + 1];
        var minX = new int[n + 1];
        var minY = new int[n + 1];
        var maxX = new int[n + 1];
        var maxY = new int[n + 1];
        var sumX = new long[n + 1];
        var sumY = new long[n + 1];
        for (int i = 1; i <= n; i++) { minX[i] = int.MaxValue; minY[i] = int.MaxValue; maxX[i] = -1; maxY[i] = -1; }

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int l = label[row + x];
                if (l == 0) continue;

                int id = slot[Find(l)];
                label[row + x] = id;        // ghi de luon: bo duoc mot luot quet rieng

                area[id]++;
                if (x < minX[id]) minX[id] = x;
                if (x > maxX[id]) maxX[id] = x;
                if (y < minY[id]) minY[id] = y;
                if (y > maxY[id]) maxY[id] = y;
                sumX[id] += x;
                sumY[id] += y;
            }
        }

        var blobs = new List<Blob>(n);
        for (int i = 1; i <= n; i++)
        {
            blobs.Add(new Blob
            {
                Area = area[i],
                Box = new Rectangle(minX[i], minY[i], maxX[i] - minX[i] + 1, maxY[i] - minY[i] + 1),
                Cx = sumX[i] / (double)area[i],
                Cy = sumY[i] / (double)area[i]
            });
        }

        return new Labeled { Width = w, Height = h, Label = label, Blobs = blobs };
    }

    /// <summary>Vẽ lại thành mặt nạ những khối thoả <paramref name="keep"/>.</summary>
    public static Mask Keep(Labeled lab, Func<Blob, bool> keep)
    {
        var take = new bool[lab.Blobs.Count + 1];
        for (int i = 0; i < lab.Blobs.Count; i++) take[i + 1] = keep(lab.Blobs[i]);

        var outp = new Mask(lab.Width, lab.Height);
        for (int i = 0; i < outp.Data.Length; i++)
        {
            int l = lab.Label[i];
            if (l != 0 && take[l]) outp.Data[i] = 1;
        }
        return outp;
    }

    // ---------------------------------------------------------------- so mau

    /// <summary>
    /// Lệch lớn nhất trên ba kênh (chuẩn L∞). Bản Python dùng đúng phép này cho màu VIỀN panel:
    /// <c>np.abs(frame - BORDER).max(axis=2) &lt;= tol</c>.
    /// </summary>
    public static int MaxChannelDiff(int b, int g, int r, int tb, int tg, int tr) =>
        Math.Max(Math.Abs(b - tb), Math.Max(Math.Abs(g - tg), Math.Abs(r - tr)));

    /// <summary>
    /// Khoảng cách Euclid trong không gian BGR, BÌNH PHƯƠNG (khỏi tính căn trong vòng lặp pixel).
    /// Bản Python dùng <c>np.linalg.norm</c> cho màu đầu dây / ổ cắm — khác phép L∞ ở trên, và
    /// đừng đổi chỗ hai cái: ngưỡng 42 là đo cho Euclid, đem sang L∞ là nới rộng ngầm.
    /// </summary>
    public static int ColorDist2(int b, int g, int r, int tb, int tg, int tr)
    {
        int db = b - tb, dg = g - tg, dr = r - tr;
        return db * db + dg * dg + dr * dr;
    }

    /// <summary>
    /// Mặt nạ "gần màu này" theo chuẩn L∞ trên đệm BGR của <see cref="IPixelSource.BgrBuffer"/>.
    /// Dùng cho màu VIỀN và NỀN panel — đúng phép bản Python dùng ở đó.
    /// </summary>
    public static Mask MaskLinf(byte[] bgr, int w, int h, int tb, int tg, int tr, int tol)
    {
        var m = new Mask(w, h);
        for (int i = 0, k = 0; k < m.Data.Length; k++, i += 3)
        {
            if (MaxChannelDiff(bgr[i], bgr[i + 1], bgr[i + 2], tb, tg, tr) <= tol) m.Data[k] = 1;
        }
        return m;
    }

    /// <summary>
    /// Mặt nạ "gần màu này" theo khoảng cách Euclid. Dùng cho màu ĐẦU DÂY / Ổ CẮM
    /// (<c>anchor_color_tolerance</c>).
    /// </summary>
    public static Mask MaskEuclid(byte[] bgr, int w, int h, int tb, int tg, int tr, double tol)
    {
        int t2 = (int)Math.Round(tol * tol);
        var m = new Mask(w, h);
        for (int i = 0, k = 0; k < m.Data.Length; k++, i += 3)
        {
            if (ColorDist2(bgr[i], bgr[i + 1], bgr[i + 2], tb, tg, tr) <= t2) m.Data[k] = 1;
        }
        return m;
    }

    /// <summary>Tỉ lệ pixel trong ô con thoả chuẩn L∞ — bản Python gọi nó là <c>bgfrac</c>.</summary>
    public static double FracLinf(byte[] bgr, int w, int h, Rectangle rect, int tb, int tg, int tr, int tol)
    {
        var c = Rectangle.Intersect(rect, new Rectangle(0, 0, w, h));
        if (c.Width <= 0 || c.Height <= 0) return 0.0;

        int n = 0;
        for (int y = c.Top; y < c.Bottom; y++)
        {
            int row = y * w * 3;
            for (int x = c.Left; x < c.Right; x++)
            {
                int i = row + x * 3;
                if (MaxChannelDiff(bgr[i], bgr[i + 1], bgr[i + 2], tb, tg, tr) <= tol) n++;
            }
        }
        return n / (double)(c.Width * c.Height);
    }

    /// <summary>Số pixel trong ô con gần màu này theo Euclid — dùng đếm màu trong ô lấy mẫu slot.</summary>
    public static int CountEuclidIn(byte[] bgr, int w, int h, Rectangle rect,
                                    int tb, int tg, int tr, double tol)
    {
        var c = Rectangle.Intersect(rect, new Rectangle(0, 0, w, h));
        if (c.Width <= 0 || c.Height <= 0) return 0;

        int t2 = (int)Math.Round(tol * tol), n = 0;
        for (int y = c.Top; y < c.Bottom; y++)
        {
            int row = y * w * 3;
            for (int x = c.Left; x < c.Right; x++)
            {
                int i = row + x * 3;
                if (ColorDist2(bgr[i], bgr[i + 1], bgr[i + 2], tb, tg, tr) <= t2) n++;
            }
        }
        return n;
    }

    // ---------------------------------------------------------------- HSV

    /// <summary>
    /// BGR → HSV theo đúng quy ước OpenCV (H 0–179). Xem <see cref="Hsv"/> để biết vì sao quy ước
    /// nửa vòng là bắt buộc ở đây.
    ///
    /// Công thức trùng <c>cv2.cvtColor(..., COLOR_BGR2HSV)</c> cho ảnh 8-bit: V = max, S =
    /// 255·(max−min)/max, H = góc chia đôi rồi làm tròn.
    /// </summary>
    public static Hsv BgrToHsv(byte[] bgr, int w, int h)
    {
        var H = new byte[w * h];
        var S = new byte[w * h];
        var V = new byte[w * h];

        for (int k = 0, i = 0; k < H.Length; k++, i += 3)
        {
            int b = bgr[i], g = bgr[i + 1], r = bgr[i + 2];
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            int d = max - min;

            V[k] = (byte)max;
            S[k] = max == 0 ? (byte)0 : (byte)((d * 255 + max / 2) / max);

            if (d == 0) { H[k] = 0; continue; }

            // OpenCV tinh H tren thang 0..360 roi chia doi. Lam tron nua-len de khop cv2.
            double hue;
            if (max == r) hue = 60.0 * (g - b) / d;
            else if (max == g) hue = 120.0 + 60.0 * (b - r) / d;
            else hue = 240.0 + 60.0 * (r - g) / d;
            if (hue < 0) hue += 360.0;

            H[k] = (byte)Math.Min(179, (int)Math.Round(hue / 2.0));
        }

        return new Hsv { Width = w, Height = h, H = H, S = S, V = V };
    }

    /// <summary>
    /// HSV của MỘT pixel, cùng quy ước <see cref="BgrToHsv"/>. Dùng khi chỉ cần vài nghìn pixel
    /// trong một ô nhỏ — dựng cả ba mảng cho một dải 121×33 là cấp phát vô ích, mà bộ theo dõi đầu
    /// dây gọi nó vài trăm lần mỗi giây.
    /// </summary>
    public static (int H, int S, int V) HsvOf(int b, int g, int r)
    {
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        int d = max - min;

        int s = max == 0 ? 0 : (d * 255 + max / 2) / max;
        if (d == 0) return (0, s, max);

        double hue;
        if (max == r) hue = 60.0 * (g - b) / d;
        else if (max == g) hue = 120.0 + 60.0 * (b - r) / d;
        else hue = 240.0 + 60.0 * (r - g) / d;
        if (hue < 0) hue += 360.0;

        return (Math.Min(179, (int)Math.Round(hue / 2.0)), s, max);
    }

    // ---------------------------------------------------------------- lam mo

    /// <summary>
    /// Trung bình cửa sổ vuông <paramref name="k"/>×<paramref name="k"/> — tương đương
    /// <c>cv2.blur</c>. Tách hai lượt, dùng tổng tiền tố nên O(w·h) bất kể k.
    ///
    /// Ở biên OpenCV mặc định phản chiếu (BORDER_REFLECT_101); ở đây cắt cửa sổ rồi chia đúng số
    /// pixel thật. Sai khác chỉ nằm trong dải k/2 pixel sát mép ROI, mà cả hai bộ dò đều đã có
    /// lề an toàn rộng hơn thế ở mép bảng.
    /// </summary>
    public static float[] BoxBlur(float[] src, int w, int h, int k)
    {
        k = Math.Max(1, k);
        int before = k / 2, after = k - 1 - before;

        var mid = new float[w * h];
        var pre = new double[w + 1];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            pre[0] = 0;
            for (int x = 0; x < w; x++) pre[x + 1] = pre[x] + src[row + x];
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - before), x1 = Math.Min(w - 1, x + after);
                mid[row + x] = (float)((pre[x1 + 1] - pre[x0]) / (x1 - x0 + 1));
            }
        }

        var outp = new float[w * h];
        var preY = new double[h + 1];
        for (int x = 0; x < w; x++)
        {
            preY[0] = 0;
            for (int y = 0; y < h; y++) preY[y + 1] = preY[y] + mid[y * w + x];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - before), y1 = Math.Min(h - 1, y + after);
                outp[y * w + x] = (float)((preY[y1 + 1] - preY[y0]) / (y1 - y0 + 1));
            }
        }
        return outp;
    }

    /// <summary>
    /// Mặt nạ "mật độ cục bộ trong cửa sổ <paramref name="k"/>×<paramref name="k"/> ≥
    /// <paramref name="frac"/>" — thay cho <c>BoxBlur(mặt nạ 0/1) &gt;= frac</c>.
    ///
    /// Cùng KẾT QUẢ, khác chi phí. Đầu vào của phép lọc mật độ trong bộ dò tường vốn đã là 0/1,
    /// nên đi qua <see cref="BoxBlur"/> là phải dựng hai mảng <c>float</c> 1.9 triệu phần tử và
    /// cộng bằng <c>double</c>; ở đây cộng bằng <c>int</c> và so sánh chéo (<c>sum·... ≥ frac·n</c>)
    /// nên không có phép chia nào trong vòng lặp pixel.
    ///
    /// Mẫu số là số pixel THẬT của cửa sổ đã cắt biên, đúng như <see cref="BoxBlur"/>: bề rộng cắt
    /// chỉ phụ thuộc x và bề cao chỉ phụ thuộc y, nên trung bình hai tầng bằng đúng trung bình 2D.
    /// </summary>
    public static Mask BoxAtLeast(Mask m, int k, double frac)
    {
        k = Math.Max(1, k);
        int w = m.Width, h = m.Height;
        int before = k / 2, after = k - 1 - before;

        // Be rong cua so da cat, chi phu thuoc x.
        var winW = new int[w];
        var x0s = new int[w];
        var x1s = new int[w];
        for (int x = 0; x < w; x++)
        {
            x0s[x] = Math.Max(0, x - before);
            x1s[x] = Math.Min(w - 1, x + after);
            winW[x] = x1s[x] - x0s[x] + 1;
        }

        // Tang 1: tong theo hang.
        var rowSum = new int[w * h];
        var pre = new int[w + 1];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            pre[0] = 0;
            for (int x = 0; x < w; x++) pre[x + 1] = pre[x] + m.Data[row + x];
            for (int x = 0; x < w; x++) rowSum[row + x] = pre[x1s[x] + 1] - pre[x0s[x]];
        }

        // Tang 2: tong theo cot, roi so sanh cheo. Chia khoi cot — xem ColumnBlock.
        var outp = new Mask(w, h);
        int b = Math.Min(ColumnBlock, w);
        var preY = new int[(h + 1) * b];

        for (int x0 = 0; x0 < w; x0 += b)
        {
            int bw = Math.Min(b, w - x0);

            for (int lx = 0; lx < bw; lx++) preY[lx] = 0;
            for (int y = 0; y < h; y++)
            {
                int src = y * w + x0;
                int cur = (y + 1) * b, prev = y * b;
                for (int lx = 0; lx < bw; lx++) preY[cur + lx] = preY[prev + lx] + rowSum[src + lx];
            }

            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - before), y1 = Math.Min(h - 1, y + after);
                int colH = y1 - y0 + 1;
                int hi = (y1 + 1) * b, lo = y0 * b, dst = y * w + x0;

                for (int lx = 0; lx < bw; lx++)
                {
                    int total = preY[hi + lx] - preY[lo + lx];
                    if (total >= frac * ((long)winW[x0 + lx] * colH)) outp.Data[dst + lx] = 1;
                }
            }
        }
        return outp;
    }

    /// <summary>
    /// Trung vị của một kênh 8-bit trên những pixel mà <paramref name="inside"/> bật, tính bằng
    /// histogram 256 ô.
    ///
    /// Bản đầu dồn hết giá trị vào một <c>List&lt;byte&gt;</c> rồi sort — trên ROI 2K với 55% diện
    /// tích là tường thì đó là một triệu phần tử và một lần sort, mỗi khung một lần.
    /// </summary>
    public static double MedianIn(byte[] channel, Mask inside)
    {
        var hist = new int[256];
        int n = 0;
        for (int i = 0; i < inside.Data.Length; i++)
        {
            if (inside.Data[i] == 0) continue;
            hist[channel[i]]++;
            n++;
        }
        if (n == 0) return 0.0;

        // Trung vi cua so chan phan tu = trung binh hai phan tu giua, giu dung nhu ban cu.
        int lower = (n - 1) / 2, upper = n / 2;
        int seen = 0, lo = -1, hi = -1;
        for (int v = 0; v < 256; v++)
        {
            if (hist[v] == 0) continue;
            seen += hist[v];
            if (lo < 0 && seen > lower) lo = v;
            if (hi < 0 && seen > upper) { hi = v; break; }
        }
        return (lo + hi) / 2.0;
    }

    // ---------------------------------------------------------------- no hinh tron

    /// <summary>
    /// Nở bằng ĐĨA bán kính <paramref name="r"/> — tương đương
    /// <c>dilate(..., getStructuringElement(MORPH_ELLIPSE, (2r+1, 2r+1)))</c>.
    ///
    /// Lề an toàn quanh tường PHẢI tròn: nở bằng ô vuông thì góc tường phình thêm r·(√2−1) và bịt
    /// luôn những khe hợp lệ mà A* cần đi qua.
    ///
    /// Cách làm: nở theo đĩa CHÍNH LÀ "mọi pixel cách tường ≤ r", nên nó là một phép ngưỡng trên
    /// <see cref="Clearance"/>. Cách tô đĩa tại từng pixel tường là O(w·h·r²) — đo trên ROI 2K với
    /// r=24 thì cỡ một tỉ phép ghi, mỗi bán kính một lần, tức không dùng được. Qua distance
    /// transform thì O(w·h) và còn CHÍNH XÁC hơn nhân ellipse rời rạc của OpenCV.
    /// </summary>
    public static Mask DilateEllipse(Mask m, int r)
    {
        r = Math.Max(0, r);
        if (r == 0) return Clone(m);
        return Within(Clearance(m), m.Width, m.Height, r);
    }

    /// <summary>
    /// Mặt nạ "cách tường không quá <paramref name="r"/>", lấy từ bản đồ khoảng thoát đã tính.
    ///
    /// Tách riêng để dựng được nhiều bán kính nở từ MỘT lần distance transform — bộ dựng tuyến thử
    /// bảy bán kính trên cùng một mặt nạ tường, mà DT là phần đắt nhất.
    /// </summary>
    public static Mask Within(float[] clearance, int w, int h, double r)
    {
        var outp = new Mask(w, h);
        for (int i = 0; i < outp.Data.Length; i++)
            if (clearance[i] <= r) outp.Data[i] = 1;
        return outp;
    }

    /// <summary>
    /// Trừ một bán kính khỏi bản đồ khoảng thoát và kẹp ở 0. Với một vật cản được nở tròn
    /// <paramref name="amount"/>, đây là cận dưới bảo thủ của khoảng thoát mới ở mọi pixel không
    /// bị chỉnh sửa thủ công sau phép nở.
    /// </summary>
    public static float[] SubtractClearance(float[] clearance, double amount)
    {
        amount = Math.Max(0.0, amount);
        var outp = new float[clearance.Length];
        for (int i = 0; i < outp.Length; i++)
            outp[i] = (float)Math.Max(0.0, clearance[i] - amount);
        return outp;
    }

    // ---------------------------------------------------------------- khoang thoat

    /// <summary>
    /// Khoảng cách Euclid CHÍNH XÁC từ mỗi pixel trống tới pixel bị chặn gần nhất — tương đương
    /// <c>cv2.distanceTransform((mask==0), DIST_L2, 5)</c> mà bản Python dùng để đo "khoảng thoát".
    ///
    /// Thuật toán Felzenszwalb–Huttenlocher: hai lượt bao dưới parabol (cột rồi hàng), O(w·h) và
    /// cho khoảng cách ĐÚNG, không phải xấp xỉ mặt nạ 3×3 hay 5×5. Đây là số liệu mà A* dùng để
    /// tránh men sát tường, nên xấp xỉ ở đây là trả giá bằng việc cắt góc trong game.
    /// </summary>
    public static float[] Clearance(Mask blocked)
    {
        int w = blocked.Width, h = blocked.Height;
        const double Inf = 1e20;

        var f = new double[Math.Max(w, h)];
        var d = new double[Math.Max(w, h)];
        var v = new int[Math.Max(w, h)];
        var z = new double[Math.Max(w, h) + 1];

        // Binh phuong khoang cach, khoi tao 0 tai pixel bi chan.
        var dist = new double[w * h];
        for (int i = 0; i < dist.Length; i++) dist[i] = blocked.Data[i] != 0 ? 0.0 : Inf;

        void Transform(int n)
        {
            int k = 0;
            v[0] = 0;
            z[0] = -Inf;
            z[1] = Inf;

            for (int q = 1; q < n; q++)
            {
                double s;
                while (true)
                {
                    s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k]))
                        / (2.0 * q - 2.0 * v[k]);
                    if (s <= z[k]) k--;
                    else break;
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = Inf;
            }

            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                double dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }

        // Luot theo cot.
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) f[y] = dist[y * w + x];
            Transform(h);
            for (int y = 0; y < h; y++) dist[y * w + x] = d[y];
        }

        // Luot theo hang.
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++) f[x] = dist[row + x];
            Transform(w);
            for (int x = 0; x < w; x++) dist[row + x] = d[x];
        }

        var outp = new float[w * h];
        for (int i = 0; i < outp.Length; i++) outp[i] = (float)Math.Sqrt(dist[i]);
        return outp;
    }

    // ---------------------------------------------------------------- doi co

    /// <summary>Thu nhỏ mặt nạ theo pixel gần nhất — <c>cv2.resize(..., INTER_NEAREST)</c>.</summary>
    public static Mask ResizeNearest(Mask m, int w2, int h2)
    {
        var outp = new Mask(Math.Max(1, w2), Math.Max(1, h2));
        for (int y = 0; y < outp.Height; y++)
        {
            int sy = Math.Min(m.Height - 1, (int)((y + 0.5) * m.Height / outp.Height));
            for (int x = 0; x < outp.Width; x++)
            {
                int sx = Math.Min(m.Width - 1, (int)((x + 0.5) * m.Width / outp.Width));
                outp.Data[y * outp.Width + x] = m.Data[sy * m.Width + sx];
            }
        }
        return outp;
    }

    /// <summary>
    /// Thu nhỏ mảng thực bằng TRUNG BÌNH vùng — <c>cv2.resize(..., INTER_AREA)</c>. Dùng khi hạ
    /// bản đồ khoảng thoát xuống lưới A*: lấy pixel gần nhất ở đó sẽ bỏ mất đúng cái khe hẹp mà
    /// giá rủi ro cần thấy.
    /// </summary>
    public static float[] ResizeArea(float[] src, int w, int h, int w2, int h2)
    {
        w2 = Math.Max(1, w2);
        h2 = Math.Max(1, h2);
        var outp = new float[w2 * h2];

        for (int y = 0; y < h2; y++)
        {
            int y0 = y * h / h2, y1 = Math.Max(y0 + 1, (y + 1) * h / h2);
            for (int x = 0; x < w2; x++)
            {
                int x0 = x * w / w2, x1 = Math.Max(x0 + 1, (x + 1) * w / w2);
                double sum = 0;
                int n = 0;
                for (int yy = y0; yy < y1 && yy < h; yy++)
                {
                    int row = yy * w;
                    for (int xx = x0; xx < x1 && xx < w; xx++) { sum += src[row + xx]; n++; }
                }
                outp[y * w2 + x] = n == 0 ? 0f : (float)(sum / n);
            }
        }
        return outp;
    }

    // ---------------------------------------------------------------- nguong Otsu

    /// <summary>
    /// Ngưỡng Otsu trên histogram 256 mức: chỗ trũng giữa hai đám.
    ///
    /// Bản Python (v72) chuyển sang cách này thay vì suy ngưỡng từ một TỈ LỆ CHE mong muốn, và
    /// ghi rõ lý do: ép mọi bảng về cùng một tỉ lệ che làm phần tối của một thân bảng thật bị
    /// dán nhãn "chỗ trống" trên những bản đồ nhiều tường hơn mức trung bình.
    /// </summary>
    public static int Otsu(long[] hist)
    {
        double total = 0, sumAll = 0;
        for (int t = 0; t < hist.Length; t++) { total += hist[t]; sumAll += (double)t * hist[t]; }
        if (total <= 0) return 0;

        double wB = 0, sumB = 0, best = -1;
        int bestT = 0;
        for (int t = 0; t < hist.Length; t++)
        {
            wB += hist[t];
            if (wB <= 0) continue;
            double wF = total - wB;
            if (wF <= 0) break;

            sumB += (double)t * hist[t];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double score = wB * wF * (mB - mF) * (mB - mF);
            if (score > best) { best = score; bestT = t; }
        }
        return bestT;
    }

    // ---------------------------------------------------------------- to ve tren mat na

    public static Mask Or(Mask a, Mask b)
    {
        var outp = new Mask(a.Width, a.Height);
        for (int i = 0; i < outp.Data.Length; i++)
            outp.Data[i] = (byte)(a.Data[i] != 0 || b.Data[i] != 0 ? 1 : 0);
        return outp;
    }

    public static Mask And(Mask a, Mask b)
    {
        var outp = new Mask(a.Width, a.Height);
        for (int i = 0; i < outp.Data.Length; i++)
            outp.Data[i] = (byte)(a.Data[i] != 0 && b.Data[i] != 0 ? 1 : 0);
        return outp;
    }

    public static Mask Clone(Mask m) => new(m.Width, m.Height, (byte[])m.Data.Clone());

    public static void FillRect(Mask m, Rectangle r, byte value)
    {
        var c = Rectangle.Intersect(r, new Rectangle(0, 0, m.Width, m.Height));
        for (int y = c.Top; y < c.Bottom; y++)
        {
            int row = y * m.Width;
            for (int x = c.Left; x < c.Right; x++) m.Data[row + x] = value;
        }
    }

    public static void FillCircle(Mask m, int cx, int cy, int r, byte value)
    {
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= m.Height) continue;

            double t = (double)r * r - (double)dy * dy;
            if (t < 0) continue;
            int s = (int)Math.Floor(Math.Sqrt(t) + 1e-9);

            int x0 = Math.Max(0, cx - s), x1 = Math.Min(m.Width - 1, cx + s);
            int row = y * m.Width;
            for (int x = x0; x <= x1; x++) m.Data[row + x] = value;
        }
    }

    /// <summary>
    /// Vẽ đoạn thẳng dày <paramref name="thickness"/> — thay cho <c>cv2.line</c>. Tô bằng đĩa tại
    /// từng bước để đoạn dày có đầu tròn, đúng như OpenCV vẽ với LINE_8 + thickness.
    /// </summary>
    public static void DrawThickLine(Mask m, Point a, Point b, int thickness, byte value)
    {
        int r = Math.Max(0, (thickness - 1) / 2);
        double dx = b.X - a.X, dy = b.Y - a.Y;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))));

        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            int x = (int)Math.Round(a.X + dx * t);
            int y = (int)Math.Round(a.Y + dy * t);
            if (r == 0)
            {
                if (x >= 0 && y >= 0 && x < m.Width && y < m.Height) m.Data[y * m.Width + x] = value;
            }
            else FillCircle(m, x, y, r, value);
        }
    }

    /// <summary>
    /// Dựng mặt nạ hành lang dày quanh một đường gấp khúc. Vẽ xương một pixel rồi nở bằng nhân
    /// vuông phân tách được, nên chi phí O(w·h + độ dài đường), không tăng theo bình phương độ dày.
    /// </summary>
    public static Mask PathCorridor(int width, int height, IReadOnlyList<Point> path, int thickness)
    {
        var spine = new Mask(width, height);
        if (path is null || path.Count == 0) return spine;

        thickness = Math.Max(1, thickness);
        if (path.Count == 1)
        {
            if (path[0].X >= 0 && path[0].Y >= 0 && path[0].X < width && path[0].Y < height)
                spine.Data[path[0].Y * width + path[0].X] = 1;
            return Dilate(spine, thickness, thickness);
        }

        for (int i = 1; i < path.Count; i++)
            DrawThickLine(spine, path[i - 1], path[i], 1, 1);
        return Dilate(spine, thickness, thickness);
    }
}
