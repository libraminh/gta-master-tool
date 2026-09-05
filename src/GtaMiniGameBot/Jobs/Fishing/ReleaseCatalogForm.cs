namespace GtaMiniGameBot;

internal enum CatchCatalogKind { Release, Sell }

/// <summary>
/// Chọn loài tự ấn THẢ RA hoặc BÁN NGAY và chụp mẫu chữ tên trên panel nhận cá.
///
/// Tách khỏi <see cref="ItemCatalogForm"/>: danh sách kia là "cá để đổ cốp", danh sách này
/// là "cá không cất vào". Một loài có thể nằm ở cả hai — thả/bán thì không bao giờ vào ba lô.
/// Thả Ra xét trước Bán Ngay nếu loài nằm cả hai danh sách.
/// </summary>
internal sealed class ReleaseCatalogForm : Form
{
    private static readonly HashSet<string> FishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fish", "catfish", "crayfish", "bass", "carp", "perch", "pike", "trout",
        "zander", "sturgeon", "bluegill", "salmon", "eel", "tilapia", "mackerel", "shrimp"
    };

    private static readonly HashSet<string> GearHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "bait", "hook", "line", "reel", "rod"
    };

    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;
    private readonly CatchCatalogKind _kind;

    private readonly Label _status = new();
    private readonly FlowLayoutPanel _sheet = new();
    private readonly ComboBox _captureItem = new();
    private readonly TextBox _log = new();
    private readonly List<CheckBox> _boxes = new();

    public ReleaseCatalogForm(
        FishingConfig cfg, Screen screen, FishingProfile profile, CatchCatalogKind kind)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _kind = kind;
        _profile.Normalize();

        Text = (_kind == CatchCatalogKind.Sell ? "Loại bán ngay — " : "Loại thả ra — ") + profile.Key;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ClientSize = new Size(940, 780);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi();
        LoadSheet();
    }

    private void BuildUi()
    {
        int y = 12;

        Controls.Add(new Label
        {
            Text = _kind == CatchCatalogKind.Sell
                ? "Tick loài muốn tự ấn BÁN NGAY. Dùng chung ô chữ tên với Thả ra. " +
                  "Không chắc thì bot vẫn CẤT VÀO. Loài cũng nằm trong Thả ra sẽ bị thả, không bán."
                : "Tick loài muốn tự ấn THẢ RA. Cần khoanh ô chữ tên trên panel nhận cá " +
                  "rồi chụp một mẫu tên cho từng loài — không chắc thì bot vẫn CẤT VÀO.",
            Location = new Point(12, y),
            Size = new Size(916, 34),
            ForeColor = Color.DimGray
        });
        y += 38;

        _status.SetBounds(12, y, 916, 20);
        _status.Font = new Font("Consolas", 9F);
        Controls.Add(_status);
        y += 26;

        _sheet.SetBounds(12, y, 916, 390);
        _sheet.AutoScroll = true;
        _sheet.BorderStyle = BorderStyle.FixedSingle;
        _sheet.BackColor = Color.FromArgb(245, 246, 248);
        _sheet.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_sheet);
        y += 400;

        var pickTitle = new Button { Text = "Khoanh ô tên cá" };
        pickTitle.SetBounds(12, y, 140, 28);
        pickTitle.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        pickTitle.Click += (_, _) => PickTitle();
        Controls.Add(pickTitle);

        Controls.Add(new Label
        {
            Text = "Chụp mẫu cho:",
            Location = new Point(162, y + 5),
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        });

        _captureItem.DropDownStyle = ComboBoxStyle.DropDownList;
        _captureItem.SetBounds(258, y, 200, 26);
        _captureItem.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(_captureItem);

        var shot = new Button { Text = "Chụp mẫu tên" };
        shot.SetBounds(466, y, 130, 28);
        shot.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        shot.Click += (_, _) => CaptureTitle();
        Controls.Add(shot);

        var none = new Button { Text = "Bỏ tích hết" };
        none.SetBounds(604, y, 100, 28);
        none.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        none.Click += (_, _) =>
        {
            foreach (var b in _boxes) b.Checked = false;
            RefreshCaptureList();
            UpdateStatus();
        };
        Controls.Add(none);

        var save = new Button { Text = "Lưu & đóng", DialogResult = DialogResult.OK };
        save.SetBounds(838, y, 90, 28);
        save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        save.Click += (_, _) => Save();
        Controls.Add(save);
        AcceptButton = save;
        y += 36;

        _log.SetBounds(12, y, 916, 780 - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        _log.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_log);
    }

    private void LoadSheet()
    {
        foreach (Control c in _sheet.Controls) c.Dispose();
        _sheet.Controls.Clear();
        _boxes.Clear();

        string dir = ItemIconExtractor.ItemDir;
        var chosen = new HashSet<string>(ChosenItems(), StringComparer.OrdinalIgnoreCase);

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
        {
            foreach (string path in Directory.EnumerateFiles(dir, "*.png"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (IsFishLike(name) || chosen.Contains(name))
                    names.Add(name);
            }
        }
        foreach (string n in chosen)
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n);

        if (names.Count == 0)
        {
            UpdateStatus();
            Append("chưa có icon cá — vào Cấu hình đổ cốp → Vật phẩm & cá rồi trích icon từ game");
            return;
        }

        foreach (string name in names)
        {
            bool hasTpl = FishingConfig.HasCatchTitleTemplate(_profile.Key, name);
            var box = new CheckBox
            {
                Text = hasTpl ? name + "  · mẫu" : name,
                Tag = name,
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.BottomCenter,
                ImageAlign = ContentAlignment.TopCenter,
                Size = new Size(104, 116),
                FlatStyle = FlatStyle.Flat,
                Checked = chosen.Contains(name),
                BackColor = hasTpl ? Color.FromArgb(220, 245, 228) : Color.White
            };
            box.FlatAppearance.BorderColor = hasTpl ? Color.ForestGreen : Color.Silver;
            box.FlatAppearance.BorderSize = hasTpl ? 2 : 1;
            box.CheckedChanged += (_, _) =>
            {
                if (box.Checked) SelectCapture(name);
                RefreshCaptureList();
                UpdateStatus();
            };

            string icon = Path.Combine(dir, name + ".png");
            if (File.Exists(icon))
            {
                try
                {
                    using var raw = new Bitmap(icon);
                    box.Image = new Bitmap(raw, new Size(72, 72));
                }
                catch { /* thiếu ảnh thì vẫn cho tích theo tên */ }
            }

            _boxes.Add(box);
            _sheet.Controls.Add(box);
        }

        RefreshCaptureList();
        UpdateStatus();
        Append($"bộ cá/tôm: {_boxes.Count} loại · đã chọn {Ticked().Count}");
        int have = Ticked().Count(n => FishingConfig.HasCatchTitleTemplate(_profile.Key, n));
        if (Ticked().Count > 0 && have < Ticked().Count)
            Append("viền xanh = đã có mẫu tên. Loài chưa có mẫu sẽ bị cất vào cho đến khi chụp.");
        if (!_profile.CatchTitle.IsSet)
            Append("chưa khoanh ô tên cá — bấm “Khoanh ô tên cá” lúc panel nhận cá đang hiện");
        if (_kind == CatchCatalogKind.Sell)
        {
            var overlap = Ticked().Where(n =>
                (_profile.AutoReleaseItems ?? new List<string>())
                    .Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (overlap.Count > 0)
                Append("thả trước bán: " + string.Join(", ", overlap) + " — sẽ bị THẢ RA, không bán");
        }
    }

    private static bool IsFishLike(string name)
    {
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        if (words.Any(GearHeads.Contains)) return false;
        return words.Any(FishWords.Contains);
    }

    private List<string> ChosenItems() =>
        (_kind == CatchCatalogKind.Sell ? _profile.AutoSellItems : _profile.AutoReleaseItems)
        ?? new List<string>();

    private void AssignItems(List<string> items)
    {
        if (_kind == CatchCatalogKind.Sell) _profile.AutoSellItems = items;
        else _profile.AutoReleaseItems = items;
    }

    private string DescribeStatus() =>
        _kind == CatchCatalogKind.Sell
            ? _profile.DescribeSellStatus(_profile.Key)
            : _profile.DescribeReleaseStatus(_profile.Key);

    private List<string> Ticked() =>
        _boxes.Where(b => b.Checked).Select(b => (string)b.Tag).OrderBy(s => s).ToList();

    private void SelectCapture(string name)
    {
        int i = _captureItem.Items.IndexOf(name);
        if (i >= 0) _captureItem.SelectedIndex = i;
    }

    private void RefreshCaptureList()
    {
        string keep = _captureItem.SelectedItem as string;
        _captureItem.Items.Clear();
        foreach (string n in Ticked())
            _captureItem.Items.Add(n);
        if (keep is not null)
        {
            int i = _captureItem.Items.IndexOf(keep);
            if (i >= 0) { _captureItem.SelectedIndex = i; return; }
        }
        if (_captureItem.Items.Count > 0)
            _captureItem.SelectedIndex = 0;
    }

    private void UpdateStatus()
    {
        AssignItems(Ticked());
        _status.Text = DescribeStatus();
        _status.ForeColor = Ticked().Count == 0 ? Color.Firebrick : Color.DarkGreen;
    }

    private void Save()
    {
        var items = Ticked();
        AssignItems(items);
        try { _cfg.Save(); } catch (Exception ex) { Append("lưu cấu hình: " + ex.Message); }
        string verb = _kind == CatchCatalogKind.Sell ? "bán" : "thả";
        Append($"đã lưu {items.Count} loại {verb}: " + string.Join(", ", items));
    }

    private void PickTitle()
    {
        var result = RegionPicker.Run(this, _screen, "Khoanh ô tên cá",
            "Kéo ôm chữ tên loài (TÔM CÀNG, CÁ TRA…) trên panel nhận cá. " +
            "Ô này đứng yên — đừng ôm hàng nút bên dưới.");
        if (result is null)
        {
            Append("đã hủy khoanh ô tên cá");
            return;
        }

        try
        {
            RegionPicker.SavePng(result.Preview, FishingConfig.CatchTitlePreviewPath(_profile.Key));
            _profile.CatchTitle = FishingRect.FromRelative(result.Relative);
            _cfg.Save();
        }
        catch (Exception ex)
        {
            Append("lưu ô tên lỗi: " + ex.Message);
            result.Preview.Dispose();
            return;
        }

        result.Preview.Dispose();
        Append($"đã khoanh ô tên  {result.Relative.Width}×{result.Relative.Height}  " +
               $"@ {result.Relative.X},{result.Relative.Y}");

        int stale = Ticked().Count(n =>
        {
            string path = FishingConfig.CatchTitlePath(_profile.Key, n);
            if (!File.Exists(path)) return false;
            try
            {
                using var bmp = new Bitmap(path);
                return bmp.Width != result.Relative.Width || bmp.Height != result.Relative.Height;
            }
            catch { return false; }
        });
        if (stale > 0)
            Append($"cảnh báo: {stale} mẫu tên lệch kích thước ô mới — chụp lại những loài đó");

        UpdateStatus();
        LoadSheet();
    }

    private void CaptureTitle()
    {
        if (!_profile.CatchTitle.IsSet)
        {
            Append("chưa khoanh ô tên cá — bấm “Khoanh ô tên cá” trước");
            return;
        }

        string name = _captureItem.SelectedItem as string;
        if (string.IsNullOrEmpty(name))
        {
            Append("tick một loại rồi chọn nó ở “Chụp mẫu cho”");
            return;
        }

        using var shot = StillPicker.CaptureWithCountdown(
            this, _screen,
            $"Để panel nhận cá đang hiện chữ tên của “{name}”, rồi bấm OK. " +
            "Bấm xong có " + _cfg.ShotCountdownSec + " giây để click vào game.",
            _cfg.ShotCountdownSec, _cfg.WindowMatch, out string problem);

        if (shot is null)
        {
            Append("chụp mẫu tên: " + (problem ?? "không chụp được"));
            return;
        }

        var abs = FishingConfig.ToAbsolute(_screen, _profile.CatchTitle);
        var inImage = new Rectangle(abs.X - _screen.Bounds.X, abs.Y - _screen.Bounds.Y, abs.Width, abs.Height);
        inImage = Rectangle.Intersect(inImage, new Rectangle(0, 0, shot.Width, shot.Height));
        if (inImage.Width < 8 || inImage.Height < 8)
        {
            Append("ô tên nằm ngoài ảnh chụp — khoanh lại ô tên");
            return;
        }

        try
        {
            using var crop = shot.Clone(inImage, shot.PixelFormat);
            RegionPicker.SavePng(crop, FishingConfig.CatchTitlePath(_profile.Key, name));
            Append($"đã chụp mẫu tên “{name}”  {crop.Width}×{crop.Height}");
        }
        catch (Exception ex)
        {
            Append("lưu mẫu tên lỗi: " + ex.Message);
            return;
        }

        LoadSheet();
        SelectCapture(name);
    }

    private void Append(string s)
    {
        _log.AppendText(s + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var b in _boxes) b.Image?.Dispose();
        base.Dispose(disposing);
    }
}
