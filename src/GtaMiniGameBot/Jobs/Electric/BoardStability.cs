namespace GtaMiniGameBot;

/// <summary>
/// Chữ ký rất nhỏ dùng để chờ bảng vẽ xong. Nó không tham gia dựng tuyến; bản đồ tường đầy đủ
/// vẫn được quét và chứng nhận sau khi hai frame khác nhau có chữ ký ổn định.
/// </summary>
internal sealed class BoardWallSignature
{
    public const int Width = 128;
    public const int Height = 72;

    public long FrameId { get; init; }
    public long Timestamp { get; init; }
    public Mask Wall { get; init; }
    public double Coverage { get; init; }
    public double PanelV { get; init; }

    public static BoardWallSignature Create(byte[] bgra, int sourceWidth, int sourceHeight,
                                            int stride, long frameId, long timestamp)
    {
        if (bgra is null || sourceWidth < 1 || sourceHeight < 1 ||
            stride < sourceWidth * 4 || bgra.Length < stride * sourceHeight)
            throw new ArgumentException("Đệm BGRA của chữ ký bảng không hợp lệ.");

        var hue = new byte[Width * Height];
        var sat = new byte[hue.Length];
        var val = new byte[hue.Length];
        var hist = new long[256];
        int greenSamples = 0;

        for (int y = 0; y < Height; y++)
        {
            int sy = Math.Min(sourceHeight - 1, (int)((y + 0.5) * sourceHeight / Height));
            int row = sy * stride;
            for (int x = 0; x < Width; x++)
            {
                int sx = Math.Min(sourceWidth - 1, (int)((x + 0.5) * sourceWidth / Width));
                int si = row + sx * 4;
                var (h, s, v) = ImageOps.HsvOf(bgra[si], bgra[si + 1], bgra[si + 2]);
                int i = y * Width + x;
                hue[i] = (byte)h;
                sat[i] = (byte)s;
                val[i] = (byte)v;
                if (h is >= 35 and <= 105 && s >= 100)
                {
                    hist[v]++;
                    greenSamples++;
                }
            }
        }

        int vt = greenSamples < 40 ? 58 : Math.Clamp(ImageOps.Otsu(hist), 52, 64);
        var raw = new Mask(Width, Height);
        for (int i = 0; i < raw.Data.Length; i++)
        {
            bool strong = hue[i] is >= 35 and <= 105 && sat[i] >= 120 && val[i] >= vt;
            bool soft = hue[i] is >= 30 and <= 115 && sat[i] >= 78 && val[i] >= Math.Max(34, vt - 10);
            if (strong || soft) raw.Data[i] = 1;
        }

        // Loại nét mạch một pixel nhưng giữ thân bảng: ở thumbnail, 4/9 điểm trong cửa sổ 3x3.
        var dense = ImageOps.BoxAtLeast(raw, 3, 4.0 / 9.0);
        var values = new List<byte>();
        for (int i = 0; i < dense.Data.Length; i++)
            if (dense.Data[i] != 0) values.Add(val[i]);
        values.Sort();

        return new BoardWallSignature
        {
            FrameId = frameId,
            Timestamp = timestamp,
            Wall = dense,
            Coverage = dense.Count / (double)dense.Data.Length,
            PanelV = values.Count == 0 ? 0 : values[values.Count / 2]
        };
    }

    public static bool Stable(BoardWallSignature previous, BoardWallSignature current,
                              out string reason)
    {
        if (previous is null || current is null)
        {
            reason = "cần hai chữ ký";
            return false;
        }
        if (previous.FrameId == current.FrameId)
        {
            reason = "hai mẫu là cùng một frame";
            return false;
        }

        double minV = Math.Min(previous.PanelV, current.PanelV);
        if (minV < 64)
        {
            reason = $"bảng còn mờ (V={minV:F1} < 64)";
            return false;
        }

        double minCoverage = Math.Min(previous.Coverage, current.Coverage);
        double maxCoverage = Math.Max(previous.Coverage, current.Coverage);
        if (minCoverage < 0.20 || maxCoverage > 0.80)
        {
            reason = $"độ che ngoài miền ({minCoverage:P1}–{maxCoverage:P1})";
            return false;
        }
        if (maxCoverage - minCoverage > 0.020)
        {
            reason = $"độ che còn đổi {(maxCoverage - minCoverage):P1}";
            return false;
        }

        int intersection = 0, union = 0;
        for (int i = 0; i < previous.Wall.Data.Length; i++)
        {
            bool a = previous.Wall.Data[i] != 0;
            bool b = current.Wall.Data[i] != 0;
            if (a && b) intersection++;
            if (a || b) union++;
        }
        double iou = intersection / Math.Max(1.0, union);
        if (iou < 0.965)
        {
            reason = $"IoU={iou:F3} < 0.965";
            return false;
        }

        reason = $"2 frame khác nhau ổn định, IoU={iou:F3}, che={current.Coverage:P1}";
        return true;
    }

    public static bool Stable(IReadOnlyList<BoardWallSignature> history, int need,
                              out string reason)
    {
        need = Math.Max(2, need);
        if (history is null || history.Count < need)
        {
            reason = $"cần {need} chữ ký";
            return false;
        }

        int start = history.Count - need;
        for (int i = start + 1; i < history.Count; i++)
        {
            if (!Stable(history[i - 1], history[i], out reason)) return false;
        }

        reason = $"{need} frame khác nhau ổn định; " +
                 $"che={history[^1].Coverage:P1}, V={history[^1].PanelV:F0}";
        return true;
    }
}
