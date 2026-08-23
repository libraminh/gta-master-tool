using System.Globalization;
using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Chi giu log trong 24 gio. Don cho cu canh exe, roi chuyen phan con lai sang AppData.
/// </summary>
internal static class LogHousekeeping
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);

    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private const string StampFmt = "yyyy-MM-dd HH:mm:ss.fff";
    private const string DumpFmt = "yyyy-MM-dd_HH-mm-ss";

    public static void RunAtStart()
    {
        try
        {
            SweepAndRelocate(AppContext.BaseDirectory);
            SweepFolder(AppPaths.Logs);
            SweepDebugDir(AppPaths.DebugDumps);
        }
        catch { /* don that bai khong chan app */ }
    }

    /// <summary>Xoa dump dau cu hon 24h. Goi them sau khi DumpEvidence cat theo so ban.</summary>
    public static void SweepDebugDir(string rootDir)
    {
        if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir)) return;
        var cutoff = DateTime.Now - Keep;
        foreach (var d in new DirectoryInfo(rootDir).GetDirectories())
        {
            DateTime when = d.LastWriteTime;
            if (DateTime.TryParseExact(d.Name, DumpFmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var named))
                when = named;
            if (when >= cutoff) continue;
            try { d.Delete(true); } catch { }
        }
    }

    public static void SweepFolder(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        PruneBotLog(Path.Combine(dir, "bot-log.txt"));
        PruneOverlay(Path.Combine(dir, "overlay-log.txt"));
        SweepDebugDir(Path.Combine(dir, "debug"));
    }

    private static void SweepAndRelocate(string exeDir)
    {
        if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir)) return;
        if (SameDir(exeDir, AppPaths.Logs) || SameDir(exeDir, AppPaths.Root))
        {
            SweepFolder(exeDir);
            return;
        }

        SweepFolder(exeDir);

        RelocateFile(
            Path.Combine(exeDir, "bot-log.txt"),
            Path.Combine(AppPaths.Logs, "bot-log.txt"),
            append: true);
        RelocateFile(
            Path.Combine(exeDir, "overlay-log.txt"),
            Path.Combine(AppPaths.Logs, "overlay-log.txt"),
            append: false);

        string fromDebug = Path.Combine(exeDir, "debug");
        string toDebug = AppPaths.DebugDumps;
        if (Directory.Exists(fromDebug))
        {
            Directory.CreateDirectory(toDebug);
            foreach (var d in new DirectoryInfo(fromDebug).GetDirectories())
            {
                string dest = Path.Combine(toDebug, d.Name);
                try
                {
                    if (Directory.Exists(dest)) d.Delete(true);
                    else d.MoveTo(dest);
                }
                catch { }
            }
            TryDeleteEmpty(fromDebug);
        }
    }

    private static void PruneBotLog(string path)
    {
        if (!File.Exists(path)) return;
        var cutoff = DateTime.Now - Keep;
        var kept = new List<string>();
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length >= 23 &&
                    DateTime.TryParseExact(line[..23], StampFmt, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var stamp))
                {
                    if (stamp >= cutoff) kept.Add(line);
                    continue;
                }
                if (kept.Count > 0) kept.Add(line);
            }

            if (kept.Count == 0) { File.Delete(path); return; }
            File.WriteAllLines(path, kept, Utf8Bom);
        }
        catch { }
    }

    private static void PruneOverlay(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            if (File.GetLastWriteTime(path) < DateTime.Now - Keep)
                File.Delete(path);
        }
        catch { }
    }

    private static void RelocateFile(string from, string to, bool append)
    {
        if (!File.Exists(from)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (!File.Exists(to))
            {
                File.Move(from, to);
                return;
            }
            if (append)
                File.AppendAllText(to, File.ReadAllText(from), Utf8Bom);
            File.Delete(from);
        }
        catch { }
    }

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }

    private static bool SameDir(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
