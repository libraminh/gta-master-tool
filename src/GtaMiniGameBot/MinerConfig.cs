using System.Text.Json;
using System.Text.Json.Serialization;

namespace GtaMiniGameBot;

/// <summary>
/// Ba ô HUD của job thợ mỏ, khoanh theo từng độ phân giải.
///
/// Dùng lại <see cref="FishingRect"/> chứ không đẻ kiểu mới: nó chỉ là hình chữ nhật toạ độ
/// TƯƠNG ĐỐI góc màn, và <see cref="StillCropForm"/> lẫn <see cref="FishingConfig.ToAbsolute"/>
/// đã nói cùng thứ tiếng đó rồi. Đổi tên nó thành cái chung chung hơn thì phải sờ vào cả job
/// câu cá đang chạy tốt — không đáng.
/// </summary>
internal sealed class MinerProfile
{
    public string Device { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Ô thông báo "ĐANG KHAI THÁC…" — hiện suốt 10 giây đào.</summary>
    public FishingRect MiningBox { get; set; } = new();

    /// <summary>Gợi ý "[E] DÙNG THANG MÁY" khi đứng đúng chỗ giếng thang.</summary>
    public FishingRect LiftPrompt { get; set; } = new();

    /// <summary>Toast "Tiền mặt: + $…" góc trái dưới — dấu hiệu giao hàng thành công.</summary>
    public FishingRect CashToast { get; set; } = new();

    [JsonIgnore]
    public string Key => $"{Width}x{Height}";

    public void Normalize()
    {
        MiningBox ??= new FishingRect();
        LiftPrompt ??= new FishingRect();
        CashToast ??= new FishingRect();
    }
}

/// <summary>
/// Cài đặt job Thợ mỏ: giữ W (+ Left Shift) để chạy tới, bấm E theo nhịp, và — khi đã khoanh
/// vùng — đọc HUD để biết lúc nào đang đào, lúc nào đứng ở thang máy, lúc nào giao hàng xong.
/// Không nhét vào <see cref="BotConfig"/> — file đó là hằng số đo được của riêng giàn dầu.
/// </summary>
internal sealed class MinerConfig
{
    /// <summary>Bấm E mỗi bao nhiêu ms. 200 là con số người chơi đo trong game.</summary>
    public int TapEveryMs { get; set; } = 200;

    /// <summary>Giữ E bao lâu trong một cú bấm — bằng mặc định của InputSender.TapKey.</summary>
    public int TapHoldMs { get; set; } = 60;

    /// <summary>Giữ W để tự chạy tới. Tắt khi muốn tự lái mà vẫn nhờ tool lo phần bấm E.</summary>
    public bool HoldRun { get; set; } = true;

    /// <summary>Thêm Left Shift cùng W (chạy nước rút). Chỉ có tác dụng khi HoldRun bật.</summary>
    public bool HoldShift { get; set; } = true;

    /// <summary>
    /// Nhịp vòng lặp. Phải NHỎ hơn nhiều so với TapEveryMs: vòng lặp còn lo giữ keep-alive cho
    /// W/Shift, đọc HUD và phát hiện mất focus, nên không được ngủ trọn một nhịp E.
    /// </summary>
    public int PollMs { get; set; } = 50;

    /// <summary>Chỉ bắn phím khi tiêu đề cửa sổ foreground chứa chuỗi này.</summary>
    public string WindowMatch { get; set; } = "PlayXGTA";

    // ---------------- doc HUD ----------------

    public double MiningNccMin { get; set; } = 0.72;
    public double LiftNccMin { get; set; } = 0.72;
    public double CashNccMin { get; set; } = 0.72;

    /// <summary>
    /// Bấm xong thang máy thì màn hình đen một lúc; trong lúc đó gợi ý vẫn có thể còn đọc ra.
    /// Chặn bấm lại trong ngần này để không gọi thang hai lần rồi đi xuống ngược.
    /// </summary>
    public int LiftCooldownMs { get; set; } = 5_000;

    /// <summary>Giây đếm ngược để click sang game trước khi chụp ảnh tĩnh.</summary>
    public int ShotCountdownSec { get; set; } = 5;

    public Dictionary<string, MinerProfile> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Json cũ thiếu field thì về 0 — trả lại mặc định, không để nhịp bằng 0 rồi spam E.</summary>
    public void Normalize()
    {
        TapEveryMs = Math.Clamp(TapEveryMs <= 0 ? 200 : TapEveryMs, 50, 5_000);
        TapHoldMs = Math.Clamp(TapHoldMs <= 0 ? 60 : TapHoldMs, 10, 500);
        PollMs = Math.Clamp(PollMs <= 0 ? 50 : PollMs, 10, 200);
        if (string.IsNullOrWhiteSpace(WindowMatch)) WindowMatch = "PlayXGTA";

        if (MiningNccMin is <= 0 or > 1) MiningNccMin = 0.72;
        if (LiftNccMin is <= 0 or > 1) LiftNccMin = 0.72;
        if (CashNccMin is <= 0 or > 1) CashNccMin = 0.72;
        if (LiftCooldownMs <= 0) LiftCooldownMs = 5_000;
        if (ShotCountdownSec <= 0) ShotCountdownSec = 5;

        Profiles ??= new Dictionary<string, MinerProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Profiles.Values) p?.Normalize();
    }

    /// <summary>Hồ sơ của màn hình đang dùng. <paramref name="create"/> = false để chỉ tra cứu.</summary>
    public MinerProfile ProfileFor(Screen screen, bool create = true)
    {
        if (screen is null) return null;
        string key = $"{screen.Bounds.Width}x{screen.Bounds.Height}";

        if (Profiles.TryGetValue(key, out var found) && found is not null)
        {
            found.Normalize();
            return found;
        }
        if (!create) return null;

        var p = new MinerProfile
        {
            Device = screen.DeviceName,
            Width = screen.Bounds.Width,
            Height = screen.Bounds.Height
        };
        Profiles[key] = p;
        return p;
    }

    // ---------------- duong dan ----------------

    public static string DefaultPath => Path.Combine(AppPaths.Root, "miner.json");

    public static string ProfileDir(string key) => Path.Combine(AppPaths.Root, "miner", key);

    public static string MiningTemplatePath(string key) => Path.Combine(ProfileDir(key), "mining.png");
    public static string LiftTemplatePath(string key) => Path.Combine(ProfileDir(key), "lift.png");
    public static string CashTemplatePath(string key) => Path.Combine(ProfileDir(key), "cash.png");

    /// <summary>
    /// Ảnh tĩnh chụp cả màn game. Phải chụp tĩnh rồi khoanh trên ảnh chứ không khoanh trực tiếp
    /// được: ô "ĐANG KHAI THÁC…" chỉ sống đúng 10 giây, không đủ để alt-tab sang app mà kéo chuột.
    /// </summary>
    public static string ShotDir(string key) => Path.Combine(ProfileDir(key), "shots");
    public static string ShotPath(string key, string name) => Path.Combine(ShotDir(key), name + ".png");

    // ---------------- luu / doc ----------------

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

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
