namespace GtaMiniGameBot;

/// <summary>
/// Cài đặt tự ăn/uống — chỉ những khoá PHỤ THUỘC HUD của từng máy hoặc là khẩu vị người dùng, đúng
/// lằn ranh mà <see cref="NavSettings"/> đã đặt. Mọi hằng số mô tả hành vi (ngưỡng 50 %, chờ 10 s,
/// dải HSV, hình học vành cung) nằm trong <see cref="NavTuning"/> dưới dạng const.
///
/// Bản Python có <c>survival_food_slots</c>/<c>survival_water_slots</c> trong config.json nhưng
/// main.py KHÔNG hề đọc — danh sách phím hardcode thẳng trong code, sửa config không có tác dụng.
/// Ở đây hai khoá đó sống thật.
/// </summary>
internal sealed class SurvivalSettings
{
    /// <summary>
    /// Mặc định TẮT: bật job Điện lên phải giữ nguyên hành vi cũ cho tới khi người dùng chủ động
    /// bật. Chỉ có tác dụng khi <see cref="ElectricConfig.AutoWalk"/> bật — bộ máy này sống trong
    /// <see cref="NavBot"/>, mà NavBot chỉ chạy khi tự đi tới điểm làm việc.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Tâm icon ĐỒ ĂN trên HUD, mốc 1080p — <c>food_center_ref [160, 1047]</c>. Đây là số phụ thuộc
    /// HUD của từng máy/server, cùng hạng với <see cref="NavSettings.PlayerOriginXRef"/>;
    /// <c>--verify-survival</c> in ra % đọc được trên ảnh chụp thật để đối chiếu và chỉnh lại.
    /// </summary>
    public double FoodCenterXRef { get; set; } = 160.0;

    public double FoodCenterYRef { get; set; } = 1047.0;

    /// <summary>Tâm icon NƯỚC — <c>water_center_ref [210, 1047]</c>.</summary>
    public double WaterCenterXRef { get; set; } = 210.0;

    public double WaterCenterYRef { get; set; } = 1047.0;

    /// <summary>
    /// Ô hotbar chứa bánh: phím chính rồi phím dự phòng, ngăn bởi dấu phẩy. Bản Python đi
    /// <c>5 → 7</c>. Chỉ nhận ký tự 1–9 và tối đa hai phím: cả bộ máy chấm điểm đúng một lần cho
    /// mỗi phím, thêm phím thứ ba là thêm 10 giây đứng chết cho một lần đoán nữa.
    /// </summary>
    public string FoodSlots { get; set; } = "5,7";

    /// <summary>Ô hotbar chứa nước. Bản Python đi <c>4 → 6</c>.</summary>
    public string WaterSlots { get; set; } = "4,6";

    /// <summary>Chỉ kẹp giá trị, KHÔNG ném: <see cref="ElectricConfig.Load"/> nuốt mọi exception.</summary>
    public void Normalize()
    {
        FoodCenterXRef = ClampRef(FoodCenterXRef, 160.0, ElectricConfig.RefW);
        FoodCenterYRef = ClampRef(FoodCenterYRef, 1047.0, ElectricConfig.RefH);
        WaterCenterXRef = ClampRef(WaterCenterXRef, 210.0, ElectricConfig.RefW);
        WaterCenterYRef = ClampRef(WaterCenterYRef, 1047.0, ElectricConfig.RefH);
        FoodSlots = NormalizeSlots(FoodSlots, "5,7");
        WaterSlots = NormalizeSlots(WaterSlots, "4,6");
    }

    private static double ClampRef(double v, double fallback, double max)
        => double.IsNaN(v) || v <= 0 || v > max ? fallback : v;

    /// <summary>Lọc lấy ký tự 1–9, bỏ trùng, cắt còn tối đa hai phím; rỗng thì về mặc định.</summary>
    private static string NormalizeSlots(string raw, string fallback)
    {
        var keep = new List<char>(2);
        foreach (char c in raw ?? "")
        {
            if (c < '1' || c > '9' || keep.Contains(c)) continue;
            keep.Add(c);
            if (keep.Count == 2) break;
        }
        return keep.Count == 0 ? fallback : string.Join(",", keep);
    }

    /// <summary>Danh sách mã phím ảo của <see cref="FoodSlots"/>/<see cref="WaterSlots"/> đã chuẩn hoá.</summary>
    public static ushort[] SlotKeys(string slots)
    {
        var list = new List<ushort>(2);
        foreach (char c in slots ?? "")
            if (c >= '1' && c <= '9') list.Add((ushort)c);   // VK '1'..'9' == mã ASCII
        return list.ToArray();
    }
}

/// <summary>Kết quả một lượt quét hai đồng hồ. <c>Pct</c> là NaN khi không đọc được icon.</summary>
internal readonly struct SurvivalReading
{
    public bool FoodValid { get; init; }
    public double FoodPct { get; init; }
    public bool WaterValid { get; init; }
    public double WaterPct { get; init; }

    /// <summary>Đã dưới ngưỡng ĐỦ SỐ LƯỢT liên tiếp — tín hiệu duy nhất được phép mở một bữa ăn.</summary>
    public bool FoodLow { get; init; }

    public bool WaterLow { get; init; }

    public static SurvivalReading None => new()
    {
        FoodPct = double.NaN,
        WaterPct = double.NaN
    };
}

/// <summary>
/// Port của <c>SurvivalGaugeDetector</c> (Navigator Python, main.py 1628–1777): đọc phần trăm của
/// hai đồng hồ tròn FOOD/WATER ở góc dưới trái HUD.
///
/// Cách đo KHÔNG phải là chiều dài một thanh mà là ĐỘ PHỦ GÓC của vòng cung màu: chia vành ngoài
/// thành <see cref="NavTuning.SurvivalAngleBins"/> nan quạt, đếm nan nào còn màu. Chính xác cỡ vài
/// phần trăm là đủ, vì quyết định duy nhất rút ra từ nó là "trên hay dưới 50 %".
///
/// Bước xác thực lõi (đếm pixel màu trong đĩa nhỏ giữa icon) mới là chốt chặn quan trọng nhất: HUD
/// ẩn, đang ở menu, hay ảnh chụp trượt vùng thì icon không có ở đó, và khi ấy hàm phải trả "không
/// đọc được" chứ KHÔNG được trả 0 % — trả 0 % là bot tự mở một bữa ăn giữa lúc không nhìn thấy gì.
/// </summary>
internal sealed class SurvivalGauge
{
    private readonly SurvivalSettings _cfg;
    private readonly NavScale _s;

    private double _nextScan;
    private double? _foodEma, _waterEma;
    private int _foodLowStreak, _waterLowStreak;
    private SurvivalReading _last = SurvivalReading.None;

    public SurvivalGauge(SurvivalSettings cfg, NavScale s)
    {
        _cfg = cfg;
        _s = s;
    }

    /// <summary>Kết quả lượt quét gần nhất, không chụp lại.</summary>
    public SurvivalReading Last => _last;

    /// <summary>
    /// Đã tới hạn quét chưa. Luồng chính hỏi TRƯỚC khi chụp: <c>survival_scan_interval_s</c> là 0.25 s
    /// còn nhịp tick là 25 ms, nên hỏi trước tiết kiệm 9 trên 10 cú chụp màn.
    /// </summary>
    public bool Due(double now) => now >= _nextScan;

    public void Reset()
    {
        _nextScan = 0;
        _foodEma = null;
        _waterEma = null;
        _foodLowStreak = 0;
        _waterLowStreak = 0;
        _last = SurvivalReading.None;
    }

    /// <summary><c>update</c>. Gọi khi <see cref="Due"/> đúng; khung phải bao vùng hai icon.</summary>
    public SurvivalReading Update(NavFrame f, double now)
    {
        _nextScan = now + NavTuning.SurvivalScanIntervalS;

        var (foodValid, foodRaw) = One(f, _cfg.FoodCenterXRef, _cfg.FoodCenterYRef,
            NavTuning.FoodHLo, NavTuning.FoodHHi, NavTuning.FoodSMin, NavTuning.FoodVMin);
        var (waterValid, waterRaw) = One(f, _cfg.WaterCenterXRef, _cfg.WaterCenterYRef,
            NavTuning.WaterHLo, NavTuning.WaterHHi, NavTuning.WaterSMin, NavTuning.WaterVMin);

        double a = NavTuning.SurvivalEmaAlpha;

        // Mat icon la XOA HAN EMA chu khong giu so cu: doc lai tu dau con hon lay so cua lan truoc
        // nhan vat con dang cam mieng banh. Streak cung ve 0 — bo dem "dat duoi nguong" phai lien tuc.
        if (foodValid) _foodEma = _foodEma is null ? foodRaw : a * foodRaw + (1 - a) * _foodEma.Value;
        else { _foodEma = null; _foodLowStreak = 0; }

        if (waterValid) _waterEma = _waterEma is null ? waterRaw : a * waterRaw + (1 - a) * _waterEma.Value;
        else { _waterEma = null; _waterLowStreak = 0; }

        double thr = NavTuning.SurvivalLowThresholdPct;
        int confirm = NavTuning.SurvivalLowConfirmScans;

        if (foodValid && _foodEma is not null && _foodEma.Value < thr) _foodLowStreak++;
        else _foodLowStreak = 0;

        if (waterValid && _waterEma is not null && _waterEma.Value < thr) _waterLowStreak++;
        else _waterLowStreak = 0;

        _last = new SurvivalReading
        {
            FoodValid = foodValid,
            WaterValid = waterValid,
            FoodPct = _foodEma ?? double.NaN,
            WaterPct = _waterEma ?? double.NaN,
            FoodLow = foodValid && _foodLowStreak >= confirm,
            WaterLow = waterValid && _waterLowStreak >= confirm
        };
        return _last;
    }

    /// <summary><c>_one</c>: xác thực icon rồi đo độ phủ góc của vòng cung.</summary>
    private (bool valid, double pct) One(NavFrame f, double cxRef, double cyRef,
        int hLo, int hHi, int sMin, int vMin)
    {
        // Tam icon o he MAN (tuong doi goc man) roi doi ve he KHUNG bang goc chup.
        double cx = cxRef * _s.Sx - f.OriginX;
        double cy = cyRef * _s.Sy - f.OriginY;

        // ---- buoc 1: icon co that o do khong ----
        double coreR = NavTuning.SurvivalCoreRadiusRef * _s.Max;
        int x0 = Math.Max(0, (int)(cx - coreR - 1));
        int x1 = Math.Min(f.Width, (int)(cx + coreR + 2));
        int y0 = Math.Max(0, (int)(cy - coreR - 1));
        int y1 = Math.Min(f.Height, (int)(cy + coreR + 2));
        if (x1 <= x0 || y1 <= y0) return (false, double.NaN);

        double coreR2 = coreR * coreR;
        int core = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * f.Stride;
            double dy = y - cy;
            for (int x = x0; x < x1; x++)
            {
                double dx = x - cx;
                if (dx * dx + dy * dy > coreR2) continue;
                int i = row + x * 4;
                if (InBand(f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2], hLo, hHi, sMin, vMin)) core++;
            }
        }

        int need = Math.Max(3, (int)(NavTuning.SurvivalCoreMinPixels * _s.Area));
        if (core < need) return (false, double.NaN);

        // ---- buoc 2: do phu goc cua vanh ngoai ----
        int bins = NavTuning.SurvivalAngleBins;
        int samples = NavTuning.SurvivalRadialSamples;
        double rmin = NavTuning.SurvivalRingRminRef * _s.Max;
        double rmax = NavTuning.SurvivalRingRmaxRef * _s.Max;
        double step = samples <= 1 ? 0 : (rmax - rmin) / (samples - 1);

        int active = 0;
        for (int i = 0; i < bins; i++)
        {
            double th = 2.0 * Math.PI * i / bins;
            double ct = Math.Cos(th), st = Math.Sin(th);
            int hits = 0;
            for (int j = 0; j < samples; j++)
            {
                double rr = rmin + step * j;
                int x = (int)Math.Round(cx + rr * ct);
                int y = (int)Math.Round(cy + rr * st);
                if (x < 0 || x >= f.Width || y < 0 || y >= f.Height) continue;
                int k = y * f.Stride + x * 4;
                if (InBand(f.Bgra[k], f.Bgra[k + 1], f.Bgra[k + 2], hLo, hHi, sMin, vMin)) hits++;
            }
            if (hits >= NavTuning.SurvivalAngleHitPixels) active++;
        }

        return (true, Math.Clamp(100.0 * active / bins, 0.0, 100.0));
    }

    /// <summary>
    /// Trong dải HSV không. Loại rẻ theo V rồi S trước, chỉ tính hue khi đã qua — đúng khuôn
    /// <c>YellowDotDetector.IsYellow</c>; vòng này chạy ~2500 lần mỗi lượt quét.
    /// </summary>
    private static bool InBand(int b, int g, int r, int hLo, int hHi, int sMin, int vMin)
    {
        int max = Math.Max(r, Math.Max(g, b));
        if (max < vMin) return false;
        int min = Math.Min(r, Math.Min(g, b));
        int d = max - min;
        int s = max == 0 ? 0 : (d * 255 + max / 2) / max;
        if (s < sMin) return false;
        var (h, _, _) = ImageOps.HsvOf(b, g, r);
        return h >= hLo && h <= hHi;
    }
}
