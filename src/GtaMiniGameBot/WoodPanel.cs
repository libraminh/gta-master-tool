namespace GtaMiniGameBot;

/// <summary>
/// Tab Thợ mộc. Theo khuôn <see cref="MinerPanel"/> — khung thô dùng Dock, bên trong từng khung
/// đặt tuyệt đối — thêm phần hiệu chuẩn ROI và một đồng hồ xem TRỰC TIẾP điểm NCC lúc đang dừng.
/// Người dùng tự thấy ngưỡng có ăn không thay vì phải bật bot lên rồi đoán qua log.
/// </summary>
internal sealed class WoodPanel : UserControl
{
    private readonly WoodConfig _cfg = WoodConfig.Load();
    private Screen _screen;
    private WoodProfile _profile;
    private WoodBot _bot;

    private readonly Label _status = new();
    private readonly Label _calib = new();
    private readonly Label _live = new();
    private readonly Label _note = new();
    private readonly DarkButton _btnSetup = new();
    private readonly DarkButton _btnToggle = new();
    private readonly LogView _log = new();
    private readonly System.Windows.Forms.Timer _watch = new();

    private WoodReader _monitor;
    private string _jobKey = HotkeyText.Job();
    private int _chops;

    public bool IsRunning => _bot is { Running: true };

    public event Action<bool> RunningChanged;

    public WoodPanel()
    {
        Font = Theme.Body;
        Dock = DockStyle.Fill;
        BackColor = Theme.Ground;

        _screen = FishingConfig.Prefer2kOrPrimary();
        _profile = _cfg.GetOrCreate(_screen);
        _cfg.Save();

        BuildUi();

        _watch.Interval = 250;
        _watch.Tick += (_, _) => Watch();

        Append($"màn hình: {_profile.Key} ({_screen.DeviceName})");
        Append(_profile.IsCalibrated
            ? "đã khoanh vùng — bot đọc HUD để biết lúc nào bấm E."
            : "CHƯA khoanh vùng — bấm “Khoanh vùng HUD…”. Chưa khoanh thì bot chỉ gõ E mù theo nhịp.");
        Append($"{_jobKey} = bật/tắt cày.");
        Append("Đứng sát gốc cây tới khi hiện “[E] KHAI THÁC” rồi mới bật.");

        RefreshCalib();
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        _log.Dock = DockStyle.Fill;
        Controls.Add(_log);

        Controls.Add(BuildSettings());
        Controls.Add(BuildCommandBar());
    }

    private DrawPanel BuildCommandBar()
    {
        var bar = new DrawPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.Px(56),
            BackColor = Theme.Surface
        };

        _status.SetBounds(Theme.Px(16), Theme.Px(16), Theme.Px(360), Theme.Px(26));
        _status.Font = Theme.StateBig;
        _status.BackColor = Theme.Surface;
        _status.ForeColor = Theme.Head;
        _status.Text = "Đang dừng";
        bar.Controls.Add(_status);

        _btnToggle.Text = $"Bật  ({_jobKey})";
        _btnToggle.Primary = true;
        _btnToggle.Font = Theme.PhaseBig;
        _btnToggle.Click += (_, _) => Toggle();
        _btnToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnToggle.SetBounds(bar.Width - Theme.Px(180), Theme.Px(13), Theme.Px(164), Theme.Px(32));
        bar.Controls.Add(_btnToggle);

        return bar;
    }

    private DrawPanel BuildSettings()
    {
        var host = new DrawPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.Px(214),
            BackColor = Theme.Ground
        };

        int w = Theme.Px(620);

        var box = new DarkGroup
        {
            Title = "Nhận dạng HUD",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(8), w, Theme.Px(104))
        };
        host.Controls.Add(box);

        _btnSetup.Text = "Khoanh vùng HUD…";
        _btnSetup.SetBounds(Theme.Px(12), Theme.Px(22), Theme.Px(170), Theme.Px(28));
        _btnSetup.Click += (_, _) => OpenSetup();
        box.Controls.Add(_btnSetup);

        _calib.AutoSize = false;
        _calib.Font = Theme.DataSm;
        _calib.BackColor = Theme.Surface;
        _calib.SetBounds(Theme.Px(194), Theme.Px(28), w - Theme.Px(206), Theme.Px(18));
        box.Controls.Add(_calib);

        Lab(box, "Điểm đọc trực tiếp (lúc đang dừng, game đang mở):",
            Theme.Px(12), Theme.Px(58), w - Theme.Px(24));

        _live.AutoSize = false;
        _live.Font = Theme.Data;
        _live.BackColor = Theme.Surface;
        _live.ForeColor = Theme.Dim;
        _live.SetBounds(Theme.Px(12), Theme.Px(78), w - Theme.Px(24), Theme.Px(18));
        _live.Text = "—";
        box.Controls.Add(_live);

        var help = new DarkGroup
        {
            Title = "Cách dùng",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(120), w, Theme.Px(86))
        };
        host.Controls.Add(help);

        _note.AutoSize = false;
        _note.Font = Theme.Body;
        _note.BackColor = Theme.Surface;
        _note.ForeColor = Theme.Dim;
        _note.SetBounds(Theme.Px(12), Theme.Px(22), w - Theme.Px(24), Theme.Px(58));
        help.Controls.Add(_note);
        RefreshNote();

        return host;
    }

    private static void Lab(Control host, string text, int x, int y, int w)
    {
        host.Controls.Add(new Label
        {
            Text = text,
            Font = Theme.Body,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Bounds = new Rectangle(x, y, w, Theme.Px(18))
        });
    }

    private void RefreshNote() =>
        _note.Text =
            "Bot bấm E khi thấy “KHAI THÁC”, ngồi im khi chữ đổi thành “ĐANG KHAI THÁC”, xong nhát thì bấm tiếp.\r\n" +
            $"{_jobKey} bật/tắt được ngay trong game, miễn là đang đứng ở tab này.\r\n" +
            "Cây hết gỗ thì bot dừng và báo — nó không tự đi tìm cây khác.";

    private void RefreshCalib()
    {
        _calib.Text = _profile.DescribeGaps();
        _calib.ForeColor = _profile.IsCalibrated ? Theme.GoodText : Theme.WarnText;
    }

    private void OpenSetup()
    {
        if (IsRunning) { Append("đang chạy — tắt trước khi khoanh lại"); return; }

        StopWatch();
        using (var f = new WoodSetupForm(_cfg, _screen, _profile))
            f.ShowDialog(FindForm());

        _profile = _cfg.GetOrCreate(_screen);
        RefreshCalib();
        Append("đã đóng cửa sổ khoanh vùng — " + _profile.DescribeGaps());
        StartWatch();
    }

    // ---------------------------------------------------------------- xem truc tiep

    /// <summary>
    /// Mở đồng hồ xem điểm. Chỉ chạy lúc bot ĐANG DỪNG: lúc chạy thì chính bot đã bắn
    /// <see cref="WoodBot.SnapshotReady"/>, mở thêm một reader nữa là chụp màn hai lần mỗi vòng.
    /// </summary>
    private void StartWatch()
    {
        if (IsRunning || !Visible || !_profile.IsCalibrated) return;
        _monitor ??= WoodReader.Open(_cfg, _screen, _profile);
        _watch.Start();
    }

    private void StopWatch()
    {
        _watch.Stop();
        _monitor?.Dispose();
        _monitor = null;
        _live.Text = "—";
        _live.ForeColor = Theme.Dim;
    }

    private void Watch()
    {
        if (IsRunning || _monitor is null) return;
        try { ShowSnapshot(_monitor.Read()); }
        catch { StopWatch(); }
    }

    private void ShowSnapshot(WoodSnapshot s)
    {
        _live.Text = s.Describe();
        _live.ForeColor = s.Ready ? Theme.Good : Theme.Dim;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) StartWatch(); else StopWatch();
    }

    // ---------------------------------------------------------------- chay

    public void StartFromHotkey()
    {
        if (!IsRunning) StartBot();
    }

    public void StopFromHotkey() => _bot?.Stop();

    private void Toggle()
    {
        if (IsRunning) StopFromHotkey();
        else StartBot();
    }

    /// <summary>Dừng bot và nhả phím khi đổi job, giữ panel để quay lại.</summary>
    public void StopWork()
    {
        _bot?.StopAndWait();
        StopWatch();
        HeldKeys.ReleaseAll();
    }

    /// <summary>Dọn hết khi đóng app.</summary>
    public void Shutdown()
    {
        _bot?.StopAndWait();
        StopWatch();
        _watch.Dispose();
        HeldKeys.ReleaseAll();
    }

    private void StartBot()
    {
        if (_bot is { Running: true }) return;

        // Doi man hinh hay do phan giai giua phien: doc lai profile truoc khi chay, khong dung
        // ban da cache tu luc dung panel.
        _screen = FishingConfig.Prefer2kOrPrimary();
        _profile = _cfg.GetOrCreate(_screen);
        RefreshCalib();

        StopWatch();

        _chops = 0;
        _bot = new WoodBot(_cfg, _screen, _profile);
        _bot.Log += s => Post(() => Append(s));
        _bot.SnapshotReady += s => Post(() => ShowSnapshot(s));
        _bot.ChopsChanged += n => Post(() =>
        {
            _chops = n;
            _status.Text = $"Đang chặt — {_chops} nhát";
        });
        _bot.Stopped += (r, msg) => Post(() =>
        {
            _status.Text = $"Đã dừng — {WoodBot.TenLyDo(r)}   ({_chops} nhát)";
            _status.ForeColor = r == WoodStopReason.UserStopped ? Theme.Head : Theme.Bad;
            if (r != WoodStopReason.UserStopped) Append("dừng: " + msg);
            SetRunningUi(false);
            StartWatch();
        });

        _status.Text = "Đang chặt";
        _status.ForeColor = Theme.Accent;
        SetRunningUi(true);
        Append("--- bắt đầu cày ---");
        _bot.Start();
    }

    /// <summary>Nguoi dung doi phim bat/tat job trong tab Tiện ích.</summary>
    public void SetJobHotkeyText(string text)
    {
        _jobKey = text;
        _btnToggle.Text = IsRunning ? $"Tắt  ({_jobKey})" : $"Bật  ({_jobKey})";
        RefreshNote();
    }

    private void SetRunningUi(bool running)
    {
        _btnToggle.Text = running ? $"Tắt  ({_jobKey})" : $"Bật  ({_jobKey})";
        _btnSetup.Enabled = !running;
        RunningChanged?.Invoke(running);
    }

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    private void Append(string line) => _log.Append(line);
}
