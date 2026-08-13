namespace GtaMiniGameBot;

/// <summary>
/// BA gia tri, khong phai hai. "Khong biet" la trang thai hop le va bat buoc:
/// cua kiem xe canh mot hanh dong pha hoai (bam E luc trong xe = bom dau vao xe,
/// do mat so dau vua cay), nen doan sai dat hon dung lai nhieu.
/// </summary>
internal enum VehicleState { OnFoot, InCar, Unknown }

/// <summary>Trang thai minigame doc duoc tu mot lan chup.</summary>
internal sealed record Snapshot(
    int[] BarMin,        // do sang thap nhat doc than tung thanh
    bool[] BarFull,      // >= FullThreshold o TOAN BO than thanh
    bool PanelOpen,
    int GreenCount,      // pixel xanh o vung so -> doi = duoc them thung
    int PanelWhite,      // chi tham khao - vung nay chong voi minimap, khong dung quyet dinh
    VehicleState Vehicle,
    double CarScore,     // NCC voi mau dong ho toc do
    int CarWhite,        // chi tham khao - cach dem cu, da bo khoi duong quyet dinh
    double PanelProminence)   // day moi la thu quyet dinh panel co mo khong
{
    public int TodoIndex => Array.FindIndex(BarFull, f => !f);
    public bool AllFull => BarFull.All(f => f);
    public bool NoneFull => BarFull.All(f => !f);

    public bool InCar => Vehicle == VehicleState.InCar;
    public bool OnFoot => Vehicle == VehicleState.OnFoot;
    public bool VehicleUnknown => Vehicle == VehicleState.Unknown;

    public string StateName => Vehicle switch
    {
        VehicleState.InCar => "trong xe",
        VehicleState.Unknown => "không rõ (ncc ở giữa hai ngưỡng)",
        _ => PanelOpen ? "dưới đất + panel mở" : "dưới đất + panel đóng"
    };
}

/// <summary>
/// Doc trang thai minigame. Dung chung cho ca che do theo doi (chi doc)
/// va vong lap cay - de hai che do khong bao gio lech nhau ve cach hieu man hinh.
/// </summary>
internal sealed class MiniGameReader : IDisposable
{
    private readonly BotConfig _cfg;
    private readonly RegionReader _bars;
    private readonly RegionReader _counter;
    private readonly RegionReader _car;
    private readonly int[] _sampleYs;
    private readonly GrayTemplate _carTemplate;

    /// <summary>Ly do khong doc duoc trang thai xe, null neu binh thuong.</summary>
    public string CarTemplateProblem { get; }

    public MiniGameReader(BotConfig cfg)
    {
        _cfg = cfg;
        _bars = new RegionReader(cfg.BarRegion);
        _counter = new RegionReader(cfg.CounterRegion);
        _car = new RegionReader(cfg.CarProbe);
        _sampleYs = cfg.SampleYs().ToArray();

        string path = cfg.CarTemplateFullPath;
        if (!File.Exists(path))
        {
            CarTemplateProblem = $"chưa có mẫu đồng hồ xe ({path}) — bấm “Chụp mẫu đồng hồ xe” khi đang ngồi trong xe";
        }
        else
        {
            try
            {
                var t = GrayTemplate.FromFile(path);
                if (t.Width != cfg.CarProbeW || t.Height != cfg.CarProbeH)
                    CarTemplateProblem = $"mẫu {t.Width}×{t.Height} không khớp ô dò {cfg.CarProbeW}×{cfg.CarProbeH} — chụp lại mẫu";
                else if (t.IsFlat)
                    CarTemplateProblem = "mẫu phẳng tuyệt đối (không có cấu trúc) — chụp lại mẫu";
                else
                    _carTemplate = t;
            }
            catch (Exception ex) { CarTemplateProblem = $"không đọc được mẫu: {ex.Message}"; }
        }
    }

    /// <summary>Chi chup lai dai 4 thanh - dung trong vong poll luc dang giu chuot.</summary>
    public void RefreshBars() => _bars.Refresh();

    /// <summary>
    /// Do sang THAP NHAT doc theo than thanh.
    /// Lay min (khong lay trung binh) vi ta doi TOAN BO than thanh phai trang:
    /// neu thanh chay day tu duoi len ma chi doc phan duoi thi bot se nha qua som
    /// va mat sach tien trinh. Lay min thi chieu nao cung dung.
    /// </summary>
    public int BarMin(int index)
    {
        int cx = _cfg.BarX[index];
        int min = int.MaxValue;
        foreach (int y in _sampleYs)
        {
            int v = _bars.GrayAvgH(cx, y, _cfg.BarHalfWidth);
            if (v >= 0) min = Math.Min(min, v);
        }
        return min == int.MaxValue ? -1 : min;
    }

    public bool BarFull(int index) => BarMin(index) >= _cfg.FullThreshold;

    /// <summary>
    /// Do sang tai tung diem mau doc than thanh (tu tren xuong).
    /// Dung de biet thanh CO DANG CHAY hay khong: khi giu dung, so diem da trang
    /// se bo dan 0 -> 8. Neu chi lay min thi min van thap cho toi luc day han,
    /// nen khong the phan biet "dang chay" voi "khong an gi".
    /// </summary>
    public int[] BarSamples(int index)
    {
        int cx = _cfg.BarX[index];
        var vals = new int[_sampleYs.Length];
        for (int i = 0; i < _sampleYs.Length; i++)
            vals[i] = _bars.GrayAvgH(cx, _sampleYs[i], _cfg.BarHalfWidth);
        return vals;
    }

    /// <summary>So diem mau da dat nguong trang.</summary>
    public int BarFullCount(int index)
    {
        int n = 0;
        foreach (int v in BarSamples(index))
            if (v >= _cfg.FullThreshold) n++;
        return n;
    }

    public int SampleCount => _sampleYs.Length;

    /// <summary>
    /// Do "noi len" cua tung thanh so voi median do sang ca vung.
    /// Day la tin hieu "panel dang mo": panel mo thi ca 4 thanh noi len +36..+37,
    /// panel dong thi it nhat mot cai tut xuong -32..-4.
    ///
    /// Thanh la vach DOC nen lay trung binh theo chieu doc se khuech dai no va dap
    /// nen dat di. Nen cung la ly do khong dung minimap hay cum chu nao lam moc:
    /// minimap chiem dung cho do va ve vach ke duong mau trang.
    /// </summary>
    public double[] BarProminences()
    {
        int x0 = _cfg.BarRegionX0, x1 = _cfg.BarRegionX1;
        int step = Math.Max(1, _cfg.ProfileRowStep);

        var prof = new double[x1 - x0 + 1];
        for (int x = x0; x <= x1; x++)
        {
            long sum = 0; int cnt = 0;
            for (int y = _cfg.BarYTop; y <= _cfg.BarYBottom; y += step)
            {
                int v = _bars.Gray(x, y);
                if (v >= 0) { sum += v; cnt++; }
            }
            prof[x - x0] = cnt == 0 ? 0 : (double)sum / cnt;
        }

        var sorted = (double[])prof.Clone();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];

        var outp = new double[_cfg.BarX.Length];
        for (int b = 0; b < _cfg.BarX.Length; b++)
        {
            double sum = 0; int cnt = 0;
            for (int x = _cfg.BarX[b] - _cfg.BarHalfWidth; x <= _cfg.BarX[b] + _cfg.BarHalfWidth; x++)
            {
                if (x < x0 || x > x1) continue;
                sum += prof[x - x0]; cnt++;
            }
            outp[b] = cnt == 0 ? double.MinValue : sum / cnt - median;
        }
        return outp;
    }

    /// <summary>Chup ca 3 vung va tra ve trang thai day du.</summary>
    public Snapshot Read()
    {
        _bars.Refresh();
        _counter.Refresh();
        _car.Refresh();
        return BuildSnapshot();
    }

    /// <summary>Dung anh vua chup (khong chup lai) de dung trang thai.</summary>
    public Snapshot BuildSnapshot()
    {
        int n = _cfg.BarX.Length;
        var mins = new int[n];
        var full = new bool[n];
        for (int i = 0; i < n; i++)
        {
            mins[i] = BarMin(i);
            full[i] = mins[i] >= _cfg.FullThreshold;
        }
        double minProm = BarProminences().Min();
        int white = _counter.CountWhite(_cfg.PanelProbe);   // chi de tham khao, KHONG dung quyet dinh
        var (score, state) = CarState();

        return new Snapshot(mins, full,
                            minProm >= _cfg.PanelBarProminenceMin,
                            _counter.CountGreen(), white,
                            state, score, CarWhite(),
                            minProm);
    }

    private int CarWhite() =>
        _car.CountWhite(_cfg.CarProbe, _cfg.CarWhiteMinBright, _cfg.CarWhiteSpread);

    /// <summary>So khop mau dong ho toc do -> (diem NCC, trang thai).</summary>
    private (double score, VehicleState state) CarState()
    {
        if (_carTemplate is null) return (0, VehicleState.Unknown);

        double s = _carTemplate.Score(_car.GrayBuffer(_cfg.CarProbe));
        if (s >= _cfg.CarNccIn) return (s, VehicleState.InCar);
        if (s <= _cfg.CarNccOut) return (s, VehicleState.OnFoot);
        return (s, VehicleState.Unknown);
    }

    /// <summary>Chi chup lai vung dong ho toc do.</summary>
    public (double score, VehicleState state) RefreshCar()
    {
        _car.Refresh();
        return CarState();
    }

    /// <summary>Chi chup lai vung so (re) - dung sau moi lan giu xong.</summary>
    public (int green, int white) RefreshCounter()
    {
        _counter.Refresh();
        return (_counter.CountGreen(), _counter.CountWhite(_cfg.PanelProbe));
    }

    public bool PanelOpen()
    {
        _bars.Refresh();
        return BarProminences().Min() >= _cfg.PanelBarProminenceMin;
    }

    public void SaveDebug(string dir)
    {
        Directory.CreateDirectory(dir);
        _bars.SaveDebug(Path.Combine(dir, "vung-4-thanh.png"));
        _counter.SaveDebug(Path.Combine(dir, "vung-so-thung.png"));
        _car.SaveDebug(Path.Combine(dir, "vung-dong-ho-xe.png"));
    }

    /// <summary>
    /// Chup bang chung khi dung: anh full man hinh + ba vung dang doc + moi so do.
    /// Anh full man hinh la thu quan trong nhat - no cho doc duoc TRANG THAI cua
    /// gieng, SAN LUONG, DANG KHAI THAC BAO NHIEU NGUOI tai dung khoanh khac do.
    /// Tra ve duong dan thu muc da luu.
    /// </summary>
    public string DumpEvidence(string rootDir, string reason, int keep)
    {
        string dir = Path.Combine(rootDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(dir);

        // 1. anh full man hinh chinh
        try
        {
            var b = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 2560, 1440);
            using var full = new Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
            full.Save(Path.Combine(dir, "toan-man-hinh.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { TryWrite(Path.Combine(dir, "loi-chup-man-hinh.txt"), ex.ToString()); }

        // 2. ba vung dang doc
        try { SaveDebug(dir); } catch { }

        // 3. moi so do dang co
        try
        {
            var s = Read();
            var proms = BarProminences();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Thời điểm     : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Lý do dừng    : {reason}");
            sb.AppendLine($"Cửa sổ đang focus: {Native.ForegroundTitle()}");
            sb.AppendLine();
            sb.AppendLine($"Panel         : {(s.PanelOpen ? "MỞ" : "ĐÓNG")}");
            sb.AppendLine($"Trạng thái    : {s.StateName}");
            sb.AppendLine($"Pixel xanh    : {s.GreenCount}   (đổi = được thêm thùng)");
            sb.AppendLine($"NCC đồng hồ xe: {s.CarScore:F3}   (chỉ tham khảo)");
            sb.AppendLine();
            sb.AppendLine($"Ngưỡng: đầy ≥ {_cfg.FullThreshold}, coi là đã reset < {_cfg.ResetThreshold}, " +
                          $"panel mở khi nổi lên ≥ {_cfg.PanelBarProminenceMin}");
            sb.AppendLine($"Thân thanh đọc ở y = {_cfg.BarYTop}…{_cfg.BarYBottom}, {_cfg.BarSamples} điểm mẫu");
            sb.AppendLine();
            for (int i = 0; i < _cfg.BarX.Length; i++)
            {
                sb.AppendLine($"Thanh {i + 1}  x={_cfg.BarX[i],4}  nổi lên={proms[i],7:F1}  " +
                              $"nhỏ nhất={s.BarMin[i],4}  {(s.BarFull[i] ? "ĐẦY" : "chưa")}");
                sb.AppendLine($"          8 điểm mẫu (trên→dưới) = [{string.Join(", ", BarSamples(i))}]");
            }
            TryWrite(Path.Combine(dir, "so-do.txt"), sb.ToString());
        }
        catch (Exception ex) { TryWrite(Path.Combine(dir, "loi-doc-so-do.txt"), ex.ToString()); }

        // 4. don thu muc cu
        try
        {
            var olds = new DirectoryInfo(rootDir).GetDirectories()
                        .OrderByDescending(d => d.CreationTimeUtc).Skip(Math.Max(1, keep));
            foreach (var d in olds) d.Delete(true);
        }
        catch { }

        return dir;
    }

    private static void TryWrite(string path, string text)
    {
        try { File.WriteAllText(path, text, new System.Text.UTF8Encoding(true)); } catch { }
    }

    public void Dispose()
    {
        _bars.Dispose();
        _counter.Dispose();
        _car.Dispose();
    }
}
