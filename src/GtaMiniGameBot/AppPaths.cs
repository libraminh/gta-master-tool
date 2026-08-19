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
            CopyIfMissing(Path.Combine(exeDir, "wood.json"), Path.Combine(Root, "wood.json"));

            CopyTree(exeDir, "fishing");
            CopyTree(exeDir, "wood");
        }
        catch { /* di tru that bai thi chay tiep voi config rong, khong chan app */ }
    }

    /// <summary>Chuyen ca cay anh mau cua mot job (fishing\, wood\...) sang Root.</summary>
    private static void CopyTree(string exeDir, string name)
    {
        var src = new DirectoryInfo(Path.Combine(exeDir, name));
        if (!src.Exists) return;

        foreach (var file in src.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src.FullName, file.FullName);
            CopyIfMissing(file.FullName, Path.Combine(Root, name, rel));
        }
    }

    private static void CopyIfMissing(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to);
    }
}
