using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Ghi %AppData%\GtaMiniGameBot\logs\bot-log.txt. Mac dinh TAT —
/// File.AppendAllText moi dong mo/dong file, chi bat khi can debug.
/// </summary>
internal static class BotLog
{
    public static string LogPath => Path.Combine(AppPaths.Logs, "bot-log.txt");
    private static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static bool Enabled => AppSettings.Current.DebugFileLog;

    /// <summary>
    /// Co nam trong app.json, chung file voi cai dat overlay. Vi the o day chi doc/ghi qua
    /// <see cref="AppSettings.Current"/> — dung mot object rieng roi WriteAllText se xoa
    /// sach cac muc khac trong file. Nap thi da co AppSettings.Load() luc khoi dong.
    /// </summary>
    public static void SetEnabled(bool on)
    {
        AppSettings.Current.DebugFileLog = on;
        AppSettings.Current.Save();
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
}
