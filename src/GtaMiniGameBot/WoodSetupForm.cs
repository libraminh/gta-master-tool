using System.Drawing.Imaging;
using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Hiệu chuẩn job Thợ mộc: chụp hai ảnh tĩnh của màn game rồi khoanh nguội trên ảnh.
///
/// Phải tách làm hai bước, không dùng <see cref="RegionPicker"/> như các ROI cũ: prompt tương tác
/// tắt ngay khi game mất focus, mà RegionPicker phủ overlay lên game để kéo chuột.
/// <see cref="StillPicker.CaptureWithCountdown"/> còn kiểm cửa sổ foreground sau khi đếm ngược, nên
/// không âm thầm cho khoanh trên ảnh desktop.
///
/// Người dùng chỉ khoanh CẢ prompt (ô phím + chữ) một lần cho mỗi trạng thái; phần tách ô phím ra
/// khỏi chữ và đo hình học do <see cref="WoodLocator.SplitPrompt"/> làm.
/// </summary>
internal sealed class WoodSetupForm : Form
{
    private enum Slot { Band, Ready }

    /// <param name="Shot">Tên ảnh tĩnh cần có trước.</param>
    /// <param name="Prompt">true = ô prompt, phải tách ô phím ra rồi lưu mẫu chữ.</param>
    private sealed record SlotInfo(string Label, string Shot, string Hint, bool Prompt);

    private static readonly (string Key, string Label, string Instruction)[] Shots =
    {
        ("ready", "Prompt khai thác",
            "Vào game, đứng cạnh gốc cây cho tới khi hiện “[E] KHAI THÁC”.\r\n" +
            "Giữ nguyên như vậy cho tới khi hết đếm ngược.")
    };

    private static readonly Dictionary<Slot, SlotInfo> Slots = new()
    {
        [Slot.Ready] = new("Prompt khai thác", "ready",
            "Khoanh trùm CẢ ô vuông chữ “E” LẪN chữ “KHAI THÁC”. Lấy dư nền cũng được. " +
            "App tự tách: ô phím chỉ dùng để biết chữ bắt đầu từ đâu, mẫu nhận dạng là phần CHỮ. " +
            "Lúc đang chặt dòng chữ đổi thành “ĐANG KHAI THÁC” nên mẫu này không khớp nữa — " +
            "đúng đó là cách bot biết nó đang bận, không cần mẫu thứ hai.",
            true),
        [Slot.Band] = new("Vùng quét (tuỳ chọn)", "ready",
            "Khoanh một vùng RỘNG trùm mọi chỗ prompt có thể hiện — nó gắn vào gốc cây nên trôi " +
            "theo góc camera, khoanh chật là hụt. Đây chỉ là phạm vi tìm, không phải mẫu. " +
            "Bỏ qua thì bot tự lấy 60%×50% giữa màn.",
            false)
    };

    private readonly WoodConfig _cfg;
    private readonly Screen _screen;
    private readonly WoodProfile _profile;
    private readonly string _key;

    private readonly Dictionary<string, Label> _shotLabels = new();
    private readonly Dictionary<Slot, Label> _slotLabels = new();
    private readonly Label _summary = new();
    private readonly TextBox _log = new();

    public WoodSetupForm(WoodConfig cfg, Screen screen, WoodProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _key = profile.Key;

        Text = $"Khoanh vùng HUD thợ mộc — {_key}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(940, 700);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi();
        RefreshAll();
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        int y = 12;
        const int w = 916;

        var title = new Label
        {
            Text = "Thợ mộc — khoanh vùng HUD",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            AutoSize = false
        };
        title.SetBounds(12, y, w, 26);
        Controls.Add(title);
        y += 30;

        _summary.SetBounds(12, y, w, 22);
        _summary.Font = new Font("Consolas", 10F);
        Controls.Add(_summary);
        y += 28;

        var boxShot = new GroupBox
        {
            Text = "1 · Chụp ảnh màn hình game",
            Location = new Point(12, y),
            Size = new Size(w, 92)
        };
        Controls.Add(boxShot);

        for (int i = 0; i < Shots.Length; i++)
        {
            var meta = Shots[i];
            int sy = 26 + i * 30;

            var btn = new Button { Text = "Chụp: " + meta.Label };
            btn.SetBounds(16, sy, 210, 26);
            string key = meta.Key;
            btn.Click += (_, _) => DoShot(key);
            boxShot.Controls.Add(btn);

            var lbl = new Label { AutoSize = false, Font = new Font("Consolas", 9F) };
            lbl.SetBounds(236, sy + 4, w - 260, 20);
            boxShot.Controls.Add(lbl);
            _shotLabels[key] = lbl;
        }

        y += 102;

        var boxCrop = new GroupBox
        {
            Text = "2 · Khoanh vùng trên ảnh đã chụp",
            Location = new Point(12, y),
            Size = new Size(w, 130)
        };
        Controls.Add(boxCrop);

        int ci = 0;
        foreach (var (slot, info) in Slots)
        {
            int sy = 26 + ci * 32;

            var btn = new Button { Text = "Khoanh: " + info.Label };
            btn.SetBounds(16, sy, 210, 26);
            var s = slot;
            btn.Click += (_, _) => DoCrop(s);
            boxCrop.Controls.Add(btn);

            var lbl = new Label { AutoSize = false, Font = new Font("Consolas", 9F) };
            lbl.SetBounds(236, sy + 4, w - 260, 20);
            boxCrop.Controls.Add(lbl);
            _slotLabels[slot] = lbl;

            ci++;
        }

        y += 140;

        var btnTest = new Button { Text = "Thử dò trên ảnh đã chụp" };
        btnTest.SetBounds(12, y, 210, 28);
        btnTest.Click += (_, _) => TestFromStills();
        Controls.Add(btnTest);

        var btnFolder = new Button { Text = "Mở thư mục dữ liệu" };
        btnFolder.SetBounds(232, y, 180, 28);
        btnFolder.Click += (_, _) => OpenFolder();
        Controls.Add(btnFolder);

        var btnClose = new Button { Text = "Đóng", DialogResult = DialogResult.OK };
        btnClose.SetBounds(w - 88, y, 100, 28);
        Controls.Add(btnClose);
        AcceptButton = btnClose;

        y += 38;

        _log.SetBounds(12, y, w, ClientSize.Height - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        Controls.Add(_log);

        Append("Thứ tự: chụp “Prompt khai thác” → khoanh nó. Vùng quét khoanh sau, hoặc bỏ qua.");
        Append("Chỉ cần một mẫu: lúc đang chặt chữ đổi thành “ĐANG KHAI THÁC” nên mẫu này tự hết khớp.");
    }

    private void RefreshAll()
    {
        foreach (var (key, lbl) in _shotLabels)
        {
            string path = WoodConfig.ShotPath(_key, key);
            if (!File.Exists(path)) { lbl.Text = "chưa chụp"; lbl.ForeColor = Color.Firebrick; continue; }
            using var bmp = StillPicker.Load(path);
            lbl.Text = bmp is null
                ? "ảnh hỏng — chụp lại"
                : $"{bmp.Width}×{bmp.Height}   {File.GetLastWriteTime(path):dd/MM HH:mm}";
            lbl.ForeColor = bmp is null ? Color.Firebrick : Color.DarkGreen;
        }

        foreach (var (slot, lbl) in _slotLabels)
        {
            var (rect, extra) = Describe(slot);
            if (!rect.IsSet)
            {
                lbl.Text = slot == Slot.Band ? "chưa khoanh (dùng mặc định giữa màn)" : "chưa khoanh";
                lbl.ForeColor = slot == Slot.Band ? Color.DimGray : Color.Firebrick;
                continue;
            }
            lbl.Text = $"{rect.W}×{rect.H} @ {rect.X},{rect.Y}{extra}";
            lbl.ForeColor = Color.DarkGreen;
        }

        _summary.Text = _profile.DescribeGaps();
        _summary.ForeColor = _profile.IsCalibrated ? Color.DarkGreen : Color.Firebrick;
    }

    private (FishingRect Rect, string Extra) Describe(Slot slot) => slot switch
    {
        Slot.Band => (_profile.Band, ""),
        _ => (_profile.Ready,
              _profile.TextH >= 6 ? $"   chữ cao {_profile.TextH}px, khe {_profile.GapSplit}px" : "")
    };

    // ---------------------------------------------------------------- chup

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
                StillPicker.Save(shot, WoodConfig.ShotPath(_key, key));
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
        string shotPath = WoodConfig.ShotPath(_key, info.Shot);
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

        var current = Describe(slot).Rect.ToRectangle();
        if (slot == Slot.Band && !_profile.Band.IsSet)
            current = _profile.ScanBand().ToRectangle();   // goi y san o mac dinh de nguoi dung keo lai

        var res = StillCropForm.Run(this, still, info.Label, info.Hint, current);
        if (res is null) { Append($"đã huỷ khoanh “{info.Label}”"); return; }

        try
        {
            if (info.Prompt) ApplyPrompt(still, res.Rect);
            else _profile.Band = FishingRect.FromRelative(res.Rect);

            _cfg.Save();
            if (!info.Prompt)
                Append($"“{info.Label}” = {res.Rect.Width}×{res.Rect.Height} @ {res.Rect.X},{res.Rect.Y}");
        }
        catch (Exception ex)
        {
            Append($"lưu “{info.Label}” lỗi: {ex.Message}");
        }
        RefreshAll();
    }

    /// <summary>
    /// Tách phần chữ ra khỏi ô đã khoanh, lưu mẫu, ghi lại hình học.
    /// Ném <see cref="InvalidOperationException"/> với lời hướng dẫn sửa nếu không tách được —
    /// người dùng phải biết khoanh lại thế nào, không chỉ biết là "lỗi".
    /// </summary>
    private void ApplyPrompt(Bitmap still, Rectangle rect)
    {
        var src = Rectangle.Intersect(rect, new Rectangle(0, 0, still.Width, still.Height));
        if (src.Width < 20 || src.Height < 10)
            throw new InvalidOperationException("vùng quá nhỏ — khoanh trùm cả ô phím lẫn chữ");

        using var crop = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(still, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);

        var parts = WoodLocator.ExtractText(crop, _cfg, out string problem);
        if (parts is null) throw new InvalidOperationException(problem);

        // Mau chi gom phan CHU. O phim chua chu E hay con so dem nguoc, va quanh no la vong tien
        // trinh dang chay — de thu dang dong vao mau la tu dim diem khop cua chinh minh.
        var tpl = GrayTemplate.FromBitmapCrop(crop, parts.Text);
        if (tpl.IsFlat)
            throw new InvalidOperationException("ô chữ phẳng tuyệt đối — khoanh trúng chỗ trống");

        tpl.Save(WoodConfig.ReadyTemplatePath(_key));

        _profile.Ready = FishingRect.FromRelative(src);
        _profile.TextH = parts.Text.Height;
        _profile.GapSplit = parts.GapSplit;

        Append($"“{Slots[Slot.Ready].Label}”: {parts.Note} → ready.png");
    }

    // ---------------------------------------------------------------- thu

    /// <summary>
    /// Chạy bộ dò trên chính hai ảnh tĩnh đã chụp. Lặp lại bao nhiêu lần cũng được, không cần vào
    /// game — nên tinh chỉnh ngưỡng ở đây rẻ hơn hẳn so với thử trực tiếp.
    /// </summary>
    private void TestFromStills()
    {
        if (!_profile.IsCalibrated) { Append("chưa khoanh đủ — " + _profile.DescribeGaps()); return; }

        foreach (var (key, label, _) in Shots)
        {
            string path = WoodConfig.ShotPath(_key, key);
            using var still = StillPicker.Load(path);
            if (still is null) { Append($"“{label}”: chưa có ảnh"); continue; }

            string report = WoodProbe.Describe(_cfg, _profile, still, out string problem);
            if (problem is not null) { Append($"“{label}”: {problem}"); continue; }
            Append($"“{label}”:\r\n{report}");
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(WoodConfig.ProfileDir(_key));
            System.Diagnostics.Process.Start("explorer.exe", WoodConfig.ProfileDir(_key));
        }
        catch (Exception ex) { Append("mở thư mục lỗi: " + ex.Message); }
    }

    // ---------------------------------------------------------------- log

    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private void Append(string line)
    {
        if (_log.Lines.Length > 400) _log.Lines = _log.Lines.Skip(150).ToArray();
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");

        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "bot-log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  wood-setup: {line}{Environment.NewLine}",
                LogEncoding);
        }
        catch { }
    }
}
