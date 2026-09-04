namespace GtaMiniGameBot;

/// <summary>
/// Dán webhook Discord và Discord User ID.
///
/// Nằm trong hộp thoại chứ không nằm thẳng trên panel vì hai lẽ: panel toàn control tối tự vẽ
/// mà bộ đó không có ô nhập text nào (chỉ DarkButton / DarkCheck / DarkSpin / DarkPick), và
/// webhook URL là thứ không nên phơi thường trực trên màn hình khi đang chia sẻ màn hay quay video.
/// </summary>
internal sealed class DiscordForm : Form
{
    private readonly FishingConfig _cfg;

    private readonly TextBox _url = new();
    private readonly TextBox _userId = new();
    private readonly Label _result = new();
    private readonly Button _test = new();

    public DiscordForm(FishingConfig cfg)
    {
        _cfg = cfg;

        Text = "Báo Discord khi phiên câu kết thúc";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(660, 268);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi();
    }

    private void BuildUi()
    {
        int y = 12;

        Controls.Add(new Label
        {
            Text = "Webhook chỉ gửi được vào MỘT kênh trong server, không nhắn riêng được. " +
                   "Lấy URL ở: Server Settings → Integrations → Webhooks → New Webhook → Copy Webhook URL.",
            Location = new Point(12, y),
            Size = new Size(636, 34),
            ForeColor = Color.DimGray
        });
        y += 42;

        Controls.Add(new Label { Text = "Webhook URL:", Location = new Point(12, y + 4), AutoSize = true });
        _url.SetBounds(110, y, 538, 24);
        _url.Text = _cfg.DiscordWebhookUrl;
        Controls.Add(_url);
        y += 32;

        Controls.Add(new Label { Text = "User ID:", Location = new Point(12, y + 4), AutoSize = true });
        _userId.SetBounds(110, y, 180, 24);
        _userId.Text = _cfg.DiscordUserId;
        Controls.Add(_userId);

        Controls.Add(new Label
        {
            Text = "để @ping cho nổ chuông điện thoại — bật Developer Mode trong Discord, " +
                   "chuột phải vào nick mình → Copy User ID. Để trống thì tin vẫn gửi nhưng im lặng.",
            Location = new Point(298, y - 2),
            Size = new Size(350, 34),
            ForeColor = Color.DimGray
        });
        y += 40;

        _test.Text = "Gửi thử";
        _test.SetBounds(110, y, 100, 28);
        _test.Click += (_, _) => SendTest();
        Controls.Add(_test);
        y += 36;

        _result.SetBounds(12, y, 636, 40);
        _result.ForeColor = Color.DimGray;
        Controls.Add(_result);
        y += 48;

        var save = new Button { Text = "Lưu", DialogResult = DialogResult.OK };
        save.SetBounds(446, y, 96, 30);
        save.Click += (_, _) => Apply();
        Controls.Add(save);

        var cancel = new Button { Text = "Huỷ", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(552, y, 96, 30);
        Controls.Add(cancel);

        AcceptButton = save;
        CancelButton = cancel;
    }

    /// <summary>
    /// Ghi vào config rồi lưu. <see cref="FishingConfig.Normalize"/> tự cắt khoảng trắng, lọc
    /// User ID còn chữ số, và tắt cờ báo nếu URL không đúng dạng webhook — nên gọi nó TRƯỚC khi
    /// lưu, không thì json giữ lại một địa chỉ rác.
    /// </summary>
    private void Apply()
    {
        _cfg.DiscordWebhookUrl = _url.Text;
        _cfg.DiscordUserId = _userId.Text;
        _cfg.Normalize();
        try { _cfg.Save(); }
        catch (Exception ex) { MessageBox.Show(this, "Lưu cấu hình lỗi: " + ex.Message, Text); }
    }

    /// <summary>
    /// Gửi một tin mẫu bằng đúng đường mà bot sẽ dùng. Đây là cách duy nhất người dùng tự xác
    /// nhận được URL dán đúng — sai một ký tự thì Discord trả 401 chứ không im lặng.
    /// </summary>
    private void SendTest()
    {
        string url = _url.Text.Trim();
        if (!DiscordNotifier.IsWebhookUrl(url))
        {
            Show("URL không đúng dạng webhook Discord (phải bắt đầu bằng https://discord.com/api/webhooks/…)",
                Color.Firebrick);
            return;
        }

        // Dung dung ham dung tin that, tren mot ban config tam — de nguoi dung thay CHINH XAC
        // cai tin ma bot se gui, khong phai mot chuoi "test" vo nghia.
        var probe = new FishingConfig
        {
            DiscordNotifyEnabled = true,
            DiscordWebhookUrl = url,
            DiscordUserId = new string(_userId.Text.Where(char.IsDigit).ToArray())
        };
        var st = new FishingState
        {
            SessionMs = 8_064_000,   // 2h 14m
            Catches = 137,
            Released = 22,
            BagKg = 28.6,
            BagCapKg = 30.0,
            TrunkFreeKg = 0.4,
            TrunkCapKg = 210,
            TrunkFull = true,
            DumpOn = true
        };

        _test.Enabled = false;
        Show("đang gửi…", Color.DimGray);

        // Khong chan luong UI: mat mang la cho het 10 giay timeout, cua so treo cung.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string json = DiscordNotifier.BuildJson(probe, FishingStopReason.BagFull, st, DateTime.Now);
            string problem = DiscordNotifier.Post(url, json);
            try
            {
                BeginInvoke(() =>
                {
                    _test.Enabled = true;
                    if (problem == null) Show("Đã gửi — kiểm tra kênh Discord.", Color.SeaGreen);
                    else Show(problem, Color.Firebrick);
                });
            }
            catch { }   // cua so dong truoc khi POST xong
        });
    }

    private void Show(string text, Color color)
    {
        _result.Text = text;
        _result.ForeColor = color;
    }
}
