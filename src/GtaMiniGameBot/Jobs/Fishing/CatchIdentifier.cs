namespace GtaMiniGameBot;

/// <summary>
/// Kết quả so ô chữ tên trên panel nhận cá với các mẫu đã chụp.
/// </summary>
internal sealed class CatchGuess
{
    public string Best { get; init; }
    public double Score { get; init; } = -1;
    public string Runner { get; init; }
    public double RunnerScore { get; init; } = -1;
    public bool Sure { get; init; }
    public string Note { get; init; }

    /// <summary>Tên chốt được, null nếu chưa đủ tự tin.</summary>
    public string Name => Sure ? Best : null;
}

/// <summary>
/// Nhận loài lúc panel nhận cá đang hiện: chụp ô <see cref="FishingProfile.CatchTitle"/>
/// rồi NCC với mẫu trong <c>catch-titles/</c>. Chỉ so loài trong danh sách đưa vào —
/// tập đóng, không đoán loài ngoài danh sách (thả hoặc bán).
/// </summary>
internal static class CatchIdentifier
{
    public static CatchGuess Identify(
        FishingConfig cfg, Screen screen, FishingProfile profile, IReadOnlyList<string> wanted)
    {
        if (profile?.CatchTitle.IsSet != true)
            return new CatchGuess { Note = "chưa khoanh ô tên cá" };

        if (wanted is not { Count: > 0 })
            return new CatchGuess { Note = "danh sách loài trống" };

        var abs = FishingConfig.ToAbsolute(screen, profile.CatchTitle);
        if (abs.Width < 8 || abs.Height < 8)
            return new CatchGuess { Note = "ô tên cá quá nhỏ" };

        byte[] gray;
        try
        {
            using var reader = new RegionReader(abs);
            reader.Refresh();
            gray = reader.GrayBuffer(abs);
        }
        catch (Exception ex)
        {
            return new CatchGuess { Note = "không chụp được ô tên: " + ex.Message };
        }

        var scored = new List<(string Name, double Score)>();
        int missing = 0, sizeSkip = 0;
        foreach (string raw in wanted)
        {
            string name = raw?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            string path = FishingConfig.CatchTitlePath(profile.Key, name);
            if (!File.Exists(path)) { missing++; continue; }

            GrayTemplate tpl;
            try { tpl = GrayTemplate.FromFile(path); }
            catch { missing++; continue; }

            if (tpl.Width != abs.Width || tpl.Height != abs.Height)
            {
                sizeSkip++;
                continue;
            }

            scored.Add((name, tpl.Score(gray)));
        }

        if (scored.Count == 0)
        {
            if (sizeSkip > 0)
                return new CatchGuess { Note = "mẫu tên lệch kích thước ô — khoanh lại hoặc chụp lại mẫu" };
            if (missing > 0)
                return new CatchGuess { Note = "chưa có mẫu tên cho loài đã chọn" };
            return new CatchGuess { Note = "không có mẫu tên để so" };
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = scored[0];
        var second = scored.Count > 1 ? scored[1] : default;

        bool sure = top.Score >= cfg.CatchTitleNccMin
                    && (scored.Count < 2 || top.Score - second.Score >= cfg.CatchTitleMarginMin);

        return new CatchGuess
        {
            Best = top.Name,
            Score = top.Score,
            Runner = scored.Count > 1 ? second.Name : null,
            RunnerScore = scored.Count > 1 ? second.Score : -1,
            Sure = sure,
            Note = sure
                ? null
                : scored.Count > 1
                    ? $"không chắc (cao nhất {top.Name} {top.Score:F2}, nhì {second.Name} {second.Score:F2})"
                    : $"không khớp mẫu ({top.Name} {top.Score:F2} < {cfg.CatchTitleNccMin:F2})"
        };
    }
}
