using System.Text.Json;
using System.Text.Json.Serialization;

namespace GtaMiniGameBot;

/// <summary>
/// Toan bo hang so da DO DUOC tu ban ghi demo (recordings/demo-01),
/// khong phai uoc luong bang mat.
/// </summary>
internal sealed class BotConfig
{
    // --- 4 gian bom: tam ngang cua tung thanh tien trinh ---
    // Do bang cach quet do sang ngang tai y=900; trung khit toa do hook bat duoc
    // khi nguoi choi click: 366 / 508 / 644 / 784.
    public int[] BarX { get; set; } = [366, 505, 644, 782];

    // --- than thanh doc: vung cho tin hieu sach nhat ---
    // 095.png (chua lam)  : ~110
    // 107.png (da xong)   : 255 bao hoa
    public int BarYTop { get; set; } = 835;

    // 975 nam sat VONG TRON sang o day thanh, nen diem mau cuoi luon cao bat thuong:
    // gieng cu doc 137..142, gieng moi doc 174..185 - tuc da vuot nguong day (180)
    // trong khi thanh con rong. Thu ve 960 thi van trong than thanh nhung roi khoi
    // vong tron.
    public int BarYBottom { get; set; } = 960;

    public int BarSamples { get; set; } = 8;
    public int BarHalfWidth { get; set; } = 3;

    /// <summary>>= nguong nay o TOAN BO than thanh => da chay day.</summary>
    public int FullThreshold { get; set; } = 180;

    /// <summary>
    /// Khoang tru xuong tu FullThreshold de coi la "khong con day".
    /// KHONG dat nguong "rong" tuyet doi nua: thanh la lop phu ban trong suot nen
    /// do sang cua thanh RONG phu thuoc nen (~51 + 0.8*nen) - gieng cu doc 91..107,
    /// gieng moi doc 125..140, tuc nguong cu 140 con bien gan bang 0.
    /// WaitForReset chi duoc goi khi ca 4 thanh DANG DAY, nen luc do thanh chi co
    /// the la 255 hoac da ve rong - moi nguong nam giua hai muc do deu dung.
    /// </summary>
    public int ResetHysteresis { get; set; } = 20;

    [JsonIgnore]
    public int ResetThreshold => FullThreshold - ResetHysteresis;

    /// <summary>
    /// Neu diem mau cua thanh RONG len toi trong khoang nay duoi FullThreshold thi
    /// canh bao. Bien mot loi am tham o gieng nen sang thanh mot dong nhin thay duoc.
    /// </summary>
    public int MarginWarnGap { get; set; } = 25;

    /// <summary>Y de dat con tro khi nhan giu (nguoi choi click quanh 901..946).</summary>
    public int ClickY { get; set; } = 900;

    // --- vung con so "SAN LUONG CA NHAN x/50" ---
    // Do duoc: chu so mau xanh o x=142..150, "/50" mau trang o x=154..204, y=1288..1330.
    // Lay dai rong hon de con so con cho ra 2 chu so (50/50) van nam trong vung.
    public int CounterX { get; set; } = 100;
    public int CounterY { get; set; } = 1280;
    public int CounterW { get; set; } = 175;
    public int CounterH { get; set; } = 60;

    [JsonIgnore]
    public Rectangle CounterRegion => new(CounterX, CounterY, CounterW, CounterH);

    // --- do "panel con mo khong": chuoi "/50" mau trang ---
    // Do duoc: panel mo  -> 691 / 661 pixel trang (095.png / 107.png)
    //          panel dong ->   0 /   0            (111.png / 112.png)
    // Khoang cach 691 vs 0 nen nguong dat dau trong 50..500 cung dung.
    public int PanelProbeX { get; set; } = 154;
    public int PanelProbeY { get; set; } = 1288;
    public int PanelProbeW { get; set; } = 51;
    public int PanelProbeH { get; set; } = 43;
    public int PanelOpenMinWhite { get; set; } = 200;

    [JsonIgnore]
    public Rectangle PanelProbe => new(PanelProbeX, PanelProbeY, PanelProbeW, PanelProbeH);

    // --- do "dang ngoi trong xe": so khop mau dong ho toc do bang NCC ---
    //
    // O nay co dinh tren man hinh nen chi can SO KHOP, khong can tim kiem.
    //
    // Truoc day cho nay dem pixel gan-trang roi so nguong 1000. Cach do da SAP:
    //     hieu chuan 13:27  ->  trong xe 3175..3235,  duoi dat 0
    //     luc loi   15:37   ->  duoi dat doc ra 5100..6246  (!!)
    // Doi gio trong game la doi anh sang; mat dat nang / thung xe co chu "LTD"
    // mau trang lot vao o do la ra hang nghin pixel. Dem do sang trong mot hinh
    // chu nhat KHONG dac trung cho cai minh muon tim.
    // NCC bat bien voi s -> a*s + b nen khong bi anh sang keo di.
    // O nay chon bang cach cham diem 73 o ung vien tren 34 frame (--pick-car-template),
    // lay o co do tach rong nhat:
    //     duoi dat toi da 0.164  |  trong xe toi thieu 0.958  |  do tach 0.795
    //     0 frame nao roi vao dai "khong biet"
    public int CarProbeX { get; set; } = 2140;
    public int CarProbeY { get; set; } = 1160;
    public int CarProbeW { get; set; } = 220;
    public int CarProbeH { get; set; } = 90;

    /// <summary>Duong dan mau (PNG thang xam). Tuong doi thi tinh tu thu muc exe.</summary>
    public string CarTemplatePath { get; set; } = "car-template.png";

    /// <summary>>= nguong nay -> chac chan DANG TRONG XE.</summary>
    public double CarNccIn { get; set; } = 0.80;

    /// <summary>&lt;= nguong nay -> chac chan DUOI DAT.</summary>
    public double CarNccOut { get; set; } = 0.40;

    // Giua hai nguong = KHONG BIET -> dung, khong doan.
    // Bat buoc phai co dai nay: cua kiem nay canh mot hanh dong pha hoai
    // (bam E luc dang trong xe = "BOM DAU VAO XE", do mat so dau vua cay).

    // Giu lai cach dem cu CHI DE IN RA LOG doi chieu, khong dung quyet dinh.
    public int CarWhiteMinBright { get; set; } = 185;
    public int CarWhiteSpread { get; set; } = 28;

    [JsonIgnore]
    public Rectangle CarProbe => new(CarProbeX, CarProbeY, CarProbeW, CarProbeH);

    [JsonIgnore]
    public string CarTemplateFullPath =>
        Path.IsPathRooted(CarTemplatePath)
            ? CarTemplatePath
            : Path.Combine(AppContext.BaseDirectory, CarTemplatePath);

    // --- reset dong ho thue xe (ESC -> F vao -> F xuong -> E) ---
    /// <summary>Mac dinh TAT. Chi bat khi da chay thu va tin.</summary>
    public bool CarResetEnabled { get; set; } = false;

    /// <summary>
    /// Bao lau thi len-xuong xe mot lan. Theo DONG HO TUONG, khong theo so thung:
    /// thoi gian moi thung thay doi theo cap khai thac, nen dem thung co the vuot
    /// qua han thue 500s va xe bi xoa giua luc dang cay.
    /// </summary>
    public int CarResetEverySec { get; set; } = 400;

    /// <summary>
    /// Moi buoc trong chuoi reset phai dat trang thai mong doi trong bao lau.
    /// Phai RONG RAI: animation len/xuong xe cua GTA V mat 1.5-4 giay, va tin hieu
    /// car~3200 chi xuat hien khi da ngoi han vao ghe. Dat 3500 ms la qua sat -
    /// gate het han giua animation, roi lan thu lai bam F them mot lan, ma F la
    /// phim BAT/TAT nen no huy dong tac vua roi va bot lech pha.
    /// </summary>
    public int GateTimeoutMs { get; set; } = 9_000;
    public int GateRetries { get; set; } = 2;

    /// <summary>Nghi sau khi mot buoc dat, cho game on dinh.</summary>
    public int AfterKeyDelayMs { get; set; } = 900;

    /// <summary>Bao nhieu lan doc lien tiep giong nhau thi coi la het animation.</summary>
    public int StableReads { get; set; } = 3;
    public int StableWaitMaxMs { get; set; } = 7_000;

    /// <summary>
    /// Phai doc thay panel DONG bao nhieu lan lien tiep moi dung.
    /// Truoc day mot lan doc la dung luon - neu panel chi nhay tat mot nhip luc game
    /// trao thung thi bot chet giua mot luot cay 30 phut.
    /// </summary>
    public int PanelClosedGraceReads { get; set; } = 3;
    public int PanelClosedGraceIntervalMs { get; set; } = 800;

    /// <summary>Chup anh + so do ra app/debug khi dung vi loi, de truy nguyen nhan.</summary>
    public bool DebugDumpEnabled { get; set; } = true;
    public int DebugDumpKeep { get; set; } = 20;

    public int VkEsc { get; set; } = 0x1B;
    public int VkVehicle { get; set; } = 0x46;    // F
    public int VkInteract { get; set; } = 0x45;   // E

    // Hai cu F chay VONG HO - bam roi cho, khong kiem trang thai xe.
    // Con so lay tu log thuc te, khong lay tu cam giac:
    //     "vao xe (F): dat sau 3178 ms"     -> animation len xe ~3.2 s
    //     trace ncc: 2340ms=-0.04, 2889ms=0.68, 3443ms=0.75  -> on dinh ~3.4 s
    //     "xuong xe (F): dat sau 2356 ms"   -> animation xuong xe ~2.4 s
    //
    // Cho 1-3 s cho buoc len xe la QUA NGAN: cu F thu hai se ban ra giua animation,
    // va F la phim BAT/TAT nen no huy dong tac dang chay thay vi xuong xe.
    public int AfterEnterCarMs { get; set; } = 4_000;
    public int AfterExitCarMs { get; set; } = 3_000;

    // Vung chup phai RONG hon 4 thanh: can du nen xung quanh de tinh median,
    // vi tin hieu "panel dang mo" = 4 thanh noi len so voi median cua ca vung.
    public int BarRegionX0 { get; set; } = 280;
    public int BarRegionX1 { get; set; } = 880;

    /// <summary>Buoc nhay dong khi tinh profile cot (4 = du chinh xac, nhanh gap 4).</summary>
    public int ProfileRowStep { get; set; } = 4;

    /// <summary>
    /// Panel duoc coi la DANG MO khi ca 4 thanh deu noi len tren median it nhat
    /// bang nguong nay. Do duoc:
    ///     panel mo   : +36.3 .. +37.0  (4 frame)
    ///     panel dong : -32.0 .. -3.8   (36 frame, co ca luc minimap hien duong)
    ///
    /// Thay cho cach cu (dem pixel trang chuoi "/50"): vung do CHONG VOI MINIMAP,
    /// nen khi minimap ve vach ke duong mau trang thi no doc ra 544 va bot tuong
    /// panel con mo -> lech pha ca chuoi reset xe.
    /// </summary>
    public double PanelBarProminenceMin { get; set; } = 15.0;

    /// <summary>Vung bao 4 thanh + nen xung quanh - chi chup dung day.</summary>
    [JsonIgnore]
    public Rectangle BarRegion
    {
        get
        {
            int top = Math.Min(BarYTop, ClickY) - 2;
            int bot = Math.Max(BarYBottom, ClickY) + 2;
            return new Rectangle(BarRegionX0, top, BarRegionX1 - BarRegionX0 + 1, bot - top + 1);
        }
    }

    // --- nhip va an toan ---
    public int PollMs { get; set; } = 50;

    /// <summary>Nha tay va bao loi neu thanh khong day sau moc nay (do duoc 7.5s o cap 1).</summary>
    public int MaxHoldMs { get; set; } = 25_000;

    /// <summary>
    /// Ca 4 thanh trang roi thi doi bao lau cho game reset ve xam.
    /// Het thoi gian nay ma khong reset = kho day (tin hieu du phong,
    /// doc lap voi tin hieu "pixel xanh khong doi").
    /// </summary>
    public int ResetWaitMs { get; set; } = 15_000;

    public int BetweenPositionsMs { get; set; } = 250;
    public int BetweenCyclesMs { get; set; } = 600;

    // --- nhip cua thao tac chuot ---
    // Nhung so nay sinh ra tu loi thuc te: teleport con tro roi nhan ngay sau 50ms
    // thi game van con nghi con tro o bieu tuong cu -> cu nhan roi vao khoang trong.

    /// <summary>So buoc nho khi di chuyen con tro (thay vi nhay mot nhat).</summary>
    public int MoveSteps { get; set; } = 8;

    /// <summary>Cho game cap nhat trang thai hover sau khi con tro tới dich.</summary>
    public int HoverSettleMs { get; set; } = 320;

    /// <summary>Cho sau khi nha chuot, truoc khi roi khoi bieu tuong.</summary>
    public int ReleaseSettleMs { get; set; } = 140;

    /// <summary>
    /// Sau khi nhan giu, cho bao lau de xac nhan thanh DA BAT DAU chay.
    /// Het thoi gian nay ma chua diem nao trang = cu nhan khong an -> thu lai.
    /// </summary>
    public int PressCheckMs { get; set; } = 1400;

    /// <summary>So lan thu lai khi cu nhan khong an.</summary>
    public int PressRetries { get; set; } = 3;

    /// <summary>Chi hanh dong khi tieu de cua so foreground chua chuoi nay.</summary>
    public string WindowMatch { get; set; } = "PlayXGTA";

    /// <summary>Dung khi xong bao nhieu chu ky lien tiep ma con so khong tang (=> kho day).</summary>
    public int StopAfterStaleCycles { get; set; } = 2;

    /// <summary>0 = chay tiep tuc, chi dung khi kho day hoac nguoi bam dung.</summary>
    public int MaxCycles { get; set; } = 0;

    // ---------------- luu / doc ----------------
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public void Save(string path = null)
    {
        path ??= DefaultPath;
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }

    public static BotConfig Load(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<BotConfig>(File.ReadAllText(path)) ?? new BotConfig();
        }
        catch { /* config hong -> dung mac dinh da do duoc */ }
        return new BotConfig();
    }

    public IEnumerable<int> SampleYs()
    {
        if (BarSamples <= 1) { yield return (BarYTop + BarYBottom) / 2; yield break; }
        double step = (BarYBottom - BarYTop) / (double)(BarSamples - 1);
        for (int i = 0; i < BarSamples; i++)
            yield return (int)Math.Round(BarYTop + i * step);
    }
}
