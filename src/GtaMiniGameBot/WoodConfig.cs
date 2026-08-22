using System.Text.Json;
using System.Text.Json.Serialization;

namespace GtaMiniGameBot;

/// <summary>
/// ROI job Thợ mộc theo từng độ phân giải.
///
/// Dùng lại <see cref="FishingRect"/> chứ không đẻ kiểu mới: nó chỉ là hình chữ nhật toạ độ
/// TƯƠNG ĐỐI góc màn, mà <see cref="StillCropForm"/> lẫn <see cref="FishingConfig.ToAbsolute"/>
/// đã nói cùng thứ tiếng đó rồi.
///
/// Khác mọi job cũ ở một chỗ: prompt "[E] KHAI THÁC" GẮN VÀO GỐC CÂY trong không gian 3D, không
/// phải HUD cố định. Đo trên hai ảnh chụp thật của cùng một cái cây: prompt nằm ở y=841 rồi y=944.
/// Nên <see cref="Ready"/> KHÔNG phải ô để đọc trực tiếp — nó chỉ là ô người dùng đã khoanh lúc
/// hiệu chuẩn, dùng để cắt mẫu và đo hình học. Lúc chạy thì dò trong <see cref="Band"/>.
/// </summary>
internal sealed class WoodProfile
{
    public string Device { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Băng quét: vùng RỘNG trùm mọi chỗ prompt có thể hiện. Rỗng thì suy từ tâm màn.</summary>
    public FishingRect Band { get; set; } = new();

    /// <summary>Ô đã khoanh lúc hiệu chuẩn: cả ô phím [E] lẫn chữ "KHAI THÁC".</summary>
    public FishingRect Ready { get; set; } = new();

    // Hai so duoi day SUY RA luc khoanh, khong bat nguoi dung nhap.

    /// <summary>
    /// Chiều cao mực của dòng chữ. Dùng để loại cảnh vật sáng: ban ngày thì trời/đá/thân xe trắng
    /// cũng thành mực, nhưng chúng trải hàng trăm hàng chứ không phải một dải cao chừng này.
    /// </summary>
    public int TextH { get; set; }

    /// <summary>
    /// Khe (px) đủ rộng để coi là ranh giới ô phím ↔ chữ. Đo trên ảnh thật: khe ô-phím→chữ 31 px,
    /// khe giữa các từ 10 px. Đo lại lúc hiệu chuẩn chứ không gõ cứng, vì nó co theo độ phân giải.
    /// </summary>
    public int GapSplit { get; set; }

    [JsonIgnore]
    public string Key => $"{Width}x{Height}";

    [JsonIgnore]
    public bool IsCalibrated => Ready.IsSet && TextH >= 6;

    public void Normalize()
    {
        Band ??= new FishingRect();
        Ready ??= new FishingRect();
    }

    /// <summary>
    /// Băng quét thật. Rỗng thì suy một ô 60%×50% giữa màn — prompt tương tác luôn hiện quanh
    /// tâm ngắm, nên người dùng bỏ qua slot này vẫn chạy được, chỉ tốn thêm chút pixel mỗi vòng.
    /// </summary>
    public FishingRect ScanBand()
    {
        if (Band.IsSet) return Band;
        if (Width < 100 || Height < 100) return new FishingRect();

        int w = (int)(Width * 0.60);
        int h = (int)(Height * 0.50);
        return new FishingRect { X = (Width - w) / 2, Y = (Height - h) / 2, W = w, H = h };
    }

    public string DescribeGaps() =>
        IsCalibrated ? $"{Key} — đủ (chữ cao {TextH}px, ngưỡng khe {GapSplit}px)"
                     : $"{Key} — chưa khoanh prompt “khai thác”";
}

/// <summary>
/// Cài đặt job Thợ mộc: đọc HUD để biết lúc nào bấm E, thay vì gõ mù theo nhịp.
///
/// Chỉ cần MỘT mẫu chữ "KHAI THÁC". Lúc đang chặt, dòng chữ đổi thành "ĐANG KHAI THÁC" và mẫu
/// được neo vào MÉP TRÁI nhóm chữ, nên nó đem so với "ĐANG KHAI" chứ không trượt sang phần
/// "KHAI THÁC" bên trong — không khớp, và đó chính là tín hiệu "đang bận". Không cần mẫu thứ hai.
///
/// Không nhét vào <see cref="MinerConfig"/>: thợ mỏ giữ W chạy tới rồi gõ E theo nhịp, thợ mộc
/// đứng một chỗ và bấm E theo TÍN HIỆU. Hai bộ hằng số không có gì chung ngoài WindowMatch.
/// </summary>
internal sealed class WoodConfig
{
    // ---------------- nhan dang ----------------

    /// <summary>
    /// Ngưỡng NCC cho mẫu chữ. 0.72 lấy bằng MenuNccMin của menu radial — cùng loại đối tượng
    /// (chữ trắng nét mảnh trên nền cảnh game), nên đo lại bằng --verify-wood rồi hãy đổi.
    /// </summary>
    public double NccMin { get; set; } = 0.72;

    /// <summary>
    /// Độ sáng tối thiểu của cả ba kênh để tính là mực chữ.
    ///
    /// 200 là số ĐO ĐƯỢC, không phải đoán. Trên ảnh chụp thật, hộp chữ "KHAI THÁC" 98×20 có 266
    /// pixel ở mức 250+ và đuôi thoải xuống ~210; còn cả băng quét 557×460 thì nền rừng dồn hết
    /// dưới 130. Đặt ở 200 nên vẫn dư mực để dựng dòng (13 pixel/hàng) mà loại được cả đá sáng lẫn
    /// thân xe trắng ban ngày (đo được 200–230) — thứ sẽ phá bộ dò nếu để ngưỡng thấp.
    /// </summary>
    public int InkMinBright { get; set; } = 200;

    /// <summary>Lệch tối đa giữa ba kênh — chữ trắng thì trung tính, đèn xe / lửa thì không.</summary>
    public int InkSpreadTol { get; set; } = 45;

    /// <summary>Số pixel mực tối thiểu trên một hàng để hàng đó tính là có chữ.</summary>
    public int InkRowMin { get; set; } = 3;

    /// <summary>
    /// Hàng có mực quá tỉ lệ này của bề rộng băng thì KHÔNG tính là hàng chữ.
    ///
    /// Chữ là nét mảnh: đo trên ảnh thật, hàng chữ chỉ chiếm ~8% bề rộng băng. Còn mảng sáng lớn
    /// (trời, thân xe) chiếm gần trọn hàng. Không có chặn trên thì một mảng sáng nằm cùng hàng với
    /// prompt sẽ nhập vào dải hàng của nó và nuốt luôn nhóm chữ.
    /// </summary>
    public double RowMaxFrac { get; set; } = 0.50;

    /// <summary>Gộp các hàng mực cách nhau ngần này — dấu mũ và chân chữ rời khỏi thân chữ.</summary>
    public int RowGapMerge { get; set; } = 2;

    /// <summary>
    /// Dải hàng cao quá ngần này lần chiều cao chữ thì bỏ. Đây chỉ là chặn CHI PHÍ (khỏi chiếu cột
    /// qua một dải khổng lồ); phần đúng/sai do bộ lọc cỡ NHÓM CHỮ lo. Nên để rộng: vòng tiến trình
    /// quanh ô phím cao hơn chữ, đo được 51 so với 20, và nó phình theo phần trăm đang chạy.
    /// </summary>
    public double LineBandMaxRatio { get; set; } = 8.0;

    /// <summary>Số dòng chữ tối đa đem so mẫu mỗi vòng.</summary>
    public int MaxLines { get; set; } = 6;

    // ---------------- nhip ----------------

    /// <summary>Nhịp vòng lặp. Chỉ chụp đúng băng quét nên 120 ms không làm game giật.</summary>
    public int PollMs { get; set; } = 120;

    /// <summary>
    /// Bấm E xong thì làm ngơ prompt ngần này. HUD mất một nhịp mới đổi sang "ĐANG KHAI THÁC";
    /// không chặn thì bot thấy prompt cũ còn đó và bấm thêm phát nữa.
    /// </summary>
    public int AfterTapBlindMs { get; set; } = 900;

    /// <summary>
    /// Đã bấm E mà prompt không hiện lại sau ngần này thì dừng: cây hết gỗ, hoặc bị đẩy ra xa.
    /// Phải rộng rãi — đây là cả một nhát chặt cộng thời gian chờ.
    /// </summary>
    public int MaxChopMs { get; set; } = 90_000;

    /// <summary>
    /// Chưa từng bấm được phát nào mà ngần này không thấy prompt thì dừng: đứng sai chỗ.
    /// Bot KHÔNG tự đi tìm cây khác — đi đường phải replay lộ trình mù, đã biết là không làm được.
    /// </summary>
    public int NoPromptMs { get; set; } = 6_000;

    // ---------------- che do mu ----------------

    /// <summary>Chưa hiệu chuẩn thì gõ E theo nhịp này — đúng hành vi job Thợ mỏ.</summary>
    public int TapEveryMs { get; set; } = 200;

    /// <summary>Giữ E bao lâu trong một cú bấm.</summary>
    public int TapHoldMs { get; set; } = 60;

    // ---------------- chung ----------------

    /// <summary>Giây đếm ngược cho người dùng bày màn hình trước khi chụp ảnh tĩnh.</summary>
    public int ShotCountdownSec { get; set; } = 5;

    /// <summary>Chỉ bắn phím khi tiêu đề cửa sổ foreground chứa chuỗi này.</summary>
    public string WindowMatch { get; set; } = "PlayXGTA";

    public Dictionary<string, WoodProfile> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Json cũ thiếu field thì về 0 — trả lại mặc định, không để nhịp bằng 0 rồi spam E.</summary>
    public void Normalize()
    {
        NccMin = Math.Clamp(NccMin <= 0 ? 0.72 : NccMin, 0.10, 0.99);
        InkMinBright = Math.Clamp(InkMinBright <= 0 ? 170 : InkMinBright, 80, 250);
        InkSpreadTol = Math.Clamp(InkSpreadTol <= 0 ? 45 : InkSpreadTol, 5, 120);
        InkRowMin = Math.Clamp(InkRowMin <= 0 ? 3 : InkRowMin, 1, 50);
        RowMaxFrac = Math.Clamp(RowMaxFrac <= 0 ? 0.50 : RowMaxFrac, 0.05, 1.0);
        RowGapMerge = Math.Clamp(RowGapMerge < 0 ? 2 : RowGapMerge, 0, 20);
        LineBandMaxRatio = Math.Clamp(LineBandMaxRatio <= 0 ? 8.0 : LineBandMaxRatio, 1.2, 40.0);
        MaxLines = Math.Clamp(MaxLines <= 0 ? 6 : MaxLines, 1, 64);

        PollMs = Math.Clamp(PollMs <= 0 ? 120 : PollMs, 40, 500);
        AfterTapBlindMs = Math.Clamp(AfterTapBlindMs <= 0 ? 900 : AfterTapBlindMs, 100, 10_000);
        MaxChopMs = Math.Clamp(MaxChopMs <= 0 ? 90_000 : MaxChopMs, 5_000, 600_000);
        NoPromptMs = Math.Clamp(NoPromptMs <= 0 ? 6_000 : NoPromptMs, 1_000, 120_000);

        TapEveryMs = Math.Clamp(TapEveryMs <= 0 ? 200 : TapEveryMs, 50, 5_000);
        TapHoldMs = Math.Clamp(TapHoldMs <= 0 ? 60 : TapHoldMs, 10, 500);
        ShotCountdownSec = Math.Clamp(ShotCountdownSec <= 0 ? 5 : ShotCountdownSec, 2, 30);

        if (string.IsNullOrWhiteSpace(WindowMatch)) WindowMatch = "PlayXGTA";

        Profiles ??= new Dictionary<string, WoodProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Profiles.Values) p?.Normalize();
    }

    // ---------------- profile ----------------

    public WoodProfile GetOrCreate(Screen screen)
    {
        var b = screen.Bounds;
        string key = $"{b.Width}x{b.Height}";
        if (!Profiles.TryGetValue(key, out var p) || p is null)
        {
            p = new WoodProfile { Device = screen.DeviceName, Width = b.Width, Height = b.Height };
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

    // ---------------- duong dan tai san ----------------

    public static string DefaultPath => Path.Combine(AppPaths.Root, "wood.json");

    public static string ProfileDir(string key) => Path.Combine(AppPaths.Root, "wood", key);

    /// <summary>Mẫu chữ "KHAI THÁC" — KHÔNG gồm ô phím, xem <see cref="WoodLocator"/>.</summary>
    public static string ReadyTemplatePath(string key) => Path.Combine(ProfileDir(key), "ready.png");

    public static string ShotDir(string key) => Path.Combine(ProfileDir(key), "shots");

    public static string ShotPath(string key, string name) =>
        Path.Combine(ShotDir(key), name + ".png");

    // ---------------- luu / doc ----------------

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public void Save(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* khong ghi duoc thi van chay voi cai dat dang dung */ }
    }

    public static WoodConfig Load(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<WoodConfig>(File.ReadAllText(path), Opts);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch { /* file hong -> ve mac dinh */ }

        var fresh = new WoodConfig();
        fresh.Normalize();
        return fresh;
    }
}
