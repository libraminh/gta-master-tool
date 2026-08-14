namespace GtaMiniGameBot;

internal sealed class HomeForm : Form
{
    private enum TabKind { Oil, Fish, Utils }

    private const int HOTKEY_TOGGLE = 1;
    private const int HOTKEY_UTILS = 2;
    private const int SidebarW = 200;

    private readonly Button _btnOil = new();
    private readonly Button _btnFish = new();
    private readonly Button _btnUtils = new();
    private readonly Panel _host = new();
    private readonly OilWellPanel _oil = new();
    private readonly FishingPanel _fish = new();
    private readonly UtilsPanel _utils = new();
    private readonly StatusOverlay _overlay = new();
    private readonly System.Windows.Forms.Timer _overlayTest = new();
    private HotkeyConfig _keys = HotkeyConfig.Load();
    private bool _hotkeysOn;
    private TabKind _tab = TabKind.Oil;

    public HomeForm()
    {
        Text = "GTA Master Tool";
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(SidebarW + 820, 760);
        MinimumSize = new Size(SidebarW + 760, 620);
        StartPosition = FormStartPosition.CenterScreen;

        BuildSidebar();
        BuildHost();
        ShowOil();

        _oil.RunningChanged += OnJobRunning;
        _fish.RunningChanged += OnJobRunning;
        _utils.TestOverlayRequested += OnTestOverlay;
        _utils.HotkeysSuspend += UnregisterHotkeys;
        _utils.HotkeysApplied += OnHotkeysApplied;

        _overlayTest.Interval = 5000;
        _overlayTest.Tick += (_, _) => StopOverlayTest();
    }

    private void OnJobRunning(bool running)
    {
        if (running)
        {
            _overlay.ShowOn("PlayXGTA");
            Text = "● ON — GTA Master Tool";
        }
        else
        {
            _overlay.Hide();
            Text = "GTA Master Tool";
        }
    }

    private void OnTestOverlay()
    {
        _overlay.ShowOn("PlayXGTA");
        _overlayTest.Stop();
        _overlayTest.Start();
    }

    private void StopOverlayTest()
    {
        _overlayTest.Stop();
        if (!_oil.IsRunning && !_fish.IsRunning)
            _overlay.Hide();
    }

    private void BuildSidebar()
    {
        var side = new Panel
        {
            Dock = DockStyle.Left,
            Width = SidebarW,
            BackColor = Color.FromArgb(245, 246, 248)
        };
        Controls.Add(side);

        var title = new Label
        {
            Text = "GTA Master Tool",
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            AutoSize = false
        };
        title.SetBounds(12, 16, SidebarW - 24, 48);
        side.Controls.Add(title);

        StyleNav(_btnOil, "Dầu khí", 76);
        _btnOil.Click += (_, _) => ShowOil();
        side.Controls.Add(_btnOil);

        StyleNav(_btnFish, "Câu cá", 118);
        _btnFish.Click += (_, _) => ShowFishing();
        side.Controls.Add(_btnFish);

        StyleNav(_btnUtils, "Tiện ích", 160);
        _btnUtils.Click += (_, _) => ShowUtils();
        side.Controls.Add(_btnUtils);
    }

    private static void StyleNav(Button b, string text, int y)
    {
        b.Text = text;
        b.SetBounds(12, y, SidebarW - 24, 36);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.Padding = new Padding(8, 0, 0, 0);
        b.Cursor = Cursors.Hand;
    }

    private void BuildHost()
    {
        _host.Dock = DockStyle.Fill;
        _host.BackColor = Color.White;
        Controls.Add(_host);
        _host.BringToFront();

        _oil.Dock = DockStyle.Fill;
        _host.Controls.Add(_oil);

        _fish.Dock = DockStyle.Fill;
        _host.Controls.Add(_fish);

        _utils.Dock = DockStyle.Fill;
        _host.Controls.Add(_utils);
    }

    private void ShowOil()
    {
        _fish.StopWork();
        _fish.Hide();
        _utils.Hide();
        _oil.Show();
        _oil.BringToFront();
        _tab = TabKind.Oil;
        HighlightNav();
    }

    private void ShowFishing()
    {
        if (_oil.IsRunning)
            _oil.StopWork();

        _oil.Hide();
        _utils.Hide();
        _fish.Show();
        _fish.BringToFront();
        _tab = TabKind.Fish;
        HighlightNav();
    }

    private void ShowUtils()
    {
        if (_oil.IsRunning)
            _oil.StopWork();
        _fish.StopWork();

        _oil.Hide();
        _fish.Hide();
        _utils.Show();
        _utils.BringToFront();
        _tab = TabKind.Utils;
        HighlightNav();
    }

    private void HighlightNav()
    {
        PaintNav(_btnOil, _tab == TabKind.Oil);
        PaintNav(_btnFish, _tab == TabKind.Fish);
        PaintNav(_btnUtils, _tab == TabKind.Utils);
    }

    private static void PaintNav(Button b, bool on)
    {
        b.BackColor = on ? Color.White : Color.Transparent;
        b.Font = new Font("Segoe UI", 9F, on ? FontStyle.Bold : FontStyle.Regular);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys(warn: true);
    }

    private void OnHotkeysApplied(HotkeyConfig cfg)
    {
        _keys = cfg;
        UnregisterHotkeys();
        RegisterHotkeys(warn: true);

        string jobKey = HotkeyConfig.Describe(_keys.JobToggleVk, _keys.JobToggleMods);
        _oil.SetJobHotkeyText(jobKey);
        _fish.SetJobHotkeyText(jobKey);
    }

    /// <summary>
    /// RegisterHotKey that bai khi app khac dang giu phim do — truoc day tra ve
    /// bi bo qua nen phim chet im lang, gio bao thang.
    /// </summary>
    private void RegisterHotkeys(bool warn)
    {
        if (!IsHandleCreated || _hotkeysOn) return;

        var failed = new List<string>();
        if (!Native.RegisterHotKey(Handle, HOTKEY_TOGGLE, _keys.JobToggleMods, _keys.JobToggleVk))
            failed.Add($"bật/tắt job: {HotkeyConfig.Describe(_keys.JobToggleVk, _keys.JobToggleMods)}");
        if (!Native.RegisterHotKey(Handle, HOTKEY_UTILS, _keys.UtilsToggleMods, _keys.UtilsToggleVk))
            failed.Add($"bật/tắt tiện ích: {HotkeyConfig.Describe(_keys.UtilsToggleVk, _keys.UtilsToggleMods)}");
        _hotkeysOn = true;

        if (!warn || failed.Count == 0) return;

        // Lan dau chay ham nay la trong OnHandleCreated, form chua hien —
        // hoan lai de hop thoai khong chen truoc cua so chinh.
        string msg = "Không đăng ký được phím sau (app khác đang giữ) — chọn phím khác:\r\n\r\n"
                     + string.Join("\r\n", failed);
        BeginInvoke(() => MessageBox.Show(this, msg, "Phím tắt",
            MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    private void UnregisterHotkeys()
    {
        if (!IsHandleCreated) return;
        Native.UnregisterHotKey(Handle, HOTKEY_TOGGLE);
        Native.UnregisterHotKey(Handle, HOTKEY_UTILS);
        _hotkeysOn = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_UTILS)
            {
                _utils.Toggle();
            }
            else if (id == HOTKEY_TOGGLE)
            {
                if (_tab == TabKind.Oil)
                {
                    if (_oil.IsRunning) _oil.StopFromHotkey();
                    else _oil.StartFromHotkey();
                }
                else if (_tab == TabKind.Fish)
                {
                    if (_fish.IsRunning) _fish.StopFromHotkey();
                    else _fish.StartFromHotkey();
                }
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UnregisterHotkeys();
        _oil.RunningChanged -= OnJobRunning;
        _fish.RunningChanged -= OnJobRunning;
        _utils.TestOverlayRequested -= OnTestOverlay;
        _utils.HotkeysSuspend -= UnregisterHotkeys;
        _utils.HotkeysApplied -= OnHotkeysApplied;
        _overlayTest.Stop();
        _overlayTest.Dispose();
        _overlay.Hide();
        _overlay.Dispose();
        _oil.Shutdown();
        _fish.Shutdown();
        _utils.Shutdown();
        base.OnFormClosing(e);
    }
}
