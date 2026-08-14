namespace GtaMiniGameBot;

/// <summary>
/// Cho lưu dữ liệu người dùng (ô đã khoanh, ảnh mẫu, config).
/// Truoc day moi thu nam canh exe nen Debug va Release la hai bo du lieu rieng,
/// va `clean` la mat sach — gio dung chung mot thu muc ngoai bin.
/// </summary>
internal static class AppPaths
{
    public static string Root { get; } = Init();

    private static string Init()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GtaMiniGameBot");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// Chuyen du lieu cu nam canh exe sang Root. Chi chay khi Root chua co gi,
    /// nen goi bao nhieu lan cung khong de len ban moi.
    /// </summary>
    public static void MigrateFromExeFolder()
    {
        string exeDir = AppContext.BaseDirectory;
        if (string.Equals(
                Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(exeDir).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            CopyIfMissing(Path.Combine(exeDir, "fishing.json"), Path.Combine(Root, "fishing.json"));
            CopyIfMissing(Path.Combine(exeDir, "config.json"), Path.Combine(Root, "config.json"));

            var oldFishing = new DirectoryInfo(Path.Combine(exeDir, "fishing"));
            if (oldFishing.Exists)
            {
                foreach (var file in oldFishing.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(oldFishing.FullName, file.FullName);
                    CopyIfMissing(file.FullName, Path.Combine(Root, "fishing", rel));
                }
            }
        }
        catch { /* di tru that bai thi chay tiep voi config rong, khong chan app */ }
    }

    private static void CopyIfMissing(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to);
    }
}
