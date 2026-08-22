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

    /// <summary>Config nam ngay canh exe trong ban portable, mot file moi job.</summary>
    private static readonly string[] MigrateFiles =
    {
        "fishing.json", "config.json", "hotkeys.json", "miner.json", "wood.json", "electric.json"
    };

    /// <summary>
    /// Thu muc du lieu di kem. "items" la bo icon vat pham — THIEU no thi bot tut ve che do
    /// tin may o da khai bao, va o khai bao trong ban ship co the tro vao o MOI chu khong phai
    /// ca, tuc keo moi vao cop. Nen no phai di theo ban portable.
    /// </summary>
    private static readonly string[] MigrateDirs = { "fishing", "items", "wood", "electric" };

    /// <summary>
    /// Chuyen du lieu cu nam canh exe sang Root. Chi chep file nao Root CHUA co,
    /// nen goi bao nhieu lan cung khong de len ban moi, va chay ban portable tren
    /// may da co du lieu thi khong lam mat hieu chinh dang dung.
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
            foreach (string name in MigrateFiles)
                CopyIfMissing(Path.Combine(exeDir, name), Path.Combine(Root, name));

            foreach (string dir in MigrateDirs)
                CopyTree(exeDir, dir);
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
