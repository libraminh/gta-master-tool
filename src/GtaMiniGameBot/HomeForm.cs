namespace GtaMiniGameBot;

internal sealed class HomeForm : Form
{
    private const int HOTKEY_TOGGLE = 1;
    private const uint VK_F9 = 0x78;
    private const int SidebarW = 200;

    private readonly Button _btnOil = new();
    private readonly Button _btnFish = new();
    private readonly Panel _host = new();
    private readonly OilWellPanel _oil = new();
    private readonly FishingPanel _fish = new();
    private bool _oilActive = true;

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
    }

    private void ShowOil()
    {
        _fish.StopWork();
        _fish.Hide();
        _oil.Show();
        _oil.BringToFront();
        _oilActive = true;
        HighlightNav(oil: true);
    }

    private void ShowFishing()
    {
        if (_oil.IsRunning)
            _oil.StopWork();

        _oil.Hide();
        _fish.Show();
        _fish.BringToFront();
        _oilActive = false;
        HighlightNav(oil: false);
    }

    private void HighlightNav(bool oil)
    {
        _btnOil.BackColor = oil ? Color.White : Color.Transparent;
        _btnOil.Font = new Font("Segoe UI", 9F, oil ? FontStyle.Bold : FontStyle.Regular);
        _btnFish.BackColor = oil ? Color.Transparent : Color.White;
        _btnFish.Font = new Font("Segoe UI", 9F, oil ? FontStyle.Regular : FontStyle.Bold);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RegisterHotKey(Handle, HOTKEY_TOGGLE, 0, VK_F9);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_TOGGLE)
        {
            if (_oilActive)
            {
                if (_oil.IsRunning) _oil.StopFromHotkey();
                else _oil.StartFromHotkey();
            }
            else
            {
                if (_fish.IsRunning) _fish.StopFromHotkey();
                else _fish.StartFromHotkey();
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Native.UnregisterHotKey(Handle, HOTKEY_TOGGLE);
        _oil.Shutdown();
        _fish.Shutdown();
        base.OnFormClosing(e);
    }
}
