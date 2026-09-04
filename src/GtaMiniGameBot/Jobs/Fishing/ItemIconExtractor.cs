using System.Text;
using System.Text.RegularExpressions;

namespace GtaMiniGameBot;

internal sealed class IconHarvest
{
    /// <summary>Tên vật phẩm → đường dẫn PNG đã chép ra.</summary>
    public Dictionary<string, string> Saved { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tên thấy trong cache nhưng không moi được ảnh.</summary>
    public List<string> Missing { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Moi icon vật phẩm ra khỏi cache Chromium của game.
///
/// Game là ứng dụng CEF: mọi icon kho đồ tải từ images.xgta.network và nằm lại trong cache
/// KÈM TÊN. Nhờ vậy bot có sẵn bộ mẫu đã gán nhãn mà không phải dạy tay con nào — đúng cái
/// giá mà bản đầu (xem chú thích <see cref="CellSignature"/>) đã phải trả rồi từ bỏ.
///
/// Cache là blockfile cổ điển: index + data_0..data_3 (bản ghi) + f_XXXXXX (thân phản hồi).
/// Bố cục bản ghi lấy từ EntryStore của Chromium:
///
///     entry+32  key_len       entry+60  data_addr[1] (thân)       entry+96  key
///
/// Key có tiền tố phân vùng "1/0/" đứng trước URL, nên khớp URL ở vị trí p thì entry ở p-100.
/// </summary>
internal static class ItemIconExtractor
{
    /// <summary>Chỉ nhận icon phẳng trong /items/ — quần áo nằm ở thư mục con, không vào kho đồ.</summary>
    private static readonly Regex ItemUrl = new(
        @"https://images\.xgta\.network/items/([A-Za-z0-9_\-]{1,60})\.png(\?t=\d+)?",
        RegexOptions.Compiled);

    private const int KeyPrefixLen = 4;      // "1/0/"
    private const int KeyOffset = 96;        // EntryStore.key
    private const int KeyLenOffset = 32;     // EntryStore.key_len
    private const int DataAddr1Offset = 60;  // EntryStore.data_addr[1]

    public static string ItemDir => Path.Combine(AppPaths.Root, "items");

    /// <summary>Quét cache, chép ảnh ra <see cref="ItemDir"/>. Không ném — mọi hỏng hóc vào Notes.</summary>
    public static IconHarvest Harvest(string cacheDir, bool allowDownload)
    {
        var res = new IconHarvest();

        if (string.IsNullOrWhiteSpace(cacheDir) || !Directory.Exists(cacheDir))
        {
            res.Notes.Add("không thấy thư mục cache: " + cacheDir);
            return res;
        }

        // Ten -> ten file than. Ban ghi sau de len ban ghi truoc: mot ten co the con hang chuc
        // entry cu (perch do duoc 67 lan), cai cuoi cung moi la cai con song.
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int blocks = 0;

        foreach (string file in new[] { "data_0", "data_1", "data_2", "data_3" })
        {
            string path = Path.Combine(cacheDir, file);
            if (!File.Exists(path)) continue;

            byte[] bytes;
            try { bytes = ReadShared(path); }
            catch (Exception ex) { res.Notes.Add($"{file}: {ex.Message}"); continue; }

            blocks++;
            ScanBlock(bytes, found, seenNames);
        }

        if (blocks == 0)
        {
            res.Notes.Add("không đọc được file data_* nào — sai thư mục cache?");
            return res;
        }
        if (seenNames.Count == 0)
        {
            // Doc duoc file ma khong ra ten nao: hoac sai thu muc, hoac CEF doi bo cuc ban ghi.
            // Cong kiem key_len duoi kia bao dam truong hop hai ra "0 icon" chu khong ra rac.
            res.Notes.Add("đọc được cache nhưng không thấy icon vật phẩm nào — sai thư mục, " +
                          "hoặc bản game đã đổi cách lưu cache");
            return res;
        }

        Directory.CreateDirectory(ItemDir);

        foreach (var kv in found.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string src = Path.Combine(cacheDir, kv.Value);
            string dst = Path.Combine(ItemDir, kv.Key + ".png");
            try
            {
                if (!File.Exists(src) || !IsPng(src)) continue;
                File.Copy(src, dst, overwrite: true);
                res.Saved[kv.Key] = dst;
            }
            catch (Exception ex) { res.Notes.Add($"{kv.Key}: {ex.Message}"); }
        }

        foreach (string name in seenNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            if (!res.Saved.ContainsKey(name))
                res.Missing.Add(name);

        if (res.Missing.Count > 0 && allowDownload) Download(res);

        return res;
    }

    private static void ScanBlock(byte[] bytes, Dictionary<string, string> found, HashSet<string> seen)
    {
        // Latin-1: moi byte thanh dung mot ky tu, nen vi tri ky tu = vi tri byte. UTF-8 thi
        // khong, va lech mot byte la doc sai ca ban ghi.
        string text = Encoding.Latin1.GetString(bytes);

        foreach (Match m in ItemUrl.Matches(text))
        {
            string name = m.Groups[1].Value;
            int entry = m.Index - KeyPrefixLen - KeyOffset;
            if (entry < 0 || entry + KeyOffset > bytes.Length - 4) continue;

            // Cong kiem duy nhat, va la cai giu cho phep doc nay trung thuc: key_len phai bang
            // do dai URL cong tien to. URL con nam trong THAN cua nhung phan hoi khac (JS,
            // JSON) nen thieu no la gan nhan bua vao mot cho khong phai ban ghi.
            if (BitConverter.ToInt32(bytes, entry + KeyLenOffset) != m.Length + KeyPrefixLen)
                continue;

            seen.Add(name);

            uint addr = BitConverter.ToUInt32(bytes, entry + DataAddr1Offset);
            if (addr == 0) continue;
            if (((addr >> 28) & 7) != 0) continue;   // khac 0 = nam trong block file, khong phai f_*

            found[name] = $"f_{addr & 0x0FFFFFFF:x6}";
        }
    }

    /// <summary>
    /// Đọc file mà game đang giữ khoá. data_* bị khoá độc quyền suốt lúc game chạy nên
    /// File.ReadAllBytes ném ngay — phải xin chia sẻ cả ghi lẫn xoá.
    /// </summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[fs.Length];
        int off = 0;
        while (off < buf.Length)
        {
            int n = fs.Read(buf, off, buf.Length - off);
            if (n <= 0) break;
            off += n;
        }
        return buf;
    }

    private static bool IsPng(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[4];
            return fs.Read(head, 0, 4) == 4
                   && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
        }
        catch { return false; }
    }

    /// <summary>Tải nốt mấy icon mà cache giữ trong block file thay vì file rời. Hỏng thì bỏ qua.</summary>
    private static void Download(IconHarvest res)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var still = new List<string>();

        foreach (string name in res.Missing)
        {
            try
            {
                var data = http.GetByteArrayAsync($"https://images.xgta.network/items/{name}.png")
                               .GetAwaiter().GetResult();
                if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50) { still.Add(name); continue; }

                string dst = Path.Combine(ItemDir, name + ".png");
                File.WriteAllBytes(dst, data);
                res.Saved[name] = dst;
            }
            catch (Exception ex)
            {
                still.Add(name);
                res.Notes.Add($"tải {name}: {ex.Message}");
            }
        }

        int got = res.Missing.Count - still.Count;
        if (got > 0) res.Notes.Add($"tải thêm được {got} icon từ images.xgta.network");
        res.Missing.Clear();
        res.Missing.AddRange(still);
    }
}
