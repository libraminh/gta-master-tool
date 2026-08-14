using System.Text.Json;
using System.Text.Json.Serialization;

namespace GtaMiniGameBot;

/// <summary>
/// ROI câu cá theo từng độ phân giải. Tọa độ tương đối góc trên-trái của màn,
/// không phải tọa độ ảo cả desktop — rút/đổi màn không làm vỡ ô đã khoanh.
/// </summary>
internal sealed class FishingRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    [JsonIgnore]
    public bool IsSet => W >= 8 && H >= 8;

    public Rectangle ToRectangle() => new(X, Y, W, H);

    public static FishingRect FromRelative(Rectangle r) => new()
    {
        X = r.X, Y = r.Y, W = r.Width, H = r.Height
    };
}

internal sealed class FishingProfile
{
    public string Device { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public FishingRect Bar { get; set; } = new();
    public FishingRect Fish { get; set; } = new();
    public FishingRect Reject { get; set; } = new();
    public FishingRect Keep { get; set; } = new();

    [JsonIgnore]
    public string Key => $"{Width}x{Height}";

    public string DescribeGaps()
    {
        var missing = new List<string>();
        if (!Bar.IsSet) missing.Add("thanh");
        if (!Fish.IsSet) missing.Add("cá");
        if (!Reject.IsSet) missing.Add("thông báo");
        if (!Keep.IsSet) missing.Add("CẤT VÀO");
        if (missing.Count == 0) return $"{Key} — đủ 4 ô";
        if (missing.Count == 4) return $"{Key} — chưa khoanh";
        return $"{Key} — thiếu {string.Join(", ", missing)}";
    }
}

internal sealed class FishingConfig
{
    public Dictionary<string, FishingProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double FishNccMin { get; set; } = 0.75;
    public double RejectNccMin { get; set; } = 0.75;
    public double KeepNccMin { get; set; } = 0.75;

    public int WaitBiteMs { get; set; } = 25_000;
    public int FightTimeoutMs { get; set; } = 40_000;
    public int AfterReleaseMs { get; set; } = 1_200;
    public int CastCooldownMs { get; set; } = 1_500;
    /// <summary>Sau phím 4, chờ rồi bấm Space (thay combo AutoHotkey).</summary>
    public int CastSpaceDelayMs { get; set; } = 200;
    /// <summary>Sau khi thấy chê mồi, chờ rồi mới bấm 4. 0 = ngay.</summary>
    public int RejectRecastMs { get; set; } = 100;
    public int PollMs { get; set; } = 100;
    public int BiteDebounceFrames { get; set; } = 3;
    public double DoneFill01 { get; set; } = 0.95;
    public int WaitKeepMs { get; set; } = 8_000;
    public int KeepAppearMs { get; set; } = 400;
    public int KeepGoneMs { get; set; } = 1_500;
    public int KeepHoverMs { get; set; } = 320;
    public int KeepMoveSteps { get; set; } = 8;
    public string WindowMatch { get; set; } = "PlayXGTA";

    /// <summary>Json cũ thiếu field thì về 0 — khôi phục mặc định, không đoán timeout = 0.</summary>
    public void Normalize()
    {
        if (FishNccMin <= 0) FishNccMin = 0.75;
        if (RejectNccMin <= 0) RejectNccMin = 0.75;
        if (KeepNccMin <= 0) KeepNccMin = 0.75;
        if (WaitBiteMs <= 0) WaitBiteMs = 25_000;
        if (FightTimeoutMs <= 0) FightTimeoutMs = 40_000;
        if (AfterReleaseMs <= 0) AfterReleaseMs = 1_200;
        if (CastCooldownMs <= 0) CastCooldownMs = 1_500;
        if (CastSpaceDelayMs < 0) CastSpaceDelayMs = 200;
        if (RejectRecastMs < 0) RejectRecastMs = 100;
        if (PollMs <= 0) PollMs = 100;
        if (BiteDebounceFrames <= 0) BiteDebounceFrames = 3;
        if (DoneFill01 <= 0 || DoneFill01 > 1) DoneFill01 = 0.95;
        if (WaitKeepMs <= 0) WaitKeepMs = 8_000;
        if (KeepAppearMs <= 0) KeepAppearMs = 400;
        if (KeepGoneMs < 0) KeepGoneMs = 1_500;
        if (KeepHoverMs <= 0) KeepHoverMs = 320;
        if (KeepMoveSteps <= 0) KeepMoveSteps = 8;
        if (string.IsNullOrWhiteSpace(WindowMatch)) WindowMatch = "PlayXGTA";
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultPath =>
        Path.Combine(AppPaths.Root, "fishing.json");

    public static string ProfileDir(string key) =>
        Path.Combine(AppPaths.Root, "fishing", key);

    public static string BarPreviewPath(string key) => Path.Combine(ProfileDir(key), "bar.png");
    public static string FishTemplatePath(string key) => Path.Combine(ProfileDir(key), "fish.png");
    public static string RejectTemplatePath(string key) => Path.Combine(ProfileDir(key), "reject.png");
    public static string KeepTemplatePath(string key) => Path.Combine(ProfileDir(key), "keep.png");

    public void Save(string path = null)
    {
        path ??= DefaultPath;
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }

    public static FishingConfig Load(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<FishingConfig>(File.ReadAllText(path), Opts);
                if (cfg is not null)
                {
                    cfg.Profiles = new Dictionary<string, FishingProfile>(
                        cfg.Profiles ?? new(), StringComparer.OrdinalIgnoreCase);
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch { /* file hong -> config rong, user khoanh lai */ }
        return new FishingConfig();
    }

    public FishingProfile GetOrCreate(Screen screen)
    {
        var b = screen.Bounds;
        string key = $"{b.Width}x{b.Height}";
        if (!Profiles.TryGetValue(key, out var p) || p is null)
        {
            p = new FishingProfile
            {
                Device = screen.DeviceName,
                Width = b.Width,
                Height = b.Height
            };
            Profiles[key] = p;
        }
        else
        {
            p.Device = screen.DeviceName;
            p.Width = b.Width;
            p.Height = b.Height;
        }
        return p;
    }

    public FishingProfile TryGet(Screen screen)
    {
        string key = $"{screen.Bounds.Width}x{screen.Bounds.Height}";
        return Profiles.TryGetValue(key, out var p) ? p : null;
    }

    /// <summary>
    /// Tìm màn đang gắn: ưu tiên DeviceName đã khoanh, không có thì khớp WxH.
    /// </summary>
    public static Screen ResolveScreen(FishingProfile profile)
    {
        if (profile is null) return null;
        var all = Screen.AllScreens;
        var byDevice = all.FirstOrDefault(s =>
            string.Equals(s.DeviceName, profile.Device, StringComparison.OrdinalIgnoreCase));
        if (byDevice is not null) return byDevice;
        return all.FirstOrDefault(s =>
            s.Bounds.Width == profile.Width && s.Bounds.Height == profile.Height);
    }

    public static Rectangle ToAbsolute(Screen screen, FishingRect r)
    {
        var o = screen.Bounds.Location;
        return new Rectangle(o.X + r.X, o.Y + r.Y, r.W, r.H);
    }

    public static Screen Prefer2kOrPrimary()
    {
        var twoK = Screen.AllScreens.FirstOrDefault(s =>
            s.Bounds.Width == 2560 && s.Bounds.Height == 1440);
        return twoK ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }
}
