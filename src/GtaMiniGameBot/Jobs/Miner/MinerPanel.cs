namespace GtaMiniGameBot;

internal sealed class MinerPanel : UserControl
{
    private readonly MinerConfig _cfg = MinerConfig.Load();
    private MinerBot _bot;

    private readonly Label _status = new();
    private readonly DarkSpin _tapMs = new();
    private readonly DarkCheck _holdShift = new();
    private readonly Label _note = new();
    private readonly DarkButton _btnToggle = new();
    private readonly LogView _log = new();
    private string _jobKey = HotkeyText.Job();

    public bool IsRunning => _bot is { Running: true };

    public event Action<bool> RunningChanged;

    public MinerPanel()
    {
        Font = Theme.Body;
        Dock = DockStyle.Fill;
        BackColor = Theme.Ground;

        BuildUi();

        Append($"cấu hình: bấm E mỗi {_cfg.TapEveryMs} ms (giữ {_cfg.TapHoldMs} ms)   |   " +
               $"giữ Left Shift: {(_cfg.HoldShift ? "có" : "không")}");
        Append($"{_jobKey} = bật/tắt cày.");
        Append("Đứng đúng chỗ đào trong game rồi mới bật — tool chỉ giữ phím, không tự tìm mỏ.");
    }

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
        HeldKeys.ReleaseAll();
    }

    /// <summary>Dọn hết khi đóng app.</summary>
    public void Shutdown()
    {
        _bot?.StopAndWait();
        HeldKeys.ReleaseAll();
    }

    // ---------------------------------------------------------------- UI

    /// <summary>
    /// Khung tho dung Dock nen chieu cao log khong con suy tu so 760 trong
    /// HomeForm.ClientSize nhu ban cu. Ben trong tung khung van dat tuyet doi.
    /// Thu tu Add: Fill truoc, roi Top theo thu tu nguoc voi thu tu nhin thay.
    /// </summary>
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
            Height = Theme.Px(206),
            BackColor = Theme.Ground
        };

        int w = Theme.Px(560);

        var box = new DarkGroup
        {
            Title = "Cài đặt",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(8), w, Theme.Px(88))
        };
        host.Controls.Add(box);

        Lab(box, "Bấm E mỗi", Theme.Px(12), Theme.Px(26), Theme.Px(84));

        _tapMs.SetBounds(Theme.Px(100), Theme.Px(22), Theme.Px(80), Theme.Px(24));
        _tapMs.Min = 50;
        _tapMs.Max = 5_000;
        _tapMs.Step = 50;
        _tapMs.SetValueQuiet(Math.Clamp(_cfg.TapEveryMs, _tapMs.Min, _tapMs.Max));
        box.Controls.Add(_tapMs);

        Lab(box, "ms   (200 = nhịp đo trong game; đổi được cả lúc đang chạy)",
            Theme.Px(188), Theme.Px(26), w - Theme.Px(200));

        // Gan sau khi da set gia tri de khoi ban ValueChanged luc dung UI.
        _tapMs.ValueChanged += OnTapMsChanged;

        _holdShift.SetBounds(Theme.Px(12), Theme.Px(54), Theme.Px(320), Theme.Px(22));
        _holdShift.Text = "Giữ Left Shift cùng W (chạy nước rút)";
        _holdShift.BackColor = Theme.Surface;
        _holdShift.SetCheckedQuiet(_cfg.HoldShift);
        _holdShift.CheckedChanged += OnHoldShiftChanged;
        box.Controls.Add(_holdShift);

        var help = new DarkGroup
        {
            Title = "Cách dùng",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(104), w, Theme.Px(92))
        };
        host.Controls.Add(help);

        _note.AutoSize = false;
        _note.Font = Theme.Body;
        _note.BackColor = Theme.Surface;
        _note.ForeColor = Theme.Dim;
        _note.SetBounds(Theme.Px(12), Theme.Px(22), w - Theme.Px(24), Theme.Px(62));
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

    private void RefreshNote()
    {
        _note.Text =
            $"Bật rồi thì tool giữ W{(_cfg.HoldShift ? " + Left Shift" : "")} và bấm E mỗi {_cfg.TapEveryMs} ms.\r\n" +
            $"{_jobKey} bật/tắt được ngay trong game, miễn là đang đứng ở tab này.\r\n" +
            "Alt-tab ra ngoài thì tự nhả phím và ngừng bấm; quay lại game là chạy tiếp.";
    }

    // ---------------------------------------------------------------- cai dat

    /// <summary>
    /// Luu ngay khi doi. Bot doc thang <see cref="_cfg"/> nen doi nhip an lien ca luc dang chay —
    /// ghi int la thao tac nguyen tu, khong can khoa.
    /// </summary>
    private void OnTapMsChanged()
    {
        _cfg.TapEveryMs = _tapMs.Value;
        _cfg.Save();
        RefreshNote();
        Append($"nhịp bấm E: {_cfg.TapEveryMs} ms");
    }

    /// <summary>
    /// Chi doi duoc luc DUNG: tat Shift giua chung thi vong lap thoi bam Shift xuong nhung
    /// khong ai nha cai dang giu — ket Shift trong game. <see cref="SetRunningUi"/> khoa o nay.
    /// </summary>
    private void OnHoldShiftChanged()
    {
        _cfg.HoldShift = _holdShift.Checked;
        _cfg.Save();
        RefreshNote();
        Append(_cfg.HoldShift ? "giữ Left Shift: có" : "giữ Left Shift: không");
    }

    // ---------------------------------------------------------------- chay

    private void StartBot()
    {
        if (_bot is { Running: true }) return;

        _bot = new MinerBot(_cfg);
        _bot.Log += s => Post(() => Append(s));
        _bot.Stopped += (r, msg) => Post(() =>
        {
            _status.Text = $"Đã dừng — {MinerBot.TenLyDo(r)}";
            _status.ForeColor = r == MinerStopReason.UserStopped ? Theme.Good : Theme.Bad;
            if (r != MinerStopReason.UserStopped) Append("dừng: " + msg);
            SetRunningUi(false);
        });

        _status.Text = "Đang cày";
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
        _holdShift.Enabled = !running;
        RunningChanged?.Invoke(running);
    }

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    private void Append(string line)
    {
        _log.Append(line);
        BotLog.Write("", line);
    }
}
