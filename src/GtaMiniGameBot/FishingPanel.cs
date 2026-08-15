using System.Text;

namespace GtaMiniGameBot;

internal sealed class FishingPanel : UserControl
{
    private FishingConfig _cfg = FishingConfig.Load();
    private FishingReader _reader;
    private FishingBot _bot;

    private readonly ComboBox _screens = new();
    private readonly Label _profile = new();
    private readonly Label _status = new();
    private readonly Label _hud = new();
    private readonly Label _fill = new();
    private readonly Label _fish = new();
    private readonly Label _reject = new();
    private readonly Label _keep = new();
    private readonly CheckBox _watch = new();
    private readonly Button _btnToggle = new();
    private readonly Button _btnBar = new();
    private readonly Button _btnFish = new();
    private readonly Button _btnReject = new();
    private readonly Button _btnKeep = new();
    private readonly Button _btnKeepBand = new();
    private readonly Button _btnAltProbe = new();
    private readonly Button _btnTrunkSetup = new();
    private readonly CheckBox _dumpEnabled = new();
    private readonly NumericUpDown _everyN = new();
    private readonly Label _dumpStatus = new();
    private readonly PictureBox _thumbBar = new();
    private readonly PictureBox _thumbFish = new();
    private readonly PictureBox _thumbReject = new();
    private readonly PictureBox _thumbKeep = new();
    private readonly PictureBox _thumbKeepBand = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private string _jobKey = HotkeyText.Job();
    private bool _syncingDumpUi;

    public FishingPanel()
    {
        Font = new Font("Segoe UI", 9F);
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        AutoScroll = true;

        BuildUi();
        FillScreens();
        RefreshProfileLabel();
        RefreshDumpStatus();
        LoadThumbs();

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
        DisposeThumb(_thumbKeep);
        DisposeThumb(_thumbKeepBand);
    }

    private void BuildUi()
    {
        int y = 12;
        const int w = 796;

        var title = new Label
        {
            Text = "Câu cá — khoanh vùng + bot",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            AutoSize = false
        };
        title.SetBounds(12, y, w, 28);
        Controls.Add(title);
        y += 34;

        Controls.Add(new Label { Text = "Màn hình game:", Location = new Point(14, y + 4), AutoSize = true });
        _screens.SetBounds(130, y, 420, 24);
        _screens.DropDownStyle = ComboBoxStyle.DropDownList;
        _screens.SelectedIndexChanged += (_, _) => OnScreenChanged();
        Controls.Add(_screens);
        y += 34;

        _profile.SetBounds(14, y, w, 22);
        _profile.Font = new Font("Consolas", 10F);
        Controls.Add(_profile);
        y += 28;

        _watch.SetBounds(14, y, 280, 22);
        _watch.Text = "Theo dõi (chỉ đọc, không bấm)";
        _watch.Checked = true;
        Controls.Add(_watch);
        y += 28;

        _status.SetBounds(14, y, 360, 26);
        _status.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        _status.Text = "Đang dừng";
        Controls.Add(_status);

        _btnToggle.SetBounds(390, y, 288, 30);
        _btnToggle.Text = $"Bật  ({_jobKey})";
        _btnToggle.Click += (_, _) => Toggle();
        Controls.Add(_btnToggle);
        y += 38;

        var box = new GroupBox { Text = "Đọc HUD", Location = new Point(12, y), Size = new Size(w, 122) };
        Controls.Add(box);
        _hud.SetBounds(16, 24, 760, 20);
        _hud.Font = new Font("Consolas", 10F);
        _hud.Text = "HUD : --";
        box.Controls.Add(_hud);
        _fill.SetBounds(16, 46, 760, 20);
        _fill.Font = new Font("Consolas", 10F);
        _fill.Text = "thanh : --";
        box.Controls.Add(_fill);
        _fish.SetBounds(16, 68, 370, 20);
        _fish.Font = new Font("Consolas", 10F);
        _fish.Text = "cá cắn : --";
        box.Controls.Add(_fish);
        _reject.SetBounds(400, 68, 380, 20);
        _reject.Font = new Font("Consolas", 10F);
        _reject.Text = "chê mồi : --";
        box.Controls.Add(_reject);
        _keep.SetBounds(16, 90, 760, 20);
        _keep.Font = new Font("Consolas", 10F);
        _keep.Text = "cất vào : --";
        box.Controls.Add(_keep);
        y += 134;

        _btnBar.SetBounds(12, y, 130, 32);
        _btnBar.Text = "Khoanh thanh";
        _btnBar.Click += (_, _) => Pick(FishingSlot.Bar);
        Controls.Add(_btnBar);

        _btnFish.SetBounds(150, y, 120, 32);
        _btnFish.Text = "Khoanh cá";
        _btnFish.Click += (_, _) => Pick(FishingSlot.Fish);
        Controls.Add(_btnFish);

        _btnReject.SetBounds(278, y, 150, 32);
        _btnReject.Text = "Khoanh thông báo";
        _btnReject.Click += (_, _) => Pick(FishingSlot.Reject);
        Controls.Add(_btnReject);

        _btnKeep.SetBounds(436, y, 160, 32);
        _btnKeep.Text = "Khoanh CẤT VÀO";
        _btnKeep.Click += (_, _) => Pick(FishingSlot.Keep);
        Controls.Add(_btnKeep);

        _btnKeepBand.SetBounds(604, y, 190, 32);
        _btnKeepBand.Text = "Khoanh vùng quét nút";
        _btnKeepBand.Click += (_, _) => Pick(FishingSlot.KeepBand);
        Controls.Add(_btnKeepBand);
        y += 44;

        AddThumb(_thumbBar, 12, y, "Thanh");
        AddThumb(_thumbFish, 172, y, "Cá");
        AddThumb(_thumbReject, 332, y, "Thông báo");
        AddThumb(_thumbKeep, 492, y, "CẤT VÀO");
        AddThumb(_thumbKeepBand, 652, y, "Vùng quét");
        y += 130;

        var dump = new GroupBox
        {
            Text = "Đổ cá vào cốp xe",
            Location = new Point(12, y),
            Size = new Size(w, 104)
        };
        Controls.Add(dump);

        _dumpEnabled.SetBounds(16, 24, 330, 22);
        _dumpEnabled.Text = "Tự đổ cá vào cốp khi ba lô gần đầy";
        _dumpEnabled.CheckedChanged += (_, _) => OnDumpEnabledChanged();
        dump.Controls.Add(_dumpEnabled);

        dump.Controls.Add(new Label { Text = "Kiểm tra KG mỗi", Location = new Point(360, 26), AutoSize = true });
        _everyN.SetBounds(468, 22, 60, 24);
        _everyN.Minimum = 1;
        _everyN.Maximum = 50;
        _everyN.Value = Math.Clamp(_cfg.WeightCheckEveryCatches, 1, 50);
        _everyN.ValueChanged += (_, _) => OnEveryNChanged();
        dump.Controls.Add(_everyN);
        dump.Controls.Add(new Label { Text = "con cá", Location = new Point(534, 26), AutoSize = true });

        _dumpStatus.SetBounds(16, 52, 760, 18);
        _dumpStatus.Font = new Font("Consolas", 9F);
        dump.Controls.Add(_dumpStatus);

        _btnTrunkSetup.SetBounds(16, 72, 190, 26);
        _btnTrunkSetup.Text = "Cấu hình đổ cốp…";
        _btnTrunkSetup.Click += (_, _) => OpenTrunkSetup();
        dump.Controls.Add(_btnTrunkSetup);

        _btnAltProbe.SetBounds(214, 72, 210, 26);
        _btnAltProbe.Text = "Test giữ Alt (menu xe)";
        _btnAltProbe.Click += (_, _) => DoAltProbe();
        dump.Controls.Add(_btnAltProbe);
        y += 116;

        _log.SetBounds(12, y, w, 760 - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_log);
    }

    private void AddThumb(PictureBox box, int x, int y, string caption)
    {
        Controls.Add(new Label { Text = caption, Location = new Point(x, y), AutoSize = true });
        box.SetBounds(x, y + 18, 148, 96);
        box.BorderStyle = BorderStyle.FixedSingle;
        box.SizeMode = PictureBoxSizeMode.Zoom;
        box.BackColor = Color.FromArgb(245, 245, 245);
        Controls.Add(box);
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
            int i = _screens.Items.Add(new ScreenItem(s));
            if (s.DeviceName == prefer.DeviceName) select = i;
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

        _syncingDumpUi = true;
        try
        {
            _dumpEnabled.Checked = p?.TrunkDumpEnabled == true;
            _everyN.Value = Math.Clamp(_cfg.WeightCheckEveryCatches, (int)_everyN.Minimum, (int)_everyN.Maximum);
        }
        finally { _syncingDumpUi = false; }

        if (p is null)
        {
            _dumpStatus.Text = "chưa có hồ sơ cho màn hình này";
            _dumpStatus.ForeColor = Color.DimGray;
            return;
        }

        string gaps = p.DescribeTrunkGaps();
        _dumpStatus.Text = gaps;
        _dumpStatus.ForeColor = gaps.StartsWith("đủ") ? Color.DarkGreen : Color.DimGray;
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
            try { _dumpEnabled.Checked = false; }
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
        _cfg.WeightCheckEveryCatches = (int)_everyN.Value;
        try { _cfg.Save(); } catch { }
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
        if (screen is null) { _profile.Text = "không thấy màn hình"; return; }
        var p = _cfg.TryGet(screen);
        _profile.Text = p is null
            ? $"{screen.Bounds.Width}x{screen.Bounds.Height} — chưa khoanh"
            : p.DescribeGaps();
        _profile.ForeColor = p is { Bar.IsSet: true, Fish.IsSet: true, Reject.IsSet: true, Keep.IsSet: true }
            ? Color.DarkGreen : Color.DimGray;
    }

    private enum FishingSlot { Bar, Fish, Reject, Keep, KeepBand }

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

        _bot = new FishingBot(_cfg, screen, profile);
        _bot.Log += s => Post(() => Append(s));
        _bot.SnapshotReady += s => Post(() => ShowSnapshot(s));
        _bot.Stopped += (r, msg) => Post(() =>
        {
            _status.Text = "Đã dừng — " + FishingBot.TenLyDo(r);
            _status.ForeColor = r == FishingStopReason.UserStopped ? Color.DarkGreen : Color.Firebrick;
            SetRunningUi(false);
            if (!string.IsNullOrEmpty(msg) && r != FishingStopReason.UserStopped)
                Append(msg);
        });

        _status.Text = "Đang câu";
        _status.ForeColor = Color.DarkBlue;
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
            _hud.Text = "lỗi đọc: " + ex.Message;
        }
    }

    private void ShowSnapshot(FishingSnapshot s)
    {
        if (!s.BarConfigured)
        {
            _hud.Text = "HUD : chưa khoanh thanh";
            _hud.ForeColor = Color.DimGray;
            _fill.Text = "thanh : --";
            _fill.ForeColor = Color.DimGray;
        }
        else
        {
            _hud.Text = "HUD : " + (s.UiOpen ? "MỞ" : "đóng");
            _hud.ForeColor = s.UiOpen ? Color.DarkGreen : Color.DimGray;
            _fill.Text = s.BlueFill01 < 0
                ? "thanh : không đọc được"
                : $"thanh : {s.BlueFill01 * 100,5:0.0}%";
            _fill.ForeColor = s.UiOpen ? Color.DarkBlue : Color.DimGray;
        }

        if (!s.FishConfigured)
        {
            _fish.Text = "cá cắn : chưa khoanh / chưa có mẫu";
            _fish.ForeColor = Color.DimGray;
        }
        else
        {
            _fish.Text = $"cá cắn : {(s.FishBite ? "CÓ" : "không")}   ncc={s.FishScore:F3}";
            _fish.ForeColor = s.FishBite ? Color.DarkGreen : Color.DimGray;
        }

        if (!s.RejectConfigured)
        {
            _reject.Text = "chê mồi : chưa khoanh / chưa có mẫu";
            _reject.ForeColor = Color.DimGray;
        }
        else
        {
            _reject.Text = $"chê mồi : {(s.FailNotice ? "CÓ" : "không")}   ncc={s.RejectScore:F3}";
            _reject.ForeColor = s.FailNotice ? Color.Firebrick : Color.DimGray;
        }

        if (!s.KeepConfigured)
        {
            _keep.Text = "cất vào : chưa khoanh / chưa có mẫu";
            _keep.ForeColor = Color.DimGray;
        }
        else
        {
            _keep.Text = s.KeepVisible
                ? $"cất vào : CÓ @ {s.KeepClick.X},{s.KeepClick.Y}  {s.KeepRect.Width}×{s.KeepRect.Height}" +
                  $"  dens={s.KeepDensity:F2}  ncc={s.KeepScore:F3}"
                : "cất vào : không";
            _keep.ForeColor = s.KeepVisible ? Color.DarkGreen : Color.DimGray;
        }
    }

    private void ClearLive()
    {
        _hud.Text = "HUD : --";
        _hud.ForeColor = Color.DimGray;
        _fill.Text = "thanh : --";
        _fill.ForeColor = Color.DimGray;
        _fish.Text = "cá cắn : --";
        _fish.ForeColor = Color.DimGray;
        _reject.Text = "chê mồi : --";
        _reject.ForeColor = Color.DimGray;
        _keep.Text = "cất vào : --";
        _keep.ForeColor = Color.DimGray;
    }

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "bot-log.txt");
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private void Append(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        if (_log.Lines.Length > 400)
            _log.Lines = _log.Lines.Skip(150).ToArray();
        _log.AppendText($"[{stamp}] {line}{Environment.NewLine}");
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [câu] {line}{Environment.NewLine}", LogEncoding);
        }
        catch { }
    }
}
