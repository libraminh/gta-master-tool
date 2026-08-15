namespace GtaMiniGameBot;

/// <summary>
/// Dạy bot từng chữ số. Một ảnh chụp hiếm khi có đủ 10 chữ số, nên bộ mẫu được xây dần và
/// giữ lại trên đĩa — mở lại hộp thoại này sau vài chuyến câu là đủ.
///
/// Mẹo tiết kiệm: chuỗi "…/30 KG" cho sẵn chữ 3 và 0, "…/60 KG" ở cốp cho thêm 6 —
/// cùng phông, cùng cỡ, cùng khung, không phải chờ ba lô nặng đúng số đó.
/// </summary>
internal sealed class LearnDigitsForm : Form
{
    private readonly FishingConfig _cfg;
    private readonly FishingProfile _profile;
    private readonly string _key;

    private readonly ComboBox _source = new();
    private readonly Canvas _canvas = new();
    private readonly CheckBox _autoThr = new();
    private readonly TrackBar _thr = new();
    private readonly TextBox _truth = new();
    private readonly CheckBox _overwrite = new();
    private readonly Label _count = new();
    private readonly Label _inventory = new();
    private readonly TextBox _log = new();
    private readonly Button _btnSave = new();

    private Bitmap _still;
    private Rectangle _roi;
    private byte[] _gray = Array.Empty<byte>();
    private int _gw, _gh;
    private List<GlyphBox> _boxes = new();
    private int _usedThreshold;

    public LearnDigitsForm(FishingConfig cfg, FishingProfile profile)
    {
        _cfg = cfg;
        _profile = profile;
        _key = profile.Key;

        Text = "Học chữ số — " + _key;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(860, 620);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi();
        LoadSource();
    }

    private void BuildUi()
    {
        int y = 12;

        Controls.Add(new Label { Text = "Lấy chữ từ:", Location = new Point(12, y + 4), AutoSize = true });
        _source.SetBounds(96, y, 300, 24);
        _source.DropDownStyle = ComboBoxStyle.DropDownList;
        _source.Items.Add("Ảnh kho đồ — số KG ba lô");
        _source.Items.Add("Ảnh cốp xe — số KG cốp");
        _source.SelectedIndex = 0;
        _source.SelectedIndexChanged += (_, _) => LoadSource();
        Controls.Add(_source);

        var reload = new Button { Text = "Nạp lại" };
        reload.SetBounds(406, y - 1, 100, 26);
        reload.Click += (_, _) => LoadSource();
        Controls.Add(reload);

        var wipe = new Button { Text = "Xoá hết mẫu, dạy lại" };
        wipe.SetBounds(514, y - 1, 170, 26);
        wipe.Click += (_, _) => WipeAtlas();
        Controls.Add(wipe);
        y += 34;

        _canvas.SetBounds(12, y, 836, 190);
        _canvas.BorderStyle = BorderStyle.FixedSingle;
        _canvas.BackColor = Color.FromArgb(30, 32, 36);
        _canvas.Paint += (_, e) => PaintCanvas(e.Graphics);
        Controls.Add(_canvas);
        y += 200;

        _autoThr.SetBounds(12, y + 2, 180, 22);
        _autoThr.Text = "Ngưỡng tự động (Otsu)";
        _autoThr.Checked = true;
        _autoThr.CheckedChanged += (_, _) => { _thr.Enabled = !_autoThr.Checked; Resegment(); };
        Controls.Add(_autoThr);

        _thr.SetBounds(196, y, 420, 40);
        _thr.Minimum = 20;
        _thr.Maximum = 245;
        _thr.TickFrequency = 15;
        _thr.Value = 130;
        _thr.Enabled = false;
        _thr.ValueChanged += (_, _) => { if (!_autoThr.Checked) Resegment(); };
        Controls.Add(_thr);

        _count.SetBounds(628, y + 4, 220, 20);
        _count.Font = new Font("Consolas", 9F);
        Controls.Add(_count);
        y += 46;

        Controls.Add(new Label
        {
            Text = "Gõ đúng các ký tự trong khung, trái sang phải. Ký tự bạn gõ hiện dưới từng khung — " +
                   "đối chiếu cho khớp 1:1 trước khi lưu.",
            Location = new Point(12, y),
            AutoSize = true
        });
        y += 22;

        _truth.SetBounds(12, y, 400, 30);
        _truth.Font = new Font("Consolas", 13F);
        _truth.CharacterCasing = CharacterCasing.Upper;
        _truth.TextChanged += (_, _) => RefreshCount();
        Controls.Add(_truth);

        _overwrite.SetBounds(430, y + 5, 210, 22);
        _overwrite.Text = "Ghi đè mẫu đã có";
        Controls.Add(_overwrite);

        _btnSave.SetBounds(650, y, 100, 30);
        _btnSave.Text = "Lưu";
        _btnSave.Click += (_, _) => DoSave();
        Controls.Add(_btnSave);

        var close = new Button { Text = "Đóng", DialogResult = DialogResult.OK };
        close.SetBounds(758, y, 90, 30);
        Controls.Add(close);
        CancelButton = close;
        y += 38;

        _inventory.SetBounds(12, y, 836, 20);
        _inventory.Font = new Font("Consolas", 9.5F);
        Controls.Add(_inventory);
        y += 26;

        _log.SetBounds(12, y, 836, 620 - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        Controls.Add(_log);
    }

    // ---------------------------------------------------------------- nạp

    private void LoadSource()
    {
        bool bag = _source.SelectedIndex == 0;
        string shot = bag ? "bag" : "trunk";
        var roiRect = bag ? _profile.BagWeight : _profile.TrunkWeight;

        _still?.Dispose();
        _still = StillPicker.Load(FishingConfig.ShotPath(_key, shot));
        _boxes = new List<GlyphBox>();
        _gray = Array.Empty<byte>();

        if (_still is null)
        {
            Append($"chưa có ảnh “{shot}” — vào Cấu hình đổ cốp chụp trước");
            RefreshAll();
            return;
        }
        if (!roiRect.IsSet)
        {
            Append(bag ? "chưa khoanh số KG ba lô" : "chưa khoanh số KG cốp");
            RefreshAll();
            return;
        }

        _roi = roiRect.ToRectangle();
        _gray = GlyphSeg.GrayOf(_still, _roi, out _gw, out _gh);
        Append($"nạp {shot}: ô {_gw}×{_gh} @ {_roi.X},{_roi.Y}");
        Resegment();
    }

    private void Resegment()
    {
        if (_gray.Length == 0) { RefreshAll(); return; }

        byte[] bin;
        if (_autoThr.Checked)
        {
            bin = GlyphSeg.Binarize(_gray, _cfg.DigitInkMinGray, out _usedThreshold);
            _thr.Value = Math.Clamp(_usedThreshold, _thr.Minimum, _thr.Maximum);
        }
        else
        {
            _usedThreshold = _thr.Value;
            bin = GlyphSeg.BinarizeAt(_gray, _usedThreshold);
        }

        _boxes = GlyphSeg.Segment(bin, _gw, _gh,
            _cfg.DigitMinGlyphW, _cfg.DigitMinGlyphInk, _cfg.DigitMergeGapPx);
        RefreshAll();
    }

    // ---------------------------------------------------------------- vẽ

    private void PaintCanvas(Graphics g)
    {
        if (_still is null || _gw < 1 || _gh < 1) return;

        int cw = _canvas.ClientSize.Width - 16;
        int ch = _canvas.ClientSize.Height - 34;
        double k = Math.Min(8.0, Math.Min(cw / (double)_gw, ch / (double)_gh));
        if (k < 1) k = 1;

        int dw = (int)(_gw * k), dh = (int)(_gh * k);
        int ox = 8, oy = 8;

        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(_still, new Rectangle(ox, oy, dw, dh), _roi, GraphicsUnit.Pixel);

        using var pen = new Pen(Color.FromArgb(90, 220, 255), 1);
        using var font = new Font("Consolas", 8F);
        using var brush = new SolidBrush(Color.FromArgb(255, 205, 70));
        for (int i = 0; i < _boxes.Count; i++)
        {
            var b = _boxes[i].Box;
            var r = new Rectangle(
                ox + (int)(b.X * k), oy + (int)(b.Y * k),
                Math.Max(1, (int)(b.Width * k)), Math.Max(1, (int)(b.Height * k)));
            g.DrawRectangle(pen, r);

            string label = i < _truth.Text.Replace(" ", "").Length
                ? _truth.Text.Replace(" ", "")[i].ToString()
                : (i + 1).ToString();
            g.DrawString(label, font, brush, r.Left, oy + dh + 2);
        }
    }

    // ---------------------------------------------------------------- lưu

    private void DoSave()
    {
        string text = _truth.Text.Replace(" ", "");
        if (_boxes.Count == 0) { Append("chưa tách được khối nào"); return; }
        if (text.Length != _boxes.Count)
        {
            Append($"gõ {text.Length} ký tự nhưng tách ra {_boxes.Count} khối — " +
                   "chỉnh ngưỡng hoặc khoanh lại ô số");
            return;
        }

        var existing = DigitAtlas.Load(_key);
        int tallest = _boxes.Max(b => b.Box.Height);

        int saved = 0, skipped = 0, warned = 0;
        for (int i = 0; i < _boxes.Count; i++)
        {
            char c = text[i];
            if (c == '.') { skipped++; continue; }   // nhận theo kích thước, không cần mẫu
            if (FishingConfig.DigitClassName(c) is null) { skipped++; continue; }

            var b = _boxes[i].Box;
            warned += WarnIfOdd(c, b, tallest, existing) ? 1 : 0;
            var crop = GlyphSeg.Crop(_gray, _gw, _gh, b.X, b.Y, b.Width, b.Height);
            try
            {
                string path = DigitAtlas.SaveGlyph(_key, c, crop, b.Width, b.Height, _overwrite.Checked);
                Append($"'{c}' {b.Width}×{b.Height} → {Path.GetFileName(path)}");
                saved++;
            }
            catch (Exception ex)
            {
                Append($"lưu '{c}' lỗi: {ex.Message}");
            }
        }

        Append($"đã lưu {saved} mẫu" +
               (skipped > 0 ? $", bỏ qua {skipped} ký tự không cần mẫu (dấu chấm, chữ cái)" : ""));
        if (warned > 0)
            Append($"CÓ {warned} MẪU ĐÁNG NGỜ ở trên — nên xoá thư mục digits rồi dạy lại, " +
                   "một mẫu gán nhầm nhãn làm hỏng mọi lần đọc sau");
        RefreshInventory();
    }

    /// <summary>
    /// Bắt ca gán nhầm nhãn ngay lúc dạy. Một mẫu sai âm thầm phá mọi lần đọc về sau và rất khó
    /// lần ra từ log — ví dụ chữ "KG" lỡ lọt vào ô rồi được dạy thành '0' sẽ biến "/60" thành
    /// "/600". Hai dấu hiệu rẻ mà chắc: dấu chấm phải thấp hơn hẳn chữ số, và mẫu mới của một
    /// ký tự phải cùng cỡ với mẫu cũ của chính ký tự đó.
    /// </summary>
    private bool WarnIfOdd(char c, Rectangle box, int tallest, DigitAtlas existing)
    {
        if (c == '.' && box.Height > tallest * 0.6)
        {
            Append($"cảnh báo: '.' cao {box.Height} px, gần bằng chữ số ({tallest}) — " +
                   "khung không khớp ký tự bạn gõ?");
            return true;
        }

        var sizes = existing.SizesOf(c);
        if (sizes.Count > 0 && sizes.All(s => Math.Abs(s.W - box.Width) > 2 || Math.Abs(s.H - box.Height) > 2))
        {
            string old = string.Join(", ", sizes.Select(s => $"{s.W}×{s.H}"));
            Append($"cảnh báo: '{c}' mới {box.Width}×{box.Height} lệch hẳn mẫu đã có ({old})");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Một mẫu gán nhầm nhãn không sửa lẻ được — nó vẫn thắng điểm ở những khối lẽ ra phải bị
    /// từ chối. Xoá sạch rồi dạy lại rẻ hơn là đi tìm mẫu nào hỏng.
    /// </summary>
    private void WipeAtlas()
    {
        string dir = FishingConfig.DigitDir(_key);
        if (!Directory.Exists(dir)) { Append("chưa có mẫu nào để xoá"); return; }

        if (MessageBox.Show(this,
                $"Xoá toàn bộ mẫu chữ số của {_key}?\r\n\r\n{dir}",
                "Xoá mẫu chữ số", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            foreach (string f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
            Append("đã xoá hết mẫu chữ số — dạy lại từ đầu");
        }
        catch (Exception ex) { Append("xoá lỗi: " + ex.Message); }
        RefreshInventory();
    }

    // ---------------------------------------------------------------- trạng thái

    private void RefreshAll()
    {
        RefreshCount();
        RefreshInventory();
        _canvas.Invalidate();
    }

    private void RefreshCount()
    {
        int n = _truth.Text.Replace(" ", "").Length;
        _count.Text = $"khối={_boxes.Count} chữ={n} ngưỡng={_usedThreshold}";
        bool match = _boxes.Count > 0 && n == _boxes.Count;
        _count.ForeColor = match ? Color.DarkGreen : Color.Firebrick;
        _btnSave.Enabled = match;
        _canvas.Invalidate();
    }

    private void RefreshInventory()
    {
        var atlas = DigitAtlas.Load(_key);
        var have = atlas.Known.OrderBy(c => c).ToArray();
        string missing = atlas.MissingText();
        _inventory.Text = $"đã có: {(have.Length == 0 ? "chưa có gì" : string.Join(" ", have))}" +
                          (missing.Length == 0 ? "   —   ĐỦ 12 KÝ TỰ" : $"   —   còn thiếu: {missing}");
        _inventory.ForeColor = missing.Length == 0 ? Color.DarkGreen : Color.DimGray;
    }

    private void Append(string line) =>
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _still?.Dispose();
        _still = null;
        base.OnFormClosed(e);
    }

    private sealed class Canvas : Panel
    {
        public Canvas()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
    }
}
