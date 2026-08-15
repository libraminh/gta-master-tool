using System.Text;

namespace GtaMiniGameBot;

internal sealed class MinerPanel : UserControl
{
    private readonly MinerConfig _cfg = MinerConfig.Load();
    private readonly Screen _screen = FishingConfig.Prefer2kOrPrimary();
    private MinerBot _bot;
    private MinerReader _monitor;

    private readonly Label _status = new();
    private readonly NumericUpDown _tapMs = new();
    private readonly CheckBox _holdRun = new();
    private readonly CheckBox _holdShift = new();
    private readonly Label _mining = new();
    private readonly Label _lift = new();
    private readonly Label _cash = new();
    private readonly Label _stats = new();
    private readonly CheckBox _watch = new();
    private readonly Button _btnSetup = new();
    private readonly Button _btnToggle = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private string _jobKey = HotkeyText.Job();

    public bool IsRunning => _bot is { Running: true };

    public event Action<bool> RunningChanged;

    public MinerPanel()
    {
        Font = new Font("Segoe UI", 9F);
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        BuildUi();

        _timer.Interval = 200;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Append($"màn hình {_screen.Bounds.Width}×{_screen.Bounds.Height}   |   " +
               $"bấm E mỗi {_cfg.TapEveryMs} ms (giữ {_cfg.TapHoldMs} ms)");
        Append($"{_jobKey} = bật/tắt cày.");
        Append("Chưa khoanh vùng thì tool gõ E mù. Khoanh xong nó biết lúc nào đang đào để ngừng gõ.");
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
        DropMonitor();
    }

    /// <summary>Dọn hết khi đóng app.</summary>
    public void Shutdown()
    {
        _timer.Stop();
        _bot?.StopAndWait();
        HeldKeys.ReleaseAll();
        DropMonitor();
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        int y = 12;
        const int w = 796;

        _status.SetBounds(12, y, w, 30);
        _status.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        _status.Text = "Đang dừng";
        Controls.Add(_status);
        y += 38;

        var read = new GroupBox { Text = "Đọc màn hình", Location = new Point(12, y), Size = new Size(w, 128) };
        Controls.Add(read);

        _mining.SetBounds(16, 24, 500, 20);
        _mining.Font = new Font("Consolas", 10F);
        read.Controls.Add(_mining);

        _lift.SetBounds(16, 46, 500, 20);
        _lift.Font = new Font("Consolas", 10F);
        read.Controls.Add(_lift);

        _cash.SetBounds(16, 68, 500, 20);
        _cash.Font = new Font("Consolas", 10F);
        read.Controls.Add(_cash);

        _stats.SetBounds(16, 96, 500, 20);
        _stats.Font = new Font("Consolas", 10F);
        _stats.Text = "0 lượt đào  |  0 chuyến giao";
        read.Controls.Add(_stats);

        _watch.SetBounds(536, 24, 244, 22);
        _watch.Text = "Theo dõi (chỉ đọc, không bấm)";
        _watch.Checked = true;
        read.Controls.Add(_watch);

        _btnSetup.SetBounds(536, 54, 244, 32);
        _btnSetup.Text = "Khoanh vùng HUD…";
        _btnSetup.Click += (_, _) => DoSetup();
        read.Controls.Add(_btnSetup);

        y += 138;

        var set = new GroupBox { Text = "Cài đặt", Location = new Point(12, y), Size = new Size(w, 92) };
        Controls.Add(set);

        set.Controls.Add(new Label { Text = "Bấm E mỗi", Location = new Point(16, 30), AutoSize = true });

        _tapMs.SetBounds(96, 26, 80, 24);
        _tapMs.Minimum = 50;
        _tapMs.Maximum = 5_000;
        _tapMs.Increment = 50;
        _tapMs.Value = Math.Clamp(_cfg.TapEveryMs, (int)_tapMs.Minimum, (int)_tapMs.Maximum);
        set.Controls.Add(_tapMs);

        set.Controls.Add(new Label
        {
            Text = "ms   (200 = nhịp đo trong game; đổi được cả lúc đang chạy)",
            Location = new Point(184, 30),
            AutoSize = true
        });

        // Gan sau khi da set .Value de khoi ban ValueChanged luc dung UI.
        _tapMs.ValueChanged += (_, _) => OnTapMsChanged();

        _holdRun.SetBounds(16, 58, 260, 22);
        _holdRun.Text = "Giữ W (tự chạy tới)";
        _holdRun.Checked = _cfg.HoldRun;
        _holdRun.CheckedChanged += (_, _) => OnHoldChanged();
        set.Controls.Add(_holdRun);

        _holdShift.SetBounds(288, 58, 300, 22);
        _holdShift.Text = "Thêm Left Shift (chạy nước rút)";
        _holdShift.Checked = _cfg.HoldShift;
        _holdShift.CheckedChanged += (_, _) => OnHoldChanged();
        set.Controls.Add(_holdShift);

        y += 102;

        _btnToggle.SetBounds(12, y, 288, 32);
        _btnToggle.Text = $"Bật  ({_jobKey})";
        _btnToggle.Click += (_, _) => Toggle();
        Controls.Add(_btnToggle);

        Controls.Add(new Label
        {
            Text = "Tự lái thì bỏ tick “Giữ W” — tool vẫn lo bấm E, thang máy và đếm chuyến.",
            Location = new Point(312, y + 8),
            AutoSize = true,
            ForeColor = Color.DimGray
        });

        y += 42;

        _log.SetBounds(12, y, w, 760 - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_log);
    }

    // ---------------------------------------------------------------- cài đặt

    /// <summary>
    /// Luu ngay khi doi. Bot doc thang <see cref="_cfg"/> nen doi nhip an lien ca luc dang chay —
    /// ghi int la thao tac nguyen tu, khong can khoa.
    /// </summary>
    private void OnTapMsChanged()
    {
        _cfg.TapEveryMs = (int)_tapMs.Value;
        _cfg.Save();
        Append($"nhịp bấm E: {_cfg.TapEveryMs} ms");
    }

    /// <summary>
    /// Chi doi duoc luc DUNG: tat giua chung thi vong lap thoi bam phim xuong nhung khong ai nha
    /// cai dang giu — ket W/Shift trong game. <see cref="SetRunningUi"/> khoa hai o nay.
    /// </summary>
    private void OnHoldChanged()
    {
        _cfg.HoldRun = _holdRun.Checked;
        _cfg.HoldShift = _holdShift.Checked;
        _cfg.Save();
        Append(_cfg.HoldRun
            ? "giữ W" + (_cfg.HoldShift ? " + Left Shift" : "")
            : "không giữ W — bạn tự lái");
    }

    private void DoSetup()
    {
        if (IsRunning) { Append("đang chạy — bấm Tắt trước khi khoanh vùng"); return; }

        DropMonitor();
        using var dlg = new MinerSetupForm(_cfg, _screen);
        dlg.ShowDialog(FindForm());
        Append("đã đóng bảng khoanh vùng — đọc lại mẫu");
    }

    // ---------------------------------------------------------------- chạy

    private void StartBot()
    {
        if (_bot is { Running: true }) return;

        DropMonitor();

        _bot = new MinerBot(_cfg, _screen, _cfg.ProfileFor(_screen));
        _bot.Log += s => Post(() => Append(s));
        _bot.SnapshotReady += s => Post(() => ShowSnapshot(s));
        _bot.StatsChanged += s => Post(() => ShowStats(s));
        _bot.Stopped += (r, msg) => Post(() =>
        {
            _status.Text = $"Đã dừng — {MinerBot.TenLyDo(r)}";
            _status.ForeColor = r == MinerStopReason.UserStopped ? Color.DarkGreen : Color.Firebrick;
            if (r != MinerStopReason.UserStopped) Append("dừng: " + msg);
            SetRunningUi(false);
        });

        _status.Text = "Đang cày";
        _status.ForeColor = Color.DarkBlue;
        SetRunningUi(true);
        Append("--- bắt đầu cày ---");
        _bot.Start();
    }

    /// <summary>Nguoi dung doi phim bat/tat job trong tab Tiện ích.</summary>
    public void SetJobHotkeyText(string text)
    {
        _jobKey = text;
        _btnToggle.Text = IsRunning ? $"Tắt  ({_jobKey})" : $"Bật  ({_jobKey})";
    }

    private void SetRunningUi(bool running)
    {
        _btnToggle.Text = running ? $"Tắt  ({_jobKey})" : $"Bật  ({_jobKey})";
        _holdRun.Enabled = !running;
        _holdShift.Enabled = !running;
        _btnSetup.Enabled = !running;
        RunningChanged?.Invoke(running);
    }

    // ---------------------------------------------------------------- theo dõi

    /// <summary>
    /// Doc HUD khi bot chua chay, de nguoi dung kiem o vua khoanh co an khong TRUOC khi bat —
    /// giong che do "Theo dõi" cua tab Dầu khí.
    /// </summary>
    private void Tick()
    {
        if (!Visible || IsRunning || !_watch.Checked) return;

        try
        {
            _monitor ??= new MinerReader(_cfg, _screen, _cfg.ProfileFor(_screen));
            ShowSnapshot(_monitor.Read());
        }
        catch (Exception ex)
        {
            _mining.Text = "lỗi đọc: " + ex.Message;
            _mining.ForeColor = Color.Firebrick;
            DropMonitor();
        }
    }

    private void DropMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;
    }

    private void ShowSnapshot(MinerSnapshot s)
    {
        Line(_mining, "đào    ", s.MiningConfigured, s.Mining, s.MiningScore, _cfg.MiningNccMin, "ĐANG KHAI THÁC");
        Line(_lift, "thang  ", s.LiftConfigured, s.LiftPrompt, s.LiftScore, _cfg.LiftNccMin, "gợi ý [E]");
        Line(_cash, "tiền   ", s.CashConfigured, s.CashToast, s.CashScore, _cfg.CashNccMin, "toast tiền");
    }

    private static void Line(Label lbl, string name, bool configured, bool hit, double score,
                             double min, string what)
    {
        if (!configured)
        {
            lbl.Text = $"{name}: chưa khoanh";
            lbl.ForeColor = Color.DimGray;
            return;
        }
        lbl.Text = $"{name}: {(hit ? "THẤY " + what : "không thấy")}   ncc={score:F3} / {min:F2}";
        lbl.ForeColor = hit ? Color.DarkGreen : Color.DimGray;
    }

    private void ShowStats(MinerStats s)
    {
        _stats.Text = $"{s.Mined} lượt đào  |  {s.Trips} chuyến giao" +
                      (s.LastMineMs > 0 ? $"  |  lần đào gần nhất {s.LastMineMs} ms" : "");
    }

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "bot-log.txt");
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private void Append(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        if (_log.Lines.Length > 600)
            _log.Lines = _log.Lines.Skip(200).ToArray();
        _log.AppendText($"[{stamp}] {line}{Environment.NewLine}");

        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}", LogEncoding);
        }
        catch { }
    }
}
