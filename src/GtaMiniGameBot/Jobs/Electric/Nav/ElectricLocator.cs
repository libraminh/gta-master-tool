namespace GtaMiniGameBot;

/// <summary>Kết quả so mẫu “[E] TƯƠNG TÁC” trên một dòng chữ.</summary>
internal sealed class ElectricPick
{
    public TextLine Line { get; init; }
    public bool Ready { get; init; }
    public double Score { get; init; }
}

/// <summary>
/// Dò prompt <c>[E] TƯƠNG TÁC</c> trong ô người dùng đã khoanh — bọc
/// <see cref="PromptLocator"/> với <see cref="PromptTuning.MatchOnInk"/> vì prompt gắn vật thể 3D
/// (nền bê tông nắng / tủ điện tối làm NCC xám rớt).
///
/// Ô <see cref="ElectricProfile.PromptBand"/> vừa là mẫu vừa là vùng quét: không suy giữa màn.
/// </summary>
internal sealed class ElectricLocator : IDisposable
{
    private readonly PromptLocator _loc;

    private ElectricLocator(PromptLocator loc) => _loc = loc;

    public Rectangle BandRegion => _loc.BandRegion;

    public static PromptTuning Tuning(ElectricProfile p) => new()
    {
        MatchOnInk = true,
        TextH = p?.PromptTextH ?? 0,
        GapSplit = p?.PromptGapSplit ?? 0
    };

    public static ElectricLocator Create(Screen screen, ElectricProfile p, out string problem)
        => Create(p, band => new RegionReader(FishingConfig.ToAbsolute(screen, band)), out problem);

    public static ElectricLocator CreateForBitmap(ElectricProfile p, Bitmap still, out string problem)
        => Create(p, band => new BitmapRegion(still, band.ToRectangle()), out problem);

    private static ElectricLocator Create(ElectricProfile p, Func<FishingRect, IPixelSource> openBand,
                                         out string problem)
    {
        problem = null;
        if (p is null) { problem = "chưa có cấu hình cho màn hình này"; return null; }
        if (!p.PromptBand.IsSet) { problem = "chưa khoanh prompt [E] TƯƠNG TÁC"; return null; }
        if (p.PromptTextH < 6) { problem = "chưa đo được cỡ chữ — khoanh lại prompt"; return null; }

        var ready = LoadTemplate(ElectricConfig.PromptTemplatePath(p.Key), out problem);
        if (ready is null) return null;

        IPixelSource band;
        try { band = openBand(p.PromptBand); }
        catch (Exception ex) { problem = "không mở được vùng quét: " + ex.Message; return null; }

        if (ready.Width > band.Region.Width || ready.Height > band.Region.Height)
        {
            band.Dispose();
            problem = "mẫu chữ to hơn vùng đã khoanh — khoanh rộng hơn một chút";
            return null;
        }

        return new ElectricLocator(new PromptLocator(Tuning(p), band, ready));
    }

    private static GrayTemplate LoadTemplate(string path, out string problem)
    {
        problem = null;
        if (!File.Exists(path)) { problem = "chưa có mẫu chữ “TƯƠNG TÁC”"; return null; }
        try
        {
            var t = GrayTemplate.FromFile(path);
            if (t.IsFlat) { problem = "mẫu chữ phẳng — khoanh lại lúc prompt đang hiện"; return null; }
            return t;
        }
        catch (Exception ex)
        {
            problem = "mẫu chữ hỏng: " + ex.Message;
            return null;
        }
    }

    public List<TextLine> FindLines() => _loc.FindLines();

    public ElectricPick Classify(TextLine line)
    {
        var (hit, score) = _loc.Match(line);
        return new ElectricPick { Line = line, Ready = hit, Score = score };
    }

    /// <summary>Một hit mẫu trong ô khoanh — đủ để arm E.</summary>
    public bool Visible()
    {
        foreach (var line in FindLines())
            if (Classify(line).Ready) return true;
        return false;
    }

    public void Dispose() => _loc?.Dispose();

    public static PromptText ExtractText(Bitmap crop, out string problem)
        => PromptLocator.ExtractText(crop, Tuning(null), out problem);
}
