using System.Drawing.Imaging;

namespace GtaMiniGameBot;

/// <summary>
/// Wizard hiệu chuẩn ăn uống: khoanh từng đồng hồ, học tâm/bán kính, chụp LOW/HIGH
/// cùng góc nhìn, chỉ cho lưu khi HIGH lớn hơn LOW rõ ràng.
/// </summary>
internal sealed class SurvivalWizardForm : Form
{
    private readonly Bitmap _still;
    private readonly Screen _screen;
    private readonly ElectricConfig _cfg;
    private readonly ElectricProfile _profile;
    private readonly NavScale _s;

    private readonly Canvas _canvas = new();
    private readonly Label _hint = new();
    private readonly Label _readout = new();
    private readonly NumericUpDown _foodCx = new(), _foodCy = new(), _foodR0 = new(), _foodR1 = new();
    private readonly NumericUpDown _waterCx = new(), _waterCy = new(), _waterR0 = new(), _waterR1 = new();
    private readonly Button _ok = new();
    private readonly Button _lowFood = new(), _highFood = new(), _lowWater = new(), _highWater = new();

    private Rectangle? _foodRoi, _waterRoi;
    private SurvivalRing _foodRing, _waterRing;
    private Bitmap _foodLow, _foodHigh, _waterLow, _waterHigh;
    private bool _pickingFood = true;
    private bool _dragging;
    private Point _dragStart;
    private Rectangle _dragRect;
    private double _scale = 1;
    private bool _syncing;

    private SurvivalWizardForm(Bitmap still, Screen screen, ElectricConfig cfg, ElectricProfile profile)
    {
        _still = still;
        _screen = screen;
        _cfg = cfg;
        _profile = profile;
        _s = new NavScale(still.Width, still.Height, cfg.Nav.ScreenPxScale);

        if (profile.SurvivalHud.FoodRoi.IsSet)
            _foodRoi = profile.SurvivalHud.FoodRoi.ToRectangle();
        if (profile.SurvivalHud.WaterRoi.IsSet)
            _waterRoi = profile.SurvivalHud.WaterRoi.ToRectangle();

        Text = "Hiệu chuẩn ăn uống";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ClientSize = new Size(1240, 900);
        MinimumSize = new Size(980, 720);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi();
        LearnFromCurrent();
        RefreshReadout();
    }

    /// <summary>True = đã lưu profile HUD (chưa gồm test phím).</summary>
    public static bool Run(IWin32Window owner, Bitmap still, Screen screen,
        ElectricConfig cfg, ElectricProfile profile)
    {
        using var f = new SurvivalWizardForm(still, screen, cfg, profile);
        return f.ShowDialog(owner) == DialogResult.OK;
    }

    private void BuildUi()
    {
        _hint.AutoSize = false;
        _hint.ForeColor = Color.DimGray;
        _hint.SetBounds(12, 8, 1216, 36);
        _hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_hint);

        _canvas.SetBounds(12, 48, 1216, 560);
        _canvas.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _canvas.BorderStyle = BorderStyle.FixedSingle;
        _canvas.BackColor = Color.FromArgb(30, 32, 36);
        _canvas.Paint += (_, e) => PaintCanvas(e.Graphics);
        _canvas.MouseDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseUp += OnUp;
        _canvas.Resize += (_, _) => _canvas.Invalidate();
        Controls.Add(_canvas);

        int y = 618;
        AddNum(_foodCx, "Bánh X", 12, y, _still.Width);
        AddNum(_foodCy, "Y", 132, y, _still.Height);
        AddNum(_foodR0, "r0", 252, y, 400);
        AddNum(_foodR1, "r1", 372, y, 400);
        AddNum(_waterCx, "Nước X", 520, y, _still.Width);
        AddNum(_waterCy, "Y", 640, y, _still.Height);
        AddNum(_waterR0, "r0", 760, y, 400);
        AddNum(_waterR1, "r1", 880, y, 400);

        _lowFood.Text = "Chụp LOW bánh";
        _highFood.Text = "Chụp HIGH bánh";
        _lowWater.Text = "Chụp LOW nước";
        _highWater.Text = "Chụp HIGH nước";
        _lowFood.SetBounds(12, 668, 140, 28);
        _highFood.SetBounds(158, 668, 140, 28);
        _lowWater.SetBounds(320, 668, 140, 28);
        _highWater.SetBounds(466, 668, 148, 28);
        _lowFood.Click += (_, _) => CaptureSample(food: true, high: false);
        _highFood.Click += (_, _) => CaptureSample(food: true, high: true);
        _lowWater.Click += (_, _) => CaptureSample(food: false, high: false);
        _highWater.Click += (_, _) => CaptureSample(food: false, high: true);
        foreach (var b in new[] { _lowFood, _highFood, _lowWater, _highWater })
        {
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(b);
        }

        _readout.SetBounds(12, 706, 980, 110);
        _readout.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _readout.Font = new Font("Consolas", 10F);
        Controls.Add(_readout);

        _ok.Text = "Lưu hiệu chuẩn HUD";
        _ok.SetBounds(900, 850, 180, 32);
        _ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _ok.Click += (_, _) => TrySave();
        Controls.Add(_ok);

        var cancel = new Button { Text = "Huỷ", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(1090, 850, 108, 32);
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Controls.Add(cancel);
        CancelButton = cancel;
    }

    private void AddNum(NumericUpDown n, string label, int x, int y, int max)
    {
        var lab = new Label { Text = label, AutoSize = true, Location = new Point(x, y - 16) };
        lab.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(lab);
        n.Minimum = 0;
        n.Maximum = Math.Max(1, max);
        n.DecimalPlaces = 0;
        n.SetBounds(x, y, 108, 24);
        n.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        n.ValueChanged += (_, _) => OnGeomChanged();
        Controls.Add(n);
    }

    private Rectangle ImageRectOnCanvas()
    {
        int cw = Math.Max(1, _canvas.ClientSize.Width);
        int ch = Math.Max(1, _canvas.ClientSize.Height);
        _scale = Math.Min(1.0, Math.Min(cw / (double)_still.Width, ch / (double)_still.Height));
        return new Rectangle(0, 0,
            (int)Math.Round(_still.Width * _scale),
            (int)Math.Round(_still.Height * _scale));
    }

    private Point ToImage(Point canvas) => new(
        Math.Clamp((int)Math.Round(canvas.X / _scale), 0, _still.Width - 1),
        Math.Clamp((int)Math.Round(canvas.Y / _scale), 0, _still.Height - 1));

    private Rectangle ToCanvas(Rectangle img) => new(
        (int)Math.Round(img.X * _scale),
        (int)Math.Round(img.Y * _scale),
        (int)Math.Round(img.Width * _scale),
        (int)Math.Round(img.Height * _scale));

    private void PaintCanvas(Graphics g)
    {
        var dest = ImageRectOnCanvas();
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(_still, dest);
        if (_foodRoi is { } fr) DrawRoi(g, fr, Color.FromArgb(255, 200, 40), "BÁNH");
        if (_waterRoi is { } wr) DrawRoi(g, wr, Color.FromArgb(40, 210, 220), "NƯỚC");
        if (_foodRing is not null) DrawRing(g, _foodRing, Color.FromArgb(255, 200, 40));
        if (_waterRing is not null) DrawRing(g, _waterRing, Color.FromArgb(40, 210, 220));
        if (_dragging && _dragRect.Width > 4)
        {
            using var p = new Pen(Color.White, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(p, ToCanvas(Square(_dragRect)));
        }
    }

    private void DrawRoi(Graphics g, Rectangle img, Color c, string label)
    {
        var r = ToCanvas(img);
        using var p = new Pen(c, 2);
        g.DrawRectangle(p, r);
        using var font = new Font("Segoe UI", 8F, FontStyle.Bold);
        using var br = new SolidBrush(c);
        g.DrawString(label, font, br, r.X, r.Y - 14);
    }

    private void DrawRing(Graphics g, SurvivalRing ring, Color c)
    {
        var p0 = new Point(
            (int)Math.Round(ring.Cx * _scale),
            (int)Math.Round(ring.Cy * _scale));
        float r0 = (float)(ring.Rmin * _scale);
        float r1 = (float)(ring.Rmax * _scale);
        using var pen = new Pen(c, 1);
        g.DrawEllipse(pen, p0.X - r0, p0.Y - r0, r0 * 2, r0 * 2);
        g.DrawEllipse(pen, p0.X - r1, p0.Y - r1, r1 * 2, r1 * 2);
        g.DrawLine(pen, p0.X - 8, p0.Y, p0.X + 8, p0.Y);
        g.DrawLine(pen, p0.X, p0.Y - 8, p0.X, p0.Y + 8);
    }

    private void OnDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ImageRectOnCanvas();
        _dragging = true;
        _dragStart = ToImage(e.Location);
        _dragRect = new Rectangle(_dragStart, Size.Empty);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = ToImage(e.Location);
        _dragRect = new Rectangle(
            Math.Min(_dragStart.X, p.X), Math.Min(_dragStart.Y, p.Y),
            Math.Abs(p.X - _dragStart.X), Math.Abs(p.Y - _dragStart.Y));
        _canvas.Invalidate();
    }

    private void OnUp(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var sq = Square(_dragRect);
        if (sq.Width < 36)
        {
            _canvas.Invalidate();
            return;
        }
        if (_pickingFood || _foodRoi is null)
        {
            _foodRoi = sq;
            _pickingFood = false;
        }
        else
        {
            _waterRoi = sq;
        }
        LearnFromCurrent();
        RefreshReadout();
        _canvas.Invalidate();
    }

    private static Rectangle Square(Rectangle r)
    {
        int side = Math.Max(r.Width, r.Height);
        if (side < 8) return r;
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return new Rectangle(cx - side / 2, cy - side / 2, side, side);
    }

    private void LearnFromCurrent()
    {
        var frame = NavFrame.FromBitmap(_still, new Rectangle(0, 0, _still.Width, _still.Height));
        if (_foodRoi is { } fr && SurvivalCalibrator.TryLearnGeometry(frame, fr, food: true, out var food))
            _foodRing = food;
        if (_waterRoi is { } wr && SurvivalCalibrator.TryLearnGeometry(frame, wr, food: false, out var water))
            _waterRing = water;
        SyncNums();
    }

    private void SyncNums()
    {
        _syncing = true;
        SetNum(_foodCx, _foodRing?.Cx ?? 0);
        SetNum(_foodCy, _foodRing?.Cy ?? 0);
        SetNum(_foodR0, _foodRing?.Rmin ?? 0);
        SetNum(_foodR1, _foodRing?.Rmax ?? 0);
        SetNum(_waterCx, _waterRing?.Cx ?? 0);
        SetNum(_waterCy, _waterRing?.Cy ?? 0);
        SetNum(_waterR0, _waterRing?.Rmin ?? 0);
        SetNum(_waterR1, _waterRing?.Rmax ?? 0);
        _syncing = false;
    }

    private static void SetNum(NumericUpDown n, double v)
    {
        decimal d = (decimal)Math.Clamp(v, (double)n.Minimum, (double)n.Maximum);
        n.Value = d;
    }

    private void OnGeomChanged()
    {
        if (_syncing) return;
        _foodRing ??= new SurvivalRing();
        _waterRing ??= new SurvivalRing();
        _foodRing.Cx = (double)_foodCx.Value;
        _foodRing.Cy = (double)_foodCy.Value;
        _foodRing.Rmin = (double)_foodR0.Value;
        _foodRing.Rmax = Math.Max((double)_foodR0.Value + 2, (double)_foodR1.Value);
        _waterRing.Cx = (double)_waterCx.Value;
        _waterRing.Cy = (double)_waterCy.Value;
        _waterRing.Rmin = (double)_waterR0.Value;
        _waterRing.Rmax = Math.Max((double)_waterR0.Value + 2, (double)_waterR1.Value);
        RefreshReadout();
        _canvas.Invalidate();
    }

    private void CaptureSample(bool food, bool high)
    {
        if ((food ? _foodRoi : _waterRoi) is null || (food ? _foodRing : _waterRing) is null)
        {
            MessageBox.Show(this, "Khoanh đồng hồ và chỉnh tâm/bán kính trước.", Text);
            return;
        }

        string who = food ? "bánh" : "nước";
        string kind = high ? "HIGH — dùng vật phẩm rồi để vành đầy" : "LOW — mắt thấy dưới 40%";
        var bmp = StillPicker.CaptureWithCountdown(this, _screen,
            $"Cùng góc nhìn với ảnh đang khoanh. {kind} đồng hồ {who}.",
            _cfg.ShotCountdownSec, _cfg.WindowMatch, out string problem);
        if (bmp is null)
        {
            MessageBox.Show(this, problem ?? "không chụp được", Text);
            return;
        }

        try
        {
            if (food && !high) Replace(ref _foodLow, bmp);
            else if (food) Replace(ref _foodHigh, bmp);
            else if (!high) Replace(ref _waterLow, bmp);
            else Replace(ref _waterHigh, bmp);
            RefreshReadout();
        }
        catch (Exception ex)
        {
            bmp.Dispose();
            MessageBox.Show(this, ex.Message, Text);
        }
    }

    private static void Replace(ref Bitmap slot, Bitmap next)
    {
        slot?.Dispose();
        slot = next;
    }

    private void RefreshReadout()
    {
        _hint.Text = _foodRoi is null
            ? "Kéo một hình vuông ôm trọn đồng hồ BÁNH (vàng)."
            : _waterRoi is null
                ? "Kéo một hình vuông ôm trọn đồng hồ NƯỚC (xanh)."
                : "Chỉnh tâm/bán kính nếu lệch. Chụp LOW rồi HIGH từng đồng hồ — cùng góc camera trong một lượt.";

        var still = NavFrame.FromBitmap(_still, new Rectangle(0, 0, _still.Width, _still.Height));
        string foodNow = Preview(still, _foodRing);
        string waterNow = Preview(still, _waterRing);
        var (foodOk, foodNote) = EvaluatePair(_foodLow, _foodHigh, _foodRing, food: true);
        var (waterOk, waterNote) = EvaluatePair(_waterLow, _waterHigh, _waterRing, food: false);

        _readout.Text =
            $"ảnh khoanh: bánh {foodNow}   nước {waterNow}\r\n" +
            $"bánh: {foodNote}\r\n" +
            $"nước: {waterNote}\r\n" +
            "Chỉ lưu khi cả hai cặp LOW/HIGH tạo cung liên tục và HIGH lớn hơn LOW rõ ràng.";
        _readout.ForeColor = foodOk && waterOk ? Color.FromArgb(20, 120, 50) : Color.FromArgb(50, 50, 50);
        _ok.Enabled = foodOk && waterOk && _foodRoi is not null && _waterRoi is not null;
    }

    private static string Preview(NavFrame frame, SurvivalRing ring)
    {
        if (ring is null) return "chưa có hình học";
        var a = SurvivalPolar.Read(frame, ring);
        return a.Valid ? $"{a.Pct:F0}% conf={a.Confidence:F2} mảnh={a.Fragments}" : "không đọc được";
    }

    private (bool ok, string note) EvaluatePair(Bitmap lowBmp, Bitmap highBmp, SurvivalRing geo, bool food)
    {
        if (geo is null) return (false, "chưa khoanh");
        if (lowBmp is null && highBmp is null) return (false, "chưa chụp LOW/HIGH");
        if (lowBmp is null) return (false, "thiếu LOW");
        if (highBmp is null) return (false, "thiếu HIGH");

        var lowF = NavFrame.FromBitmap(lowBmp, new Rectangle(0, 0, lowBmp.Width, lowBmp.Height));
        var highF = NavFrame.FromBitmap(highBmp, new Rectangle(0, 0, highBmp.Width, highBmp.Height));
        if (!SurvivalCalibrator.TryLearnColor(lowF, highF, geo, food, out var learned))
            return (false, "không học được màu từ hai mẫu");

        var low = SurvivalPolar.Read(lowF, learned);
        var high = SurvivalPolar.Read(highF, learned);
        string note = $"LOW {(low.Valid ? $"{low.Pct:F0}%" : "?")} → HIGH {(high.Valid ? $"{high.Pct:F0}%" : "?")}";
        if (!SurvivalCalibrator.SamplesAcceptable(low, high))
            return (false, note + " — chưa đạt (LOW phải dưới 55%, HIGH trên 55% và cách ≥ 15 điểm)");
        return (true, note + " — đạt");
    }

    private void TrySave()
    {
        if (!_ok.Enabled) return;
        var hud = _profile.SurvivalHud ?? new SurvivalHudProfile();
        hud.FoodRoi = FishingRect.FromRelative(_foodRoi!.Value);
        hud.WaterRoi = FishingRect.FromRelative(_waterRoi!.Value);

        var lowFood = NavFrame.FromBitmap(_foodLow, new Rectangle(0, 0, _foodLow.Width, _foodLow.Height));
        var highFood = NavFrame.FromBitmap(_foodHigh, new Rectangle(0, 0, _foodHigh.Width, _foodHigh.Height));
        var lowWater = NavFrame.FromBitmap(_waterLow, new Rectangle(0, 0, _waterLow.Width, _waterLow.Height));
        var highWater = NavFrame.FromBitmap(_waterHigh, new Rectangle(0, 0, _waterHigh.Width, _waterHigh.Height));
        if (!SurvivalCalibrator.TryLearnColor(lowFood, highFood, _foodRing, true, out var foodRing)) return;
        if (!SurvivalCalibrator.TryLearnColor(lowWater, highWater, _waterRing, false, out var waterRing)) return;

        hud.ApplyRing(true, foodRing);
        hud.ApplyRing(false, waterRing);
        hud.FoodHudReady = true;
        hud.WaterHudReady = true;
        hud.FoodSlotVerified = false;
        hud.WaterSlotVerified = false;
        hud.FoodVerifiedSlots = "";
        hud.WaterVerifiedSlots = "";
        hud.Normalize();
        _profile.SurvivalHud = hud;

        _cfg.Survival.FoodCenterXRef = foodRing.Cx * ElectricConfig.RefW / _still.Width;
        _cfg.Survival.FoodCenterYRef = foodRing.Cy * ElectricConfig.RefH / _still.Height;
        _cfg.Survival.WaterCenterXRef = waterRing.Cx * ElectricConfig.RefW / _still.Width;
        _cfg.Survival.WaterCenterYRef = waterRing.Cy * ElectricConfig.RefH / _still.Height;
        _cfg.Survival.Normalize();

        Directory.CreateDirectory(ElectricConfig.SurvivalDir(_profile.Key));
        StillPicker.Save(_foodLow, ElectricConfig.SurvivalSamplePath(_profile.Key, "food-low"));
        StillPicker.Save(_foodHigh, ElectricConfig.SurvivalSamplePath(_profile.Key, "food-high"));
        StillPicker.Save(_waterLow, ElectricConfig.SurvivalSamplePath(_profile.Key, "water-low"));
        StillPicker.Save(_waterHigh, ElectricConfig.SurvivalSamplePath(_profile.Key, "water-high"));
        SaveCrop(_foodLow, _foodRoi.Value, ElectricConfig.SurvivalSamplePath(_profile.Key, "food-low-crop"));
        SaveCrop(_foodHigh, _foodRoi.Value, ElectricConfig.SurvivalSamplePath(_profile.Key, "food-high-crop"));
        SaveCrop(_waterLow, _waterRoi.Value, ElectricConfig.SurvivalSamplePath(_profile.Key, "water-low-crop"));
        SaveCrop(_waterHigh, _waterRoi.Value, ElectricConfig.SurvivalSamplePath(_profile.Key, "water-high-crop"));
        SaveCrop(_still, Rectangle.Union(_foodRoi.Value, _waterRoi.Value),
            ElectricConfig.SurvivalSamplePath(_profile.Key, "roi"));

        DialogResult = DialogResult.OK;
    }

    private static void SaveCrop(Bitmap src, Rectangle roi, string path)
    {
        var r = Rectangle.Intersect(roi, new Rectangle(0, 0, src.Width, src.Height));
        if (r.Width < 4 || r.Height < 4) return;
        using var crop = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        StillPicker.Save(crop, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _foodLow?.Dispose();
            _foodHigh?.Dispose();
            _waterLow?.Dispose();
            _waterHigh?.Dispose();
        }
        base.Dispose(disposing);
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

/// <summary>Phép thử phím hotbar: đo LOW ổn định → bấm → chờ animation → xác nhận HIGH ổn định.</summary>
internal static class SurvivalHotbarTest
{
    public static bool Run(Form owner, Screen screen, ElectricConfig cfg, ElectricProfile profile,
        bool food, Action<string> log)
    {
        var hud = profile.SurvivalHud;
        if (hud is null || !hud.IsHudReady)
        {
            log("chưa hiệu chuẩn HUD bánh/nước — chạy wizard trước");
            return false;
        }

        char slot = cfg.Survival.PrimarySlot(food);
        string who = food ? "bánh" : "nước";
        var ask = MessageBox.Show(owner,
            $"Test {who} phím {slot}. Đồng hồ đang thấp (dưới 50%). " +
            $"Bấm OK rồi click vào game trong {cfg.ShotCountdownSec}s.",
            "Test hotbar ăn uống", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (ask != DialogResult.OK) { log("đã huỷ test " + who); return false; }

        bool wasVisible = owner is { Visible: true };
        if (wasVisible) owner.Hide();

        try
        {
            var s = new NavScale(screen.Bounds.Width, screen.Bounds.Height, cfg.Nav.ScreenPxScale);
            var rel = cfg.Survival.CaptureRoi(s, hud);
            if (rel.IsEmpty) { log("ROI ăn uống rỗng"); return false; }
            var abs = new Rectangle(screen.Bounds.X + rel.X, screen.Bounds.Y + rel.Y, rel.Width, rel.Height);
            using var reader = new RegionReader(abs);
            var gauge = new SurvivalGauge(cfg.Survival, s, hud);
            var watch = new SurvivalUseWatch();

            StillPicker.WaitFocus(screen, cfg.ShotCountdownSec);
            if (!FocusOk(cfg.WindowMatch, out string why)) { log(why); return false; }

            double t = 0;
            SurvivalReading last = SurvivalReading.None;
            for (int i = 0; i < NavTuning.SurvivalMedianWindow + 2; i++)
            {
                last = Pulse(reader, rel, gauge, t);
                t += NavTuning.SurvivalScanIntervalS;
                Thread.Sleep((int)(NavTuning.SurvivalScanIntervalS * 1000));
                Application.DoEvents();
            }

            double? before = food
                ? (last.FoodValid ? last.FoodPct : null)
                : (last.WaterValid ? last.WaterPct : null);
            if (before is null)
            {
                log($"test {who}: không đọc được đồng hồ trước khi bấm");
                return false;
            }

            log($"test {who}: mốc {before.Value:F0}% → bấm phím {slot}");
            InputSender.KeyDown((ushort)slot);
            Thread.Sleep((int)(NavTuning.SurvivalKeyHoldS * 1000));
            InputSender.KeyUp((ushort)slot);

            gauge.BeginUse();
            watch.Start(before.Value, t);
            double end = t + NavTuning.SurvivalUseTimeoutS + 0.5;
            while (t < end)
            {
                last = Pulse(reader, rel, gauge, t);
                double? stable = food
                    ? (last.FoodValid ? last.FoodPct : null)
                    : (last.WaterValid ? last.WaterPct : null);
                var v = watch.Observe(stable, t, out double after);
                if (v == SurvivalUseVerdict.Success)
                {
                    hud.MarkSlotVerified(food, slot);
                    log($"test {who}: ĐẠT phím {slot} {before.Value:F0}% → {after:F0}%");
                    return true;
                }
                if (v == SurvivalUseVerdict.Failed)
                {
                    log($"test {who}: phím {slot} không có tác dụng ({before.Value:F0}% → {(double.IsNaN(after) ? "?" : after.ToString("F0") + "%")})");
                    return false;
                }
                t += NavTuning.SurvivalScanIntervalS;
                Thread.Sleep((int)(NavTuning.SurvivalScanIntervalS * 1000));
                Application.DoEvents();
            }

            log($"test {who}: hết giờ — phím {slot} không có tác dụng");
            return false;
        }
        finally
        {
            if (wasVisible)
            {
                owner.Show();
                owner.Activate();
            }
        }
    }

    private static SurvivalReading Pulse(RegionReader reader, Rectangle rel, SurvivalGauge gauge, double t)
    {
        reader.Refresh();
        var frame = new NavFrame
        {
            Bgra = reader.Raw, Stride = reader.Stride,
            Width = rel.Width, Height = rel.Height,
            OriginX = rel.X, OriginY = rel.Y, T = t
        };
        return gauge.Update(frame, t);
    }

    private static bool FocusOk(string match, out string why)
    {
        why = null;
        string title = Native.ForegroundTitle();
        if (!string.IsNullOrWhiteSpace(match)
            && !title.Contains(match, StringComparison.OrdinalIgnoreCase))
        {
            why = $"cửa sổ đang focus là “{title}”, không phải “{match}”";
            return false;
        }
        return true;
    }

}
