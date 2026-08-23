namespace GtaMiniGameBot;

internal sealed class FishingPanel : UserControl
{
    private FishingConfig _cfg = FishingConfig.Load();
    private FishingReader _reader;
    private FishingBot _bot;

    /// <summary>Phiên vừa rồi kết thúc vì hết chỗ chứa — cú bấm kế tiếp chỉ để xác nhận, không chạy.</summary>
    private bool _finished;

    private readonly DarkPick _screens = new();
    private readonly Label _profile = new();
    private readonly Label _status = new();
    private readonly Label _ctx = new();
    private readonly DarkCheck _watch = new();
    private readonly DarkButton _btnToggle = new();
    private readonly DarkButton _btnBar = new();
    private readonly DarkButton _btnFish = new();
    private readonly DarkButton _btnReject = new();
    private readonly DarkButton _btnNoWater = new();
    private readonly DarkButton _btnKeep = new();
    private readonly DarkButton _btnKeepBand = new();
    private readonly DarkButton _btnAltProbe = new();
    private readonly DarkButton _btnTrunkSetup = new();
    private readonly DarkCheck _dumpEnabled = new();
    private readonly DarkSpin _everyN = new();
    private readonly DarkSpin _dumpEvery = new();
    private readonly DarkSpin _turnMs = new();
    private readonly Label _dumpStatus = new();
    private readonly PictureBox _thumbBar = new();
    private readonly PictureBox _thumbFish = new();
    private readonly PictureBox _thumbReject = new();
    private readonly PictureBox _thumbNoWater = new();
    private readonly PictureBox _thumbKeep = new();
    private readonly PictureBox _thumbKeepBand = new();
    private readonly LogView _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private string _jobKey = HotkeyText.Job();
    private bool _syncingDumpUi;

    // ---------------- phan hien so lieu ----------------
    private readonly PhaseTrack _phase = new();
    private readonly MeterList _meters = new();
    private readonly MetricTile _tileCatch = new();
    private readonly MetricTile _tileRate = new();
    private readonly MetricTile _tileUptime = new();
    private readonly MetricTile _tileHit = new();
    private readonly CapacityBar _capBag = new();
    private readonly CapacityBar _capTrunk = new();

    private MeterList.Row _mHud, _mFill, _mFish, _mReject, _mNoWater, _mKeep;
    private FishingState _state = FishingState.Idle;
    private int _sparkAt = -1;

    /// <summary>Chuyen tiep trang thai bot ra ngoai — HomeForm dua thang cho badge overlay.</summary>
    public event Action<FishingState> StateChanged;

    public FishingPanel()
    {
        Font = Theme.Body;
        Dock = DockStyle.Fill;
        BackColor = Theme.Ground;

        BuildUi();
        FillScreens();
        RefreshProfileLabel();
        RefreshDumpStatus();
        LoadThumbs();
        ShowState(FishingState.Idle);

        _timer.Interval = 100;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Append($"Khoanh thanh + cá rồi {_jobKey} để bật/tắt câu.");
        Append("CẤT VÀO: ôm trọn khối nền màu của nút trái — bot lấy màu và kích thước nút từ ô này.");
        Append("Vùng quét: khoanh cả khoảng nút trượt lên/xuống — tên cá dài đẩy hàng nút xuống.");
        Append("Game nên cửa sổ không viền / fullscreen windowed — exclusive có thể che overlay.");
    }

    public bool IsRunning => _bot is { Running: true };

    public event Action<bool> RunningChanged;

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

    public void StopWork()
    {
        _bot?.StopAndWait();
        HeldKeys.ReleaseAll();
        _reader?.Dispose();
        _reader = null;
    }

    public void Shutdown()
    {
        _timer.Stop();
        _bot?.StopAndWait();
        HeldKeys.ReleaseAll();
        _reader?.Dispose();
        _reader = null;
        DisposeThumb(_thumbBar);
        DisposeThumb(_thumbFish);
        DisposeThumb(_thumbReject);
        DisposeThumb(_thumbNoWater);
        DisposeThumb(_thumbKeep);
        DisposeThumb(_thumbKeepBand);
    }

    /// <summary>
    /// Bo cuc: thanh lenh — so do vong cau — dai chi so — hai cot (so lieu | dien bien).
    ///
    /// Khung tho dung Dock nen khong con so 796 va cong thuc `760 - y - 12` an theo
    /// chieu cao cua so nhu ban cu. Ben trong moi khung nho van dat tuyet doi, dung
    /// loi cu cua repo, va cot trai co AutoScroll de khong bi cat.
    ///
    /// Thu tu Add quan trong: WinForms dock theo thu tu NGUOC z-order, nen control
    /// Fill phai them TRUOC, roi cac control Top them theo thu tu nguoc voi thu tu
    /// nhin thay.
    /// </summary>
    private void BuildUi()
    {
        var split = new DrawPanel { Dock = DockStyle.Fill, BackColor = Theme.Ground };
        Controls.Add(split);

        var tiles = BuildTiles();
        Controls.Add(tiles);

        _phase.Dock = DockStyle.Top;
        Controls.Add(_phase);

        Controls.Add(BuildCommandBar());

        // --- trong split: cot phai la log, cot trai la so lieu ---
        _log.Dock = DockStyle.Fill;
        split.Controls.Add(_log);

        var left = new DrawPanel
        {
            Dock = DockStyle.Left,
            Width = Theme.Px(438),
            BackColor = Theme.Ground,
            AutoScroll = true
        };
        split.Controls.Add(left);

        var edge = new DrawPanel
        {
            Dock = DockStyle.Left,
            Width = 1,
            BackColor = Theme.Line
        };
        split.Controls.Add(edge);

        BuildLeftColumn(left);
    }

    private DrawPanel BuildCommandBar()
    {
        var bar = new DrawPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.Px(62),
            BackColor = Theme.Surface,
            Padding = new Padding(0, 0, 0, 1)
        };

        _status.SetBounds(Theme.Px(16), Theme.Px(8), Theme.Px(240), Theme.Px(24));
        _status.Font = Theme.StateBig;
        _status.BackColor = Theme.Surface;
        _status.ForeColor = Theme.Head;
        _status.Text = "Đang dừng";
        bar.Controls.Add(_status);

        _ctx.SetBounds(Theme.Px(16), Theme.Px(34), Theme.Px(520), Theme.Px(20));
        _ctx.Font = Theme.DataSm;
        _ctx.BackColor = Theme.Surface;
        _ctx.ForeColor = Theme.Dim;
        bar.Controls.Add(_ctx);

        _btnToggle.Text = $"Bật  ({_jobKey})";
        _btnToggle.Primary = true;
        _btnToggle.Font = Theme.PhaseBig;
        _btnToggle.Click += (_, _) => Toggle();
        _btnToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnToggle.SetBounds(bar.Width - Theme.Px(180), Theme.Px(16), Theme.Px(164), Theme.Px(32));
        bar.Controls.Add(_btnToggle);

        var kbd = new Label
        {
            Text = _jobKey,
            Font = Theme.DataSm,
            BackColor = Theme.Surface,
            ForeColor = Theme.Dimmer,
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        kbd.SetBounds(bar.Width - Theme.Px(240), Theme.Px(22), Theme.Px(52), Theme.Px(20));
        bar.Controls.Add(kbd);

        return bar;
    }

    private DrawPanel BuildTiles()
    {
        var host = new DrawPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.Px(78),
            BackColor = Theme.Surface
        };

        _tileCatch.Caption = "Cá phiên này";
        _tileCatch.Kind = SparkKind.Line;
        _tileCatch.Ink = Theme.Accent;

        _tileRate.Caption = "Cá / giờ";
        _tileRate.Kind = SparkKind.Line;
        _tileRate.Ink = Theme.Good;

        _tileUptime.Caption = "Chạy liên tục";
        _tileUptime.Kind = SparkKind.None;

        _tileHit.Caption = "Thả câu ăn";
        _tileHit.Kind = SparkKind.Stack;
        _tileHit.Divider = false;

        var all = new[] { _tileCatch, _tileRate, _tileUptime, _tileHit };
        foreach (var t in all) host.Controls.Add(t);

        void Place()
        {
            int w = host.ClientSize.Width / 4;
            for (int i = 0; i < all.Length; i++)
                all[i].SetBounds(i * w, 0, i == all.Length - 1 ? host.ClientSize.Width - i * w : w,
                                 host.ClientSize.Height);
        }

        host.Resize += (_, _) => Place();
        Place();
        return host;
    }

    private void BuildLeftColumn(Control host)
    {
        // Cot rong Px(438). Thanh cuon doc cua AutoScroll an mat ~17 px, nen khung
        // phai hep hon the — khong thi WinForms them ca thanh cuon NGANG.
        int w = Theme.Px(438) - Theme.Px(12) - SystemInformation.VerticalScrollBarWidth - Theme.Px(4);
        int y = Theme.Px(10);

        // ---- Doc HUD ----
        var hud = new DarkGroup { Title = "Đọc HUD", Bounds = new Rectangle(Theme.Px(12), y, w, Theme.Px(181)) };
        host.Controls.Add(hud);

        _watch.Text = "Theo dõi (chỉ đọc, không bấm)";
        _watch.SetCheckedQuiet(true);
        _watch.BackColor = Theme.Surface;
        _watch.SetBounds(Theme.Px(12), Theme.Px(22), Theme.Px(260), Theme.Px(22));
        hud.Controls.Add(_watch);

        _mHud = _meters.Add("HUD");
        _mFill = _meters.Add("thanh");
        _mFish = _meters.Add("cá cắn");
        _mReject = _meters.Add("chê mồi");
        _mNoWater = _meters.Add("xa nước");
        _mKeep = _meters.Add("cất vào");
        // 6 hang x Px(21). Doi so hang thi phai doi ca chieu cao nay, chieu cao group ben tren,
        // va buoc y ben duoi — MeterList khong tu gian.
        _meters.SetBounds(Theme.Px(12), Theme.Px(48), w - Theme.Px(24), Theme.Px(127));
        hud.Controls.Add(_meters);
        y += Theme.Px(191);

        // ---- Ba lo / cop ----
        var kg = new DarkGroup { Title = "Ba lô & cốp xe", Bounds = new Rectangle(Theme.Px(12), y, w, Theme.Px(152)) };
        host.Controls.Add(kg);

        _capBag.Label = "ba lô";
        _capBag.SetBounds(Theme.Px(12), Theme.Px(22), w - Theme.Px(24), Theme.Px(56));
        kg.Controls.Add(_capBag);

        _capTrunk.Label = "cốp xe";
        _capTrunk.SetBounds(Theme.Px(12), Theme.Px(84), w - Theme.Px(24), Theme.Px(56));
        kg.Controls.Add(_capTrunk);
        y += Theme.Px(162);

        // ---- Vung da khoanh ----
        var reg = new DarkGroup
        {
            Title = "Vùng đã khoanh",
            Bounds = new Rectangle(Theme.Px(12), y, w, Theme.Px(284))
        };
        host.Controls.Add(reg);

        Lab(reg, "Màn hình game:", Theme.Px(12), Theme.Px(25), Theme.Px(104));
        _screens.SetBounds(Theme.Px(118), Theme.Px(21), w - Theme.Px(130), Theme.Px(24));
        _screens.SelectedIndexChanged += OnScreenChanged;
        reg.Controls.Add(_screens);

        _profile.SetBounds(Theme.Px(12), Theme.Px(52), w - Theme.Px(24), Theme.Px(18));
        _profile.Font = Theme.DataSm;
        _profile.BackColor = Theme.Surface;
        reg.Controls.Add(_profile);

        int tw = (w - Theme.Px(24) - Theme.Px(20)) / 6;
        AddThumb(reg, _thumbBar, Theme.Px(12) + 0 * (tw + Theme.Px(4)), Theme.Px(78), tw, "Thanh");
        AddThumb(reg, _thumbFish, Theme.Px(12) + 1 * (tw + Theme.Px(4)), Theme.Px(78), tw, "Cá");
        AddThumb(reg, _thumbReject, Theme.Px(12) + 2 * (tw + Theme.Px(4)), Theme.Px(78), tw, "Thông báo");
        AddThumb(reg, _thumbNoWater, Theme.Px(12) + 3 * (tw + Theme.Px(4)), Theme.Px(78), tw, "Xa nước");
        AddThumb(reg, _thumbKeep, Theme.Px(12) + 4 * (tw + Theme.Px(4)), Theme.Px(78), tw, "CẤT VÀO");
        AddThumb(reg, _thumbKeepBand, Theme.Px(12) + 5 * (tw + Theme.Px(4)), Theme.Px(78), tw, "Vùng quét");

        int by = Theme.Px(158);
        int bw = (w - Theme.Px(24) - Theme.Px(8)) / 3;
        Btn(reg, _btnBar, Theme.Px(12), by, bw, "Khoanh thanh", () => Pick(FishingSlot.Bar));
        Btn(reg, _btnFish, Theme.Px(12) + bw + Theme.Px(4), by, bw, "Khoanh cá", () => Pick(FishingSlot.Fish));
        Btn(reg, _btnReject, Theme.Px(12) + (bw + Theme.Px(4)) * 2, by, bw, "Khoanh thông báo",
            () => Pick(FishingSlot.Reject));

        by += Theme.Px(34);
        int bw2 = (w - Theme.Px(24) - Theme.Px(4)) / 2;
        Btn(reg, _btnKeep, Theme.Px(12), by, bw2, "Khoanh CẤT VÀO", () => Pick(FishingSlot.Keep));
        Btn(reg, _btnKeepBand, Theme.Px(12) + bw2 + Theme.Px(4), by, bw2, "Khoanh vùng quét nút",
            () => Pick(FishingSlot.KeepBand));

        // Rieng mau nay KHONG khoanh tay: no phai trung khop tung pixel voi o "thong bao" o tren,
        // nen nut chup lai dung rect do. Xem CaptureNoWater.
        by += Theme.Px(34);
        Btn(reg, _btnNoWater, Theme.Px(12), by, w - Theme.Px(24),
            "Chụp mẫu “không đứng gần mặt nước”", CaptureNoWater);
        y += Theme.Px(294);

        // ---- Do ca vao cop xe ----
        var dump = new DarkGroup
        {
            Title = "Đổ cá vào cốp xe",
            Bounds = new Rectangle(Theme.Px(12), y, w, Theme.Px(206))
        };
        host.Controls.Add(dump);

        _dumpEnabled.Text = "Tự đổ cá vào cốp khi ba lô gần đầy";
        _dumpEnabled.BackColor = Theme.Surface;
        _dumpEnabled.SetBounds(Theme.Px(12), Theme.Px(22), w - Theme.Px(24), Theme.Px(22));
        _dumpEnabled.CheckedChanged += OnDumpEnabledChanged;
        dump.Controls.Add(_dumpEnabled);

        int ry = Theme.Px(50);
        Lab(dump, "Kiểm tra KG mỗi", Theme.Px(12), ry + Theme.Px(4), Theme.Px(120));
        _everyN.SetBounds(Theme.Px(138), ry, Theme.Px(62), Theme.Px(24));
        _everyN.Min = 1;
        _everyN.Max = 50;
        _everyN.SetValueQuiet(Math.Clamp(_cfg.WeightCheckEveryCatches, 1, 50));
        _everyN.ValueChanged += OnEveryNChanged;
        dump.Controls.Add(_everyN);
        Lab(dump, "con cá", Theme.Px(206), ry + Theme.Px(4), Theme.Px(60));

        Lab(dump, "· đổ mỗi", Theme.Px(266), ry + Theme.Px(4), Theme.Px(66));
        _dumpEvery.SetBounds(Theme.Px(332), ry, Theme.Px(56), Theme.Px(24));
        _dumpEvery.Min = 0;
        _dumpEvery.Max = 50;
        _dumpEvery.SetValueQuiet(Math.Clamp(_cfg.DumpEveryCatches, 0, 50));
        _dumpEvery.ValueChanged += OnDumpEveryChanged;
        dump.Controls.Add(_dumpEvery);

        ry += Theme.Px(30);
        Lab(dump, "Quay mặt sau khi đổ: giữ S", Theme.Px(12), ry + Theme.Px(4), Theme.Px(190));
        _turnMs.SetBounds(Theme.Px(206), ry, Theme.Px(70), Theme.Px(24));
        _turnMs.Min = 0;
        _turnMs.Max = 3000;
        _turnMs.Step = 50;
        _turnMs.SetValueQuiet(Math.Clamp(_cfg.AfterDumpTurnMs, 0, 3000));
        _turnMs.ValueChanged += OnTurnMsChanged;
        dump.Controls.Add(_turnMs);
        Lab(dump, "ms (0=tắt)", Theme.Px(282), ry + Theme.Px(4), Theme.Px(90));

        ry += Theme.Px(32);
        _dumpStatus.SetBounds(Theme.Px(12), ry, w - Theme.Px(24), Theme.Px(34));
        _dumpStatus.Font = Theme.DataSm;
        _dumpStatus.BackColor = Theme.Surface;
        dump.Controls.Add(_dumpStatus);

        ry += Theme.Px(40);
        Btn(dump, _btnTrunkSetup, Theme.Px(12), ry, Theme.Px(184), "Cấu hình đổ cốp…", OpenTrunkSetup);
        Btn(dump, _btnAltProbe, Theme.Px(202), ry, Theme.Px(184), "Test giữ Alt (menu xe)", DoAltProbe);
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

    private static void Btn(Control host, DarkButton b, int x, int y, int w, string text, Action onClick)
    {
        b.Text = text;
        b.SetBounds(x, y, w, Theme.Px(30));
        b.Click += (_, _) => onClick();
        host.Controls.Add(b);
    }

    private static void AddThumb(Control host, PictureBox box, int x, int y, int w, string caption)
    {
        host.Controls.Add(new Label
        {
            Text = caption,
            Font = Theme.DataSm,
            BackColor = Theme.Surface,
            ForeColor = Theme.Dim,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(x, y, w, Theme.Px(14))
        });
        box.SetBounds(x, y + Theme.Px(16), w, Theme.Px(58));
        box.BorderStyle = BorderStyle.FixedSingle;
        box.SizeMode = PictureBoxSizeMode.Zoom;
        box.BackColor = Theme.Well;
        host.Controls.Add(box);
    }

    private sealed class ScreenItem
    {
        public Screen Screen { get; }
        public ScreenItem(Screen s) => Screen = s;
        public override string ToString()
        {
            var b = Screen.Bounds;
            string tag = b.Width == 2560 && b.Height == 1440 ? "  (2K)" : "";
            return $"{Screen.DeviceName}  {b.Width}×{b.Height}{tag}";
        }
    }

    private Screen SelectedScreen => (_screens.SelectedItem as ScreenItem)?.Screen;

    private void FillScreens()
    {
        _screens.Items.Clear();
        Screen prefer = FishingConfig.Prefer2kOrPrimary();
        int select = 0;
        foreach (var s in Screen.AllScreens)
        {
            _screens.Items.Add(new ScreenItem(s));
            if (s.DeviceName == prefer.DeviceName) select = _screens.Items.Count - 1;
        }
        if (_screens.Items.Count > 0)
            _screens.SelectedIndex = select;
    }

    private void OnScreenChanged()
    {
        if (IsRunning) return;
        _reader?.Dispose();
        _reader = null;
        RefreshProfileLabel();
        RefreshDumpStatus();
        LoadThumbs();
        ClearLive();
    }

    // ------------------------------------------------- đổ cá vào cốp

    private void RefreshDumpStatus()
    {
        var screen = SelectedScreen;
        var p = screen is null ? null : _cfg.TryGet(screen);

        // Control tu ve co ban Quiet rieng, nhung van giu co _syncingDumpUi: nhanh
        // veto trong OnDumpEnabledChanged dua vao no, va bo di la mo duong cho de quy.
        _syncingDumpUi = true;
        try
        {
            _dumpEnabled.SetCheckedQuiet(p?.TrunkDumpEnabled == true);
            _everyN.SetValueQuiet(Math.Clamp(_cfg.WeightCheckEveryCatches, _everyN.Min, _everyN.Max));
            _dumpEvery.SetValueQuiet(Math.Clamp(_cfg.DumpEveryCatches, _dumpEvery.Min, _dumpEvery.Max));
            _turnMs.SetValueQuiet(Math.Clamp(_cfg.AfterDumpTurnMs, _turnMs.Min, _turnMs.Max));
        }
        finally { _syncingDumpUi = false; }

        if (p is null)
        {
            _dumpStatus.Text = "chưa có hồ sơ cho màn hình này";
            _dumpStatus.ForeColor = Theme.Dim;
            return;
        }

        string gaps = p.DescribeTrunkGaps();
        _dumpStatus.Text = gaps;
        _dumpStatus.ForeColor = gaps.StartsWith("đủ") ? Theme.Good : Theme.Warn;
    }

    private void OnDumpEnabledChanged()
    {
        if (_syncingDumpUi) return;
        var screen = SelectedScreen;
        if (screen is null) return;

        var p = _cfg.GetOrCreate(screen);
        // Bat khi chua khoanh du thi bot se chet giua chung — chan ngay tai day cho de hieu.
        if (_dumpEnabled.Checked && !p.DescribeTrunkGaps().StartsWith("đủ"))
        {
            Append("chưa bật được: " + p.DescribeTrunkGaps());
            _syncingDumpUi = true;
            try { _dumpEnabled.SetCheckedQuiet(false); }
            finally { _syncingDumpUi = false; }
            return;
        }

        p.TrunkDumpEnabled = _dumpEnabled.Checked;
        try { _cfg.Save(); } catch (Exception ex) { Append("lưu cấu hình lỗi: " + ex.Message); }
        Append(p.TrunkDumpEnabled ? "bật tự đổ cốp" : "tắt tự đổ cốp");
    }

    private void OnEveryNChanged()
    {
        if (_syncingDumpUi) return;
        _cfg.WeightCheckEveryCatches = _everyN.Value;
        try { _cfg.Save(); } catch { }
    }

    private void OnDumpEveryChanged()
    {
        if (_syncingDumpUi) return;
        _cfg.DumpEveryCatches = _dumpEvery.Value;
        try { _cfg.Save(); } catch { }
        Append(_cfg.DumpEveryCatches == 0
            ? "trần cứng theo số con: tắt — chỉ đổ theo cân nặng"
            : $"trần cứng: đổ cốp mỗi {_cfg.DumpEveryCatches} con");
    }

    private void OnTurnMsChanged()
    {
        if (_syncingDumpUi) return;
        _cfg.AfterDumpTurnMs = _turnMs.Value;
        try { _cfg.Save(); } catch { }
        Append(_cfg.AfterDumpTurnMs == 0
            ? "quay mặt sau khi đổ: tắt"
            : $"quay mặt sau khi đổ: giữ S {_cfg.AfterDumpTurnMs} ms");
    }

    private void OpenTrunkSetup()
    {
        if (IsRunning) { Append("đang câu — dừng bot trước khi cấu hình"); return; }
        var screen = SelectedScreen;
        if (screen is null) { Append("không chọn được màn hình"); return; }

        var p = _cfg.GetOrCreate(screen);
        using (var f = new TrunkSetupForm(_cfg, screen, p))
            f.ShowDialog(FindForm());

        _cfg = FishingConfig.Load();
        RefreshProfileLabel();
        RefreshDumpStatus();
    }

    private void RefreshProfileLabel()
    {
        var screen = SelectedScreen;
        if (screen is null)
        {
            _profile.Text = "không thấy màn hình";
            _ctx.Text = $"game: {_cfg.WindowMatch}";
            return;
        }
        var p = _cfg.TryGet(screen);
        _profile.Text = p is null
            ? $"{screen.Bounds.Width}x{screen.Bounds.Height} — chưa khoanh"
            : p.DescribeGaps();
        _profile.ForeColor = p is { Bar.IsSet: true, Fish.IsSet: true, Reject.IsSet: true, Keep.IsSet: true }
            ? Theme.Good : Theme.Dim;

        // Thanh lenh nhac lai cua so game va man hinh dang nham vao: hai thu nay sai
        // la bot doc vao khoang khong, ma truoc day chung nam mat trong combo box.
        var b = screen.Bounds;
        _ctx.Text = $"game: {_cfg.WindowMatch}   ·   {screen.DeviceName}  {b.Width}×{b.Height}   ·   {_profile.Text}";
    }

    private enum FishingSlot { Bar, Fish, Reject, Keep, KeepBand }

    /// <summary>
    /// Chụp mẫu "Bạn không đứng gần mặt nước".
    ///
    /// KHÔNG cho kéo tay như các mẫu khác. Mẫu này chấm trên đúng ô đã khoanh cho thông báo chê
    /// mồi — game vẽ hai thông báo cùng chỗ — nên nó phải trùng khớp kích thước ô đó. Kéo tay thì
    /// lệch một pixel là <c>LoadTemplate</c> báo "lệch ô" và tính năng im lặng không chạy. Ở đây
    /// cắt thẳng tại rect của ô, nên lệch kích thước thành chuyện không thể xảy ra.
    /// </summary>
    private void CaptureNoWater()
    {
        if (IsRunning) { Append("đang chạy — dừng trước khi chụp mẫu"); return; }
        var screen = SelectedScreen;
        if (screen is null) { Append("không chọn được màn hình"); return; }

        var profile = _cfg.TryGet(screen);
        if (profile is null || !profile.Reject.IsSet)
        {
            Append("chưa khoanh ô thông báo — bấm “Khoanh thông báo” trước, mẫu này dùng chung ô đó");
            return;
        }

        var host = FindForm();
        using var shot = StillPicker.CaptureWithCountdown(
            host, screen,
            "Đứng XA mặt nước rồi bấm 4 cho hiện “Bạn không đứng gần mặt nước”. " +
            "Bấm xong có " + _cfg.ShotCountdownSec + " giây.",
            _cfg.ShotCountdownSec, _cfg.WindowMatch, out string problem);

        if (shot is null)
        {
            Append("chụp mẫu không gần nước: " + (problem ?? "không chụp được"));
            return;
        }

        var abs = FishingConfig.ToAbsolute(screen, profile.Reject);
        // Anh chup theo man hinh dang chon, nen doi toa do tuyet doi ve toa do TRONG anh.
        var inImage = new Rectangle(abs.X - screen.Bounds.X, abs.Y - screen.Bounds.Y, abs.Width, abs.Height);
        inImage = Rectangle.Intersect(inImage, new Rectangle(0, 0, shot.Width, shot.Height));
        if (inImage.Width < 1 || inImage.Height < 1)
        {
            Append("ô thông báo nằm ngoài ảnh chụp — khoanh lại ô thông báo");
            return;
        }

        var crop = shot.Clone(inImage, shot.PixelFormat);
        try
        {
            RegionPicker.SavePng(crop, FishingConfig.NoWaterTemplatePath(profile.Key));
        }
        catch (Exception ex)
        {
            Append("lưu mẫu lỗi: " + ex.Message);
            crop.Dispose();
            return;
        }

        DisposeThumb(_thumbNoWater);
        _thumbNoWater.Image = crop;
        _reader?.Dispose();
        _reader = null;
        Append($"đã chụp mẫu không gần nước  {crop.Width}×{crop.Height} → {profile.Key}");
        Append("kiểm bằng hàng “xa nước” ở Đọc HUD: lúc thông báo hiện phải lên ≥ " +
               $"{_cfg.NoWaterNccMin:F2}, lúc không hiện phải thấp.");
    }

    private void Pick(FishingSlot slot)
    {
        if (IsRunning) { Append("đang chạy — dừng trước khi khoanh lại"); return; }
        var screen = SelectedScreen;
        if (screen is null) { Append("không chọn được màn hình"); return; }

        var (title, hint) = slot switch
        {
            FishingSlot.Bar => ("Khoanh thanh câu",
                "Kéo ôm cột xanh (HUD đang mở). Không cần icon cá."),
            FishingSlot.Fish => ("Khoanh icon cá",
                "Kéo ôm icon cá phía trên thanh — phải đang hiện lúc cá cắn."),
            FishingSlot.Keep => ("Khoanh CẤT VÀO",
                "Ôm TRỌN khối nền màu của nút CẤT VÀO (trái), không ôm sát chữ — bot lấy màu và kích thước nút từ ô này."),
            FishingSlot.KeepBand => ("Khoanh vùng quét nút",
                "Kéo ôm cả khoảng nút CẤT VÀO có thể trượt tới — từ mức cao nhất tới mức thấp nhất ở mọi loại cá."),
            _ => ("Khoanh thông báo",
                "Kéo ôm hộp đỏ CÂU CÁ + chữ “Cá chê mồi của bạn”. Đừng kéo xuống minimap.")
        };

        var host = FindForm();
        var result = RegionPicker.Run(host, screen, title, hint);
        if (result is null)
        {
            Append("đã hủy khoanh " + SlotName(slot));
            return;
        }

        var profile = _cfg.GetOrCreate(screen);
        string key = profile.Key;
        string path = slot switch
        {
            FishingSlot.Bar => FishingConfig.BarPreviewPath(key),
            FishingSlot.Fish => FishingConfig.FishTemplatePath(key),
            FishingSlot.Keep => FishingConfig.KeepTemplatePath(key),
            FishingSlot.KeepBand => FishingConfig.KeepBandPreviewPath(key),
            _ => FishingConfig.RejectTemplatePath(key)
        };

        try
        {
            RegionPicker.SavePng(result.Preview, path);
            switch (slot)
            {
                case FishingSlot.Bar:
                    profile.Bar = FishingRect.FromRelative(result.Relative);
                    break;
                case FishingSlot.Fish:
                    profile.Fish = FishingRect.FromRelative(result.Relative);
                    break;
                case FishingSlot.Keep:
                    profile.Keep = FishingRect.FromRelative(result.Relative);
                    break;
                case FishingSlot.KeepBand:
                    profile.KeepBand = FishingRect.FromRelative(result.Relative);
                    break;
                default:
                    profile.Reject = FishingRect.FromRelative(result.Relative);
                    break;
            }
            _cfg.Save();
        }
        catch (Exception ex)
        {
            Append("lưu ROI lỗi: " + ex.Message);
            result.Preview.Dispose();
            return;
        }

        SetThumb(slot, result.Preview);
        _reader?.Dispose();
        _reader = null;
        RefreshProfileLabel();
        Append($"đã khoanh {SlotName(slot)}  {result.Relative.Width}×{result.Relative.Height}  @ {result.Relative.X},{result.Relative.Y}  → {key}");

        if (slot is FishingSlot.Keep or FishingSlot.KeepBand)
            WarnBandFit(profile);
    }

    /// <summary>Bot chỉ dò nút bên trong vùng quét, nên ô CẤT VÀO phải nằm trong đó.</summary>
    private void WarnBandFit(FishingProfile profile)
    {
        if (!profile.Keep.IsSet || !profile.KeepBand.IsSet) return;
        if (profile.KeepBand.ToRectangle().Contains(profile.Keep.ToRectangle())) return;
        Append("cảnh báo: ô CẤT VÀO nằm ngoài vùng quét — khoanh lại vùng quét trùm cả ô nút");
    }

    private static string SlotName(FishingSlot s) => s switch
    {
        FishingSlot.Bar => "thanh",
        FishingSlot.Fish => "cá",
        FishingSlot.Keep => "CẤT VÀO",
        FishingSlot.KeepBand => "vùng quét",
        _ => "thông báo"
    };

    private void LoadThumbs()
    {
        var screen = SelectedScreen;
        if (screen is null) return;
        var p = _cfg.TryGet(screen);
        string key = p?.Key ?? $"{screen.Bounds.Width}x{screen.Bounds.Height}";
        LoadThumb(_thumbBar, FishingConfig.BarPreviewPath(key));
        LoadThumb(_thumbFish, FishingConfig.FishTemplatePath(key));
        LoadThumb(_thumbReject, FishingConfig.RejectTemplatePath(key));
        LoadThumb(_thumbNoWater, FishingConfig.NoWaterTemplatePath(key));
        LoadThumb(_thumbKeep, FishingConfig.KeepTemplatePath(key));
        LoadThumb(_thumbKeepBand, FishingConfig.KeepBandPreviewPath(key));
    }

    private static void LoadThumb(PictureBox box, string path)
    {
        DisposeThumb(box);
        if (!File.Exists(path)) return;
        try
        {
            using var fs = File.OpenRead(path);
            box.Image = Image.FromStream(fs);
        }
        catch { /* file hong — bo qua thumb */ }
    }

    private void SetThumb(FishingSlot slot, Bitmap bmp)
    {
        var box = slot switch
        {
            FishingSlot.Bar => _thumbBar,
            FishingSlot.Fish => _thumbFish,
            FishingSlot.Keep => _thumbKeep,
            FishingSlot.KeepBand => _thumbKeepBand,
            _ => _thumbReject
        };
        DisposeThumb(box);
        box.Image = bmp;
    }

    private static void DisposeThumb(PictureBox box)
    {
        var old = box.Image;
        box.Image = null;
        old?.Dispose();
    }

    private void StartBot()
    {
        if (IsRunning) return;

        // Phien truoc da chay het muc (cop day + ba lo day) thi cu bam dau tien khong chay lai.
        //
        // Vi sao chan: bot tu ngat, nguoi dung thay ba lo day nen bam F4 de "tat" — nhung no
        // dung san roi, va cu bam do hoa ra BAT LEN. Log 17/08 co dung canh nay: 20:59:13 ngat
        // vi ba lo day, 20:59:14 chay lai, roi cau tiep vao cai ba lo khong con cho. Nhin tu
        // ngoai thi giong het "bot khong chiu tu ngat".
        if (_finished)
        {
            _finished = false;
            _status.Text = "Phiên trước đã xong — bấm lần nữa để câu tiếp";
            _status.ForeColor = Theme.Good;
            Append("phiên trước đã xong: cốp đầy và ba lô đầy. Đi bán cá đi — " +
                   "muốn câu tiếp ngay thì bấm lần nữa.");
            return;
        }

        var screen = SelectedScreen;
        if (screen is null) { Append("không chọn được màn hình"); return; }

        _cfg = FishingConfig.Load();
        _cfg.Normalize();
        var profile = _cfg.TryGet(screen);
        if (profile is null || !profile.Bar.IsSet || !profile.Fish.IsSet)
        {
            Append("KHÔNG chạy: cần khoanh thanh và cá");
            MessageBox.Show("Cần khoanh thanh câu và icon cá trước.", "Câu cá",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _reader?.Dispose();
        _reader = null;

        try { _cfg.Save(); } catch { }

        // Dat lai o chi so: so cua phien truoc de lai la sai, va sparkline phai bat
        // dau lai tu day chu khong noi tiep duong cu.
        foreach (var t in new[] { _tileCatch, _tileRate, _tileUptime, _tileHit }) t.Reset();
        _sparkAt = -1;
        ShowState(FishingState.Idle);

        _bot = new FishingBot(_cfg, screen, profile);
        _bot.Log += s => Post(() => Append(s));
        _bot.SnapshotReady += s => Post(() => ShowSnapshot(s));
        _bot.StateChanged += s => Post(() => ShowState(s));
        _bot.Stopped += (r, msg) => Post(() =>
        {
            _status.Text = "Đã dừng — " + FishingBot.TenLyDo(r);
            // BagFull la phien chay het muc chu khong phai su co — dung to do cho no.
            _status.ForeColor = r is FishingStopReason.UserStopped or FishingStopReason.BagFull
                ? Theme.Good
                : Theme.Bad;
            SetRunningUi(false);
            if (!string.IsNullOrEmpty(msg) && r != FishingStopReason.UserStopped)
                Append(msg);

            _finished = r == FishingStopReason.BagFull;
            // Keu mot tieng: luc nay nguoi dung gan nhu chac chan dang o cua so khac (log day
            // dong "cho cua so PlayXGTA — dang focus Chrome/Discord"), nen mot cai nhan xanh
            // trong panel nen sau la thu khong ai thay.
            if (_finished) { try { System.Media.SystemSounds.Exclamation.Play(); } catch { } }
        });

        _status.Text = "Đang câu";
        _status.ForeColor = Theme.Accent;
        SetRunningUi(true);
        Append("--- bắt đầu câu ---");
        _bot.Start();
    }

    // ------------------------------------------------- test giữ Alt (menu xe)

    /// <summary>
    /// Tìm cách rê con trỏ tới nút mà KHÔNG làm tắt menu radial.
    ///
    /// Lần chạy đầu cho thấy <see cref="InputSender.MoveSmooth"/> vừa làm camera xoay vừa làm
    /// menu tắt: nó bắn kèm MOUSEEVENTF_MOVE, mà GTA đọc raw input để xoay camera. Nên probe
    /// này thử ba kiểu trong một lần bấm — đứng yên, SetCursorPos, SendInput — rồi chụp lại
    /// từng bước để so bằng mắt xem kiểu nào giữ được menu.
    /// </summary>
    private void DoAltProbe()
    {
        if (IsRunning) { Append("đang câu — dừng bot trước khi test"); return; }
        var screen = SelectedScreen;
        if (screen is null) { Append("không chọn được màn hình"); return; }

        var ok = MessageBox.Show(this,
            "Test này chạy thử cả chuỗi mở cốp bằng toạ độ hai ô nút bạn đã khoanh:\r\n" +
            "giữ Alt → click Tương tác → click Cốp xe → Esc đóng lại.\r\n\r\n" +
            "LƯU Ý: bot sẽ click thật. Nếu menu không hiện (xe quá xa, camera không hướng vào xe) " +
            "thì cú click đó rơi vào thế giới game — hãy đứng sát xe, tay không cầm súng.\r\n\r\n" +
            "Bấm OK rồi có 5 giây để click vào cửa sổ game.",
            "Test giữ Alt", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (ok != DialogResult.OK) return;

        _btnAltProbe.Enabled = false;
        Append("--- test giữ Alt ---");
        new Thread(() => AltProbeWorker(screen)) { IsBackground = true, Name = "AltProbe" }.Start();
    }

    private void AltProbeWorker(Screen screen)
    {
        void Log(string s) => Post(() => Append(s));

        try
        {
            for (int i = 5; i >= 1; i--)
            {
                int n = i;
                Log($"...{n}");
                Thread.Sleep(1000);
            }

            string title = Native.ForegroundTitle();
            Log($"cửa sổ đang focus: “{title}”");
            if (!title.Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase))
            {
                Log($"KHÔNG phải cửa sổ game (“{_cfg.WindowMatch}”) — huỷ test");
                return;
            }

            // Nguoi dung dang giu Alt that thi khong gianh quyen so huu phim: cu Alt-up cua
            // minh se lam phim cua ho "dinh len" cho toi khi ho bam lai.
            if (Native.IsKeyDown(HeldKeys.VK_ALT))
            {
                Log("Alt đang được giữ sẵn — huỷ test, nhả Alt ra rồi thử lại");
                return;
            }

            string dir = Path.Combine(AppPaths.Root, "debug-alt");
            Directory.CreateDirectory(dir);

            var b = screen.Bounds;
            var target = AltProbeTarget(screen, out string how);
            var trunk = AltProbeTrunk(screen);
            Native.GetCursorPos(out var before);
            Log($"chuột trước khi bấm Alt: {before.x},{before.y}");
            Log($"Tương tác: {target.X},{target.Y} ({how})");
            Log(trunk is null ? "Cốp xe: chưa khoanh" : $"Cốp xe: {trunk.Value.X},{trunk.Value.Y}");

            InputSender.AltDown();
            try
            {
                Thread.Sleep(500);
                Log($"IsKeyDown(Alt) sau khi bấm = {Native.IsKeyDown(HeldKeys.VK_ALT)}");
                SaveProbeShot(dir, "alt-1-menu", b, Log);

                InputSender.MoveCursorOnlySmooth(target.X, target.Y, 12);
                Thread.Sleep(400);
                SaveProbeShot(dir, "alt-2-hover-tuongtac", b, Log);

                Log("click Tương tác");
                ProbeClick();
                Thread.Sleep(700);
                SaveProbeShot(dir, "alt-3-sau-click", b, Log);

                if (trunk is null)
                {
                    Log("chưa khoanh Nút Cốp xe — dừng ở đây");
                }
                else
                {
                    InputSender.MoveCursorOnlySmooth(trunk.Value.X, trunk.Value.Y, 12);
                    Thread.Sleep(400);
                    SaveProbeShot(dir, "alt-4-hover-copxe", b, Log);

                    Log("click Cốp xe");
                    ProbeClick();
                    Thread.Sleep(1200);
                }
            }
            finally
            {
                InputSender.AltUp();
            }

            Thread.Sleep(150);
            bool stillDown = Native.IsKeyDown(HeldKeys.VK_ALT);
            Log($"IsKeyDown(Alt) sau khi nhả = {stillDown}" + (stillDown ? "  ← ALT ĐANG KẸT" : ""));

            SaveProbeShot(dir, "alt-5-sau-copxe", b, Log);

            // Dong lai de khong bo man hinh kho do dang mo.
            InputSender.TapKey(0x1B);
            Thread.Sleep(700);
            SaveProbeShot(dir, "alt-6-sau-esc", b, Log);

            Log("xong — mở 6 ảnh trong " + dir);
            Log("ảnh 3 hiện menu 4 nút = click ăn. Ảnh 5 hiện cốp xe = đi hết được chuỗi.");
        }
        catch (Exception ex)
        {
            Log("lỗi test: " + ex.Message);
        }
        finally
        {
            HeldKeys.ReleaseAll();
            Post(() => _btnAltProbe.Enabled = !IsRunning);
        }
    }

    /// <summary>
    /// Đích rê tới. Ưu tiên tâm ô "Nút Tương tác" đã khoanh — lần trước tôi đoán một offset
    /// theo phần trăm bề rộng màn và trượt nút cả trăm pixel, nên có nhìn ảnh cũng không kết
    /// luận được là menu tắt hay chỉ là rê hụt.
    /// </summary>
    private Point AltProbeTarget(Screen screen, out string how)
    {
        var p = _cfg.TryGet(screen);
        if (p?.AltInteract.IsSet == true)
        {
            var r = FishingConfig.ToAbsolute(screen, p.AltInteract);
            how = "tâm ô Nút Tương tác đã khoanh";
            return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        }

        var b = screen.Bounds;
        how = "đoán bên trái tâm màn — chưa khoanh Nút Tương tác";
        return new Point(b.Left + b.Width / 2 - (int)(b.Width * 0.04), b.Top + b.Height / 2);
    }

    private Point? AltProbeTrunk(Screen screen)
    {
        var p = _cfg.TryGet(screen);
        if (p?.AltTrunk.IsSet != true) return null;
        var r = FishingConfig.ToAbsolute(screen, p.AltTrunk);
        return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    /// <summary>Click tại chỗ — KHÔNG rê, vì cú rê mới là thứ làm tắt menu.</summary>
    private static void ProbeClick()
    {
        InputSender.LeftDown();
        Thread.Sleep(60);
        InputSender.LeftUp();
    }

    private static void SaveProbeShot(string dir, string name, Rectangle bounds, Action<string> log)
    {
        string path = Path.Combine(dir, name + ".png");
        try
        {
            using var bmp = RegionPicker.Capture(bounds);
            RegionPicker.SavePng(bmp, path);
            log($"đã chụp {name}.png");
        }
        catch (Exception ex)
        {
            log($"chụp {name} lỗi: {ex.Message}");
        }
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
        _btnBar.Enabled = !running;
        _btnFish.Enabled = !running;
        _btnReject.Enabled = !running;
        _btnKeep.Enabled = !running;
        _btnKeepBand.Enabled = !running;
        _btnAltProbe.Enabled = !running;
        _btnTrunkSetup.Enabled = !running;
        _dumpEnabled.Enabled = !running;
        _everyN.Enabled = !running;
        _dumpEvery.Enabled = !running;
        _turnMs.Enabled = !running;
        _screens.Enabled = !running;
        RunningChanged?.Invoke(running);
    }

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    private void Tick()
    {
        if (!Visible) return;
        if (IsRunning) return;
        if (!_watch.Checked) return;
        var screen = SelectedScreen;
        if (screen is null) return;

        try
        {
            if (_reader is null)
            {
                var profile = _cfg.TryGet(screen);
                if (profile is null)
                {
                    ClearLive();
                    return;
                }
                _reader = new FishingReader(_cfg, screen, profile);
                if (_reader.FishTemplateProblem is { } fp) Append("mẫu cá: " + fp);
                if (_reader.RejectTemplateProblem is { } rp) Append("mẫu thông báo: " + rp);
                if (_reader.KeepTemplateProblem is { } kp) Append("mẫu CẤT VÀO: " + kp);
                else
                {
                    var band = _reader.KeepBandRegion;
                    var c = _reader.KeepColor;
                    Append($"dò CẤT VÀO: vùng {band.Width}×{band.Height} @ {band.X},{band.Y}, " +
                           $"màu nền nút #{c.R:X2}{c.G:X2}{c.B:X2} ±{_cfg.KeepColorTol}");
                }
            }
            ShowSnapshot(_reader.Read());
        }
        catch (Exception ex)
        {
            // Ban cu nhet loi vao nhan HUD roi de do mai. Nhan gio la thanh do nen
            // khong cho chu dai duoc — day vao log, va tat cac hang ve "--".
            ClearLive();
            _meters.Footer = "lỗi đọc: " + ex.Message;
            _meters.Invalidate();
        }
    }

    /// <summary>
    /// Cung con so nhu nam nhan Consolas cu, nhung moi hang gio co mot thanh do va
    /// mot vach nguong. Doc "ncc=0.873" mot minh thi khong biet la cao hay thap;
    /// canh vach 0.75 thi biet ngay.
    /// </summary>
    private void ShowSnapshot(FishingSnapshot s)
    {
        if (!s.BarConfigured)
        {
            Set(_mHud, -1, -1, "chưa khoanh", Theme.Dimmer, Theme.Dimmer);
            Set(_mFill, -1, -1, "--", Theme.Dimmer, Theme.Dimmer);
        }
        else
        {
            Set(_mHud, s.UiOpen ? 1 : 0.02, -1, s.UiOpen ? "MỞ" : "đóng",
                s.UiOpen ? Theme.Good : Theme.Dimmer, s.UiOpen ? Theme.Good : Theme.Dimmer);

            if (s.BlueFill01 < 0)
                Set(_mFill, -1, _cfg.DoneFill01, "không đọc được", Theme.Dimmer, Theme.Dimmer);
            else
                Set(_mFill, s.BlueFill01, _cfg.DoneFill01,
                    $"{s.BlueFill01 * 100,5:0.0}% /{_cfg.DoneFill01 * 100:0}",
                    s.UiOpen ? Theme.Accent : Theme.Dimmer, s.UiOpen ? Theme.Head : Theme.Dimmer);
        }

        if (!s.FishConfigured)
            Set(_mFish, -1, -1, "chưa có mẫu", Theme.Dimmer, Theme.Dimmer);
        else
            Set(_mFish, Ncc(s.FishScore), _cfg.FishNccMin,
                $"{(s.FishBite ? "CÓ" : "không")} {s.FishScore:F3}",
                s.FishBite ? Theme.Good : Theme.Dimmer, s.FishBite ? Theme.Good : Theme.Text);

        if (!s.RejectConfigured)
            Set(_mReject, -1, -1, "chưa có mẫu", Theme.Dimmer, Theme.Dimmer);
        else
            Set(_mReject, Ncc(s.RejectScore), _cfg.RejectNccMin,
                $"{(s.FailNotice ? "CÓ" : "không")} {s.RejectScore:F3}",
                s.FailNotice ? Theme.Bad : Theme.Dimmer, s.FailNotice ? Theme.Bad : Theme.Text);

        if (!s.NoWaterConfigured)
            Set(_mNoWater, -1, -1, "chưa có mẫu", Theme.Dimmer, Theme.Dimmer);
        else
            Set(_mNoWater, Ncc(s.NoWaterScore), _cfg.NoWaterNccMin,
                $"{(s.NoWaterNotice ? "CÓ" : "không")} {s.NoWaterScore:F3}",
                s.NoWaterNotice ? Theme.Bad : Theme.Dimmer, s.NoWaterNotice ? Theme.Bad : Theme.Text);

        if (!s.KeepConfigured)
        {
            Set(_mKeep, -1, -1, "chưa có mẫu", Theme.Dimmer, Theme.Dimmer);
            _meters.Footer = "";
        }
        else
        {
            Set(_mKeep, Ncc(s.KeepScore), _cfg.KeepNccMin,
                $"{(s.KeepVisible ? "CÓ" : "không")} {s.KeepScore:F3}",
                s.KeepVisible ? Theme.Good : Theme.Dimmer, s.KeepVisible ? Theme.Good : Theme.Text);

            if (s.KeepVisible)
                _meters.Footer = $"nút @ {s.KeepClick.X},{s.KeepClick.Y}  " +
                                 $"{s.KeepRect.Width}×{s.KeepRect.Height}  dens={s.KeepDensity:F2}";
            else if (s.KeepDensity >= 0)
                // Có khối đúng màu nhưng NCC dưới ngưỡng — hiện số ra để còn chỉnh KeepNccMin,
                // đừng để lẫn với "chẳng thấy gì".
                _meters.Footer = $"loại vì ncc={s.KeepScore:F3} < {_cfg.KeepNccMin:F2}  " +
                                 $"(dens={s.KeepDensity:F2})";
            else
                _meters.Footer = "không thấy khối đúng màu";
        }

        _meters.Invalidate();
    }

    /// <summary>NCC chay tu -1..1; thanh do chi nhan 0..1 nen cat phan am.</summary>
    private static double Ncc(double v) => v < 0 ? 0 : Math.Min(1, v);

    private static void Set(MeterList.Row r, double fill, double thr, string value,
                            Color ink, Color valueInk)
    {
        if (r is null) return;
        r.Fill01 = fill;
        r.Thr01 = thr;
        r.Value = value;
        r.Ink = ink;
        r.ValueInk = valueInk;
    }

    private void ClearLive()
    {
        Set(_mHud, -1, -1, "--", Theme.Dimmer, Theme.Dimmer);
        Set(_mFill, -1, -1, "--", Theme.Dimmer, Theme.Dimmer);
        Set(_mFish, -1, -1, "--", Theme.Dimmer, Theme.Dimmer);
        Set(_mReject, -1, -1, "--", Theme.Dimmer, Theme.Dimmer);
        Set(_mNoWater, -1, -1, "--", Theme.Dimmer, Theme.Dimmer);
        Set(_mKeep, -1, -1, "--", Theme.Dimmer, Theme.Dimmer);
        _meters.Footer = "";
        _meters.Invalidate();
    }

    // ---------------------------------------------------------------- trang thai bot

    /// <summary>
    /// Ve pha + so dem. Duoc goi ca tu event cua bot lan tu cho dat lai luc dung,
    /// nen phai chiu duoc <see cref="FishingState.Idle"/>.
    /// </summary>
    private void ShowState(FishingState s)
    {
        _state = s ?? FishingState.Idle;

        _phase.Update(_state, _cfg);

        _tileCatch.Value = _state.Catches.ToString();
        _tileCatch.Foot = _state.CatchesSinceDump > 0 ? $"chưa đổ: {_state.CatchesSinceDump} con" : "";

        _tileRate.Value = _state.CatchesPerHour < 0 ? "--" : $"{_state.CatchesPerHour:0}";
        _tileRate.Unit = _state.CatchesPerHour < 0 ? "" : "con";

        long sec = _state.SessionMs / 1000;
        _tileUptime.Value = _state.SessionMs <= 0 ? "--" : $"{sec / 60}:{sec % 60:00}";
        _tileUptime.Foot = _state.SecondsPerCatch < 0 ? "" : $"{_state.SecondsPerCatch:0} s / con";

        if (_state.BiteRate01 < 0)
        {
            _tileHit.Value = "--";
            _tileHit.Unit = "";
            _tileHit.SetStack();
        }
        else
        {
            _tileHit.Value = $"{_state.BiteRate01 * 100:0}";
            _tileHit.Unit = "%";
            double n = Math.Max(1, _state.Casts);
            // NoWater phai co khuc rieng: truoc day nhung cu tha do bi tinh vao CastMissed, nen
            // bo qua no la thanh nay tu dung hut mot khuc ma khong ai hieu vi sao.
            _tileHit.SetStack(
                (_state.Bites / n, Theme.Good),
                (_state.Rejects / n, Theme.Warn),
                (_state.NoWater / n, Theme.AccentDim),
                (_state.BiteTimeouts / n, Theme.Bad),
                (_state.CastMissed / n, Theme.Dimmer));
        }

        // Sparkline chi lay mot diem moi khi bat duoc them mot con — lay theo tick
        // thi duong se phang lì roi giat, khong noi len duoc gi.
        if (_state.Catches != _sparkAt)
        {
            _sparkAt = _state.Catches;
            _tileCatch.Push(_state.Catches);
            if (_state.CatchesPerHour >= 0) _tileRate.Push(_state.CatchesPerHour);
        }

        ShowCapacity();

        foreach (var t in new[] { _tileCatch, _tileRate, _tileUptime, _tileHit }) t.Invalidate();

        StateChanged?.Invoke(_state);
    }

    private void ShowCapacity()
    {
        var s = _state;

        double bagCap = s.BagCapKg > 0 ? s.BagCapKg : _cfg.BagCapKg;
        double stopKg = _cfg.BagStopKg(bagCap);
        _capBag.ValueText = s.BagKg < 0 ? "-- / " + $"{bagCap:F0} kg" : $"{s.BagKg:F1} / {bagCap:F0} kg";
        _capBag.Fill01 = s.BagKg < 0 ? -1 : Math.Min(1, s.BagKg / bagCap);
        _capBag.Pending01 = s.PendingFishKg <= 0 ? -1 : Math.Min(1, s.PendingFishKg / bagCap);
        _capBag.Thr01 = Math.Min(1, stopKg / bagCap);
        _capBag.Note = s.PendingFishKg > 0
            ? $"chỗ cá ≈ {s.PendingFishKg:F1} kg · dừng ở {stopKg:F1}"
            : $"dừng phiên ở {stopKg:F1} kg";
        _capBag.Invalidate();

        double trunkCap = s.TrunkCapKg > 0 ? s.TrunkCapKg : _cfg.TrunkCapKg;
        if (s.TrunkFreeKg < 0)
        {
            _capTrunk.ValueText = s.TrunkFull ? "đầy" : $"-- / {trunkCap:F0} kg";
            _capTrunk.Fill01 = s.TrunkFull ? 1 : -1;
        }
        else
        {
            _capTrunk.ValueText = $"còn trống {s.TrunkFreeKg:F1} / {trunkCap:F0} kg";
            _capTrunk.Fill01 = Math.Clamp(1 - s.TrunkFreeKg / trunkCap, 0, 1);
        }
        _capTrunk.Pending01 = -1;
        _capTrunk.Thr01 = _cfg.TrunkTightKg > 0
            ? Math.Clamp(1 - _cfg.TrunkTightKg / trunkCap, 0, 1)
            : -1;
        _capTrunk.Note = s.DumpOn
            ? $"lượt hỏng {s.TrunkFullStrikes}/{s.TrunkFullTries}" +
              (s.OcrHealthy ? "" : " · đọc KG đã tắt, đang đếm cá")
            : "tự đổ cốp: tắt";
        _capTrunk.Invalidate();
    }

    /// <summary>
    /// LogView tu dong dau gio va tu cat bot khi day. File bot-log.txt chi ghi
    /// khi BotLog.Enabled — mac dinh tat de khong mo/dong file moi dong.
    /// </summary>
    private void Append(string line)
    {
        _log.Append(line);
        BotLog.Write("câu", line);
    }
}
