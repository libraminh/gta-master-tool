using System.Text.Json;

namespace GtaMiniGameBot;

/// <summary>
/// Phím tắt người dùng đặt lại được.
/// JobToggle / UtilsToggle di qua RegisterHotKey nen nhan duoc to hop.
/// AutoRun / HoldCtrl / Sprint di qua low-level hook nen chi nhan phim don.
/// </summary>
internal sealed class HotkeyConfig
{
    public const uint DefaultJobVk = 0x78;    // F9
    public const uint DefaultUtilsVk = 0x70;  // F1
    public const uint DefaultAutoRunVk = 0x14; // CapsLock
    public const uint DefaultHoldCtrlVk = 0x46; // F
    public const uint DefaultSprintVk = 0xC0; // ` (Oemtilde)

    private const uint ModMask =
        Native.MOD_ALT | Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_WIN;

    public uint JobToggleVk { get; set; } = DefaultJobVk;
    public uint JobToggleMods { get; set; }
    public uint UtilsToggleVk { get; set; } = DefaultUtilsVk;
    public uint UtilsToggleMods { get; set; }
    public uint AutoRunVk { get; set; } = DefaultAutoRunVk;
    public uint HoldCtrlVk { get; set; } = DefaultHoldCtrlVk;
    public uint SprintVk { get; set; } = DefaultSprintVk;

    /// <summary>Json cũ thiếu field thì về 0 — trả lại mặc định, không để phím rỗng.</summary>
    public void Normalize()
    {
        if (JobToggleVk == 0) { JobToggleVk = DefaultJobVk; JobToggleMods = 0; }
        if (UtilsToggleVk == 0) { UtilsToggleVk = DefaultUtilsVk; UtilsToggleMods = 0; }
        if (AutoRunVk == 0) AutoRunVk = DefaultAutoRunVk;
        if (HoldCtrlVk == 0) HoldCtrlVk = DefaultHoldCtrlVk;
        if (SprintVk == 0) SprintVk = DefaultSprintVk;
        JobToggleMods &= ModMask;
        UtilsToggleMods &= ModMask;
    }

    public HotkeyConfig Clone() => new()
    {
        JobToggleVk = JobToggleVk,
        JobToggleMods = JobToggleMods,
        UtilsToggleVk = UtilsToggleVk,
        UtilsToggleMods = UtilsToggleMods,
        AutoRunVk = AutoRunVk,
        HoldCtrlVk = HoldCtrlVk,
        SprintVk = SprintVk
    };

    // ---------------- hien thi ----------------

    public static string Describe(uint vk, uint mods = 0)
    {
        var parts = new List<string>(4);
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    /// <summary>Tên enum Keys khá xấu với vài phím hay dùng nên đặt tên riêng.</summary>
    public static string KeyName(uint vk) => (Keys)vk switch
    {
        Keys.Capital => "CapsLock",
        Keys.Escape => "Esc",
        Keys.Return => "Enter",
        Keys.Back => "Backspace",
        Keys.PageUp => "PageUp",
        Keys.PageDown => "PageDown",
        Keys.Oemtilde => "`",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.OemPipe => "\\",
        var k when k >= Keys.D0 && k <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
        var k => k.ToString()
    };

    // ---------------- luu / doc ----------------

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultPath => Path.Combine(AppPaths.Root, "hotkeys.json");

    public void Save(string path = null)
    {
        path ??= DefaultPath;
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Opts)); }
        catch { /* khong ghi duoc thi van chay voi phim dang dung */ }
    }

    public static HotkeyConfig Load(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<HotkeyConfig>(File.ReadAllText(path), Opts);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch { /* file hong -> ve mac dinh */ }
        return new HotkeyConfig();
    }
}

/// <summary>Nhãn phím cho các panel job, đọc lúc dựng UI.</summary>
internal static class HotkeyText
{
    public static string Job()
    {
        var k = HotkeyConfig.Load();
        return HotkeyConfig.Describe(k.JobToggleVk, k.JobToggleMods);
    }
}
