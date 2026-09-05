using System.Drawing.Imaging;

namespace GtaMiniGameBot;

/// <summary>
/// Tab Thợ điện. Theo khuôn <see cref="WoodPanel"/> — khung thô dùng Dock, bên trong đặt tuyệt đối
/// — thêm bộ chọn màn hình như <see cref="FishingPanel"/> và một chỗ chụp ảnh tĩnh để chạy
/// <c>--verify-wire</c> / <c>--verify-board</c>.
///
/// Tự đi bắt buộc khoanh <c>[E] TƯƠNG TÁC</c> (ảnh tĩnh rồi crop). ROI bảng / panel dây vẫn suy
/// từ độ phân giải.
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
    private readonly DarkButton _btnBoardRoi = new();
    private readonly DarkButton _btnBoardDefault = new();
    private readonly DarkButton _btnPrompt = new();
    private readonly DarkButton _btnEatCenters = new();
    private readonly DarkButton _btnTestFood = new();
    private readonly DarkButton _btnTestWater = new();
    private readonly DarkPick _foodSlot = new();
    private readonly DarkPick _waterSlot = new();
    private readonly Label _eatStatus = new();
    private readonly DarkButton _btnToggle = new();
    private readonly DarkCheck _autoWalk = new();
    private readonly DarkCheck _autoLoop = new();
    private readonly DarkCheck _autoEat = new();
    private readonly LogView _log = new();

    private string _jobKey = HotkeyText.Job();
    private int _rounds;
    private bool _syncingSlots;

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
        Append("Tự đi: khoanh [E] TƯƠNG TÁC lúc prompt đang hiện. " +
               "Chỉ giải minigame thì đứng sẵn ở bảng rồi bật.");

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
            Height = Theme.Px(548),
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
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(128), w, Theme.Px(108))
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

        _btnBoardRoi.Text = "Khoanh vùng bảng…";
        _btnBoardRoi.SetBounds(Theme.Px(12), Theme.Px(54), Theme.Px(196), Theme.Px(26));
        _btnBoardRoi.Click += (_, _) => CalibrateBoardRegions();
        shotBox.Controls.Add(_btnBoardRoi);

        _btnBoardDefault.Text = "Dùng vùng mặc định";
        _btnBoardDefault.SetBounds(Theme.Px(218), Theme.Px(54), Theme.Px(194), Theme.Px(26));
        _btnBoardDefault.Click += (_, _) => ResetBoardRegions();
        shotBox.Controls.Add(_btnBoardDefault);

        Lab(shotBox, "Khoanh bảng chỉ là tùy chọn; bỏ trống vẫn suy đúng theo độ phân giải.",
            Theme.Px(12), Theme.Px(86), w - Theme.Px(24));

        var navBox = new DarkGroup
        {
            Title = "Tự đi tới điểm làm việc",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(244), w, Theme.Px(100))
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

        _btnPrompt.Text = "Khoanh [E] TƯƠNG TÁC…";
        _btnPrompt.SetBounds(Theme.Px(330), Theme.Px(22), Theme.Px(260), Theme.Px(26));
        _btnPrompt.Click += (_, _) => CalibratePrompt();
        navBox.Controls.Add(_btnPrompt);

        Lab(navBox, "Chụp lúc prompt hiện, khoanh trùm ô E lẫn chữ. Ô đó là vùng quét.",
            Theme.Px(330), Theme.Px(54), w - Theme.Px(342));
        Lab(navBox, "Lượt đầu tắt “Chạy liên tục” để đọc log.",
            Theme.Px(12), Theme.Px(76), Theme.Px(300));

        var eatBox = new DarkGroup
        {
            Title = "Tự ăn bánh / uống nước",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(352), w, Theme.Px(136))
        };
        host.Controls.Add(eatBox);

        _autoEat.Text = "Tự ăn / uống khi dưới 50%";
        _autoEat.SetBounds(Theme.Px(12), Theme.Px(22), Theme.Px(280), Theme.Px(22));
        _autoEat.SetCheckedQuiet(_cfg.Survival.Enabled);
        _autoEat.CheckedChanged += OnAutoEatChanged;
        eatBox.Controls.Add(_autoEat);

        Lab(eatBox, "Ô bánh:", Theme.Px(12), Theme.Px(52), Theme.Px(56));
        _foodSlot.SetBounds(Theme.Px(68), Theme.Px(48), Theme.Px(56), Theme.Px(24));
        FillSlots(_foodSlot, _cfg.Survival.PrimarySlot(true));
        _foodSlot.SelectedIndexChanged += OnFoodSlotChanged;
        eatBox.Controls.Add(_foodSlot);

        Lab(eatBox, "Ô nước:", Theme.Px(136), Theme.Px(52), Theme.Px(56));
        _waterSlot.SetBounds(Theme.Px(194), Theme.Px(48), Theme.Px(56), Theme.Px(24));
        FillSlots(_waterSlot, _cfg.Survival.PrimarySlot(false));
        _waterSlot.SelectedIndexChanged += OnWaterSlotChanged;
        eatBox.Controls.Add(_waterSlot);

        _btnEatCenters.Text = "Hiệu chuẩn HUD…";
        _btnEatCenters.SetBounds(Theme.Px(268), Theme.Px(47), Theme.Px(150), Theme.Px(26));
        _btnEatCenters.Click += (_, _) => CalibrateSurvivalCenters();
        eatBox.Controls.Add(_btnEatCenters);

        _btnTestFood.Text = "Test bánh";
        _btnTestFood.SetBounds(Theme.Px(426), Theme.Px(47), Theme.Px(84), Theme.Px(26));
        _btnTestFood.Click += (_, _) => TestSurvivalSlot(food: true);
        eatBox.Controls.Add(_btnTestFood);

        _btnTestWater.Text = "Test nước";
        _btnTestWater.SetBounds(Theme.Px(516), Theme.Px(47), Theme.Px(84), Theme.Px(26));
        _btnTestWater.Click += (_, _) => TestSurvivalSlot(food: false);
        eatBox.Controls.Add(_btnTestWater);

        _eatStatus.AutoSize = false;
        _eatStatus.Font = Theme.DataSm;
        _eatStatus.BackColor = Theme.Surface;
        _eatStatus.SetBounds(Theme.Px(12), Theme.Px(82), w - Theme.Px(24), Theme.Px(44));
        eatBox.Controls.Add(_eatStatus);

        var help = new DarkGroup
        {
            Title = "Cách dùng",
            Bounds = new Rectangle(Theme.Px(16), Theme.Px(496), w, Theme.Px(40))
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
        RefreshEatStatus();
        return host;
    }

    private void FillSlots(DarkPick pick, char selected)
    {
        _syncingSlots = true;
        pick.Items.Clear();
        int idx = 0;
        for (char c = '1'; c <= '9'; c++)
        {
            pick.Items.Add(c.ToString());
            if (c == selected) idx = pick.Items.Count - 1;
        }
        pick.SelectedIndex = idx;
        _syncingSlots = false;
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
            new("hud-no", "Đồng hồ đói/khát — lúc còn no đủ"),
            new("hud-doi", "Đồng hồ đói/khát — lúc đã dưới 50%")
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
        RefreshEatStatus();
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
        _calib.ForeColor = _cfg.AutoWalk && !_profile.IsPromptCalibrated ? Theme.WarnText : Theme.GoodText;
    }

    /// <summary>Tự đi không cần hiệu chuẩn gì: mọi vùng đọc và bộ dò prompt đều suy từ độ phân giải.</summary>
    private void OnAutoWalkChanged()
    {
        _cfg.AutoWalk = _autoWalk.Checked;
        _cfg.Save();
        Append(_cfg.AutoWalk
            ? (_profile.IsPromptCalibrated
                ? "tự đi tới điểm làm việc: BẬT"
                : "tự đi: BẬT — chưa khoanh [E] TƯƠNG TÁC, bấm nút khoanh trước khi chạy")
            : "tự đi tới điểm làm việc: TẮT (đứng sẵn ở bảng rồi bật job)");
        RefreshCalib();
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

    private void OnAutoEatChanged()
    {
        _cfg.Survival.Enabled = _autoEat.Checked;
        _cfg.Save();
        RefreshEatStatus();

        if (!_cfg.Survival.Enabled)
        {
            Append("ăn uống: TẮT");
            return;
        }

        if (!_cfg.Survival.CanRun(_profile.SurvivalHud))
        {
            Append("ăn uống: BẬT nhưng CHƯA hiệu chuẩn — bot sẽ không chạy mù. " +
                   "Khoanh HUD, chụp LOW/HIGH, rồi Test bánh và Test nước.");
            return;
        }

        Append($"ăn uống: BẬT — bánh ô {_cfg.Survival.FoodSlots}, nước ô {_cfg.Survival.WaterSlots}");
        if (!_cfg.AutoWalk)
            Append("lưu ý: ăn uống nằm trong bộ tự đi, phải bật “Tự tìm điểm vàng…” mới có tác dụng");
    }

    private void OnFoodSlotChanged()
    {
        if (_syncingSlots) return;
        if (_foodSlot.SelectedItem is not string t || t.Length != 1) return;
        char slot = t[0];
        if (slot == _cfg.Survival.PrimarySlot(false))
        {
            Append("ô bánh trùng ô nước — chọn ô khác");
            FillSlots(_foodSlot, _cfg.Survival.PrimarySlot(true));
            return;
        }
        _cfg.Survival.SetPrimarySlot(true, slot);
        _profile.SurvivalHud.FoodSlotVerified = false;
        _cfg.Save();
        RefreshEatStatus();
        Append($"ô bánh: {slot} — cần Test bánh lại");
    }

    private void OnWaterSlotChanged()
    {
        if (_syncingSlots) return;
        if (_waterSlot.SelectedItem is not string t || t.Length != 1) return;
        char slot = t[0];
        if (slot == _cfg.Survival.PrimarySlot(true))
        {
            Append("ô nước trùng ô bánh — chọn ô khác");
            FillSlots(_waterSlot, _cfg.Survival.PrimarySlot(false));
            return;
        }
        _cfg.Survival.SetPrimarySlot(false, slot);
        _profile.SurvivalHud.WaterSlotVerified = false;
        _cfg.Save();
        RefreshEatStatus();
        Append($"ô nước: {slot} — cần Test nước lại");
    }

    private void RefreshEatStatus()
    {
        var hud = _profile.SurvivalHud;
        string hudNote = hud.IsHudReady
            ? $"HUD đã khoanh (bánh {hud.FoodCx:F0},{hud.FoodCy:F0} r {hud.FoodRmin:F0}–{hud.FoodRmax:F0})"
            : "chưa khoanh / chưa chụp LOW-HIGH";
        string keys = $"phím bánh {(hud.FoodSlotVerified ? "đã test" : "chưa test")}, " +
                      $"nước {(hud.WaterSlotVerified ? "đã test" : "chưa test")}";
        _eatStatus.Text = hud.IsReady
            ? $"Sẵn sàng: {hudNote}; {keys}."
            : $"Chưa sẵn sàng — {hudNote}; {keys}. Bật tự ăn lúc này chỉ hiện cảnh báo, không chạy mù.";
        _eatStatus.ForeColor = hud.IsReady ? Theme.GoodText : Theme.WarnText;
        _autoEat.Text = hud.IsReady
            ? "Tự ăn / uống khi dưới 50%"
            : "Tự ăn / uống khi dưới 50%  (chưa hiệu chuẩn)";
    }

    private void RefreshNote() =>
        _note.Text = $"{_jobKey} bật/tắt ngay trong game. Chế độ: {ElectricBot.TenCheDo(_cfg.Mode)}. " +
                     (_cfg.AutoWalk
                         ? (_profile.IsPromptCalibrated ? "Tự đi tới điểm." : "Tự đi — chưa khoanh [E] TƯƠNG TÁC.")
                         : "Đứng sẵn ở bảng rồi bật.");

    // ---------------------------------------------------------------- tâm bánh / nước

    private void CalibrateSurvivalCenters()
    {
        if (IsRunning) { Append("đang chạy — tắt trước khi hiệu chuẩn ăn uống"); return; }

        var bmp = StillPicker.CaptureWithCountdown(
            FindForm(), _screen,
            "HUD đang hiện (góc dưới trái có icon bánh và nước). Cùng góc nhìn này sẽ dùng cho LOW/HIGH.",
            _cfg.ShotCountdownSec, _cfg.WindowMatch, out string problem);

        if (bmp is null)
        {
            Append("không chụp được: " + (problem ?? "không rõ"));
            return;
        }

        try
        {
            StillPicker.Save(bmp, ElectricConfig.ShotPath(_profile.Key, "hud-calib"));
            if (!SurvivalWizardForm.Run(FindForm(), bmp, _screen, _cfg, _profile))
            {
                Append("đã huỷ hiệu chuẩn ăn uống");
                return;
            }
            _cfg.Save();
            RefreshCalib();
            RefreshEatStatus();
            Append($"HUD ăn uống: bánh tâm ({_profile.SurvivalHud.FoodCx:F0},{_profile.SurvivalHud.FoodCy:F0}) " +
                   $"r {_profile.SurvivalHud.FoodRmin:F0}–{_profile.SurvivalHud.FoodRmax:F0}; " +
                   $"nước tâm ({_profile.SurvivalHud.WaterCx:F0},{_profile.SurvivalHud.WaterCy:F0}) " +
                   $"r {_profile.SurvivalHud.WaterRmin:F0}–{_profile.SurvivalHud.WaterRmax:F0}. " +
                   "Còn phải Test bánh và Test nước.");
        }
        catch (Exception ex) { Append("hiệu chuẩn ăn uống lỗi: " + ex.Message); }
        finally { bmp.Dispose(); }
    }

    private void TestSurvivalSlot(bool food)
    {
        if (IsRunning) { Append("đang chạy — tắt trước khi test phím"); return; }
        var form = FindForm();
        bool ok = SurvivalHotbarTest.Run(form, _screen, _cfg, _profile, food, Append);
        if (ok) _cfg.Save();
        RefreshEatStatus();
        RefreshCalib();
    }

    // ---------------------------------------------------------------- khoanh prompt

    private void CalibratePrompt()
    {
        if (IsRunning) { Append("đang chạy — tắt trước khi khoanh"); return; }

        var bmp = StillPicker.CaptureWithCountdown(
            FindForm(), _screen,
            "GÓC 1. Đứng vào mốc cho HIỆN nút [E] TƯƠNG TÁC. Giữ nguyên đến hết đếm ngược.",
            _cfg.ShotCountdownSec, _cfg.WindowMatch, out string problem);

        if (bmp is null)
        {
            Append("không chụp được: " + (problem ?? "không rõ"));
            return;
        }

        try
        {
            StillPicker.Save(bmp, ElectricConfig.ShotPath(_profile.Key, "nav-prompt"));
            var current = _profile.PromptBand.IsSet
                ? _profile.PromptBand.ToRectangle()
                : Rectangle.Empty;
            var res = StillCropForm.Run(FindForm(), bmp, "[E] TƯƠNG TÁC",
                "Khoanh trùm CẢ ô vuông chữ E LẪN chữ TƯƠNG TÁC. Lấy dư nền cũng được nếu prompt trôi theo camera. " +
                "App tự tách ô phím; mẫu nhận dạng là phần CHỮ. Ô khoanh chính là vùng quét lúc chạy.",
                current);
            if (res is null) { Append("đã huỷ khoanh [E] TƯƠNG TÁC"); return; }
            ApplyPrompt(bmp, res.Rect);
            _cfg.Save();
            RefreshCalib();
            RefreshNote();
        }
        catch (Exception ex) { Append("khoanh prompt lỗi: " + ex.Message); }
        finally { bmp.Dispose(); }
    }

    private void ApplyPrompt(Bitmap still, Rectangle rect)
    {
        var src = Rectangle.Intersect(rect, new Rectangle(0, 0, still.Width, still.Height));
        if (src.Width < 20 || src.Height < 10)
            throw new InvalidOperationException("vùng quá nhỏ — khoanh trùm cả ô phím lẫn chữ");

        using var crop = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(still, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);

        var parts = ElectricLocator.ExtractText(crop, out string problem);
        if (parts is null) throw new InvalidOperationException(problem);

        var tpl = GrayTemplate.FromBitmapCrop(crop, parts.Text);
        if (tpl.IsFlat)
            throw new InvalidOperationException("ô chữ phẳng tuyệt đối — khoanh trúng chỗ trống");

        tpl.Save(ElectricConfig.PromptTemplatePath(_profile.Key));
        _profile.PromptBand = FishingRect.FromRelative(src);
        _profile.PromptTextH = parts.Text.Height;
        _profile.PromptGapSplit = parts.GapSplit;
        Append($"[E] TƯƠNG TÁC: {parts.Note} → tuong-tac.png  vùng {src.Width}×{src.Height} @ {src.X},{src.Y}");
    }

    private void CalibrateBoardRegions()
    {
        if (IsRunning) { Append("đang chạy — tắt trước khi khoanh bảng"); return; }

        var bmp = StillPicker.CaptureWithCountdown(
            FindForm(), _screen,
            "Mở bảng WATER & POWER ở trạng thái mới bắt đầu, chưa có overlay KHÔNG THÀNH CÔNG.",
            _cfg.ShotCountdownSec, _cfg.WindowMatch, out string problem);
        if (bmp is null)
        {
            Append("không chụp được: " + (problem ?? "không rõ"));
            return;
        }

        try
        {
            StillPicker.Save(bmp, ElectricConfig.ShotPath(_profile.Key, "board"));

            var board = StillCropForm.Run(
                FindForm(), bmp, "VÙNG MÊ CUNG WATER & POWER",
                "Khoanh sát phần bảng xanh chứa toàn bộ tường và hai đầu nối. Không lấy tiêu đề, cụm WASD hay logo bên phải.",
                _profile.ScanBoardRoi().ToRectangle());
            if (board is null) { Append("đã huỷ khoanh vùng bảng"); return; }
            if (board.Rect.Width < 300 || board.Rect.Height < 200)
                throw new InvalidOperationException("vùng mê cung quá nhỏ");

            var title = StillCropForm.Run(
                FindForm(), bmp, "TIÊU ĐỀ WATER & POWER",
                "Khoanh riêng hai dòng tiêu đề xanh phía trên. Dải này chỉ dùng để biết panel đang mở hay đã đóng.",
                _profile.ScanTitleBand().ToRectangle());
            if (title is null) { Append("đã huỷ khoanh tiêu đề; chưa lưu thay đổi"); return; }
            if (title.Rect.Width < 120 || title.Rect.Height < 20)
                throw new InvalidOperationException("vùng tiêu đề quá nhỏ");

            _profile.BoardRoi = FishingRect.FromRelative(board.Rect);
            _profile.TitleBand = FishingRect.FromRelative(title.Rect);
            _cfg.Save();
            RefreshCalib();
            Append($"đã khoanh bảng {board.Rect.Width}×{board.Rect.Height} @ {board.Rect.X},{board.Rect.Y}; " +
                   $"tiêu đề {title.Rect.Width}×{title.Rect.Height} @ {title.Rect.X},{title.Rect.Y}");
        }
        catch (Exception ex) { Append("khoanh bảng lỗi: " + ex.Message); }
        finally { bmp.Dispose(); }
    }

    private void ResetBoardRegions()
    {
        if (IsRunning) { Append("đang chạy — tắt trước khi đổi vùng bảng"); return; }
        _profile.BoardRoi = new FishingRect();
        _profile.TitleBand = new FishingRect();
        _cfg.Save();
        RefreshCalib();
        Append("vùng bảng và tiêu đề: đã trả về suy theo độ phân giải");
    }

    // ---------------------------------------------------------------- anh tinh

    private void CaptureShot()
    {
        if (IsRunning) { Append("đang chạy — tắt trước khi chụp ảnh"); return; }
        if (_shots.SelectedItem is not ShotItem shot) return;

        string instruction = shot.Name switch
        {
            "board" => "Mở bảng nước/điện trong game, để nguyên màn đó.",
            // Ca bo anh nav phai chup o GOC NHIN THU NHAT — do la goc bot chay. Chup o goc 3 thi
            // khung hinh khac han (co than nhan vat, cu ly toi moc khac), va phan kiem anh that
            // cua --verify-nav se soi nham thu.
            "nav-far" => "GÓC 1. Đứng XA điểm làm việc: minimap có chấm vàng, nhưng KHÔNG thấy mốc " +
                         "vàng dưới đất trong khung hình.",
            "nav-marker" => "GÓC 1. Đứng chỗ NHÌN THẤY mốc vàng dưới đất, nhưng CHƯA hiện nút E.",
            "nav-prompt" => "GÓC 1. Đứng vào mốc cho HIỆN nút [E] TƯƠNG TÁC — để kiểm bộ dò prompt.",
            "hud-no" => "HUD đang hiện, đồng hồ bánh/nước còn no (trên 60%).",
            "hud-doi" => "HUD đang hiện, đồng hồ bánh/nước đã dưới 50%.",
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
                _ when shot.Name.StartsWith("nav-") => "chạy “--verify-nav” để kiểm trên ảnh này.",
                _ when shot.Name.StartsWith("hud-") => "chạy “--verify-survival” để kiểm % bánh/nước trên ảnh này.",
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

        if (_cfg.AutoWalk && !_profile.IsPromptCalibrated)
        {
            Append("chưa khoanh [E] TƯƠNG TÁC — không tự đi được. " +
                   "Bấm “Khoanh [E] TƯƠNG TÁC” lúc prompt đang hiện, hoặc tắt tự đi.");
            return;
        }

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
        _btnBoardRoi.Enabled = !running;
        _btnBoardDefault.Enabled = !running;
        _btnPrompt.Enabled = !running;
        _btnEatCenters.Enabled = !running;
        _btnTestFood.Enabled = !running;
        _btnTestWater.Enabled = !running;
        _foodSlot.Enabled = !running;
        _waterSlot.Enabled = !running;
        _screens.Enabled = !running;
        _modes.Enabled = !running;
        _autoWalk.Enabled = !running;
        _autoLoop.Enabled = !running;
        _autoEat.Enabled = !running;
        RunningChanged?.Invoke(running);
    }

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    /// <summary>
    /// Ghi ra man hinh, va ra file khi BotLog.Enabled — cung khuon <see cref="MinerPanel"/>.
    /// File mac dinh tat: mo/dong bot-log.txt moi dong rat ton. Bat o tab Tiện ích khi debug.
    /// </summary>
    private void Append(string line)
    {
        _log.Append(line);
        BotLog.Write("điện", line);
    }
}
