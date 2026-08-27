using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace GtaMiniGameBot;

/// <summary>
/// Báo về Discord khi một phiên câu kết thúc — bot chạy hàng giờ không người trông, tiếng chuông
/// Windows ở <see cref="FishingPanel"/> chỉ nghe được khi đang ngồi trước máy.
///
/// Đi bằng WEBHOOK chứ không bằng bot token. Webhook chỉ ghi được vào một kênh trong server và
/// không DM được ai, nhưng đổi lại lộ URL thì cùng lắm bị spam đúng kênh đó — lộ bot token là
/// mất quyền điều khiển bot. Muốn nổ chuông điện thoại thì chèn "&lt;@userId&gt;" vào `content`;
/// mention nằm trong embed KHÔNG kêu, đây là chỗ hay nhầm.
///
/// Lớp này cố tình không biết gì về WinForms để <see cref="VerifyDiscord"/> gọi được, và mọi
/// đường ra đều nuốt exception: chuyện gửi tin không bao giờ được làm sập phiên câu.
/// </summary>
internal static class DiscordNotifier
{
    /// <summary>
    /// Một instance dùng lại cho cả đời app. Chỗ dùng HttpClient còn lại
    /// (<see cref="ItemIconExtractor"/>) tạo rồi bỏ vì nó chạy một lần; cái này chạy lại mỗi
    /// phiên nên tạo mới liên tục là đường tới cạn socket.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Trần độ dài trường content của Discord.</summary>
    private const int ContentMax = 2000;

    private static readonly JsonSerializerOptions Opts = new()
    {
        // Không escape non-ASCII: tiếng Việt trong tin nhắn phải đọc được khi soi JSON bằng mắt,
        // và Discord nhận UTF-8 thẳng.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Mau vien trai cua embed, dang int nhu Discord doi.
    private const int ColorGood = 0x3BA55D;   // xanh la  — phien chay het muc
    private const int ColorMuted = 0x747F8D;  // xam      — nguoi dung tu bam dung
    private const int ColorBad = 0xED4245;    // do       — su co
    private const int ColorWarn = 0xFAA61A;   // vang     — canh bao giua chung, bot van chay

    /// <summary>
    /// URL có đúng là webhook Discord không. Dùng ở cả <see cref="FishingConfig.Normalize"/>
    /// lẫn hộp thoại cấu hình, nên để một chỗ duy nhất.
    /// </summary>
    public static bool IsWebhookUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://ptb.discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://canary.discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Dựng thân JSON của tin nhắn. Tách hẳn khỏi phần gửi để <c>--verify-discord</c> soi được
    /// nội dung mà không cần mạng — đấy là nửa dễ sai: số -1 lọt ra ngoài, ping đặt nhầm chỗ.
    /// </summary>
    public static string BuildJson(FishingConfig cfg, FishingStopReason reason, FishingState st, DateTime now)
    {
        st ??= FishingState.Idle;

        // Bam dung thi nguoi dung dang ngoi day roi — van gui tin de luu vet, nhung khong rung
        // dien thoai. Cung cach phan loai ma handler Stopped ben panel dang dung.
        bool ping = reason != FishingStopReason.UserStopped &&
                    !string.IsNullOrEmpty(cfg?.DiscordUserId);

        string head = reason switch
        {
            FishingStopReason.BagFull => "🎣 Đầy hàng rồi — đi bán cá thôi",
            FishingStopReason.NoFishMatch => "⚠️ Bot dừng — sai cần hoặc sai độ sâu",
            FishingStopReason.NoFishArea => "⚠️ Bot dừng — khu vực này hết cá",
            FishingStopReason.NoWater => "⚠️ Bot dừng — nhân vật không đứng gần mặt nước",
            _ => "🎣 Phiên câu đã kết thúc"
        };
        string content = ping ? $"<@{cfg.DiscordUserId}> {head}" : head;
        if (content.Length > ContentMax) content = content[..ContentMax];

        var fields = new List<object>
        {
            Field("Thời lượng", FormatDuration(st.SessionMs), inline: true)
        };

        // Moi o duoi day chi hien khi co so that. -1 la "chua biet" chu khong phai gia tri —
        // in ra la nguoi doc tuong bot dem duoc am mot con ca.
        if (st.Catches > 0 || st.Released > 0)
        {
            var sb = new StringBuilder();
            sb.Append(st.Catches).Append(" con");
            if (st.Released > 0) sb.Append(" (thả ").Append(st.Released).Append(')');
            if (st.CatchesPerHour >= 0) sb.Append(" · ").Append(st.CatchesPerHour.ToString("F0")).Append(" con/giờ");
            fields.Add(Field("Cá giữ", sb.ToString(), inline: true));
        }

        if (st.BagKg >= 0)
            fields.Add(Field("Ba lô",
                st.BagCapKg > 0 ? $"{st.BagKg:F1} / {st.BagCapKg:F1} kg" : $"{st.BagKg:F1} kg",
                inline: true));

        // Tat do cop thi khong co so cop nao co nghia ca.
        if (st.DumpOn && st.TrunkFreeKg >= 0)
        {
            string trunk = st.TrunkCapKg > 0
                ? $"còn trống {st.TrunkFreeKg:F1} / {st.TrunkCapKg:F0} kg"
                : $"còn trống {st.TrunkFreeKg:F1} kg";
            if (st.TrunkFull) trunk += " — ĐẦY";
            fields.Add(Field("Cốp", trunk, inline: true));
        }

        var payload = new Dictionary<string, object>
        {
            ["content"] = content,
            ["embeds"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["title"] = "Đã dừng — " + FishingBot.TenLyDo(reason),
                    ["color"] = ColorFor(reason),
                    ["fields"] = fields,
                    ["footer"] = new Dictionary<string, object>
                    {
                        ["text"] = now.ToString("dd/MM/yyyy HH:mm")
                    }
                }
            },
            // Chi cho phep ping DUNG mot nguoi. Khong co cai nay thi mot chuoi "@everyone" lot
            // vao noi dung la ca server an chuong.
            ["allowed_mentions"] = new Dictionary<string, object>
            {
                ["parse"] = Array.Empty<string>(),
                ["users"] = ping ? new[] { cfg.DiscordUserId } : Array.Empty<string>()
            }
        };

        return JsonSerializer.Serialize(payload, Opts);
    }

    /// <summary>
    /// Bắn tin rồi quên. Trả về NGAY: phần POST chạy trên ThreadPool vì hàm này được gọi từ
    /// luồng UI, mất mạng mà chờ hết 10 giây timeout là đơ cả cửa sổ.
    /// <paramref name="log"/> có thể được gọi từ luồng khác — bên gọi tự lo marshal về UI.
    /// </summary>
    public static void Notify(FishingConfig cfg, FishingStopReason reason, FishingState st, Action<string> log)
    {
        if (cfg == null || !cfg.DiscordNotifyEnabled) return;
        if (!IsWebhookUrl(cfg.DiscordWebhookUrl)) { Report(log, "chưa dán webhook URL hợp lệ"); return; }

        string url = cfg.DiscordWebhookUrl;
        string json;
        try
        {
            json = BuildJson(cfg, reason, st, DateTime.Now);
        }
        catch (Exception ex)
        {
            Report(log, "dựng tin lỗi: " + ex.Message);
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            string problem = Post(url, json);
            if (problem == null) Report(log, "đã gửi thông báo");
            else Report(log, problem);
        });
    }

    /// <summary>
    /// Cảnh báo rời: một chuyện đáng biết vừa xảy ra nhưng bot VẪN ĐANG CHẠY. Không kèm số liệu
    /// phiên (phiên chưa xong) và cố tình KHÔNG ping — tin dừng phiên mới là tin đáng rung điện
    /// thoại, mà nó có thể tới chỉ vài giây sau cái này.
    /// </summary>
    public static void NotifyAlert(FishingConfig cfg, string title, string detail, Action<string> log)
    {
        if (cfg == null || !cfg.DiscordNotifyEnabled) return;
        if (!IsWebhookUrl(cfg.DiscordWebhookUrl)) { Report(log, "chưa dán webhook URL hợp lệ"); return; }

        string url = cfg.DiscordWebhookUrl;
        string json;
        try
        {
            json = BuildAlertJson(title, detail, DateTime.Now);
        }
        catch (Exception ex)
        {
            Report(log, "dựng cảnh báo lỗi: " + ex.Message);
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            string problem = Post(url, json);
            Report(log, problem ?? "đã gửi cảnh báo");
        });
    }

    /// <summary>Tách khỏi <see cref="NotifyAlert"/> để tự kiểm được nội dung mà không cần mạng.</summary>
    public static string BuildAlertJson(string title, string detail, DateTime now)
    {
        var embed = new Dictionary<string, object>
        {
            ["title"] = "⚠️ " + (title ?? ""),
            ["color"] = ColorWarn,
            ["footer"] = new Dictionary<string, object> { ["text"] = now.ToString("dd/MM/yyyy HH:mm") }
        };
        if (!string.IsNullOrWhiteSpace(detail)) embed["description"] = detail;

        var payload = new Dictionary<string, object>
        {
            // content rong = khong ping ai, ke ca khi title/detail lo co chuoi "@everyone":
            // allowed_mentions duoi day chan tuyet doi.
            ["content"] = "",
            ["embeds"] = new[] { embed },
            ["allowed_mentions"] = new Dictionary<string, object>
            {
                ["parse"] = Array.Empty<string>(),
                ["users"] = Array.Empty<string>()
            }
        };

        return JsonSerializer.Serialize(payload, Opts);
    }

    /// <summary>
    /// POST đồng bộ. Trả về null nếu xong, ngược lại là câu mô tả lỗi đã sẵn sàng in ra.
    /// Không bao giờ ném — hộp thoại "Gửi thử" gọi thẳng vào đây để lấy kết quả.
    /// </summary>
    public static string Post(string url, string json)
    {
        try
        {
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = Http.PostAsync(url, body).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode) return null;

            // Doc than loi: Discord tra ly do ro rang (webhook bi xoa, token sai, rate limit).
            string detail = "";
            try { detail = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(); } catch { }
            if (detail.Length > 300) detail = detail[..300];
            return $"Discord trả {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                   (string.IsNullOrWhiteSpace(detail) ? "" : " — " + detail);
        }
        catch (Exception ex)
        {
            return "gửi lỗi: " + ex.Message;
        }
    }

    private static int ColorFor(FishingStopReason r) => r switch
    {
        FishingStopReason.BagFull => ColorGood,
        FishingStopReason.UserStopped => ColorMuted,
        _ => ColorBad
    };

    private static Dictionary<string, object> Field(string name, string value, bool inline) => new()
    {
        ["name"] = name,
        ["value"] = value,
        ["inline"] = inline
    };

    /// <summary>"2h 14m" / "8m 30s" / "42s" — người đọc trên điện thoại không cần mili giây.</summary>
    public static string FormatDuration(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    private static void Report(Action<string> log, string message)
    {
        BotLog.Write("discord", message);
        try { log?.Invoke(message); } catch { }
    }
}
