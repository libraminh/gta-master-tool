using System.Drawing.Imaging;
using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Cấu hình phần đổ cá vào cốp xe: chụp vài ảnh tĩnh của màn game, rồi khoanh nguội mọi vùng
/// trên ảnh đó. Tách làm hai bước vì lúc khoanh thì game không còn focus — mà menu radial và
/// kho đồ chỉ tồn tại khi game đang focus.
/// </summary>
internal sealed class TrunkSetupForm : Form
{
    private enum Slot
    {
        BagWeight, TrunkWeight, BagHeader, TrunkHeader, PauseMarker,
        AltInteract, AltTrunk, AltFuel, AltBand,
        GridHotbar, GridBag, GridTrunk
    }

    /// <param name="Shot">Ảnh tĩnh cần có trước.</param>
    /// <param name="Template">Tên file mẫu NCC, null nếu chỉ cần toạ độ.</param>
    private sealed record SlotInfo(string Label, string Shot, string Hint, bool Grid, string Template);

    private static readonly (string Key, string Label, string Instruction)[] Shots =
    {
        ("bag", "Kho đồ (Tab)",
            "Mở kho đồ bằng phím Tab.\r\nCần thấy rõ: số KG ba lô, chữ BA LÔ, hàng phím nhanh bên trái và lưới ba lô."),
        ("trunk", "Cốp xe đang mở",
            "Mở cốp xe (giữ Alt → Tương tác → Cốp xe).\r\nCần thấy rõ: chữ CỐP PHƯƠNG TIỆN, số KG cốp và lưới ô cốp."),
        ("alt2", "Menu Alt — 2 nút",
            "Đứng cạnh xe, camera hướng vào xe.\r\nGIỮ Alt cho tới khi hết đếm ngược. Cần thấy nút Tương tác."),
        ("alt4", "Menu Alt — 4 nút",
            "Giữ Alt, click Tương tác, rồi GIỮ NGUYÊN Alt cho tới khi hết đếm ngược.\r\n" +
            "Cần thấy cả Cốp xe lẫn Bơm nhiên liệu."),
        ("pause", "Menu tạm dừng (tuỳ chọn)",
            "Bấm Esc khi KHÔNG có màn hình nào đang mở, để chụp menu tạm dừng.\r\n" +
            "Dùng để bot biết lúc nào nó bấm Esc nhầm chỗ.")
    };

    private static readonly Dictionary<Slot, SlotInfo> Slots = new()
    {
        [Slot.BagWeight] = new("Số KG ba lô", "bag",
            "Khoanh trùm CẢ chuỗi “27.4/30 KG”, chừa mỗi bên vài pixel. " +
            "Phải có cả mẫu số vì bot lấy “/30” làm neo chống đọc sai.", false, null),
        [Slot.TrunkWeight] = new("Số KG cốp", "trunk",
            "Khoanh trùm cả chuỗi “9.7/60 KG” ở cột cốp phương tiện.", false, null),
        [Slot.BagHeader] = new("Chữ BA LÔ", "bag",
            "Khoanh sát chữ “BA LÔ”. Bot dùng nó để biết kho đồ đã mở hay chưa.", false, "hdr-bag"),
        [Slot.TrunkHeader] = new("Chữ CỐP PHƯƠNG TIỆN", "trunk",
            "Khoanh sát chữ “CỐP PHƯƠNG TIỆN”. Bot dùng nó để biết cốp đã mở hay chưa.", false, "hdr-trunk"),
        [Slot.PauseMarker] = new("Dấu menu tạm dừng", "pause",
            "Khoanh một chỗ chỉ menu tạm dừng mới có. Bỏ qua cũng được, " +
            "nhưng có thì bot phát hiện được lúc Esc bấm nhầm.", false, "hdr-pause"),
        [Slot.AltInteract] = new("Nút Tương tác", "alt2",
            "Khoanh trùm khối trắng của nút “Tương tác”, cả chữ lẫn nền. " +
            "Ô này vừa là mẫu để nhận chữ, vừa là toạ độ dự phòng khi dò không ra.", false, "menu-interact"),
        [Slot.AltTrunk] = new("Nút Cốp xe", "alt4",
            "Khoanh trùm khối trắng của nút “Cốp xe”, cùng cỡ với nút Tương tác.", false, "menu-trunk"),
        [Slot.AltFuel] = new("Nút Bơm nhiên liệu", "alt4",
            "Khoanh trùm nút “Bơm nhiên liệu”. Nút này nằm cùng hàng với Cốp xe — " +
            "có mẫu của nó thì bot mới phân biệt được hai nút bằng cách SO SÁNH.", false, "menu-fuel"),
        [Slot.AltBand] = new("Vùng quét menu (tuỳ chọn)", "alt4",
            "Khoanh vùng trùm cả 4 nút. Bỏ qua thì bot tự suy một vùng quanh tâm màn.", false, null),
        [Slot.GridHotbar] = new("Lưới phím nhanh", "bag",
            "Khoanh từ mép ngoài ô trên cùng tới mép ngoài ô dưới cùng của hàng phím nhanh, " +
            "rồi chỉnh số cột/hàng cho các đường kẻ trùng khe giữa các ô.", true, null),
        [Slot.GridBag] = new("Lưới ba lô", "bag",
            "Khoanh trùm cả lưới BA LÔ, mép ngoài tới mép ngoài. Chỉnh cột/hàng cho khớp.", true, null),
        [Slot.GridTrunk] = new("Lưới cốp", "trunk",
            "Khoanh trùm cả lưới CỐP PHƯƠNG TIỆN. Chỉnh cột/hàng cho khớp.", true, null)
    };

    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;
    private readonly string _key;

    private readonly Dictionary<string, Label> _shotLabels = new();
    private readonly Dictionary<Slot, Label> _slotLabels = new();
    private readonly Label _summary = new();
    private readonly Label _ocrStatus = new();
    private readonly TextBox _log = new();

    public TrunkSetupForm(FishingConfig cfg, Screen screen, FishingProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _key = profile.Key;

        Text = $"Cấu hình đổ cá vào cốp — {_key}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(940, 786);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi();
        RefreshAll();
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        int y = 12;

        var title = new Label
        {
            Text = "Đổ cá vào cốp xe — cấu hình",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            AutoSize = false
        };
        title.SetBounds(12, y, 916, 26);
        Controls.Add(title);
        y += 30;

        _summary.SetBounds(12, y, 916, 22);
        _summary.Font = new Font("Consolas", 10F);
        Controls.Add(_summary);
        y += 30;

        var boxShot = new GroupBox
        {
            Text = "1 · Chụp ảnh màn hình game",
            Location = new Point(12, y),
            Size = new Size(916, 118)
        };
        Controls.Add(boxShot);

        for (int i = 0; i < Shots.Length; i++)
        {
            var (key, label, _) = Shots[i];
            int col = i % 3, row = i / 3;
            int bx = 14 + col * 300, by = 26 + row * 44;

            var b = new Button { Text = "Chụp: " + label };
            b.SetBounds(bx, by, 190, 30);
            string k = key;
            b.Click += (_, _) => DoShot(k);
            boxShot.Controls.Add(b);

            var lbl = new Label { AutoSize = false, ForeColor = Color.DimGray };
            lbl.SetBounds(bx + 196, by + 6, 100, 20);
            boxShot.Controls.Add(lbl);
            _shotLabels[key] = lbl;
        }
        y += 130;

        var boxCrop = new GroupBox
        {
            Text = "2 · Khoanh vùng trên ảnh đã chụp",
            Location = new Point(12, y),
            Size = new Size(916, 286)
        };
        Controls.Add(boxCrop);

        int i2 = 0;
        foreach (var slot in Slots.Keys)
        {
            int col = i2 % 2, row = i2 / 2;
            int bx = 14 + col * 452, by = 26 + row * 42;

            var b = new Button { Text = Slots[slot].Label };
            b.SetBounds(bx, by, 210, 30);
            var s = slot;
            b.Click += (_, _) => DoCrop(s);
            boxCrop.Controls.Add(b);

            var lbl = new Label { AutoSize = false, Font = new Font("Consolas", 8.5F) };
            lbl.SetBounds(bx + 216, by + 7, 230, 20);
            boxCrop.Controls.Add(lbl);
            _slotLabels[slot] = lbl;
            i2++;
        }
        y += 298;

        var boxOcr = new GroupBox
        {
            Text = "3 · Đọc chữ số KG",
            Location = new Point(12, y),
            Size = new Size(916, 70)
        };
        Controls.Add(boxOcr);

        var btnLearn = new Button { Text = "Học chữ số…" };
        btnLearn.SetBounds(14, 26, 150, 30);
        btnLearn.Click += (_, _) => OpenLearnDigits();
        boxOcr.Controls.Add(btnLearn);

        var btnTestOcr = new Button { Text = "Thử đọc KG từ ảnh" };
        btnTestOcr.SetBounds(172, 26, 170, 30);
        btnTestOcr.Click += (_, _) => TestOcrFromStills();
        boxOcr.Controls.Add(btnTestOcr);

        _ocrStatus.SetBounds(352, 33, 550, 20);
        _ocrStatus.Font = new Font("Consolas", 9F);
        boxOcr.Controls.Add(_ocrStatus);
        y += 82;

        _log.SetBounds(12, y, 916, 786 - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        Controls.Add(_log);

        Append("Chụp ảnh trước, khoanh sau. Ảnh lưu ở " + FishingConfig.ShotDir(_key));
    }

    // ---------------------------------------------------------------- chụp

    private void DoShot(string key)
    {
        var meta = Shots.First(s => s.Key == key);
        var shot = StillPicker.CaptureWithCountdown(
            this, _screen, meta.Instruction, _cfg.ShotCountdownSec, _cfg.WindowMatch, out string problem);

        if (shot is null)
        {
            Append($"chụp “{meta.Label}”: {problem ?? "không chụp được"}");
            return;
        }

        using (shot)
        {
            try
            {
                StillPicker.Save(shot, FishingConfig.ShotPath(_key, key));
                Append($"đã chụp “{meta.Label}” {shot.Width}×{shot.Height}");
            }
            catch (Exception ex)
            {
                Append($"lưu ảnh “{meta.Label}” lỗi: {ex.Message}");
            }
        }
        RefreshAll();
    }

    // ---------------------------------------------------------------- khoanh

    private void DoCrop(Slot slot)
    {
        var info = Slots[slot];
        string shotPath = FishingConfig.ShotPath(_key, info.Shot);
        using var still = StillPicker.Load(shotPath);
        if (still is null)
        {
            string label = Shots.First(s => s.Key == info.Shot).Label;
            Append($"chưa có ảnh “{label}” — chụp ảnh đó trước");
            return;
        }
        if (still.Width != _profile.Width || still.Height != _profile.Height)
        {
            Append($"ảnh {still.Width}×{still.Height} lệch màn hình {_profile.Width}×{_profile.Height} — chụp lại");
            return;
        }

        var (current, cols, rows) = Current(slot);
        var res = StillCropForm.Run(this, still, info.Label, info.Hint, current, info.Grid, cols, rows);
        if (res is null) { Append($"đã huỷ khoanh “{info.Label}”"); return; }

        try
        {
            if (info.Template is not null)
                SaveTemplate(still, res.Rect, info.Template);

            Apply(slot, res);
            _cfg.Save();
            Append($"“{info.Label}” = {res.Rect.Width}×{res.Rect.Height} @ {res.Rect.X},{res.Rect.Y}" +
                   (info.Grid ? $"  {res.Cols}×{res.Rows} ô" : "") +
                   (info.Template is not null ? $"  → {info.Template}.png" : ""));
        }
        catch (Exception ex)
        {
            Append($"lưu “{info.Label}” lỗi: {ex.Message}");
        }
        RefreshAll();
    }

    private void SaveTemplate(Bitmap still, Rectangle rect, string name)
    {
        var src = Rectangle.Intersect(rect, new Rectangle(0, 0, still.Width, still.Height));
        if (src.Width < 4 || src.Height < 4) throw new InvalidOperationException("vùng quá nhỏ");

        using var crop = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(still, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);

        StillPicker.Save(crop, FishingConfig.TrunkTemplatePath(_key, name));
    }

    private (Rectangle rect, int cols, int rows) Current(Slot slot) => slot switch
    {
        Slot.BagWeight => (_profile.BagWeight.ToRectangle(), 1, 1),
        Slot.TrunkWeight => (_profile.TrunkWeight.ToRectangle(), 1, 1),
        Slot.BagHeader => (_profile.BagHeader.ToRectangle(), 1, 1),
        Slot.TrunkHeader => (_profile.TrunkHeader.ToRectangle(), 1, 1),
        Slot.PauseMarker => (_profile.PauseMarker.ToRectangle(), 1, 1),
        Slot.AltInteract => (_profile.AltInteract.ToRectangle(), 1, 1),
        Slot.AltTrunk => (_profile.AltTrunk.ToRectangle(), 1, 1),
        Slot.AltFuel => (_profile.AltFuel.ToRectangle(), 1, 1),
        Slot.AltBand => (_profile.AltBand.ToRectangle(), 1, 1),
        Slot.GridHotbar => (_profile.Hotbar.Area.ToRectangle(), _profile.Hotbar.Cols, _profile.Hotbar.Rows),
        Slot.GridBag => (_profile.Bag.Area.ToRectangle(), _profile.Bag.Cols, _profile.Bag.Rows),
        _ => (_profile.Trunk.Area.ToRectangle(), _profile.Trunk.Cols, _profile.Trunk.Rows)
    };

    private void Apply(Slot slot, StillCropResult r)
    {
        var rect = FishingRect.FromRelative(r.Rect);
        switch (slot)
        {
            case Slot.BagWeight: _profile.BagWeight = rect; break;
            case Slot.TrunkWeight: _profile.TrunkWeight = rect; break;
            case Slot.BagHeader: _profile.BagHeader = rect; break;
            case Slot.TrunkHeader: _profile.TrunkHeader = rect; break;
            case Slot.PauseMarker: _profile.PauseMarker = rect; break;
            case Slot.AltInteract: _profile.AltInteract = rect; break;
            case Slot.AltTrunk: _profile.AltTrunk = rect; break;
            case Slot.AltFuel: _profile.AltFuel = rect; break;
            case Slot.AltBand: _profile.AltBand = rect; break;
            case Slot.GridHotbar: _profile.Hotbar = Grid(r); break;
            case Slot.GridBag: _profile.Bag = Grid(r); break;
            default: _profile.Trunk = Grid(r); break;
        }
    }

    private static GridSpec Grid(StillCropResult r) =>
        new() { Area = FishingRect.FromRelative(r.Rect), Cols = r.Cols, Rows = r.Rows };

    // ---------------------------------------------------------------- đọc chữ số

    private void OpenLearnDigits()
    {
        using var f = new LearnDigitsForm(_cfg, _profile);
        f.ShowDialog(this);
        RefreshAll();
    }

    /// <summary>
    /// Thử đọc trên ẢNH TĨNH đã chụp — lặp lại được bao nhiêu lần cũng được, không cần đứng
    /// trong game, nên tinh chỉnh ngưỡng ở đây rẻ hơn hẳn so với thử trực tiếp.
    /// </summary>
    private void TestOcrFromStills()
    {
        var atlas = DigitAtlas.Load(_key);
        string missing = atlas.MissingText();
        if (atlas.Count == 0) { Append("chưa có mẫu chữ số nào — bấm “Học chữ số…” trước"); return; }
        if (missing.Length > 0) Append($"cảnh báo: còn thiếu mẫu {missing} — kết quả có thể ra '?'");

        TryOne("bag", "ba lô", _profile.BagWeight, _cfg.BagCapKg, atlas);
        TryOne("trunk", "cốp", _profile.TrunkWeight, _cfg.TrunkCapKg, atlas);
        RefreshAll();
    }

    private void TryOne(string shot, string label, FishingRect roi, double cap, DigitAtlas atlas)
    {
        if (!roi.IsSet) { Append($"{label}: chưa khoanh ô số KG"); return; }

        using var still = StillPicker.Load(FishingConfig.ShotPath(_key, shot));
        if (still is null) { Append($"{label}: chưa có ảnh “{shot}”"); return; }

        var r = WeightReader.ReadStill(still, roi, atlas, _cfg, cap);
        Append($"{label}: {r}");
        Append("   " + r.Trace);
    }

    // ---------------------------------------------------------------- trạng thái

    private void RefreshAll()
    {
        foreach (var (key, label) in _shotLabels)
        {
            string p = FishingConfig.ShotPath(_key, key);
            if (File.Exists(p))
            {
                label.Text = File.GetLastWriteTime(p).ToString("dd/MM HH:mm");
                label.ForeColor = Color.DarkGreen;
            }
            else
            {
                label.Text = "chưa chụp";
                label.ForeColor = Color.DimGray;
            }
        }

        foreach (var (slot, label) in _slotLabels)
        {
            var (rect, cols, rows) = Current(slot);
            bool set = rect.Width >= 8 && rect.Height >= 8;
            label.Text = set
                ? $"{rect.Width}×{rect.Height}" + (Slots[slot].Grid ? $"  {cols}×{rows} ô" : "")
                : "chưa khoanh";
            label.ForeColor = set ? Color.DarkGreen : Color.DimGray;
        }

        string gaps = _profile.DescribeTrunkGaps();
        _summary.Text = gaps;
        _summary.ForeColor = gaps.StartsWith("đủ") ? Color.DarkGreen : Color.Firebrick;

        var atlas = DigitAtlas.Load(_key);
        string missing = atlas.MissingText();
        _ocrStatus.Text = atlas.Count == 0
            ? "chưa có mẫu chữ số nào"
            : missing.Length == 0
                ? $"đủ 12 ký tự ({atlas.Count} mẫu)"
                : $"còn thiếu: {missing}";
        _ocrStatus.ForeColor = atlas.Count > 0 && missing.Length == 0 ? Color.DarkGreen : Color.DimGray;
    }

    private void Append(string line)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "bot-log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [cốp] {line}{Environment.NewLine}",
                new UTF8Encoding(true));
        }
        catch { }
    }
}
