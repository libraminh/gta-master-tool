using System.Text.Json;

namespace GtaMiniGameBot;

/// <summary>
/// Cài đặt job Thợ mỏ: giữ W (+ Left Shift) để chạy tới, và tự bấm E theo nhịp.
/// Không nhét vào <see cref="BotConfig"/> — file đó là hằng số đo được của riêng giàn dầu.
/// </summary>
internal sealed class MinerConfig
{
    /// <summary>Bấm E mỗi bao nhiêu ms. 200 là con số người chơi đo trong game.</summary>
    public int TapEveryMs { get; set; } = 200;

    /// <summary>Giữ E bao lâu trong một cú bấm — bằng mặc định của InputSender.TapKey.</summary>
    public int TapHoldMs { get; set; } = 60;

    /// <summary>Giữ Left Shift cùng W (chạy nước rút). Tắt được phòng khi cách chơi đổi.</summary>
    public bool HoldShift { get; set; } = true;

    /// <summary>
    /// Nhịp vòng lặp. Phải NHỎ hơn nhiều so với TapEveryMs: vòng lặp còn lo giữ keep-alive cho
    /// W/Shift và phát hiện mất focus, nên không được ngủ trọn một nhịp E. Bằng UtilityService.TickMs.
    /// </summary>
    public int PollMs { get; set; } = 50;

    /// <summary>Chỉ bắn phím khi tiêu đề cửa sổ foreground chứa chuỗi này.</summary>
    public string WindowMatch { get; set; } = "PlayXGTA";

    /// <summary>Json cũ thiếu field thì về 0 — trả lại mặc định, không để nhịp bằng 0 rồi spam E.</summary>
    public void Normalize()
    {
        TapEveryMs = Math.Clamp(TapEveryMs <= 0 ? 200 : TapEveryMs, 50, 5_000);
        TapHoldMs = Math.Clamp(TapHoldMs <= 0 ? 60 : TapHoldMs, 10, 500);
        PollMs = Math.Clamp(PollMs <= 0 ? 50 : PollMs, 10, 200);
        if (string.IsNullOrWhiteSpace(WindowMatch)) WindowMatch = "PlayXGTA";
    }

    // ---------------- luu / doc ----------------

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultPath => Path.Combine(AppPaths.Root, "miner.json");

    public void Save(string path = null)
    {
        path ??= DefaultPath;
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Opts)); }
        catch { /* khong ghi duoc thi van chay voi cai dat dang dung */ }
    }

    public static MinerConfig Load(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<MinerConfig>(File.ReadAllText(path), Opts);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch { /* file hong -> ve mac dinh */ }
        return new MinerConfig();
    }
}
