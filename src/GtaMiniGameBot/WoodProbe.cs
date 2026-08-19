namespace GtaMiniGameBot;

/// <summary>
/// Chạy bộ dò thợ mộc trên một ẢNH TĨNH và kể lại từng con số.
///
/// Vì sao có lớp này: ngưỡng NCC phải chỉnh bằng số đo thật, mà thử trực tiếp trong game thì mỗi
/// lần phải alt-tab, đi lại, chờ cây. Chạy trên ảnh đã chụp thì lặp bao nhiêu lần cũng được và
/// luôn cho cùng kết quả — cùng lý do <see cref="VerifyOcr"/> tồn tại cho phần đọc chữ số.
/// Dùng chung bởi <see cref="WoodSetupForm"/> (nút "Thử dò") và <see cref="VerifyWood"/>.
/// </summary>
internal static class WoodProbe
{
    /// <summary>Có thấy prompt "KHAI THÁC" trên ảnh không, kèm bảng điểm từng dòng chữ.</summary>
    public static bool Detect(WoodConfig cfg, WoodProfile profile, Bitmap still,
                             out string report, out string problem)
    {
        report = null;
        using var loc = WoodLocator.CreateForBitmap(cfg, profile, still, out problem);
        if (loc is null) return false;

        var lines = loc.FindLines();
        if (lines.Count == 0)
        {
            report = "  không thấy dòng chữ nào trong vùng quét\r\n";
            return false;
        }

        bool ready = false;
        var rows = new List<string>();
        foreach (var l in lines)
        {
            var pick = loc.Classify(l);
            rows.Add("  " + pick.Detail);
            if (pick.Ready) ready = true;
        }

        report = string.Join("\r\n", rows) + "\r\n";
        return ready;
    }

    /// <summary>Bản một dòng cho log của form hiệu chuẩn.</summary>
    public static string Describe(WoodConfig cfg, WoodProfile profile, Bitmap still, out string problem)
    {
        bool ready = Detect(cfg, profile, still, out string report, out problem);
        if (problem is not null) return null;
        return report + (ready ? "  → SẴN SÀNG" : "  → không thấy prompt khai thác");
    }
}
