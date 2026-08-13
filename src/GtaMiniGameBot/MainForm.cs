using System.Text;

namespace GtaMiniGameBot;

internal sealed class MainForm : Form
{
    private const int HOTKEY_START = 1, HOTKEY_STOP = 2;
    private const uint VK_F8 = 0x77, VK_F9 = 0x78;

    private readonly BotConfig _cfg = BotConfig.Load();
    private OilWellBot _bot;
    private MiniGameReader _monitor;

    private readonly Label _status = new();
    private readonly Label[] _barLabels = new Label[4];
    private readonly Label _panelLabel = new();
    private readonly Label _greenLabel = new();
    private readonly Label _progressLabel = new();
    private readonly Label _carLabel = new();
    private readonly CheckBox _carReset = new();
    private readonly NumericUpDown _carResetSec = new();
    private readonly NumericUpDown _afterEnter = new();
    private readonly NumericUpDown _afterExit = new();
    private readonly TextBox _log = new();
    private readonly CheckBox _watch = new();
    private readonly CheckBox _jitter = new();
    private readonly NumericUpDown _maxCycles = new();
    private readonly Button _btnCalibrate = new();
    private readonly Button _btnCarTemplate = new();
    private readonly Button _btnOneCycle = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly Button _btnDebug = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    public MainForm()
    {
        Text = "GtaMiniGameBot — Giếng Khoan Dầu";
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(820, 760);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(740, 580);

        BuildUi();

        _timer.Interval = 150;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Append($"cấu hình: thanh x = {string.Join(", ", _cfg.BarX)}   |   thân thanh y = {_cfg.BarYTop}…{_cfg.BarYBottom}");
        Append($"ngưỡng: đầy ≥ {_cfg.FullThreshold}   |   coi là đã reset < {_cfg.ResetThreshold}   |   " +
               $"panel mở khi nổi lên ≥ {_cfg.PanelBarProminenceMin}");
        Append("F8 = bắt đầu   |   F9 = dừng.");
        Append("Ở giàn khoan mới: bấm “Hiệu chỉnh” một lần để kiểm — phải ra 4 toạ độ với độ nổi ≥ 30.");
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        int y = 12;
        int w = ClientSize.Width - 24;

        _status.SetBounds(12, y, w, 30);
        _status.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        _status.Text = "Đang dừng";
        Controls.Add(_status);
        y += 38;

        var box = new GroupBox { Text = "Đọc màn hình", Location = new Point(12, y), Size = new Size(w, 160) };
        Controls.Add(box);

        for (int i = 0; i < 4; i++)
        {
            _barLabels[i] = new Label
            {
                Location = new Point(16, 24 + i * 22),
                Size = new Size(380, 20),
                Font = new Font("Consolas", 10F),
                Text = $"thanh {i + 1}: --"
            };
            box.Controls.Add(_barLabels[i]);
        }

        _panelLabel.SetBounds(410, 24, 370, 20);
        _panelLabel.Font = new Font("Consolas", 10F);
        box.Controls.Add(_panelLabel);

        _greenLabel.SetBounds(410, 46, 370, 20);
        _greenLabel.Font = new Font("Consolas", 10F);
        box.Controls.Add(_greenLabel);

        _progressLabel.SetBounds(410, 68, 370, 20);
        _progressLabel.Font = new Font("Consolas", 10F);
        _progressLabel.Text = "chu kỳ 0  |  thùng 0";
        box.Controls.Add(_progressLabel);

        _carLabel.SetBounds(16, 112, 380, 20);
        _carLabel.Font = new Font("Consolas", 10F);
        _carLabel.Text = "trạng thái : --";
        box.Controls.Add(_carLabel);

        _watch.SetBounds(410, 94, 260, 22);
        _watch.Text = "Theo dõi (chỉ đọc, không bấm)";
        _watch.Checked = true;
        box.Controls.Add(_watch);

        _carReset.SetBounds(410, 120, 220, 22);
        _carReset.Text = "Reset xe thuê mỗi … giây:";
        _carReset.Checked = _cfg.CarResetEnabled;
        box.Controls.Add(_carReset);

        _carResetSec.SetBounds(638, 118, 70, 24);
        _carResetSec.Minimum = 60;
        _carResetSec.Maximum = 480;
        _carResetSec.Value = Math.Clamp(_cfg.CarResetEverySec, 60, 480);
        box.Controls.Add(_carResetSec);

        y += 170;

        // Hai khoang cho cua chuoi reset xe - de sua duoc khi server lag.
        Controls.Add(new Label { Text = "Chờ sau khi lên xe (ms):", Location = new Point(14, y + 4), AutoSize = true });
        _afterEnter.SetBounds(180, y, 80, 24);
        _afterEnter.Minimum = 500; _afterEnter.Maximum = 15000; _afterEnter.Increment = 250;
        _afterEnter.Value = Math.Clamp(_cfg.AfterEnterCarMs, 500, 15000);
        Controls.Add(_afterEnter);

        Controls.Add(new Label { Text = "Chờ sau khi xuống xe (ms):", Location = new Point(290, y + 4), AutoSize = true });
        _afterExit.SetBounds(470, y, 80, 24);
        _afterExit.Minimum = 500; _afterExit.Maximum = 15000; _afterExit.Increment = 250;
        _afterExit.Value = Math.Clamp(_cfg.AfterExitCarMs, 500, 15000);
        Controls.Add(_afterExit);

        y += 34;

        _btnCalibrate.SetBounds(12, y, 110, 32);
        _btnCalibrate.Text = "Hiệu chỉnh";
        _btnCalibrate.Click += (_, _) => DoCalibrate();
        Controls.Add(_btnCalibrate);

        _btnCarTemplate.SetBounds(128, y, 170, 32);
        _btnCarTemplate.Text = "Chụp mẫu đồng hồ xe";
        _btnCarTemplate.Click += (_, _) => DoCaptureCarTemplate();
        Controls.Add(_btnCarTemplate);

        _btnOneCycle.SetBounds(304, y, 150, 32);
        _btnOneCycle.Text = "Chạy thử 1 chu kỳ";
        _btnOneCycle.Click += (_, _) => StartBot(oneCycle: true);
        Controls.Add(_btnOneCycle);

        _btnStart.SetBounds(460, y, 160, 32);
        _btnStart.Text = "Bắt đầu cày  (F8)";
        _btnStart.Click += (_, _) => StartBot(oneCycle: false);
        Controls.Add(_btnStart);

        _btnStop.SetBounds(626, y, 110, 32);
        _btnStop.Text = "Dừng  (F9)";
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => _bot?.Stop();
        Controls.Add(_btnStop);

        y += 42;

        _jitter.SetBounds(14, y, 280, 22);
        _jitter.Text = "Nhiễu ngẫu nhiên (toạ độ + nhịp nghỉ)";
        Controls.Add(_jitter);

        Controls.Add(new Label
        {
            Text = "Dừng sau N chu kỳ (0 = không giới hạn):",
            Location = new Point(310, y + 2), AutoSize = true
        });
        _maxCycles.SetBounds(560, y, 80, 24);
        _maxCycles.Maximum = 10000;
        _maxCycles.Value = 0;
        Controls.Add(_maxCycles);

        _btnDebug.SetBounds(660, y - 4, 136, 28);
        _btnDebug.Text = "Mở thư mục bằng chứng";
        _btnDebug.Click += (_, _) => OpenDebugFolder();
        Controls.Add(_btnDebug);

        y += 34;

        _log.SetBounds(12, y, w, ClientSize.Height - y - 12);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_log);
    }

    // ---------------------------------------------------------------- hotkey

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!Native.RegisterHotKey(Handle, HOTKEY_START, 0, VK_F8))
            Append("cảnh báo: không đăng ký được F8 (có thể app khác đang giữ phím này)");
        if (!Native.RegisterHotKey(Handle, HOTKEY_STOP, 0, VK_F9))
            Append("cảnh báo: không đăng ký được F9");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_START && _bot is null or { Running: false }) StartBot(oneCycle: false);
            else if (id == HOTKEY_STOP) _bot?.Stop();
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _bot?.Stop();
        try { InputSender.LeftUp(); } catch { }   // khong bao gio de chuot ket
        Native.UnregisterHotKey(Handle, HOTKEY_START);
        Native.UnregisterHotKey(Handle, HOTKEY_STOP);
        _monitor?.Dispose();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- hanh dong

    private void DoCalibrate()
    {
        try
        {
            var r = Calibrator.FromScreen(_cfg);
            Append($"hiệu chỉnh: median={r.Median:F1}, ngưỡng={r.Threshold:F1}, tìm được {r.Clusters.Count} cụm");
            foreach (var c in r.Clusters)
                Append($"    cụm x {c.Lo}…{c.Hi}  tâm {c.Center}  đỉnh {c.Peak:F1}  nổi lên {c.Prominence:F1}");

            if (!r.Ok) { Append("hiệu chỉnh THẤT BẠI: " + r.Note + " — giữ nguyên toạ độ cũ"); return; }

            Append($"hiệu chỉnh OK: {string.Join(", ", r.Centers)} " +
                   $"(khoảng cách {r.Spacing:F1}, lệch nội bộ {r.Deviation:F2})");

            var old = string.Join(", ", _cfg.BarX);
            if (MessageBox.Show(
                    $"Toạ độ cũ:  {old}\nToạ độ mới: {string.Join(", ", r.Centers)}\n\nDùng toạ độ mới?",
                    "Hiệu chỉnh", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _cfg.BarX = r.Centers;
                _cfg.Save();
                _monitor?.Dispose();
                _monitor = null;
                Append("đã lưu toạ độ mới vào config.json");
            }
        }
        catch (Exception ex) { Append("hiệu chỉnh lỗi: " + ex.Message); }
    }

    /// <summary>
    /// Chup mau dong ho toc do. Phai bam KHI DANG NGOI TRONG XE.
    /// </summary>
    private void DoCaptureCarTemplate()
    {
        string overlap = ProbeOverlapWarning();
        if (overlap is not null)
        {
            Append(overlap);
            MessageBox.Show(overlap, "Cửa sổ app đang che vùng đọc", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var r = _cfg.CarProbe;
        if (MessageBox.Show(
                "Bạn PHẢI đang ngồi trong xe, và đồng hồ tốc độ đang hiện rõ.\n\n" +
                $"Ô sẽ chụp: x {r.Left}…{r.Right - 1}, y {r.Top}…{r.Bottom - 1}\n\n" +
                "Chụp bây giờ?", "Chụp mẫu đồng hồ xe",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try
        {
            var t = GrayTemplate.FromScreen(r);
            if (t.IsFlat) { Append("mẫu phẳng tuyệt đối — ô đó không có gì, không lưu"); return; }

            t.Save(_cfg.CarTemplateFullPath);
            Append($"đã lưu mẫu {t.Width}×{t.Height} → {_cfg.CarTemplateFullPath}");
            Append($"giờ tự xuống xe và xem “ncc” có tụt xuống dưới {_cfg.CarNccOut:F2} không; " +
                   $"trong xe phải ≥ {_cfg.CarNccIn:F2}. (Số này chỉ để tham khảo, không dùng quyết định.)");

            _monitor?.Dispose();
            _monitor = null;   // nap lai mau o lan doc ke tiep
        }
        catch (Exception ex) { Append("chụp mẫu lỗi: " + ex.Message); }
    }

    private void OpenDebugFolder()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "debug");
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Append("không mở được thư mục bằng chứng: " + ex.Message); }
    }

    /// <summary>
    /// Cua so app de len vung do thi moi phep do se doc pixel cua chinh app.
    /// Kiem truoc khi chay - loai hang mot nguyen nhan rat kho doan.
    /// </summary>
    private string ProbeOverlapWarning()
    {
        var mine = Bounds;
        var zones = new (string name, Rectangle r)[]
        {
            ("dải 4 thanh", _cfg.BarRegion),
            ("vùng số thùng", _cfg.CounterRegion),
            ("vùng đồng hồ xe", _cfg.CarProbe),
        };
        var hit = zones.Where(z => mine.IntersectsWith(z.r)).Select(z => z.name).ToArray();
        return hit.Length == 0
            ? null
            : $"cửa sổ app đang che: {string.Join(", ", hit)}. Kéo app ra khỏi các vùng đó rồi thử lại " +
              $"(cửa sổ đang ở {mine.Left},{mine.Top} — {mine.Width}×{mine.Height}).";
    }

    private void StartBot(bool oneCycle)
    {
        if (_bot is { Running: true }) return;

        string overlap = ProbeOverlapWarning();
        if (overlap is not null)
        {
            Append("KHÔNG chạy: " + overlap);
            MessageBox.Show(overlap, "Cửa sổ app đang che vùng đọc", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cfg.MaxCycles = oneCycle ? 1 : (int)_maxCycles.Value;
        _cfg.CarResetEnabled = _carReset.Checked;
        _cfg.CarResetEverySec = (int)_carResetSec.Value;
        _cfg.AfterEnterCarMs = (int)_afterEnter.Value;
        _cfg.AfterExitCarMs = (int)_afterExit.Value;

        _bot = new OilWellBot(_cfg) { Jitter = _jitter.Checked };
        _bot.Log += s => Post(() => Append(s));
        _bot.SnapshotReady += s => Post(() => ShowSnapshot(s));
        _bot.Progress += (c, b) => Post(() => _progressLabel.Text = $"chu kỳ {c}  |  thùng {b}");
        _bot.Stopped += (r, msg) => Post(() =>
        {
            _status.Text = $"Đã dừng — {OilWellBot.TenLyDo(r)}";
            _status.ForeColor = r is StopReason.UserStopped or StopReason.InventoryFullNoIncrement
                                     or StopReason.InventoryFullNoReset or StopReason.MaxCyclesReached
                ? Color.DarkGreen : Color.Firebrick;
            SetRunningUi(false);
        });

        _status.Text = oneCycle ? "Đang chạy — 1 chu kỳ" : "Đang cày";
        _status.ForeColor = Color.DarkBlue;
        SetRunningUi(true);
        Append(oneCycle ? "--- chạy thử 1 chu kỳ ---" : "--- bắt đầu cày ---");
        _bot.Start();
    }

    private void SetRunningUi(bool running)
    {
        _btnStart.Enabled = !running;
        _btnOneCycle.Enabled = !running;
        _btnCalibrate.Enabled = !running;
        _btnCarTemplate.Enabled = !running;
        _btnStop.Enabled = running;
    }

    // ---------------------------------------------------------------- theo doi

    private void Tick()
    {
        if (_bot is { Running: true }) return;   // bot dang chay thi no tu cap nhat
        if (!_watch.Checked) return;

        try
        {
            if (_monitor is null)
            {
                _monitor = new MiniGameReader(_cfg);
                if (_monitor.CarTemplateProblem is { } p) Append("mẫu đồng hồ xe: " + p);
            }
            ShowSnapshot(_monitor.Read());
        }
        catch (Exception ex)
        {
            _panelLabel.Text = "lỗi đọc: " + ex.Message;
        }
    }

    private void ShowSnapshot(Snapshot s)
    {
        for (int i = 0; i < _barLabels.Length && i < s.BarMin.Length; i++)
        {
            bool full = s.BarFull[i];
            _barLabels[i].Text = $"thanh {i + 1}  x={_cfg.BarX[i],4}  nhỏ nhất={s.BarMin[i],4}  " +
                                 (full ? "ĐẦY" : "chưa");
            _barLabels[i].ForeColor = full ? Color.DarkGreen : Color.DimGray;
        }

        _panelLabel.Text = $"panel : {(s.PanelOpen ? "MỞ" : "ĐÓNG")}  (nổi lên={s.PanelProminence:F1})";
        _panelLabel.ForeColor = s.PanelOpen ? Color.DarkGreen : Color.Firebrick;

        _greenLabel.Text = $"pixel xanh (số thùng) : {s.GreenCount}";

        string carAge = _bot is { Running: true } && _cfg.CarResetEnabled
            ? $"   | reset xe sau {Math.Max(0, _cfg.CarResetEverySec - _bot.SecondsSinceCarReset)}s"
            : "";
        _carLabel.Text = $"trạng thái : {s.StateName}  (ncc={s.CarScore:F3}){carAge}";
        _carLabel.ForeColor = s.Vehicle switch
        {
            VehicleState.InCar => Color.DarkOrange,
            VehicleState.Unknown => Color.Firebrick,
            _ => s.PanelOpen ? Color.DarkGreen : Color.DimGray
        };
    }

    // ---------------------------------------------------------------- log

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "bot-log.txt");

    // BOM de Notepad nhan ra UTF-8, khong thi tieng Viet co dau se thanh ky tu la.
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private void Append(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        if (_log.Lines.Length > 600)
            _log.Lines = _log.Lines.Skip(200).ToArray();
        _log.AppendText($"[{stamp}] {line}{Environment.NewLine}");

        // Ghi ra file luon: khong co no thi moi lan co su co lai phai doan
        // thay vi doc, va doan thi ton mot luot chay thu cua nguoi dung.
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}", LogEncoding);
        }
        catch { /* het cho ghi / bi khoa - khong duoc lam sap UI vi chuyen nay */ }
    }
}
