using System.Text;
using System.Text.Json;

namespace GtaMiniGameBot;

/// <summary>
/// Ghi %AppData%\GtaMiniGameBot\logs\bot-log.txt. Mac dinh TAT —
/// File.AppendAllText moi dong mo/dong file, chi bat khi can debug.
/// </summary>
internal static class BotLog
{
    public static string LogPath => Path.Combine(AppPaths.Logs, "bot-log.txt");
    private static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static bool Enabled { get; private set; }

    public static string SettingsPath => Path.Combine(AppPaths.Root, "app.json");

    public static void Load()
    {
        Enabled = false;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var cfg = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), Opts);
            if (cfg is not null) Enabled = cfg.DebugFileLog;
        }
        catch { /* file hong -> tat, dung nhu mac dinh */ }
    }

    public static void SetEnabled(bool on)
    {
        Enabled = on;
        try
        {
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(new Settings { DebugFileLog = on }, Opts));
        }
        catch { /* khong ghi duoc thi van giu flag trong RAM */ }
    }

    /// <summary>
    /// <paramref name="tag"/> rong thi khong them prefix. Co tag thi viet <c>[tag]</c>
    /// de phan biet job trong cung mot file.
    /// </summary>
    public static void Write(string tag, string line)
    {
        if (!Enabled) return;
        try
        {
            string prefix = string.IsNullOrEmpty(tag) ? "" : $"[{tag}] ";
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {prefix}{line}{Environment.NewLine}",
                Encoding);
        }
        catch { }
    }

    private sealed class Settings
    {
        public bool DebugFileLog { get; set; }
    }
}
