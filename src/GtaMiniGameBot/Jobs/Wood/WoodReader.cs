namespace GtaMiniGameBot;

/// <summary>
/// Một lần đọc HUD thợ mộc. Chưa hiệu chuẩn thì <see cref="Configured"/> = false và mọi cờ = false
/// — KHÔNG đoán, đúng quy ước của <see cref="FishingSnapshot"/>.
/// </summary>
internal sealed class WoodSnapshot
{
    public bool Configured { get; init; }

    /// <summary>
    /// Thấy "KHAI THÁC" — sẵn sàng bấm E. Không thấy nghĩa là đang chặt (dòng chữ đã đổi thành
    /// "ĐANG KHAI THÁC") HOẶC không đứng cạnh cây nào; bot phân biệt hai ca đó bằng thời gian.
    /// </summary>
    public bool Ready { get; init; }

    /// <summary>Điểm NCC cao nhất trên mọi dòng chữ dò được — để chỉnh ngưỡng.</summary>
    public double Score { get; init; } = -1;

    /// <summary>Số dòng chữ trắng dò được trong băng. 0 = không có chữ nào trên màn.</summary>
    public int LineCount { get; init; }

    /// <summary>Chưa đọc được thì đây là lý do; null khi bình thường.</summary>
    public string Problem { get; init; }

    public string Describe() =>
        !Configured
            ? $"chưa hiệu chuẩn: {Problem ?? "thiếu vùng"}"
            : $"{LineCount} dòng chữ  ncc={Score:F2}" + (Ready ? "  → SẴN SÀNG" : "");
}

/// <summary>
/// Đọc trạng thái prompt thợ mộc. Mỏng: <see cref="WoodLocator"/> làm hết phần khó, lớp này chỉ
/// gom kết quả của mọi dòng chữ thành một câu trả lời cho bot.
///
/// Cùng khuôn <see cref="FishingReader"/>: <see cref="Open"/> trả về cả lý do không mở được, và
/// <see cref="Read"/> không bao giờ ném — bot chạy giảm chất lượng chứ không chết giữa vòng lặp.
/// </summary>
internal sealed class WoodReader : IDisposable
{
    private readonly WoodLocator _loc;
    private readonly string _problem;

    private WoodReader(WoodLocator loc, string problem)
    {
        _loc = loc;
        _problem = problem;
    }

    /// <summary>Luôn trả về một reader dùng được; chưa hiệu chuẩn thì nó báo qua snapshot.</summary>
    public static WoodReader Open(WoodConfig cfg, Screen screen, WoodProfile profile)
    {
        var loc = WoodLocator.Create(cfg, screen, profile, out string problem);
        return new WoodReader(loc, problem);
    }

    public bool Configured => _loc is not null;
    public string Problem => _problem;
    public Rectangle BandRegion => _loc?.BandRegion ?? Rectangle.Empty;

    public WoodSnapshot Read()
    {
        if (_loc is null)
            return new WoodSnapshot { Configured = false, Problem = _problem };

        List<TextLine> lines;
        try { lines = _loc.FindLines(); }
        catch (Exception ex)
        {
            // CopyFromScreen thi thoang that bai khi doi che do man hinh — mot khung xau khong
            // duoc giet ca phien lam viec.
            return new WoodSnapshot { Configured = true, Problem = ex.Message };
        }

        bool ready = false;
        double best = -1;
        foreach (var l in lines)
        {
            var pick = _loc.Classify(l);
            if (pick.Score > best) best = pick.Score;
            if (pick.Ready) ready = true;
        }

        return new WoodSnapshot
        {
            Configured = true,
            Ready = ready,
            Score = best,
            LineCount = lines.Count
        };
    }

    /// <summary>Chi tiết từng dòng — dùng cho log chẩn đoán và <c>--verify-wood</c>.</summary>
    public string DescribeAll()
    {
        if (_loc is null) return _problem ?? "chưa hiệu chuẩn";
        var lines = _loc.FindLines();
        if (lines.Count == 0) return "không thấy dòng chữ nào trong vùng quét";
        return string.Join("\r\n", lines.Select(l => "  " + _loc.DescribeScores(l)));
    }

    public void Dispose() => _loc?.Dispose();
}
