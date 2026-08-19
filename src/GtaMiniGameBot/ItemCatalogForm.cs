
namespace GtaMiniGameBot;

/// <summary>
/// Trích icon vật phẩm từ cache game rồi tích loài nào là cá.
///
/// Thay cho việc dạy mẫu từng con: bộ icon đã có sẵn nhãn trong cache, nên việc còn lại của
/// người dùng chỉ là xác nhận bằng mắt. Tự tích theo tên là để đỡ công, KHÔNG phải để tin —
/// tên rất dễ lừa (bait_fish_chunk có chữ "fish" nhưng là mồi), nên bảng ảnh mới là chỗ chốt.
/// </summary>
internal sealed class ItemCatalogForm : Form
{
    /// <summary>
    /// Từ chỉ loài cá. So theo TỪ chứ không so chuỗi con: "reel_callisto_mg" có chứa "eel",
    /// và mọi cái cần câu cũng sẽ thành cá nếu so chuỗi con.
    /// </summary>
    private static readonly HashSet<string> FishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fish", "catfish", "crayfish", "bass", "carp", "perch", "pike", "trout",
        "zander", "sturgeon", "bluegill", "salmon", "eel", "tilapia", "mackerel"
    };

    /// <summary>
    /// Đồ câu thì KHÔNG phải cá dù tên có nghe như cá. Thiếu luật này là bait_fish_chunk và
    /// bait_mackerel lọt lưới — tích nhầm mồi thành cá thì bot ném sạch mồi vào cốp ngay lượt
    /// đổ đầu tiên, mà mồi thì không kéo ngược lại được.
    /// </summary>
    private static readonly HashSet<string> GearHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "bait", "hook", "line", "reel", "rod"
    };

    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;

    private readonly TextBox _cachePath = new();
    private readonly Label _summary = new();
    private readonly FlowLayoutPanel _sheet = new();
    private readonly TextBox _log = new();
    private readonly List<CheckBox> _boxes = new();

    public ItemCatalogForm(FishingConfig cfg, Screen screen, FishingProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;

        Text = "Vật phẩm & cá — " + profile.Key;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ClientSize = new Size(940, 760);
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
            Text = "Icon vật phẩm nằm sẵn trong cache của game kèm tên. Trích ra một lần là bot " +
                   "nhận được cá ở BẤT KỲ ô nào trong ba lô, không cần khai báo ô.",
            Location = new Point(12, y),
            Size = new Size(916, 34),
            ForeColor = Color.DimGray
        });
        y += 40;

        Controls.Add(new Label { Text = "Thư mục cache:", Location = new Point(12, y + 4), AutoSize = true });
        _cachePath.SetBounds(110, y, 600, 24);
        _cachePath.Text = _cfg.ItemCachePath;
        Controls.Add(_cachePath);

        var browse = new Button { Text = "Chọn…" };
        browse.SetBounds(718, y - 1, 70, 26);
        browse.Click += (_, _) => Browse();
        Controls.Add(browse);

        var extract = new Button { Text = "Trích icon từ game" };
        extract.SetBounds(796, y - 1, 132, 26);
        extract.Click += (_, _) => Extract();
        Controls.Add(extract);
        y += 34;

        _summary.SetBounds(12, y, 916, 20);
        _summary.Font = new Font("Consolas", 9F);
        Controls.Add(_summary);
        y += 26;

        _sheet.SetBounds(12, y, 916, 380);
        _sheet.AutoScroll = true;
        _sheet.BorderStyle = BorderStyle.FixedSingle;
        _sheet.BackColor = Color.FromArgb(245, 246, 248);
        _sheet.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_sheet);
        y += 390;

        var tick = new Button { Text = "Tích lại theo tên" };
        tick.SetBounds(12, y, 130, 28);
        tick.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        tick.Click += (_, _) => { AutoTick(); UpdateSummary(); };
        Controls.Add(tick);

        var none = new Button { Text = "Bỏ tích hết" };
        none.SetBounds(150, y, 100, 28);
        none.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        none.Click += (_, _) => { foreach (var b in _boxes) b.Checked = false; UpdateSummary(); };
        Controls.Add(none);

        var test = new Button { Text = "Thử nhận diện (mở kho đồ trước)" };
        test.SetBounds(258, y, 220, 28);
        test.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        test.Click += (_, _) => TestIdentify();
        Controls.Add(test);

        var save = new Button { Text = "Lưu & đóng", DialogResult = DialogResult.OK };
        save.SetBounds(838, y, 90, 28);
        save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        save.Click += (_, _) => Save();
        Controls.Add(save);
        AcceptButton = save;
        y += 36;

        _log.SetBounds(12, y, 916, 760 - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        _log.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_log);
    }

    // ---------------------------------------------------------------- trích

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = _cachePath.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK) _cachePath.Text = dlg.SelectedPath;
    }

    private void Extract()
    {
        _cfg.ItemCachePath = _cachePath.Text.Trim();
        Append("--- trích icon từ " + _cfg.ItemCachePath + " ---");

        IconHarvest res;
        using (new WaitCursorScope())
            res = ItemIconExtractor.Harvest(_cfg.ItemCachePath, _cfg.AllowIconDownload);

        foreach (string n in res.Notes) Append("   " + n);
        Append($"lấy được {res.Saved.Count} icon vào {ItemIconExtractor.ItemDir}");
        if (res.Missing.Count > 0)
            Append($"thiếu ảnh {res.Missing.Count}: {string.Join(", ", res.Missing.Take(12))}");

        try { _cfg.Save(); } catch (Exception ex) { Append("lưu cấu hình: " + ex.Message); }
        LoadSheet();
    }

    // ---------------------------------------------------------------- bảng ảnh

    private void LoadSheet()
    {
        foreach (Control c in _sheet.Controls) c.Dispose();
        _sheet.Controls.Clear();
        _boxes.Clear();

        string dir = ItemIconExtractor.ItemDir;
        if (!Directory.Exists(dir))
        {
            UpdateSummary();
            Append("chưa có bộ icon — bấm “Trích icon từ game”");
            return;
        }

        var chosen = new HashSet<string>(_profile.FishItems ?? new List<string>(),
                                         StringComparer.OrdinalIgnoreCase);
        bool first = chosen.Count == 0;

        foreach (string path in Directory.EnumerateFiles(dir, "*.png").OrderBy(p => p))
        {
            string name = Path.GetFileNameWithoutExtension(path);

            var box = new CheckBox
            {
                Text = name,
                Tag = name,
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.BottomCenter,
                ImageAlign = ContentAlignment.TopCenter,
                Size = new Size(104, 116),
                FlatStyle = FlatStyle.Standard,
                Checked = first ? IsFishLike(name) : chosen.Contains(name)
            };
            box.CheckedChanged += (_, _) => UpdateSummary();

            try
            {
                // Chep qua bitmap moi roi dong file goc: giu Bitmap(path) song la khoa luon file,
                // lan trich sau se khong ghi de duoc.
                using var raw = new Bitmap(path);
                var thumb = new Bitmap(raw, new Size(72, 72));
                box.Image = thumb;
            }
            catch { /* thieu anh thi van cho tich theo ten */ }

            _boxes.Add(box);
            _sheet.Controls.Add(box);
        }

        UpdateSummary();
        Append($"bộ icon: {_boxes.Count} vật phẩm");
        if (first && _boxes.Count > 0)
            Append("đã tự tích theo tên — soát lại bằng mắt rồi Lưu");
    }

    /// <summary>
    /// Đoán theo tên, chỉ để tích sẵn cho đỡ công — quyết định cuối vẫn là mắt người dùng.
    /// Tên tách theo dấu gạch dưới: có từ nào là tên loài cá thì tích, trừ khi từ đầu (hoặc
    /// bất kỳ từ nào) là tên đồ câu.
    /// </summary>
    private static bool IsFishLike(string name)
    {
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        if (words.Any(GearHeads.Contains)) return false;
        return words.Any(FishWords.Contains);
    }

    private void AutoTick()
    {
        foreach (var b in _boxes) b.Checked = IsFishLike((string)b.Tag);
    }

    private void UpdateSummary()
    {
        var ticked = Ticked();
        _summary.Text = $"{_boxes.Count} vật phẩm · {ticked.Count} loại được tính là cá" +
                        (ticked.Count == 0 ? "  (chưa tích gì — bot sẽ dùng ô khai báo như cũ)" : "");
        _summary.ForeColor = ticked.Count == 0 ? Color.Firebrick : Color.DarkGreen;
    }

    private List<string> Ticked() =>
        _boxes.Where(b => b.Checked).Select(b => (string)b.Tag).OrderBy(s => s).ToList();

    private void Save()
    {
        _profile.FishItems = Ticked();
        _cfg.ItemCachePath = _cachePath.Text.Trim();
        try { _cfg.Save(); } catch (Exception ex) { Append("lưu cấu hình: " + ex.Message); }
        Append($"đã lưu {_profile.FishItems.Count} loại cá: {string.Join(", ", _profile.FishItems)}");
    }

    // ---------------------------------------------------------------- thử nhận diện

    /// <summary>
    /// Chấm điểm từng ô đang có đồ trên màn hình thật và in top-3.
    ///
    /// Đây là chỗ duy nhất chỉnh được ngưỡng mà không phải đoán: nhìn bảng điểm là biết ngay
    /// ItemNccMin đặt ở đâu, và icon có khớp cách game vẽ hay không.
    /// </summary>
    private void TestIdentify()
    {
        var cat = ItemCatalog.Load(_cfg);
        if (cat.Count == 0) { Append("chưa có bộ icon nào để so"); return; }

        Append($"--- thử nhận diện ({cat.Count} mẫu, sàn {_cfg.ItemNccMin:F2}, " +
               $"cách biệt {_cfg.ItemMarginMin:F2}) ---");

        var fish = new HashSet<string>(Ticked(), StringComparer.OrdinalIgnoreCase);

        // Luu dung mang xam ma bo so khop nhin thay. Bang diem noi duoc "khop kem", chi anh moi
        // noi duoc "kem vi sao" — le lech, bi cat vien, hay nhan nham o trong.
        string dumpDir = Path.Combine(FishingConfig.ProfileDir(_profile.Key), "debug-items");
        try { Directory.CreateDirectory(dumpDir); } catch { }

        foreach (var (label, tag, grid) in new[]
                 {
                     ("phím nhanh", FishSlot.GridHotbar, _profile.Hotbar),
                     ("trên người", FishSlot.GridPockets, _profile.Pockets),
                     ("ba lô", FishSlot.GridBag, _profile.Bag)
                 })
        {
            if (!grid.IsSet) { Append($"{label}: chưa khoanh lưới"); continue; }

            using var scanner = new GridScanner(_cfg, _screen, grid);
            int seen = 0;

            using (new WaitCursorScope())
            {
                foreach (var (cell, gray) in scanner.ScanScreenPixels())
                {
                    if (cell is null || cell.IsEmpty) continue;
                    seen++;

                    int w = cell.Rect.Width, h = cell.Rect.Height;
                    var top = cat.Top(gray, w, h, 3);
                    var guess = cat.Classify(gray, w, h);

                    // Bao cao theo DUNG luat bot dung. Truoc day man nay chi doc Name, nen
                    // mot o bot van keo lai hien "KHÔNG RÕ" — nhin log xong lai di sua sai cho.
                    string fishName = guess.FishName(fish, _cfg.ItemNccMin);
                    string verdict = fishName is not null
                        ? (guess.Name is null ? "CÁ → kéo (lẫn loài)" : "CÁ → kéo")
                        : guess.Name is null ? "KHÔNG RÕ" : "không phải cá";

                    Append($"{label} #{cell.Index,-2} {w}x{h} {verdict,-14} " +
                           string.Join("  ", top.Select(t => $"{t.Name} {t.Score:F2}@{t.Scale:F2}")));

                    SaveGray(Path.Combine(dumpDir, $"{tag}-{cell.Index:00}.png"), gray, w, h);
                }
            }

            if (seen == 0) Append($"{label}: không ô nào có đồ (kho đồ đã mở chưa?)");
        }

        Append("ảnh từng ô đã lưu ở " + dumpDir);
    }

    private static void SaveGray(string path, byte[] gray, int w, int h)
    {
        try
        {
            using var bmp = new Bitmap(w, h);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = gray[y * w + x];
                bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { }
    }

    // ---------------------------------------------------------------- vặt

    private void Append(string s)
    {
        _log.AppendText(s + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private sealed class WaitCursorScope : IDisposable
    {
        public WaitCursorScope() => Cursor.Current = Cursors.WaitCursor;
        public void Dispose() => Cursor.Current = Cursors.Default;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var b in _boxes) b.Image?.Dispose();
        base.Dispose(disposing);
    }
}
