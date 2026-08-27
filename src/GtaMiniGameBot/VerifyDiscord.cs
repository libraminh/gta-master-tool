using System.Text.Json;

namespace GtaMiniGameBot;

/// <summary>
/// Tự kiểm tin nhắn Discord mà KHÔNG cần mạng và không cần vào game.
///
/// Phần dễ sai của tính năng này không nằm ở chỗ POST — nó nằm ở nội dung: số -1 ("chưa đọc
/// được") lọt ra thành "-1.0 kg" trên điện thoại, ping đặt nhầm vào embed nên không kêu, hoặc
/// ping bắn cả khi chính người dùng vừa bấm Dừng. Mỗi ca dưới đây khoá đúng một trong các lỗi đó.
///
/// Chạy: GtaMiniGameBot.exe --verify-discord          (dựng tin, không ra mạng)
///       GtaMiniGameBot.exe --verify-discord --live   (POST thật bằng cấu hình đang lưu)
/// </summary>
internal static class VerifyDiscord
{
    private static int _fail;

    public static int Run(string[] args)
    {
        if (args.Length > 1 && args[1].Equals("--live", StringComparison.OrdinalIgnoreCase))
            return Live();

        Console.WriteLine("== tự kiểm báo Discord ==");
        Console.WriteLine();

        CheckUrlFilter();
        CheckDuration();
        CheckFullSession();
        CheckShortSession();
        CheckNoOcr();
        CheckDumpOff();
        CheckUserStopped();
        CheckNoUserId();
        CheckMentionInjection();
        CheckNormalizeTurnsOff();
        CheckNoFishStop();
        CheckNoFishAreaStop();
        CheckNoWaterStop();
        CheckAlert();

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "TAT CA DAT" : $"HONG {_fail} ca");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- cac ca

    private static void CheckUrlFilter()
    {
        Console.WriteLine("-- nhan dang webhook URL --");
        Ok("nhận URL thật", DiscordNotifier.IsWebhookUrl(
            "https://discord.com/api/webhooks/123456789/abcDEF-_ghi"));
        Ok("nhận discordapp.com", DiscordNotifier.IsWebhookUrl(
            "https://discordapp.com/api/webhooks/1/x"));
        Ok("từ chối rỗng", !DiscordNotifier.IsWebhookUrl(""));
        Ok("từ chối null", !DiscordNotifier.IsWebhookUrl(null));
        // Ke tan cong dan mot URL la vao day thi app se POST toan bo so lieu phien sang do.
        Ok("từ chối host lạ", !DiscordNotifier.IsWebhookUrl("https://evil.example/api/webhooks/1/x"));
        Ok("từ chối http trần", !DiscordNotifier.IsWebhookUrl("http://discord.com/api/webhooks/1/x"));
        // "discord.com.evil.example" bat dau bang chuoi con "discord.com" nhung khong phai Discord.
        Ok("từ chối tên miền đội lốt",
            !DiscordNotifier.IsWebhookUrl("https://discord.com.evil.example/api/webhooks/1/x"));
        Console.WriteLine();
    }

    private static void CheckDuration()
    {
        Console.WriteLine("-- doi thoi luong --");
        Eq("42 giây", "42s", DiscordNotifier.FormatDuration(42_000));
        Eq("8 phút 30", "8m 30s", DiscordNotifier.FormatDuration(510_000));
        Eq("2h 14m", "2h 14m", DiscordNotifier.FormatDuration(8_064_000));
        Eq("âm về 0", "0s", DiscordNotifier.FormatDuration(-5));
        Console.WriteLine();
    }

    private static void CheckFullSession()
    {
        Console.WriteLine("-- phien day du, cop day (BagFull) --");
        var doc = Build(Cfg("123456789012345678"), FishingStopReason.BagFull, new FishingState
        {
            SessionMs = 8_064_000,
            Catches = 137,
            Released = 22,
            BagKg = 28.6,
            BagCapKg = 30.0,
            TrunkFreeKg = 0.4,
            TrunkCapKg = 210,
            TrunkFull = true,
            DumpOn = true
        });
        if (doc == null) return;

        string content = doc.RootElement.GetProperty("content").GetString();
        Ok("có ping trong content", content.Contains("<@123456789012345678>"));
        Ok("content dưới 2000 ký tự", content.Length <= 2000);
        HasFields(doc, "Thời lượng", "Cá giữ", "Ba lô", "Cốp");
        Eq("thời lượng", "2h 14m", FieldValue(doc, "Thời lượng"));
        Eq("cá giữ", "137 con (thả 22) · 61 con/giờ", FieldValue(doc, "Cá giữ"));
        Eq("ba lô", "28.6 / 30.0 kg", FieldValue(doc, "Ba lô"));
        Eq("cốp", "còn trống 0.4 / 210 kg — ĐẦY", FieldValue(doc, "Cốp"));
        Ok("viền xanh", doc.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32() == 0x3BA55D);
        Console.WriteLine();
    }

    private static void CheckShortSession()
    {
        // Duoi 60 giay thi CatchesPerHour = -1. In ra la thanh "-1 con/gio".
        Console.WriteLine("-- phien 10 giay: khong duoc co con/gio --");
        var doc = Build(Cfg("1"), FishingStopReason.Error, new FishingState
        {
            SessionMs = 10_000,
            Catches = 1,
            BagKg = 3.2,
            BagCapKg = 30.0,
            DumpOn = true,
            TrunkFreeKg = 50,
            TrunkCapKg = 60
        });
        if (doc == null) return;

        Eq("cá giữ không kèm tốc độ", "1 con", FieldValue(doc, "Cá giữ"));
        Ok("viền đỏ", doc.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32() == 0xED4245);
        Console.WriteLine();
    }

    private static void CheckNoOcr()
    {
        // OCR chua doc duoc kg: BagKg va TrunkFreeKg deu la -1.
        Console.WriteLine("-- chua can duoc kg: bo han o thay vi in -1 --");
        var doc = Build(Cfg("1"), FishingStopReason.TrunkDump, new FishingState
        {
            SessionMs = 600_000,
            Catches = 12,
            DumpOn = true
        });
        if (doc == null) return;

        Ok("không có ô Ba lô", FieldValue(doc, "Ba lô") == null);
        Ok("không có ô Cốp", FieldValue(doc, "Cốp") == null);
        Ok("cả tin không chứa -1", !Raw(doc).Contains("-1"));
        Console.WriteLine();
    }

    private static void CheckDumpOff()
    {
        Console.WriteLine("-- tat do cop: khong bao so cop --");
        var doc = Build(Cfg("1"), FishingStopReason.BagFull, new FishingState
        {
            SessionMs = 600_000,
            Catches = 12,
            BagKg = 29.1,
            BagCapKg = 30.0,
            DumpOn = false,
            TrunkFreeKg = 7,      // con so cu con sot lai, khong duoc tin
            TrunkCapKg = 60
        });
        if (doc == null) return;

        Ok("có ô Ba lô", FieldValue(doc, "Ba lô") != null);
        Ok("không có ô Cốp", FieldValue(doc, "Cốp") == null);
        Console.WriteLine();
    }

    private static void CheckUserStopped()
    {
        // Tu bam Dung = dang ngoi truoc may. Van gui tin, nhung khong duoc rung dien thoai.
        Console.WriteLine("-- tu bam Dung: gui tin nhung KHONG ping --");
        var doc = Build(Cfg("123456789012345678"), FishingStopReason.UserStopped, new FishingState
        {
            SessionMs = 300_000,
            Catches = 5
        });
        if (doc == null) return;

        Ok("content không có mention", !doc.RootElement.GetProperty("content").GetString().Contains("<@"));
        Ok("allowed_mentions.users rỗng",
            doc.RootElement.GetProperty("allowed_mentions").GetProperty("users").GetArrayLength() == 0);
        Ok("viền xám", doc.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32() == 0x747F8D);
        Console.WriteLine();
    }

    private static void CheckNoUserId()
    {
        Console.WriteLine("-- chua co User ID: gui im lang --");
        var doc = Build(Cfg(""), FishingStopReason.BagFull, new FishingState { SessionMs = 60_000 });
        if (doc == null) return;

        Ok("content không có mention", !doc.RootElement.GetProperty("content").GetString().Contains("<@"));
        Ok("vẫn có tiêu đề", doc.RootElement.GetProperty("embeds")[0].GetProperty("title")
            .GetString().StartsWith("Đã dừng"));
        Console.WriteLine();
    }

    private static void CheckMentionInjection()
    {
        // Normalize loc User ID con chu so, nhung BuildJson khong duoc dua vao mot minh dieu do:
        // allowed_mentions phai chan @everyone du noi dung co lot chuoi gi.
        Console.WriteLine("-- chan ping ca server --");
        var doc = Build(Cfg("123"), FishingStopReason.Error, new FishingState { SessionMs = 60_000 });
        if (doc == null) return;

        var am = doc.RootElement.GetProperty("allowed_mentions");
        Ok("parse rỗng (chặn @everyone/@here/role)", am.GetProperty("parse").GetArrayLength() == 0);
        Ok("chỉ cho phép đúng một user",
            am.GetProperty("users").GetArrayLength() == 1 &&
            am.GetProperty("users")[0].GetString() == "123");
        Console.WriteLine();
    }

    private static void CheckNormalizeTurnsOff()
    {
        Console.WriteLine("-- Normalize don dep cau hinh --");
        var cfg = new FishingConfig
        {
            DiscordNotifyEnabled = true,
            DiscordWebhookUrl = "  https://evil.example/hook  ",
            DiscordUserId = "<@ 123 456 >"
        };
        cfg.Normalize();
        Ok("URL lạ thì tắt cờ báo", !cfg.DiscordNotifyEnabled);
        Eq("User ID lọc còn chữ số", "123456", cfg.DiscordUserId);

        var good = new FishingConfig
        {
            DiscordNotifyEnabled = true,
            DiscordWebhookUrl = "  https://discord.com/api/webhooks/1/x  "
        };
        good.Normalize();
        Ok("URL thật thì giữ cờ", good.DiscordNotifyEnabled);
        Eq("URL đã cắt khoảng trắng", "https://discord.com/api/webhooks/1/x", good.DiscordWebhookUrl);
        Console.WriteLine();
    }

    private static void CheckNoFishStop()
    {
        // Ly do dung moi. Quen mot nhanh trong TenLyDo la no roi vao "_ => lỗi" khong tieng dong,
        // va tin Discord chi ghi "Đã dừng — lỗi" — dung cai lam mat het gia tri cua canh bao.
        Console.WriteLine("-- dung vi sai can / sai do sau --");
        Eq("TenLyDo không rơi vào mặc định", "không có cá hợp cần và độ sâu",
            FishingBot.TenLyDo(FishingStopReason.NoFishMatch));

        var doc = Build(Cfg("123456789012345678"), FishingStopReason.NoFishMatch, new FishingState
        {
            SessionMs = 45_000,
            Catches = 0,
            DumpOn = true
        });
        if (doc == null) return;

        string content = doc.RootElement.GetProperty("content").GetString();
        Ok("CÓ ping (khác hẳn tin cảnh báo)", content.Contains("<@123456789012345678>"));
        Ok("tiêu đề riêng, không đọc trơ trọi", content.Contains("sai cần hoặc sai độ sâu"));
        Ok("viền đỏ", doc.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32() == 0xED4245);
        Ok("không có ô Cá giữ (chưa câu được con nào)", FieldValue(doc, "Cá giữ") == null);
        Console.WriteLine();
    }

    private static void CheckNoFishAreaStop()
    {
        // Chung chuoi dem va chung tran voi "sai can", nhung PHAI ra tin khac: mot cai bao doi
        // can, mot cai bao di cho khac. Lan lon la nguoi dung sua nham thu.
        Console.WriteLine("-- dung vi khu vuc het ca --");
        Eq("TenLyDo không rơi vào mặc định", "khu vực này hết cá",
            FishingBot.TenLyDo(FishingStopReason.NoFishArea));

        var doc = Build(Cfg("123456789012345678"), FishingStopReason.NoFishArea, new FishingState
        {
            SessionMs = 30_000,
            DumpOn = true
        });
        if (doc == null) return;

        string content = doc.RootElement.GetProperty("content").GetString();
        Ok("CÓ ping", content.Contains("<@123456789012345678>"));
        Ok("tiêu đề nói khu vực hết cá", content.Contains("khu vực này hết cá"));
        Ok("KHÔNG lẫn sang tiêu đề sai cần", !content.Contains("sai cần"));
        Ok("viền đỏ", doc.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32() == 0xED4245);
        Console.WriteLine();
    }

    private static void CheckNoWaterStop()
    {
        Console.WriteLine("-- dung vi da roi mep nuoc --");
        Eq("TenLyDo không rơi vào mặc định", "không đứng gần mặt nước",
            FishingBot.TenLyDo(FishingStopReason.NoWater));

        var doc = Build(Cfg("123456789012345678"), FishingStopReason.NoWater, new FishingState
        {
            SessionMs = 3_600_000,
            Catches = 64,
            BagKg = 14.2,
            BagCapKg = 30.0,
            DumpOn = true,
            TrunkFreeKg = 88,
            TrunkCapKg = 210
        });
        if (doc == null) return;

        string content = doc.RootElement.GetProperty("content").GetString();
        Ok("CÓ ping", content.Contains("<@123456789012345678>"));
        Ok("tiêu đề riêng, không lẫn với sai cần", content.Contains("không đứng gần mặt nước"));
        Ok("không lẫn tiêu đề sai cần", !content.Contains("sai cần"));
        Ok("viền đỏ", doc.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32() == 0xED4245);
        // Khac han ca "sai can": day co the la mot phien da chay ca tieng va bat duoc nhieu ca
        // roi moi bi keo khoi mep nuoc — so lieu phien phai con nguyen.
        Eq("giữ số liệu phiên", "64 con · 64 con/giờ", FieldValue(doc, "Cá giữ"));
        Console.WriteLine();
    }

    private static void CheckAlert()
    {
        // Tin canh bao: bot VAN DANG CHAY. Khong duoc ping — tin dung phien (co ping) co the toi
        // chi vai giay sau, rung dien thoai hai lan la phien.
        Console.WriteLine("-- canh bao giua chung --");
        string json;
        try
        {
            json = DiscordNotifier.BuildAlertJson(
                "Không có cá phù hợp với cần và độ sâu",
                "đang thử câu lại 2 lần nữa",
                new DateTime(2026, 8, 27, 22, 32, 0));
        }
        catch (Exception ex) { Bad("dựng cảnh báo", "ném " + ex.Message); return; }

        Console.WriteLine("   " + json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { Bad("JSON hợp lệ", ex.Message); return; }

        Eq("content rỗng", "", doc.RootElement.GetProperty("content").GetString());
        Ok("cả tin không có mention nào", !json.Contains("<@"));
        var am = doc.RootElement.GetProperty("allowed_mentions");
        Ok("parse rỗng (chặn @everyone/@here/role)", am.GetProperty("parse").GetArrayLength() == 0);
        Ok("users rỗng", am.GetProperty("users").GetArrayLength() == 0);

        var embed = doc.RootElement.GetProperty("embeds")[0];
        Ok("viền vàng cảnh báo", embed.GetProperty("color").GetInt32() == 0xFAA61A);
        Ok("có mô tả", embed.GetProperty("description").GetString() == "đang thử câu lại 2 lần nữa");
        Ok("không kèm số liệu phiên", !embed.TryGetProperty("fields", out _));

        // Bo trong phan mo ta thi bo han truong, khong de "description": "" tro tren Discord.
        string bare = DiscordNotifier.BuildAlertJson("Chỉ có tiêu đề", "  ", new DateTime(2026, 8, 27));
        using var bareDoc = JsonDocument.Parse(bare);
        Ok("mô tả trắng thì bỏ hẳn trường",
            !bareDoc.RootElement.GetProperty("embeds")[0].TryGetProperty("description", out _));
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- duong that

    private static int Live()
    {
        Console.WriteLine("== gui that bang cau hinh dang luu ==");
        var cfg = FishingConfig.Load();
        if (!DiscordNotifier.IsWebhookUrl(cfg.DiscordWebhookUrl))
        {
            Console.WriteLine("chua dan webhook URL trong fishing.json — mo app, bam Cau hinh Discord…");
            return 2;
        }

        var st = new FishingState
        {
            SessionMs = 8_064_000,
            Catches = 137,
            Released = 22,
            BagKg = 28.6,
            BagCapKg = 30.0,
            TrunkFreeKg = 0.4,
            TrunkCapKg = 210,
            TrunkFull = true,
            DumpOn = true
        };
        string json = DiscordNotifier.BuildJson(cfg, FishingStopReason.BagFull, st, DateTime.Now);
        Console.WriteLine(json);

        string problem = DiscordNotifier.Post(cfg.DiscordWebhookUrl, json);
        Console.WriteLine(problem ?? "da gui — kiem tra kenh Discord");
        return problem == null ? 0 : 1;
    }

    // ---------------------------------------------------------------- tro giup

    private static FishingConfig Cfg(string userId) => new()
    {
        DiscordNotifyEnabled = true,
        DiscordWebhookUrl = "https://discord.com/api/webhooks/1/x",
        DiscordUserId = userId
    };

    /// <summary>Dựng tin rồi parse lại — parse hỏng là JSON hỏng, và đó cũng là một ca kiểm.</summary>
    private static JsonDocument Build(FishingConfig cfg, FishingStopReason r, FishingState st)
    {
        string json;
        try { json = DiscordNotifier.BuildJson(cfg, r, st, new DateTime(2026, 8, 27, 14, 32, 0)); }
        catch (Exception ex) { Bad("dựng tin", "ném " + ex.Message); return null; }

        Console.WriteLine("   " + json);
        try { return JsonDocument.Parse(json); }
        catch (Exception ex) { Bad("JSON hợp lệ", ex.Message); return null; }
    }

    private static string Raw(JsonDocument doc) => doc.RootElement.GetRawText();

    private static string FieldValue(JsonDocument doc, string name)
    {
        foreach (var f in doc.RootElement.GetProperty("embeds")[0].GetProperty("fields").EnumerateArray())
            if (f.GetProperty("name").GetString() == name)
                return f.GetProperty("value").GetString();
        return null;
    }

    private static void HasFields(JsonDocument doc, params string[] names)
    {
        foreach (string n in names)
            Ok("có ô " + n, FieldValue(doc, n) != null);
    }

    private static void Ok(string what, bool pass)
    {
        Console.WriteLine((pass ? "   [dat]  " : "   [HONG] ") + what);
        if (!pass) _fail++;
    }

    private static void Eq(string what, string expect, string actual)
    {
        bool pass = expect == actual;
        Console.WriteLine((pass ? "   [dat]  " : "   [HONG] ") + what +
                          (pass ? $" = \"{actual}\"" : $" — cho \"{expect}\", nhan \"{actual}\""));
        if (!pass) _fail++;
    }

    private static void Bad(string what, string why)
    {
        Console.WriteLine($"   [HONG] {what} — {why}");
        _fail++;
    }
}
