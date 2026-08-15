using System.Text;

namespace GtaMiniGameBot;

internal sealed class MinerPanel : UserControl
{
    private readonly MinerConfig _cfg = MinerConfig.Load();
    private MinerBot _bot;

    private readonly Label _status = new();
    private readonly NumericUpDown _tapMs = new();
    private readonly CheckBox _holdShift = new();
    private readonly Label _note = new();
    private readonly Button _btnToggle = new();
    private readonly TextBox _log = new();
    private string _jobKey = HotkeyText.Job();

    public bool IsRunning => _bot is { Running: true };

    public event Action<bool> RunningChanged;

    public MinerPanel()
    {
        Font = new Font("Segoe UI", 9F);
        Dock = DockStyle.Fill;
        BackColor = Color.White;

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

    private void BuildUi()
    {
        int y = 12;
        const int w = 796;

        _status.SetBounds(12, y, w, 30);
        _status.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        _status.Text = "Đang dừng";
        Controls.Add(_status);
        y += 38;

        var box = new GroupBox { Text = "Cài đặt", Location = new Point(12, y), Size = new Size(w, 96) };
        Controls.Add(box);

        box.Controls.Add(new Label { Text = "Bấm E mỗi", Location = new Point(16, 30), AutoSize = true });

        _tapMs.SetBounds(96, 26, 80, 24);
        _tapMs.Minimum = 50;
        _tapMs.Maximum = 5_000;
        _tapMs.Increment = 50;
        _tapMs.Value = Math.Clamp(_cfg.TapEveryMs, (int)_tapMs.Minimum, (int)_tapMs.Maximum);
        box.Controls.Add(_tapMs);

        box.Controls.Add(new Label
        {
            Text = "ms   (200 = nhịp đo trong game; đổi được cả lúc đang chạy)",
            Location = new Point(184, 30),
            AutoSize = true
        });

        // Gan sau khi da set .Value de khoi ban ValueChanged luc dung UI.
        _tapMs.ValueChanged += (_, _) => OnTapMsChanged();

        _holdShift.SetBounds(16, 60, 320, 22);
        _holdShift.Text = "Giữ Left Shift cùng W (chạy nước rút)";
        _holdShift.Checked = _cfg.HoldShift;
        _holdShift.CheckedChanged += (_, _) => OnHoldShiftChanged();
        box.Controls.Add(_holdShift);

        y += 106;

        var help = new GroupBox { Text = "Cách dùng", Location = new Point(12, y), Size = new Size(w, 96) };
        Controls.Add(help);

        _note.AutoSize = false;
        _note.Font = new Font("Segoe UI", 9.5F);
        _note.SetBounds(16, 24, 760, 64);
        help.Controls.Add(_note);
        RefreshNote();

        y += 106;

        _btnToggle.SetBounds(12, y, 288, 32);
        _btnToggle.Text = $"Bật  ({_jobKey})";
        _btnToggle.Click += (_, _) => Toggle();
        Controls.Add(_btnToggle);

        y += 42;

        _log.SetBounds(12, y, w, 760 - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_log);
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
        _cfg.TapEveryMs = (int)_tapMs.Value;
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
