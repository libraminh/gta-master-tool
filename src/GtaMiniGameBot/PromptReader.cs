namespace GtaMiniGameBot;

/// <summary>Kết quả dò prompt "[E] TƯƠNG TÁC" trên một khung.</summary>
internal sealed class PromptHit
{
    public bool Visible { get; init; }

    /// <summary>Hộp mực nhóm chữ, toạ độ như của nguồn pixel.</summary>
    public Rectangle Rect { get; init; }

    public double Score { get; init; }

    /// <summary>Bảng điểm từng dòng chữ tìm được, để chỉnh ngưỡng bằng số thật.</summary>
    public List<string> Rows { get; init; } = new();

    public override string ToString() =>
        Visible ? $"prompt @{Rect.X},{Rect.Y} ncc={Score:F2}" : "không thấy prompt";
}

/// <summary>
/// Dò prompt "[E] TƯƠNG TÁC" của job Thợ điện.
///
/// Toàn bộ phần khó nằm ở <see cref="PromptLocator"/> — người chơi xác nhận prompt này GIỐNG HỆT
/// prompt "[E] KHAI THÁC" của job thợ mộc, nên dùng chung lõi đó thay vì port bộ dò của bản
/// Python (bộ đó đi tìm một khối TRẮNG ĐẶC, trong khi ô phím ở đây TỐI và chỉ chữ mới trắng).
///
/// Prompt là TÍN HIỆU TỚI NƠI duy nhất được tin. Không đo "còn cách bao nhiêu pixel" như bản
/// Python (<c>arrival_radius_px 2.2</c> trên minimap) — game đã tự trả lời câu hỏi đó bằng chính
/// cái prompt, và câu trả lời của nó thì không bao giờ sai.
///
/// Cảnh ở đây là trạm điện GIỮA TRƯA, bê tông trắng nắng, khác hẳn rừng đêm mà ngưỡng mực của job
/// thợ mộc được đo. Bê tông sáng cũng lọt cửa "mực trắng"; thứ chặn nó là các cửa KÍCH CỠ (cột bê
/// tông cao hơn <c>TextH × 8</c> nên bị loại). Đó là điều <c>--verify-nav</c> phải chứng minh trên
/// ảnh thật trước khi tin.
/// </summary>
internal sealed class PromptReader : IDisposable
{
    private readonly PromptLocator _loc;

    private PromptReader(PromptLocator loc) => _loc = loc;

    public Rectangle Region => _loc.BandRegion;

    public static PromptReader Open(ElectricConfig cfg, Screen screen, ElectricProfile p, out string problem)
        => Create(cfg, p, r => new RegionReader(FishingConfig.ToAbsolute(screen, r)), out problem);

    public static PromptReader ForBitmap(ElectricConfig cfg, ElectricProfile p, Bitmap still, out string problem)
        => Create(cfg, p, r => new BitmapRegion(still, r.ToRectangle()), out problem);

    private static PromptReader Create(ElectricConfig cfg, ElectricProfile p,
                                       Func<FishingRect, IPixelSource> open, out string problem)
    {
        problem = null;
        if (p is null) { problem = "chưa có cấu hình cho màn hình này"; return null; }
        if (!p.PromptReady) { problem = "chưa khoanh mẫu chữ “TƯƠNG TÁC”"; return null; }

        var band = p.ScanPromptBand();
        if (!band.IsSet) { problem = "băng quét prompt quá nhỏ"; return null; }

        string path = ElectricConfig.PromptTemplatePath(p.Key);
        if (!File.Exists(path)) { problem = "chưa có mẫu chữ “TƯƠNG TÁC”"; return null; }

        GrayTemplate tpl;
        try
        {
            tpl = GrayTemplate.FromFile(path);
            if (tpl.IsFlat) { problem = "mẫu “TƯƠNG TÁC” phẳng — khoanh lại lúc prompt đang hiện"; return null; }
        }
        catch (Exception ex) { problem = "mẫu “TƯƠNG TÁC” hỏng: " + ex.Message; return null; }

        IPixelSource src;
        try { src = open(band); }
        catch (Exception ex) { problem = "không mở được băng quét prompt: " + ex.Message; return null; }

        if (tpl.Width > src.Region.Width || tpl.Height > src.Region.Height)
        {
            src.Dispose();
            problem = "mẫu chữ to hơn băng quét — khoanh băng rộng hơn";
            return null;
        }

        return new PromptReader(new PromptLocator(cfg.Nav.PromptTuning(p), src, tpl));
    }

    /// <summary>Một lượt dò. Luôn trả bảng điểm, kể cả khi không khớp.</summary>
    public PromptHit Read()
    {
        var rows = new List<string>();

        double best = -1;
        Rectangle bestRect = Rectangle.Empty;
        bool any = false;

        foreach (var line in _loc.FindLines())
        {
            var (ok, score) = _loc.Match(line);
            rows.Add($"{line}  ncc={score:F2} → {(ok ? "TƯƠNG TÁC" : "–")}");
            if (score > best) { best = score; bestRect = line.Rect; }
            if (ok) any = true;
        }

        return new PromptHit { Visible = any, Rect = bestRect, Score = best, Rows = rows };
    }

    public void Dispose() => _loc?.Dispose();
}
