namespace GtaMiniGameBot;

internal sealed class HomeForm : Form
{
    private enum TabKind { Oil, Fish, Mine, Wood, Elec, Utils }

    private const int HOTKEY_TOGGLE = 1;
    private const int HOTKEY_UTILS = 2;

    private static readonly int RailW = Theme.Px(60);

    private readonly RailButton _btnOil = new("Dầu", RailIcon.Oil);
    private readonly RailButton _btnFish = new("Câu", RailIcon.Fish);
    private readonly RailButton _btnMine = new("Mỏ", RailIcon.Mine);
    private readonly RailButton _btnWood = new("Mộc", RailIcon.Wood);
    private readonly RailButton _btnElec = new("Điện", RailIcon.Elec);
    private readonly RailButton _btnUtils = new("Tiện ích", RailIcon.Utils);
    private readonly Panel _host = new();
    private readonly OilWellPanel _oil = new();
    private readonly FishingPanel _fish = new();
    private readonly MinerPanel _mine = new();
    private readonly WoodPanel _wood = new();
    private readonly ElectricPanel _elec = new();
    private readonly UtilsPanel _utils = new();
    private readonly StatusOverlay _overlay = new();
    private readonly System.Windows.Forms.Timer _overlayTest = new();
    private HotkeyConfig _keys = HotkeyConfig.Load();
    private bool _hotkeysOn;
    private TabKind _tab = TabKind.Oil;

    public HomeForm()
    {
        Text = "GTA Master Tool";
        Font = Theme.Body;
        BackColor = Theme.Ground;
        ForeColor = Theme.Text;
        ClientSize = new Size(RailW + Theme.Px(880), Theme.Px(780));
        MinimumSize = new Size(RailW + Theme.Px(820), Theme.Px(640));
        StartPosition = FormStartPosition.CenterScreen;

        BuildRail();
        BuildHost();
        Apply(TabKind.Oil);

        _oil.RunningChanged += OnJobRunning;
        _fish.RunningChanged += OnJobRunning;
        _mine.RunningChanged += OnJobRunning;
        _wood.RunningChanged += OnJobRunning;
        _elec.RunningChanged += OnJobRunning;
        _utils.TestOverlayRequested += OnTestOverlay;
        _utils.HotkeysSuspend += UnregisterHotkeys;
        _utils.HotkeysApplied += OnHotkeysApplied;

        _fish.StateChanged += _overlay.Update;

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
        RefreshRail();
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
        if (!_oil.IsRunning && !_fish.IsRunning && !_mine.IsRunning && !_wood.IsRunning
            && !_elec.IsRunning)
            _overlay.Hide();
    }

    private void BuildRail()
    {
        var side = new DrawPanel
        {
            Dock = DockStyle.Left,
            Width = RailW,
            BackColor = Theme.Sunk
        };
        Controls.Add(side);

        int y = Theme.Px(12);
        foreach (var (b, act) in new (RailButton, Action)[]
                 {
                     (_btnOil, ShowOil), (_btnFish, ShowFishing),
                     (_btnMine, ShowMining), (_btnWood, ShowWood), (_btnElec, ShowElectric)
                 })
        {
            b.SetBounds(Theme.Px(6), y, RailW - Theme.Px(12), Theme.Px(52));
            b.Click += (_, _) => act();
            side.Controls.Add(b);
            y += Theme.Px(56);
        }

        // Cac job dung lien nhau, Tiện ích xuong duoi — no khong phai job.
        y += Theme.Px(10);
        var sep = new DrawPanel
        {
            BackColor = Theme.Line,
            Bounds = new Rectangle(Theme.Px(18), y, RailW - Theme.Px(36), 1)
        };
        side.Controls.Add(sep);
        y += Theme.Px(12);

        _btnUtils.SetBounds(Theme.Px(6), y, RailW - Theme.Px(12), Theme.Px(52));
        _btnUtils.Click += (_, _) => ShowUtils();
        side.Controls.Add(_btnUtils);
    }

    private void BuildHost()
    {
        _host.Dock = DockStyle.Fill;
        _host.BackColor = Theme.Ground;
        Controls.Add(_host);
        _host.BringToFront();

        foreach (Control c in new Control[] { _oil, _fish, _mine, _wood, _elec, _utils })
        {
            c.Dock = DockStyle.Fill;
            _host.Controls.Add(c);
        }
    }

    /// <summary>
    /// Doi tab la TAT job dang chay — <see cref="OilWellPanel.StopWork"/> va ban cua
    /// hai panel kia deu bi goi vo dieu kien. Truoc day no im lang, tuc bam lech mot
    /// icon la mat ca phien. Gio hoi lai.
    /// </summary>
    private bool MayLeaveRunningJob()
    {
        string what =
            _oil.IsRunning ? "Dầu khí" :
            _fish.IsRunning ? "Câu cá" :
            _mine.IsRunning ? "Thợ mỏ" :
            _wood.IsRunning ? "Thợ mộc" :
            _elec.IsRunning ? "Thợ điện" : null;
        if (what is null) return true;

        return MessageBox.Show(this,
            $"Job “{what}” đang chạy. Đổi tab sẽ tắt nó.\r\n\r\nĐổi tab?",
            "Đang chạy", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    private void ShowOil() => Select(TabKind.Oil);
    private void ShowFishing() => Select(TabKind.Fish);
    private void ShowMining() => Select(TabKind.Mine);
    private void ShowWood() => Select(TabKind.Wood);
    private void ShowElectric() => Select(TabKind.Elec);
    private void ShowUtils() => Select(TabKind.Utils);

    private void Select(TabKind t)
    {
        if (_tab == t) return;
        if (!MayLeaveRunningJob()) return;
        Apply(t);
    }

    private void Apply(TabKind t)
    {
        // Oil giu chot IsRunning nhu ban cu; ba panel kia StopWork() vo dieu kien
        // vi no chi huy bot roi nha phim, goi khi dang dung la vo hai.
        if (t != TabKind.Oil && _oil.IsRunning) _oil.StopWork();
        if (t != TabKind.Fish) _fish.StopWork();
        if (t != TabKind.Mine) _mine.StopWork();
        if (t != TabKind.Wood) _wood.StopWork();
        if (t != TabKind.Elec) _elec.StopWork();

        _oil.Visible = t == TabKind.Oil;
        _fish.Visible = t == TabKind.Fish;
        _mine.Visible = t == TabKind.Mine;
        _wood.Visible = t == TabKind.Wood;
        _elec.Visible = t == TabKind.Elec;
        _utils.Visible = t == TabKind.Utils;

        Control front = t switch
        {
            TabKind.Oil => _oil,
            TabKind.Fish => _fish,
            TabKind.Mine => _mine,
            TabKind.Wood => _wood,
            TabKind.Elec => _elec,
            _ => _utils
        };
        front.BringToFront();

        _tab = t;
        RefreshRail();
    }

    private void RefreshRail()
    {
        _btnOil.SetState(_tab == TabKind.Oil, _oil.IsRunning);
        _btnFish.SetState(_tab == TabKind.Fish, _fish.IsRunning);
        _btnMine.SetState(_tab == TabKind.Mine, _mine.IsRunning);
        _btnWood.SetState(_tab == TabKind.Wood, _wood.IsRunning);
        _btnElec.SetState(_tab == TabKind.Elec, _elec.IsRunning);
        _btnUtils.SetState(_tab == TabKind.Utils, false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
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
        _mine.SetJobHotkeyText(jobKey);
        _wood.SetJobHotkeyText(jobKey);
        _elec.SetJobHotkeyText(jobKey);
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
                else if (_tab == TabKind.Mine)
                {
                    if (_mine.IsRunning) _mine.StopFromHotkey();
                    else _mine.StartFromHotkey();
                }
                else if (_tab == TabKind.Wood)
                {
                    if (_wood.IsRunning) _wood.StopFromHotkey();
                    else _wood.StartFromHotkey();
                }
                else if (_tab == TabKind.Elec)
                {
                    if (_elec.IsRunning) _elec.StopFromHotkey();
                    else _elec.StartFromHotkey();
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
        _mine.RunningChanged -= OnJobRunning;
        _wood.RunningChanged -= OnJobRunning;
        _elec.RunningChanged -= OnJobRunning;
        _fish.StateChanged -= _overlay.Update;
        _utils.TestOverlayRequested -= OnTestOverlay;
        _utils.HotkeysSuspend -= UnregisterHotkeys;
        _utils.HotkeysApplied -= OnHotkeysApplied;
        _overlayTest.Stop();
        _overlayTest.Dispose();
        _overlay.Hide();
        _overlay.Dispose();
        _oil.Shutdown();
        _fish.Shutdown();
        _mine.Shutdown();
        _wood.Shutdown();
        _elec.Shutdown();
        _utils.Shutdown();
        base.OnFormClosing(e);
    }
}

// ------------------------------------------------------------------ rail

internal enum RailIcon { Oil, Fish, Mine, Wood, Elec, Utils }

/// <summary>
/// Mot o tren rail: icon + nhan ngan, cham xanh khi job do dang chay.
///
/// Ban cu dung Button voi BackColor/Font doi qua lai, va ham PaintNav tao mot
/// Font moi moi lan doi tab ma khong bao gio dispose.
/// </summary>
internal sealed class RailButton : DrawPanel
{
    private readonly string _label;
    private readonly RailIcon _icon;
    private bool _on;
    private bool _running;
    private bool _hot;

    public RailButton(string label, RailIcon icon)
    {
        _label = label;
        _icon = icon;
        BackColor = Theme.Sunk;
        Cursor = Cursors.Hand;
    }

    public void SetState(bool selected, bool running)
    {
        if (_on == selected && _running == running) return;
        _on = selected;
        _running = running;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prep(g);

        var r = new Rectangle(0, 0, Width, Height);
        if (_on) Theme.Fill(g, r, Theme.AccentWash);
        else if (_hot) Theme.Fill(g, r, Theme.Surface);

        if (_on)
            Theme.Fill(g, new Rectangle(0, Theme.Px(8), Theme.Px(2), Height - Theme.Px(16)), Theme.Accent);

        Color ink = _on ? Theme.Accent : _hot ? Theme.Text : Theme.Dimmer;

        int s = Theme.Px(20);
        var box = new Rectangle((Width - s) / 2, Theme.Px(8), s, s);
        Icon(g, box, ink);

        TextRenderer.DrawText(g, _label, Theme.Nav,
            new Rectangle(0, Theme.Px(32), Width, Theme.Px(16)), ink, Theme.Centre);

        if (!_running) return;
        using var b = new SolidBrush(Theme.Good);
        g.FillEllipse(b, Width - Theme.Px(11), Theme.Px(5), Theme.Px(6), Theme.Px(6));
    }

    private void Icon(Graphics g, Rectangle b, Color ink)
    {
        using var p = new Pen(ink, Math.Max(1.4f, Theme.Px(2) * 0.8f));
        float x = b.X, y = b.Y, w = b.Width, h = b.Height;

        switch (_icon)
        {
            case RailIcon.Oil:
                // Gian khoan: mot thap.
                g.DrawLines(p, new[]
                {
                    new PointF(x + w * 0.1f, y + h),
                    new PointF(x + w * 0.5f, y),
                    new PointF(x + w * 0.9f, y + h)
                });
                g.DrawLine(p, x + w * 0.26f, y + h * 0.6f, x + w * 0.74f, y + h * 0.6f);
                break;

            case RailIcon.Fish:
                // Con ca: than + duoi.
                g.DrawCurve(p, new[]
                {
                    new PointF(x + w * 0.08f, y + h * 0.5f),
                    new PointF(x + w * 0.4f, y + h * 0.2f),
                    new PointF(x + w * 0.72f, y + h * 0.5f),
                    new PointF(x + w * 0.4f, y + h * 0.8f),
                    new PointF(x + w * 0.08f, y + h * 0.5f)
                });
                g.DrawLines(p, new[]
                {
                    new PointF(x + w * 0.72f, y + h * 0.5f),
                    new PointF(x + w, y + h * 0.24f),
                    new PointF(x + w, y + h * 0.76f),
                    new PointF(x + w * 0.72f, y + h * 0.5f)
                });
                break;

            case RailIcon.Mine:
                // Cai bua: can + dau.
                g.DrawLine(p, x + w * 0.1f, y + h * 0.9f, x + w * 0.58f, y + h * 0.42f);
                g.DrawCurve(p, new[]
                {
                    new PointF(x + w * 0.44f, y + h * 0.28f),
                    new PointF(x + w * 0.72f, y + h * 0.06f),
                    new PointF(x + w * 0.96f, y + h * 0.3f)
                });
                g.DrawLine(p, x + w * 0.44f, y + h * 0.28f, x + w * 0.72f, y + h * 0.56f);
                g.DrawLine(p, x + w * 0.96f, y + h * 0.3f, x + w * 0.72f, y + h * 0.56f);
                break;

            case RailIcon.Wood:
                // Cai riu bo vao khuc go: luoi riu + can, tren mot khuc nam ngang.
                // Khac icon Mine (bua) o cho luoi la mang TAM GIAC dac chu khong phai cung mo.
                g.DrawLine(p, x + w * 0.16f, y + h * 0.62f, x + w * 0.6f, y + h * 0.18f);
                using (var blade = new SolidBrush(ink))
                    g.FillPolygon(blade, new[]
                    {
                        new PointF(x + w * 0.54f, y + h * 0.12f),
                        new PointF(x + w * 0.94f, y + h * 0.06f),
                        new PointF(x + w * 0.88f, y + h * 0.46f)
                    });
                g.DrawLine(p, x + w * 0.06f, y + h * 0.88f, x + w * 0.94f, y + h * 0.88f);
                break;

            case RailIcon.Elec:
                // Tia set: mang zigzag dac. Khac icon Mộc (cung la mang dac) o cho no KHONG co
                // can, va khac Mỏ o cho khong co cung mo — nhin mot cai la biet ngay o rail.
                using (var bolt = new SolidBrush(ink))
                    g.FillPolygon(bolt, new[]
                    {
                        new PointF(x + w * 0.56f, y),
                        new PointF(x + w * 0.20f, y + h * 0.56f),
                        new PointF(x + w * 0.46f, y + h * 0.56f),
                        new PointF(x + w * 0.38f, y + h),
                        new PointF(x + w * 0.80f, y + h * 0.40f),
                        new PointF(x + w * 0.52f, y + h * 0.40f),
                        new PointF(x + w * 0.66f, y)
                    });
                break;

            default:
                // Banh rang gian luoc: vong tron + bon nan hoa.
                g.DrawEllipse(p, x + w * 0.3f, y + h * 0.3f, w * 0.4f, h * 0.4f);
                for (int i = 0; i < 4; i++)
                {
                    double a = Math.PI / 2 * i;
                    float cx = x + w / 2, cy = y + h / 2;
                    g.DrawLine(p,
                        cx + (float)Math.Cos(a) * w * 0.26f, cy + (float)Math.Sin(a) * h * 0.26f,
                        cx + (float)Math.Cos(a) * w * 0.48f, cy + (float)Math.Sin(a) * h * 0.48f);
                }
                break;
        }
    }
}
