using System.Drawing.Imaging;
using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Tab Thợ điện. Theo khuôn <see cref="WoodPanel"/> — khung thô dùng Dock, bên trong đặt tuyệt đối
/// — thêm bộ chọn màn hình như <see cref="FishingPanel"/> và một chỗ chụp ảnh tĩnh để chạy
/// <c>--verify-wire</c> / <c>--verify-board</c>.
///
/// Khác các job cũ ở một điểm dễ gây bất ngờ: job này KHÔNG cần khoanh vùng tay. Cả ROI bảng lẫn
/// vùng quét panel dây đều suy được từ độ phân giải (xem <see cref="ElectricProfile"/>), nên bật
/// là chạy. Ảnh tĩnh chỉ dùng để KIỂM TRA ngoài game, không phải để hiệu chuẩn.
/// </summary>
internal sealed class ElectricPanel : UserControl
{
    private readonly ElectricConfig _cfg = ElectricConfig.Load();
    private Screen _screen;
    private ElectricProfile _profile;
    private ElectricBot _bot;

    private readonly Label _status = new();
    private readonly Label _calib = new();
    private readonly Label _note = new();
    private readonly DarkPick _screens = new();
    private readonly DarkPick _modes = new();
    private readonly DarkPick _shots = new();
    private readonly DarkButton _btnShot = new();
    private readonly DarkButton _btnToggle = new();
    private readonly DarkCheck _autoWalk = new();
    private readonly DarkCheck _autoLoop = new();
    private readonly DarkButton _btnPrompt = new();
    private readonly Label _navState = new();
    private readonly LogView _log = new();

    private string _jobKey = HotkeyText.Job();
    private int _rounds;

    public bool IsRunning => _bot is { Running: true };

    public event Action<bool> RunningChanged;

    public ElectricPanel()
    {
        Font = Theme.Body;
        Dock = DockStyle.Fill;
        BackColor = Theme.Ground;

        _screen = FishingConfig.Prefer2kOrPrimary();
        _profile = _cfg.GetOrCreate(_screen);
        _cfg.Save();

        BuildUi();

        Append($"màn hình: {_profile.Key} ({_screen.DeviceName})");
        Append(_profile.Describe());
        Append($"{_jobKey} = bật/tắt.");
        Append("Job này không cần khoanh vùng: vùng đọc suy từ độ phân giải. " +
               "Đứng vào panel/bảng rồi bật.");

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

        _status.SetBounds(Theme.Px(16), Theme.Px(16), Theme.Px(420), Theme.Px(26));
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
            Height = Theme.Px(352),
            BackColor = Theme.Ground
        };

        int w = Theme.Px(620);

        var box = new DarkGroup
        {
            Title = "Màn hình & minigame",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(8), w, Theme.Px(112))
        };
        host.Controls.Add(box);

        Lab(box, "Màn hình:", Theme.Px(12), Theme.Px(26), Theme.Px(74));
        _screens.SetBounds(Theme.Px(92), Theme.Px(22), w - Theme.Px(108), Theme.Px(24));
        _screens.SelectedIndexChanged += OnScreenChanged;
        box.Controls.Add(_screens);

        Lab(box, "Giải:", Theme.Px(12), Theme.Px(58), Theme.Px(74));
        _modes.SetBounds(Theme.Px(92), Theme.Px(54), Theme.Px(240), Theme.Px(24));
        foreach (var m in new[] { ElectricMode.Both, ElectricMode.Wire, ElectricMode.Board })
            _modes.Items.Add(new ModeItem(m));
        _modes.SelectedIndex = Array.FindIndex(
            new[] { ElectricMode.Both, ElectricMode.Wire, ElectricMode.Board }, x => x == _cfg.Mode);
        _modes.SelectedIndexChanged += OnModeChanged;
        box.Controls.Add(_modes);

        _calib.AutoSize = false;
        _calib.Font = Theme.DataSm;
        _calib.BackColor = Theme.Surface;
        _calib.SetBounds(Theme.Px(12), Theme.Px(84), w - Theme.Px(24), Theme.Px(18));
        box.Controls.Add(_calib);

        var shotBox = new DarkGroup
        {
            Title = "Ảnh tĩnh để kiểm tra ngoài game",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(128), w, Theme.Px(76))
        };
        host.Controls.Add(shotBox);

        _shots.SetBounds(Theme.Px(12), Theme.Px(24), Theme.Px(240), Theme.Px(24));
        foreach (var s in ShotItem.All) _shots.Items.Add(s);
        _shots.SelectedIndex = 0;
        shotBox.Controls.Add(_shots);

        _btnShot.Text = "Chụp ảnh tĩnh…";
        _btnShot.SetBounds(Theme.Px(262), Theme.Px(23), Theme.Px(150), Theme.Px(26));
        _btnShot.Click += (_, _) => CaptureShot();
        shotBox.Controls.Add(_btnShot);

        Lab(shotBox, "Chụp xong chạy:  --verify-wire  /  --verify-board  /  --verify-nav",
            Theme.Px(12), Theme.Px(54), w - Theme.Px(24));

        var navBox = new DarkGroup
        {
            Title = "Tự đi tới điểm làm việc",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(212), w, Theme.Px(88))
        };
        host.Controls.Add(navBox);

        _autoWalk.Text = "Tự tìm điểm vàng, đi tới và bấm E";
        _autoWalk.SetBounds(Theme.Px(12), Theme.Px(24), Theme.Px(300), Theme.Px(22));
        _autoWalk.SetCheckedQuiet(_cfg.AutoWalk);
        _autoWalk.CheckedChanged += OnAutoWalkChanged;
        navBox.Controls.Add(_autoWalk);

        _autoLoop.Text = "Chạy liên tục (giải xong tìm điểm tiếp)";
        _autoLoop.SetBounds(Theme.Px(12), Theme.Px(50), Theme.Px(300), Theme.Px(22));
        _autoLoop.SetCheckedQuiet(_cfg.AutoLoop);
        _autoLoop.CheckedChanged += OnAutoLoopChanged;
        navBox.Controls.Add(_autoLoop);

        _btnPrompt.Text = "Khoanh mẫu TƯƠNG TÁC…";
        _btnPrompt.SetBounds(Theme.Px(330), Theme.Px(23), Theme.Px(212), Theme.Px(26));
        _btnPrompt.Click += (_, _) => CropPromptTemplate();
        navBox.Controls.Add(_btnPrompt);

        _navState.AutoSize = false;
        _navState.Font = Theme.DataSm;
        _navState.BackColor = Theme.Surface;
        _navState.SetBounds(Theme.Px(330), Theme.Px(54), w - Theme.Px(342), Theme.Px(18));
        navBox.Controls.Add(_navState);

        var help = new DarkGroup
        {
            Title = "Cách dùng",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(308), w, Theme.Px(40))
        };
        host.Controls.Add(help);

        _note.AutoSize = false;
        _note.Font = Theme.Body;
        _note.BackColor = Theme.Surface;
        _note.ForeColor = Theme.Dim;
        _note.SetBounds(Theme.Px(12), Theme.Px(16), w - Theme.Px(24), Theme.Px(18));
        help.Controls.Add(_note);

        FillScreens();
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

    /// <summary>Gắn nhãn cả 2K lẫn FHD — job này hỗ trợ cả hai, nên nói rõ cái nào là cái nào.</summary>
    private sealed class ScreenItem
    {
        public Screen Screen { get; }
        public ScreenItem(Screen s) => Screen = s;

        public override string ToString()
        {
            var b = Screen.Bounds;
            string tag = (b.Width, b.Height) switch
            {
                (2560, 1440) => "  (2K)",
                (1920, 1080) => "  (FHD)",
                _ => ""
            };
            return $"{Screen.DeviceName}  {b.Width}×{b.Height}{tag}";
        }
    }

    private sealed class ModeItem
    {
        public ElectricMode Mode { get; }
        public ModeItem(ElectricMode m) => Mode = m;
        public override string ToString() => ElectricBot.TenCheDo(Mode);
    }

    private sealed class ShotItem
    {
        public static readonly ShotItem[] All =
        {
            new("wire3", "Panel đi dây — 3 dây"),
            new("wire5", "Panel đi dây — 5 dây"),
            new("board", "Bảng nước/điện"),
            new("nav-far", "Đi đường — xa, chỉ thấy chấm minimap"),
            new("nav-marker", "Đi đường — thấy mốc vàng dưới đất"),
            new("nav-prompt", "Đi đường — đang hiện nút E TƯƠNG TÁC"),
            new("nav-pair-a", "Đi đường — cặp xoay camera, ảnh A"),
            new("nav-pair-b", "Đi đường — cặp xoay camera, ảnh B")
        };

        public string Name { get; }
        private readonly string _label;

        private ShotItem(string name, string label) { Name = name; _label = label; }

        public override string ToString() => _label;
    }

    private Screen SelectedScreen => (_screens.SelectedItem as ScreenItem)?.Screen;

    private void FillScreens()
    {
        _screens.Items.Clear();
        int select = 0;
        foreach (var s in Screen.AllScreens)
        {
            _screens.Items.Add(new ScreenItem(s));
            if (s.DeviceName == _screen.DeviceName) select = _screens.Items.Count - 1;
        }
        if (_screens.Items.Count > 0) _screens.SelectedIndex = select;
    }

    private void OnScreenChanged()
    {
        var s = SelectedScreen;
        if (s is null || IsRunning) return;

        _screen = s;
        _profile = _cfg.GetOrCreate(_screen);
        _cfg.Save();
        RefreshCalib();
        Append($"đổi màn hình: {_profile.Key} ({_screen.DeviceName}) — {_profile.Describe()}");
    }

    private void OnModeChanged()
    {
        if (_modes.SelectedItem is not ModeItem m) return;

        _cfg.Mode = m.Mode;
        _cfg.Save();
        RefreshNote();
        Append("chế độ: " + ElectricBot.TenCheDo(m.Mode));
    }

    private void RefreshCalib()
    {
        _calib.Text = _profile.Describe();
        _calib.ForeColor = Theme.GoodText;
        RefreshNavState();
    }

    private void RefreshNavState()
    {
        bool ready = _profile.PromptReady && File.Exists(ElectricConfig.PromptTemplatePath(_profile.Key));
        _navState.Text = ready
            ? $"mẫu TƯƠNG TÁC: chữ cao {_profile.PromptTextH}px, khe {_profile.PromptGapSplit}px"
            : "chưa có mẫu TƯƠNG TÁC — chụp “nav-prompt” rồi khoanh";
        _navState.ForeColor = ready ? Theme.GoodText : Theme.Dim;
    }

    /// <summary>
    /// Bật tự đi mà chưa có mẫu chữ thì gỡ tick ngay: bot không có cách nào biết lúc nào tới nơi,
    /// và để nó chạy là để nó đi lạc rồi mới báo.
    /// </summary>
    private void OnAutoWalkChanged()
    {
        if (_autoWalk.Checked &&
            !(_profile.PromptReady && File.Exists(ElectricConfig.PromptTemplatePath(_profile.Key))))
        {
            _autoWalk.SetCheckedQuiet(false);
            Append("chưa có mẫu chữ TƯƠNG TÁC — chụp ảnh “nav-prompt” rồi bấm “Khoanh mẫu TƯƠNG TÁC…”");
            return;
        }

        _cfg.AutoWalk = _autoWalk.Checked;
        _cfg.Save();
        Append(_cfg.AutoWalk
            ? "tự đi tới điểm làm việc: BẬT"
            : "tự đi tới điểm làm việc: TẮT (đứng sẵn ở bảng rồi bật job)");
        RefreshNote();
    }

    private void OnAutoLoopChanged()
    {
        _cfg.AutoLoop = _autoLoop.Checked;
        _cfg.Save();
        Append(_cfg.AutoLoop
            ? "chạy liên tục: BẬT"
            : "chạy liên tục: TẮT — giải xong một lượt là dừng để đọc log");
    }

    /// <summary>
    /// Khoanh mẫu chữ "TƯƠNG TÁC" trên ảnh "nav-prompt", đi đúng đường
    /// <see cref="WoodSetupForm"/> đi cho job thợ mộc.
    /// </summary>
    private void CropPromptTemplate()
    {
        if (IsRunning) { Append("đang chạy — tắt trước khi khoanh mẫu"); return; }

        string shotPath = ElectricConfig.ShotPath(_profile.Key, "nav-prompt");
        using var still = StillPicker.Load(shotPath);
        if (still is null)
        {
            Append("chưa có ảnh “nav-prompt” — chọn nó ở ô trên rồi bấm “Chụp ảnh tĩnh…”");
            return;
        }
        if (still.Width != _profile.Width || still.Height != _profile.Height)
        {
            Append($"ảnh {still.Width}×{still.Height} lệch màn hình {_profile.Width}×{_profile.Height} — chụp lại");
            return;
        }

        var res = StillCropForm.Run(this, still, "Mẫu chữ TƯƠNG TÁC",
            "Khoanh trùm CẢ ô phím [E] LẪN chữ TƯƠNG TÁC. Khoanh rộng tay một chút cũng được — " +
            "phần chữ được tách ra tự động, ô phím không vào mẫu.",
            Rectangle.Empty);
        if (res is null) { Append("đã huỷ khoanh mẫu"); return; }

        try
        {
            ApplyPromptCrop(still, res.Rect);
            _cfg.Save();
        }
        catch (Exception ex) { Append("khoanh mẫu lỗi: " + ex.Message); }

        RefreshNavState();
    }

    private void ApplyPromptCrop(Bitmap still, Rectangle rect)
    {
        var src = Rectangle.Intersect(rect, new Rectangle(0, 0, still.Width, still.Height));
        if (src.Width < 20 || src.Height < 10)
            throw new InvalidOperationException("vùng quá nhỏ — khoanh trùm cả ô phím lẫn chữ");

        using var crop = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(still, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);

        var parts = PromptLocator.ExtractText(crop, _cfg.Nav.PromptTuning(_profile), out string problem);
        if (parts is null) throw new InvalidOperationException(problem);

        // Chi phan CHU vao mau: trong o phim la chu E hay so dem nguoc, quanh no la vong tien trinh
        // — de thu dang dong vao mau la tu dim diem khop cua chinh minh.
        var tpl = GrayTemplate.FromBitmapCrop(crop, parts.Text);
        if (tpl.IsFlat)
            throw new InvalidOperationException("ô chữ phẳng tuyệt đối — khoanh trúng chỗ trống");

        tpl.Save(ElectricConfig.PromptTemplatePath(_profile.Key));

        _profile.PromptTextH = parts.Text.Height;
        _profile.PromptGapSplit = parts.GapSplit;
        Append($"mẫu TƯƠNG TÁC: {parts.Note} → tuong-tac.png");
    }

    private void RefreshNote() =>
        _note.Text = $"{_jobKey} bật/tắt ngay trong game. Chế độ: {ElectricBot.TenCheDo(_cfg.Mode)}. " +
                     (_cfg.AutoWalk ? "Tự đi tới điểm." : "Đứng sẵn ở bảng rồi bật.");

    // ---------------------------------------------------------------- anh tinh

    private void CaptureShot()
    {
        if (IsRunning) { Append("đang chạy — tắt trước khi chụp ảnh"); return; }
        if (_shots.SelectedItem is not ShotItem shot) return;

        string instruction = shot.Name switch
        {
            "board" => "Mở bảng nước/điện trong game, để nguyên màn đó.",
            "nav-far" => "Đứng XA điểm làm việc: minimap có chấm vàng, nhưng KHÔNG thấy mốc vàng " +
                         "dưới đất trong khung hình.",
            "nav-marker" => "Đứng chỗ NHÌN THẤY mốc vàng dưới đất, nhưng CHƯA hiện nút E.",
            "nav-prompt" => "Đứng vào mốc cho HIỆN nút [E] TƯƠNG TÁC. Ảnh này vừa để kiểm, vừa để " +
                            "cắt mẫu chữ.",
            "nav-pair-a" => "ĐỨNG YÊN, đừng đi. Chụp ảnh A. Xong xoay camera ~90° rồi chụp ảnh B.",
            "nav-pair-b" => "Vẫn ĐỨNG YÊN ở đúng chỗ cũ, camera đã xoay ~90° so với ảnh A.",
            _ => "Mở panel đi dây trong game, để nguyên màn đó."
        };

        var bmp = StillPicker.CaptureWithCountdown(
            FindForm(), _screen, instruction, _cfg.ShotCountdownSec, _cfg.WindowMatch,
            out string problem);

        if (bmp is null)
        {
            Append("không chụp được: " + (problem ?? "không rõ"));
            return;
        }

        try
        {
            string path = ElectricConfig.ShotPath(_profile.Key, shot.Name);
            StillPicker.Save(bmp, path);
            Append($"đã lưu {path}");
            Append(shot.Name switch
            {
                "board" => "chạy “--verify-board” để kiểm tuyến đi trên ảnh này.",
                "nav-prompt" => "bấm “Khoanh mẫu TƯƠNG TÁC…”, rồi chạy “--verify-nav”.",
                _ when shot.Name.StartsWith("nav-") => "chạy “--verify-nav” để kiểm trên ảnh này.",
                _ => "chạy “--verify-wire” để kiểm nhận dạng slot trên ảnh này."
            });
        }
        catch (Exception ex) { Append("không lưu được ảnh: " + ex.Message); }
        finally { bmp.Dispose(); }
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
        HeldKeys.ReleaseAll();
    }

    /// <summary>Dọn hết khi đóng app.</summary>
    public void Shutdown()
    {
        _bot?.StopAndWait();
        HeldKeys.ReleaseAll();
    }

    private void StartBot()
    {
        if (_bot is { Running: true }) return;

        // Doi man hinh hay do phan giai giua phien: doc lai profile truoc khi chay, khong dung ban
        // da cache tu luc dung UI.
        _screen = SelectedScreen ?? FishingConfig.Prefer2kOrPrimary();
        _profile = _cfg.GetOrCreate(_screen);
        _cfg.Save();
        RefreshCalib();

        _rounds = 0;
        _bot = new ElectricBot(_cfg, _screen, _profile);
        _bot.Log += s => Post(() => Append(s));
        _bot.RoundsChanged += n => Post(() =>
        {
            _rounds = n;
            _status.Text = $"Đang giải — {_rounds} lượt";
        });
        _bot.Stopped += msg => Post(() =>
        {
            _status.Text = $"Đã dừng   ({_rounds} lượt)";
            _status.ForeColor = Theme.Head;
            if (!string.IsNullOrWhiteSpace(msg) && msg != "người dùng bấm dừng")
                Append("dừng: " + msg);
            SetRunningUi(false);
        });

        _status.Text = "Đang chờ minigame";
        _status.ForeColor = Theme.Accent;
        SetRunningUi(true);
        Append($"--- bắt đầu ({ElectricBot.TenCheDo(_cfg.Mode)}) ---");
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
        _btnShot.Enabled = !running;
        _btnPrompt.Enabled = !running;
        _screens.Enabled = !running;
        _modes.Enabled = !running;
        _autoWalk.Enabled = !running;
        _autoLoop.Enabled = !running;
        RunningChanged?.Invoke(running);
    }

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "bot-log.txt");
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Ghi ra CẢ màn hình lẫn file, cùng khuôn <see cref="MinerPanel"/>.
    ///
    /// Job này chạy trong game và người dùng không thể vừa chơi vừa đọc log trên màn — mà khi nó
    /// giữ không bấm gì thì lý do nằm đúng trong log đó. Chỉ hiện trên màn nghĩa là mất bằng chứng
    /// ngay lúc cần nhất.
    /// </summary>
    private void Append(string line)
    {
        _log.Append(line);

        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [điện] {line}{Environment.NewLine}", LogEncoding);
        }
        catch { }
    }
}
