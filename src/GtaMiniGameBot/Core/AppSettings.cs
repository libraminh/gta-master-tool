using System.Text.Json;

namespace GtaMiniGameBot;

/// <summary>
/// Cài đặt chung của app (<c>app.json</c>) — không thuộc job nào.
///
/// Mot ban duy nhat song suot doi app: <see cref="Current"/>. Panel sua thang vao no roi goi
/// <see cref="Save"/>; khong ai duoc dung mot ban rieng roi ghi de, vi nhu the cai dat cua
/// muc khac trong cung file se bay mat.
/// </summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Ghi log debug ra <c>logs\bot-log.txt</c>. Mặc định tắt.</summary>
    public bool DebugFileLog { get; set; }

    /// <summary>Hiện thẻ trạng thái trong game khi job chạy.</summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>
    /// DeviceName man hinh ep the trang thai vao. Rong = tu dong bam theo cua so game.
    /// </summary>
    public string OverlayScreen { get; set; } = "";

    /// <summary>Ban dang dung. Nap mot lan luc khoi dong, xem <see cref="Load"/>.</summary>
    public static AppSettings Current { get; private set; } = new();

    public static string DefaultPath => Path.Combine(AppPaths.Root, "app.json");

    /// <summary>Json cũ thiếu field thì về mặc định, không để null lọt xuống dưới.</summary>
    public void Normalize()
    {
        OverlayScreen ??= "";
    }

    public void Save(string path = null)
    {
        path ??= DefaultPath;
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Opts)); }
        catch { /* khong ghi duoc thi van chay voi cai dat dang dung */ }
    }

    /// <summary>Nạp vào <see cref="Current"/>. Gọi một lần lúc khởi động.</summary>
    public static AppSettings Load(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    Current = cfg;
                    return cfg;
                }
            }
        }
        catch { /* file hong -> ve mac dinh */ }

        Current = new AppSettings();
        return Current;
    }
}
