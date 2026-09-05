namespace GtaMiniGameBot;

/// <summary>Kết quả so mẫu trên một dòng chữ.</summary>
internal sealed class WoodPick
{
    public TextLine Line { get; init; }

    /// <summary>Đúng là prompt "KHAI THÁC" đang sẵn sàng.</summary>
    public bool Ready { get; init; }

    public double Score { get; init; }

    public string Detail => $"{Line}  ncc={Score:F2} → {(Ready ? "SẴN SÀNG" : "–")}";
}

/// <summary>
/// Dò prompt tương tác "[E] KHAI THÁC" / "[40] ĐANG KHAI THÁC" của job thợ mộc.
///
/// Phần khó nằm ở <see cref="PromptLocator"/> — lõi dùng chung với job Thợ điện, và mọi lý lẽ về
/// mực trắng / neo mép trái nhóm chữ / tách khe đều ghi ở đó. Lớp này chỉ còn ba việc riêng của
/// thợ mộc: kiểm tra đã hiệu chuẩn chưa, nạp mẫu "khai thác", và gói điểm NCC thành
/// <see cref="WoodPick"/> với chữ nghĩa của job.
///
/// Vì sao CHỈ MỘT mẫu là đủ, không cần mẫu riêng cho lúc đang chặt: "ĐANG KHAI THÁC" có chứa
/// "KHAI THÁC" thật, nhưng nhóm chữ lúc đó bắt đầu từ "ĐANG" (đo được x=331 so với x=327 lúc
/// sẵn sàng). Mẫu neo TRÁI nên nó đem so với "ĐANG KHAI" chứ không trượt sang được phần trùng —
/// không khớp, và đúng đó là tín hiệu "đang bận".
/// </summary>
internal sealed class WoodLocator : IDisposable
{
    private readonly PromptLocator _loc;

    private WoodLocator(PromptLocator loc) => _loc = loc;

    public Rectangle BandRegion => _loc.BandRegion;

    /// <summary>Con số của bộ dò, dựng từ config + profile của job thợ mộc.</summary>
    public static PromptTuning Tuning(WoodConfig cfg, WoodProfile p) => new()
    {
        InkMinBright = cfg.InkMinBright,
        InkSpreadTol = cfg.InkSpreadTol,
        InkRowMin = cfg.InkRowMin,
        RowMaxFrac = cfg.RowMaxFrac,
        RowGapMerge = cfg.RowGapMerge,
        LineBandMaxRatio = cfg.LineBandMaxRatio,
        MaxLines = cfg.MaxLines,
        NccMin = cfg.NccMin,
        TextH = p?.TextH ?? 0,
        GapSplit = p?.GapSplit ?? 0
    };

    /// <summary>Dò trên MÀN HÌNH THẬT. Null + lý do tiếng Việt nếu chưa đủ ô/mẫu.</summary>
    public static WoodLocator Create(WoodConfig cfg, Screen screen, WoodProfile p, out string problem)
        => Create(cfg, p, band => new RegionReader(FishingConfig.ToAbsolute(screen, band)), out problem);

    /// <summary>
    /// Dò trên một ẢNH TĨNH đã chụp — dùng cho form hiệu chuẩn và <c>--verify-wood</c>.
    /// Chạy đúng đoạn code của đường thật, chỉ đổi nguồn pixel.
    /// </summary>
    public static WoodLocator CreateForBitmap(WoodConfig cfg, WoodProfile p, Bitmap still, out string problem)
        => Create(cfg, p, band => new BitmapRegion(still, band.ToRectangle()), out problem);

    private static WoodLocator Create(WoodConfig cfg, WoodProfile p,
                                      Func<FishingRect, IPixelSource> openBand, out string problem)
    {
        problem = null;
        if (p is null) { problem = "chưa có cấu hình cho màn hình này"; return null; }
        if (!p.Ready.IsSet) { problem = "chưa khoanh prompt “khai thác”"; return null; }
        if (p.TextH < 6) { problem = "chưa đo được cỡ chữ — khoanh lại prompt"; return null; }

        var bandRect = p.ScanBand();
        if (!bandRect.IsSet) { problem = "vùng quét quá nhỏ"; return null; }

        var ready = LoadTemplate(WoodConfig.ReadyTemplatePath(p.Key), "khai thác", out problem);
        if (ready is null) return null;

        IPixelSource band;
        try { band = openBand(bandRect); }
        catch (Exception ex) { problem = "không mở được vùng quét: " + ex.Message; return null; }

        if (ready.Width > band.Region.Width || ready.Height > band.Region.Height)
        {
            band.Dispose();
            problem = "mẫu chữ to hơn vùng quét — khoanh vùng quét rộng hơn";
            return null;
        }

        return new WoodLocator(new PromptLocator(Tuning(cfg, p), band, ready));
    }

    private static GrayTemplate LoadTemplate(string path, string name, out string problem)
    {
        problem = null;
        if (!File.Exists(path)) { problem = $"chưa có mẫu chữ “{name}”"; return null; }
        try
        {
            var t = GrayTemplate.FromFile(path);
            if (t.IsFlat) { problem = $"mẫu “{name}” phẳng — khoanh lại lúc prompt đang hiện"; return null; }
            return t;
        }
        catch (Exception ex)
        {
            problem = $"mẫu “{name}” hỏng: {ex.Message}";
            return null;
        }
    }

    /// <summary>Chụp lại băng quét rồi trả về mọi dòng chữ trắng có kích cỡ hợp lý.</summary>
    public List<TextLine> FindLines() => _loc.FindLines();

    /// <summary>
    /// So mẫu tại một dòng chữ. Không đạt ngưỡng là câu trả lời hợp lệ — thà bỏ một khung còn
    /// hơn bấm E giữa lúc đang chặt.
    /// </summary>
    public WoodPick Classify(TextLine line)
    {
        var (hit, score) = _loc.Match(line);
        return new WoodPick { Line = line, Ready = hit, Score = score };
    }

    /// <summary>Điểm tại một dòng — để chỉnh ngưỡng bằng số thật, không đoán.</summary>
    public string DescribeScores(TextLine line) => Classify(line).Detail;

    public void Dispose() => _loc?.Dispose();

    /// <summary>Phần chữ tách được từ ô người dùng vừa khoanh.</summary>
    public static PromptText ExtractText(Bitmap crop, WoodConfig cfg, out string problem)
        => PromptLocator.ExtractText(crop, Tuning(cfg, null), out problem);
}
