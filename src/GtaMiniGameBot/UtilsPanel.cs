namespace GtaMiniGameBot;

internal sealed class UtilsPanel : UserControl
{
    private enum Hk { Job, Utils, AutoRun, HoldCtrl, Sprint }

    private readonly UtilityService _svc = new();
    private HotkeyConfig _keys = HotkeyConfig.Load();

    private readonly Label _title = new();
    private readonly Label _status = new();
    private readonly Button _btnToggle = new();
    private readonly Label _autoRun = new();
    private readonly Label _sprint = new();
    private readonly Label _ctrlHold = new();
    private readonly Label _focus = new();
    private readonly Button _btnTestOverlay = new();
    private readonly Label _note = new();
    private readonly Dictionary<Hk, Label> _keyLabels = new();

    /// <summary>Hien badge ON 5 giay de thu, khong can chay job.</summary>
    public event Action TestOverlayRequested;

    /// <summary>Nha hotkey toan cuc de hop thoai bat duoc chinh phim do.</summary>
    public event Action HotkeysSuspend;

    /// <summary>Bao config moi (hoac config cu neu huy) de dang ky lai.</summary>
    public event Action<HotkeyConfig> HotkeysApplied;

    public UtilsPanel()
    {
        Font = new Font("Segoe UI", 9F);
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        AutoScroll = true;

        BuildUi();
        _svc.SetKeys(_keys.AutoRunVk, _keys.HoldCtrlVk, _keys.SprintVk);
        _svc.Changed += OnChanged;
        RefreshHotkeyUi();
    }

    public void Toggle() => _svc.Toggle();

    public void Shutdown()
    {
        _svc.Changed -= OnChanged;
        _svc.Shutdown();
    }

    private void BuildUi()
    {
        int y = 12;
        const int w = 796;

        _title.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        _title.AutoSize = false;
        _title.SetBounds(12, y, w, 28);
        Controls.Add(_title);
        y += 36;

        _status.SetBounds(14, y, 360, 30);
        _status.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        Controls.Add(_status);

        _btnToggle.SetBounds(390, y, 288, 32);
        _btnToggle.Click += (_, _) => Toggle();
        Controls.Add(_btnToggle);
        y += 44;

        var box = new GroupBox { Text = "Trạng thái", Location = new Point(12, y), Size = new Size(w, 122) };
        Controls.Add(box);

        _focus.SetBounds(16, 24, 760, 20);
        _focus.Font = new Font("Consolas", 10F);
        box.Controls.Add(_focus);

        _autoRun.SetBounds(16, 46, 760, 20);
        _autoRun.Font = new Font("Consolas", 10F);
        box.Controls.Add(_autoRun);

        _sprint.SetBounds(16, 68, 760, 20);
        _sprint.Font = new Font("Consolas", 10F);
        box.Controls.Add(_sprint);

        _ctrlHold.SetBounds(16, 90, 760, 20);
        _ctrlHold.Font = new Font("Consolas", 10F);
        box.Controls.Add(_ctrlHold);
        y += 138;

        var help = new GroupBox { Text = "Cách dùng", Location = new Point(12, y), Size = new Size(w, 188) };
        Controls.Add(help);

        _note.AutoSize = false;
        _note.Font = new Font("Segoe UI", 9.5F);
        _note.SetBounds(16, 24, 760, 150);
        help.Controls.Add(_note);
        y += 196;

        _btnTestOverlay.SetBounds(12, y, 220, 30);
        _btnTestOverlay.Text = "Test overlay 5 giây";
        _btnTestOverlay.Click += (_, _) => TestOverlayRequested?.Invoke();
        Controls.Add(_btnTestOverlay);

        Controls.Add(new Label
        {
            Text = "Hiện badge ● ON ở góc màn hình game. Kết quả ghi vào overlay-log.txt cạnh exe.",
            Location = new Point(244, y + 6),
            AutoSize = true,
            ForeColor = Color.DimGray
        });
        y += 40;

        BuildHotkeyBox(y, w);
    }

    private void BuildHotkeyBox(int y, int w)
    {
        var box = new GroupBox { Text = "Phím tắt", Location = new Point(12, y), Size = new Size(w, 254) };
        Controls.Add(box);

        AddHotkeyRow(box, 26, Hk.Job, "Bật/tắt job (Dầu khí, Câu cá, Thợ mỏ)");
        AddHotkeyRow(box, 64, Hk.Utils, "Bật/tắt tiện ích");
        AddHotkeyRow(box, 102, Hk.AutoRun, "Tự chạy — giữ W");
        AddHotkeyRow(box, 140, Hk.Sprint, "Chạy nước rút — giữ W + Left Shift");
        AddHotkeyRow(box, 178, Hk.HoldCtrl, "Giữ lâu để thêm Left Ctrl");

        box.Controls.Add(new Label
        {
            Text = "Ba phím dưới đi qua hook và bị chặn không cho tới game khi tiện ích đang bật — "
                 + "đừng chọn phím vẫn cần dùng lúc chơi. Hai phím trên nhận được tổ hợp Ctrl/Shift/Alt.",
            ForeColor = Color.DimGray,
            AutoSize = false,
            Bounds = new Rectangle(16, 210, 760, 36)
        });
    }

    private void AddHotkeyRow(GroupBox box, int rowY, Hk kind, string desc)
    {
        box.Controls.Add(new Label
        {
            Text = desc,
            AutoSize = false,
            Bounds = new Rectangle(16, rowY + 5, 290, 20)
        });

        var key = new Label
        {
            Font = new Font("Consolas", 10F, FontStyle.Bold),
            AutoSize = false,
            Bounds = new Rectangle(312, rowY + 5, 150, 20)
        };
        box.Controls.Add(key);
        _keyLabels[kind] = key;

        var change = new Button { Text = "Đổi phím", Bounds = new Rectangle(468, rowY, 110, 28) };
        change.Click += (_, _) => ChangeKey(kind);
        box.Controls.Add(change);

        var reset = new Button { Text = "Mặc định", Bounds = new Rectangle(586, rowY, 100, 28) };
        reset.Click += (_, _) => ResetKey(kind);
        box.Controls.Add(reset);
    }

    // ---------------- doi phim ----------------

    private void ChangeKey(Hk kind)
    {
        HotkeysSuspend?.Invoke();
        try
        {
            using var dlg = new KeyCaptureDialog(KindName(kind), AllowMods(kind));
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            Assign(kind, dlg.Vk, dlg.Mods);
        }
        finally
        {
            // Ke ca khi huy van phai bao lai de HomeForm dang ky lai phim.
            HotkeysApplied?.Invoke(_keys.Clone());
        }
    }

    private void ResetKey(Hk kind)
    {
        var (vk, mods) = Defaults(kind);
        Assign(kind, vk, mods);
        HotkeysApplied?.Invoke(_keys.Clone());
    }

    private void Assign(Hk kind, uint vk, uint mods)
    {
        foreach (Hk other in Enum.GetValues<Hk>())
        {
            if (other == kind) continue;
            var cur = Current(other);
            if (cur.vk != vk || cur.mods != mods) continue;

            MessageBox.Show(FindForm(),
                $"{HotkeyConfig.Describe(vk, mods)} đang dùng cho \"{KindName(other)}\". Chọn phím khác.",
                "Trùng phím", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        switch (kind)
        {
            case Hk.Job: _keys.JobToggleVk = vk; _keys.JobToggleMods = mods; break;
            case Hk.Utils: _keys.UtilsToggleVk = vk; _keys.UtilsToggleMods = mods; break;
            case Hk.AutoRun: _keys.AutoRunVk = vk; break;
            case Hk.Sprint: _keys.SprintVk = vk; break;
            case Hk.HoldCtrl: _keys.HoldCtrlVk = vk; break;
        }

        _keys.Save();
        _svc.SetKeys(_keys.AutoRunVk, _keys.HoldCtrlVk, _keys.SprintVk);
        RefreshHotkeyUi();
    }

    private (uint vk, uint mods) Current(Hk kind) => kind switch
    {
        Hk.Job => (_keys.JobToggleVk, _keys.JobToggleMods),
        Hk.Utils => (_keys.UtilsToggleVk, _keys.UtilsToggleMods),
        Hk.AutoRun => (_keys.AutoRunVk, 0u),
        Hk.Sprint => (_keys.SprintVk, 0u),
        _ => (_keys.HoldCtrlVk, 0u)
    };

    private static (uint vk, uint mods) Defaults(Hk kind) => kind switch
    {
        Hk.Job => (HotkeyConfig.DefaultJobVk, 0u),
        Hk.Utils => (HotkeyConfig.DefaultUtilsVk, 0u),
        Hk.AutoRun => (HotkeyConfig.DefaultAutoRunVk, 0u),
        Hk.Sprint => (HotkeyConfig.DefaultSprintVk, 0u),
        _ => (HotkeyConfig.DefaultHoldCtrlVk, 0u)
    };

    private static bool AllowMods(Hk kind) => kind is Hk.Job or Hk.Utils;

    private static string KindName(Hk kind) => kind switch
    {
        Hk.Job => "bật/tắt job",
        Hk.Utils => "bật/tắt tiện ích",
        Hk.AutoRun => "tự chạy",
        Hk.Sprint => "chạy nước rút",
        _ => "giữ để thêm Ctrl"
    };

    private string KeyText(Hk kind)
    {
        var (vk, mods) = Current(kind);
        return HotkeyConfig.Describe(vk, mods);
    }

    // ---------------- hien thi ----------------

    private void OnChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(RefreshUi); } catch { }
            return;
        }
        RefreshUi();
    }

    private void RefreshHotkeyUi()
    {
        foreach (var (kind, label) in _keyLabels)
            label.Text = KeyText(kind);

        _title.Text = $"Tiện ích — không phải job, bật {KeyText(Hk.Utils)} rồi dùng trong game";

        _note.Text =
            $"{KeyText(Hk.Utils)} — bật/tắt cả hai tính năng (toggle, dùng được từ mọi tab).\r\n" +
            $"{KeyText(Hk.AutoRun)} — bật/tắt tự chạy (giữ W). Chỉ khi tiện ích đang bật và game đang focus.\r\n" +
            $"{KeyText(Hk.Sprint)} — bật/tắt chạy nước rút (giữ W + Left Shift). Bật cái này thì tự chạy tắt, và ngược lại.\r\n" +
            $"Giữ {KeyText(Hk.HoldCtrl)} hơn 200 ms — vừa phím đó vừa Left Ctrl. Nhấn thả nhanh thì chỉ phím đó.\r\n" +
            "\r\n" +
            $"{KeyText(Hk.Job)} không dùng ở tab này ({KeyText(Hk.Job)} vẫn bật/tắt job Dầu khí / Câu cá / Thợ mỏ).\r\n" +
            "Mất focus game thì tạm nhả W/Shift/Ctrl; tự chạy và nước rút vẫn nhớ, quay lại game sẽ giữ tiếp.";

        RefreshUi();
    }

    private void RefreshUi()
    {
        bool on = _svc.Enabled;
        string utilsKey = KeyText(Hk.Utils);
        string autoKey = KeyText(Hk.AutoRun).PadRight(9);
        string sprintKey = KeyText(Hk.Sprint).PadRight(9);
        string holdKey = ("giữ " + KeyText(Hk.HoldCtrl)).PadRight(9);

        _status.Text = on ? $"{utilsKey} đang BẬT" : $"{utilsKey} đang TẮT";
        _status.ForeColor = on ? Color.DarkGreen : Color.Firebrick;
        _btnToggle.Text = on ? $"Tắt  ({utilsKey})" : $"Bật  ({utilsKey})";

        _focus.Text = _svc.GameFocused
            ? "cửa sổ : game đang focus"
            : "cửa sổ : chưa focus game (PlayXGTA)";
        _focus.ForeColor = _svc.GameFocused ? Color.DarkGreen : Color.DimGray;

        if (!on)
        {
            _autoRun.Text = $"{autoKey}: --  (bật {utilsKey} trước)";
            _autoRun.ForeColor = Color.DimGray;
            _sprint.Text = $"{sprintKey}: --  (bật {utilsKey} trước)";
            _sprint.ForeColor = Color.DimGray;
            _ctrlHold.Text = $"{holdKey}: --  (bật {utilsKey} trước)";
            _ctrlHold.ForeColor = Color.DimGray;
            return;
        }

        _autoRun.Text = _svc.AutoRun
            ? $"{autoKey}: đang tự chạy (giữ W)"
            : $"{autoKey}: tắt — nhấn {KeyText(Hk.AutoRun)} để chạy";
        _autoRun.ForeColor = _svc.AutoRun ? Color.DarkGreen : Color.DimGray;

        _sprint.Text = _svc.Sprint
            ? $"{sprintKey}: đang nước rút (giữ W + Left Shift)"
            : $"{sprintKey}: tắt — nhấn {KeyText(Hk.Sprint)} để nước rút";
        _sprint.ForeColor = _svc.Sprint ? Color.DarkGreen : Color.DimGray;

        _ctrlHold.Text = _svc.CtrlHeld
            ? $"{holdKey}: đang thêm Left Ctrl"
            : $"{holdKey}: chờ — giữ {KeyText(Hk.HoldCtrl)} > 200 ms để thêm Ctrl";
        _ctrlHold.ForeColor = _svc.CtrlHeld ? Color.DarkGreen : Color.DimGray;
    }
}
