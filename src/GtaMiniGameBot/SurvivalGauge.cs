namespace GtaMiniGameBot;

/// <summary>
/// Cài đặt tự ăn/uống — chỉ những khoá PHỤ THUỘC HUD của từng máy hoặc là khẩu vị người dùng, đúng
/// lằn ranh mà <see cref="NavSettings"/> đã đặt. Mọi hằng số mô tả hành vi (chờ 10 s, dải HSV, hình
/// học vành cung) nằm trong <see cref="NavTuning"/> dưới dạng const.
///
/// Bản Python có <c>survival_food_slots</c>/<c>survival_water_slots</c> trong config.json nhưng
/// main.py KHÔNG hề đọc — danh sách phím hardcode thẳng trong code, sửa config không có tác dụng.
/// Ở đây hai khoá đó sống thật, và có cả UI trong tab Thợ điện.
/// </summary>
internal sealed class SurvivalSettings
{
    /// <summary>Ô hotbar nhỏ nhất/lớn nhất bấm được trong game. UI và bộ chuẩn hoá dùng chung cặp này.</summary>
    public const char SlotMin = '4';

    public const char SlotMax = '8';

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
    ///
    /// Lệch vài pixel thì bộ đọc tự bù được (xem <see cref="SurvivalGauge"/>), lệch chục pixel thì
    /// mất luôn icon và tính năng chết câm — lúc đó mới phải sửa số này.
    /// </summary>
    public double FoodCenterXRef { get; set; } = 160.0;

    public double FoodCenterYRef { get; set; } = 1047.0;

    /// <summary>Tâm icon NƯỚC — <c>water_center_ref [210, 1047]</c>.</summary>
    public double WaterCenterXRef { get; set; } = 210.0;

    public double WaterCenterYRef { get; set; } = 1047.0;

    /// <summary>
    /// Tụt dưới bao nhiêu phần trăm thì mở bữa ăn. Người dùng chỉnh được trong tab Thợ điện: bộ đọc
    /// chỉ chính xác cỡ vài phần trăm nên ai muốn ăn sớm/muộn hơn thì kéo số này chứ đừng sửa code.
    /// </summary>
    public double LowThresholdPct { get; set; } = NavTuning.SurvivalLowThresholdPct;

    /// <summary>
    /// Hai ô hotbar chứa bánh: phím chính rồi phím dự phòng, ngăn bởi dấu phẩy. Bản Python đi
    /// <c>5 → 7</c>. Chỉ nhận ô <see cref="SlotMin"/>..<see cref="SlotMax"/> và luôn ĐÚNG hai phím:
    /// cả bộ máy chấm điểm đúng một lần cho mỗi phím, thêm phím thứ ba là thêm 10 giây đứng chết cho
    /// một lần đoán nữa.
    /// </summary>
    public string FoodSlots { get; set; } = "5,7";

    /// <summary>Hai ô hotbar chứa nước. Bản Python đi <c>4 → 6</c>.</summary>
    public string WaterSlots { get; set; } = "4,6";

    /// <summary>Chỉ kẹp giá trị, KHÔNG ném: <see cref="ElectricConfig.Load"/> nuốt mọi exception.</summary>
    public void Normalize()
    {
        FoodCenterXRef = ClampRef(FoodCenterXRef, 160.0, ElectricConfig.RefW);
        FoodCenterYRef = ClampRef(FoodCenterYRef, 1047.0, ElectricConfig.RefH);
        WaterCenterXRef = ClampRef(WaterCenterXRef, 210.0, ElectricConfig.RefW);
        WaterCenterYRef = ClampRef(WaterCenterYRef, 1047.0, ElectricConfig.RefH);

        LowThresholdPct = double.IsNaN(LowThresholdPct)
            ? NavTuning.SurvivalLowThresholdPct
            : Math.Clamp(LowThresholdPct, NavTuning.SurvivalThresholdMinPct, NavTuning.SurvivalThresholdMaxPct);

        FoodSlots = NormalizeSlots(FoodSlots, "5,7");
        WaterSlots = NormalizeSlots(WaterSlots, "4,6");
    }

    private static double ClampRef(double v, double fallback, double max)
        => double.IsNaN(v) || v <= 0 || v > max ? fallback : v;

    /// <summary>
    /// Lọc lấy ký tự trong dải ô hợp lệ, bỏ trùng, cắt còn hai phím — rồi BÙ cho đủ hai từ mặc định.
    /// Luôn trả về đúng hai ô vì UI có đúng hai ô cho mỗi loại; thiếu một ô là mất luôn ô dự phòng
    /// mà người dùng không thấy vì sao.
    /// </summary>
    private static string NormalizeSlots(string raw, string fallback)
    {
        var keep = new List<char>(2);
        Take(raw);
        Take(fallback);
        for (char c = SlotMin; c <= SlotMax; c++) Take(c.ToString());
        return string.Join(",", keep);

        void Take(string src)
        {
            foreach (char c in src ?? "")
            {
                if (keep.Count == 2) return;
                if (c < SlotMin || c > SlotMax || keep.Contains(c)) continue;
                keep.Add(c);
            }
        }
    }

    /// <summary>Danh sách mã phím ảo của <see cref="FoodSlots"/>/<see cref="WaterSlots"/> đã chuẩn hoá.</summary>
    public static ushort[] SlotKeys(string slots)
    {
        var list = new List<ushort>(2);
        foreach (char c in slots ?? "")
            if (c >= SlotMin && c <= SlotMax) list.Add((ushort)c);   // VK '4'..'8' == ma ASCII
        return list.ToArray();
    }
}

/// <summary>
/// Vành cung của MỘT icon sau khi tự hiệu chuẩn. Toạ độ lệch tính bằng pixel màn.
///
/// Hai mức, và ranh giới giữa chúng quan trọng: bán kính (<see cref="ROk"/>) dò được từ một cung
/// ngắn cũng đúng, vì cung ngắn vẫn nằm trên đúng đường tròn ấy. Tâm (<see cref="CentreOk"/>) thì
/// KHÔNG: một cung 90° khớp với vô số đường tròn, và bộ dò sẽ chọn cái làm cung trông dày nhất —
/// tâm lệch hẳn đi, phần trăm phồng lên. Nên tâm chỉ được dò khi vành gần đầy.
/// </summary>
internal sealed class SurvivalRing
{
    /// <summary>Đã dò được bán kính vành.</summary>
    public bool ROk;

    /// <summary>Đã chốt được cả độ lệch tâm — từ lúc này thôi dò lại.</summary>
    public bool CentreOk;

    /// <summary>Lệch của tâm thật so với tâm trong config.</summary>
    public double OffX, OffY;

    /// <summary>Bán kính vành đo được, pixel màn.</summary>
    public double R;

    /// <summary>Số pixel trong dải lúc hiệu chuẩn — càng lớn càng chắc.</summary>
    public int Strength;
}

/// <summary>
/// Trạng thái ăn uống sống suốt MỘT LƯỢT bật job, không serialize.
///
/// Vì sao phải là một object riêng chứ không để field trong <see cref="NavBot"/>:
/// <see cref="ElectricBot"/> dựng NavBot MỚI sau mỗi minigame, nên mọi thứ nhớ trong NavBot bị xoá
/// sạch mỗi lượt — đó chính là lý do bot cứ giải xong một bảng là lại đứng chờ dùng đồ ăn dù vừa
/// kết luận "hết bánh" mấy chục giây trước. Mốc thời gian ở đây là <see cref="NavClock"/>, đồng hồ
/// static toàn tiến trình, nên so sánh được giữa các đời NavBot.
/// </summary>
internal sealed class SurvivalState
{
    public readonly SurvivalRing FoodRing = new();
    public readonly SurvivalRing WaterRing = new();

    /// <summary>Đã bỏ hẳn loại này cho tới khi người dùng tắt/bật lại job.</summary>
    public bool FoodOff, WaterOff;

    /// <summary>Số bữa liên tiếp bấm hết ô mà đồng hồ không nhúc nhích.</summary>
    public int FoodFails, WaterFails;

    public double FoodBlockUntil, WaterBlockUntil;

    public bool AllOff => FoodOff && WaterOff;

    private readonly Queue<string> _notes = new();

    /// <summary>Bộ đọc không có đường ra log; nó gửi vào đây để <see cref="NavBot"/> nhả ra.</summary>
    public void Note(string s)
    {
        if (_notes.Count < 8) _notes.Enqueue(s);
    }

    public string TakeNote() => _notes.Count > 0 ? _notes.Dequeue() : null;
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
/// phần trăm là đủ, vì quyết định duy nhất rút ra từ nó là "trên hay dưới ngưỡng".
///
/// Khác bản Python ở một chỗ, và đó là chỗ sửa lỗi "ăn sớm": bản Python bắn 7 tia ở dải bán kính
/// CỐ ĐỊNH 17–23 px (mốc 1080p) rồi bắt mỗi nan quạt trúng ít nhất 2 tia. Hai con số đó chép từ HUD
/// của họ; HUD scale khác đi, hoặc tâm icon trong config lệch vài pixel, là cả một múi góc trượt ra
/// ngoài dải bán kính — nan quạt bị coi là "hết màu" dù trên màn vẫn còn màu, và bot ăn sớm đều đặn
/// một khoảng phần trăm cố định. Ở đây vành được TỰ DÒ từ chính ảnh (tâm + bán kính), nhớ lại trong
/// <see cref="SurvivalState"/> nên chỉ tốn một lần cho cả lượt bật job, và độ phủ góc đếm THẲNG
/// pixel trong dải chứ không lấy mẫu bằng tia.
///
/// Bước xác thực lõi (đếm pixel màu trong đĩa nhỏ giữa icon) vẫn là chốt chặn quan trọng nhất: HUD
/// ẩn, đang ở menu, hay ảnh chụp trượt vùng thì icon không có ở đó, và khi ấy hàm phải trả "không
/// đọc được" chứ KHÔNG được trả 0 % — trả 0 % là bot tự mở một bữa ăn giữa lúc không nhìn thấy gì.
/// </summary>
internal sealed class SurvivalGauge
{
    private readonly SurvivalSettings _cfg;
    private readonly NavScale _s;
    private readonly SurvivalState _state;

    private double _nextScan;
    private double? _foodEma, _waterEma;
    private int _foodLowStreak, _waterLowStreak;
    private SurvivalReading _last = SurvivalReading.None;

    // Bo dem dung lai giua cac luot quet: mot luot cham vai nghin pixel, cap phat lai moi lan la
    // rac GC deu dan suot ca phien chay.
    private readonly List<float> _ringX = new(4096);
    private readonly List<float> _ringY = new(4096);
    private readonly int[] _bins = new int[NavTuning.SurvivalAngleBins];
    private int[] _hist = Array.Empty<int>();

    public SurvivalGauge(SurvivalSettings cfg, NavScale s, SurvivalState state = null)
    {
        _cfg = cfg;
        _s = s;
        _state = state ?? new SurvivalState();
    }

    /// <summary>Kết quả lượt quét gần nhất, không chụp lại.</summary>
    public SurvivalReading Last => _last;

    /// <summary>
    /// Đã tới hạn quét chưa. Luồng chính hỏi TRƯỚC khi chụp: <c>survival_scan_interval_s</c> là 0.25 s
    /// còn nhịp tick là 25 ms, nên hỏi trước tiết kiệm 9 trên 10 cú chụp màn.
    /// </summary>
    public bool Due(double now) => now >= _nextScan;

    /// <summary>
    /// Xoá bộ nhớ ngắn hạn (EMA, streak, hạn quét). CỐ Ý không đụng tới kết quả hiệu chuẩn vành
    /// trong <see cref="SurvivalState"/>: vành nằm ở đâu là chuyện của cái HUD chứ không phải của
    /// bữa ăn vừa rồi, dò lại mỗi lần là phí và còn dễ dò trúng lúc vạch gần cạn.
    /// </summary>
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

        var (foodValid, foodRaw) = One(f, _cfg.FoodCenterXRef, _cfg.FoodCenterYRef, _state.FoodRing, "bánh",
            NavTuning.FoodHLo, NavTuning.FoodHHi, NavTuning.FoodSMin, NavTuning.FoodVMin);
        var (waterValid, waterRaw) = One(f, _cfg.WaterCenterXRef, _cfg.WaterCenterYRef, _state.WaterRing, "nước",
            NavTuning.WaterHLo, NavTuning.WaterHHi, NavTuning.WaterSMin, NavTuning.WaterVMin);

        double a = NavTuning.SurvivalEmaAlpha;

        // Mat icon la XOA HAN EMA chu khong giu so cu: doc lai tu dau con hon lay so cua lan truoc
        // nhan vat con dang cam mieng banh. Streak cung ve 0 — bo dem "dat duoi nguong" phai lien tuc.
        if (foodValid) _foodEma = _foodEma is null ? foodRaw : a * foodRaw + (1 - a) * _foodEma.Value;
        else { _foodEma = null; _foodLowStreak = 0; }

        if (waterValid) _waterEma = _waterEma is null ? waterRaw : a * waterRaw + (1 - a) * _waterEma.Value;
        else { _waterEma = null; _waterLowStreak = 0; }

        double thr = _cfg.LowThresholdPct;
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

    /// <summary><c>_one</c>: xác thực icon → tự dò vành (một lần) → đo độ phủ góc.</summary>
    private (bool valid, double pct) One(NavFrame f, double cxRef, double cyRef, SurvivalRing ring, string who,
        int hLo, int hHi, int sMin, int vMin)
    {
        // Tam icon o he MAN (tuong doi goc man) roi doi ve he KHUNG bang goc chup.
        double cx = cxRef * _s.Sx - f.OriginX;
        double cy = cyRef * _s.Sy - f.OriginY;

        double coreR = NavTuning.SurvivalCoreRadiusRef * _s.Max;
        double span = NavTuning.SurvivalCenterSearchRef * _s.Max;
        double rLo = NavTuning.SurvivalRingSearchRminRef * _s.Max;
        double rHi = NavTuning.SurvivalRingSearchRmaxRef * _s.Max;

        // Chi gom pixel co the la VANH: bo han dia loi (hinh banh mi/chai nuoc cung mau) de no khong
        // deo vao bieu do ban kinh va keo buoc hieu chuan ve sai cho.
        double gLo = Math.Max(coreR, rLo - span - 2.0);
        double gHi = rHi + span + 2.0;

        int x0 = Math.Max(0, (int)(cx - gHi - 1));
        int x1 = Math.Min(f.Width, (int)(cx + gHi + 2));
        int y0 = Math.Max(0, (int)(cy - gHi - 1));
        int y1 = Math.Min(f.Height, (int)(cy + gHi + 2));
        if (x1 <= x0 || y1 <= y0) return (false, double.NaN);

        _ringX.Clear();
        _ringY.Clear();

        double coreR2 = coreR * coreR, gLo2 = gLo * gLo, gHi2 = gHi * gHi;
        int core = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * f.Stride;
            double dy = y - cy;
            for (int x = x0; x < x1; x++)
            {
                double dx = x - cx;
                double d2 = dx * dx + dy * dy;
                if (d2 > gHi2) continue;

                bool inCore = d2 <= coreR2;
                bool inRing = d2 >= gLo2;
                if (!inCore && !inRing) continue;

                int i = row + x * 4;
                if (!InBand(f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2], hLo, hHi, sMin, vMin)) continue;

                if (inCore) core++;
                if (inRing) { _ringX.Add((float)dx); _ringY.Add((float)dy); }
            }
        }

        // ---- buoc 1: icon co that o do khong ----
        int need = Math.Max(3, (int)(NavTuning.SurvivalCoreMinPixels * _s.Area));
        if (core < need) return (false, double.NaN);

        // ---- buoc 2: vanh nam o dau ----
        if (!ring.CentreOk) Calibrate(ring, who, rLo, rHi, span);

        double rc = ring.ROk
            ? ring.R
            : 0.5 * (NavTuning.SurvivalRingRminRef + NavTuning.SurvivalRingRmaxRef) * _s.Max;

        // Chua chot duoc tam thi NOI RONG dai ra dung bang do lech con cho phep. Lech tam ma dai
        // hep la mat mot mui goc, tuc la doc HUT — dung cai sai da sinh ra loi "an som". Roi ra thi
        // cung chi bat them chinh cai vanh nay, vi mau hai icon canh nhau khac han nhau.
        double halfW = (NavTuning.SurvivalRingHalfWidthRef
                        + (ring.CentreOk ? 0.0 : NavTuning.SurvivalCenterSearchRef)) * _s.Max;

        // ---- buoc 3: do phu goc ----
        int active = Coverage(ring.OffX, ring.OffY, rc, halfW);
        return (true, Math.Clamp(100.0 * active / _bins.Length, 0.0, 100.0));
    }

    /// <summary>Số nan quạt còn màu quanh tâm <c>(ox,oy)</c> trong dải <c>rc ± halfW</c>.</summary>
    private int Coverage(double ox, double oy, double rc, double halfW)
    {
        Array.Clear(_bins);
        double lo = Math.Max(0.0, rc - halfW), hi = rc + halfW;
        double lo2 = lo * lo, hi2 = hi * hi;
        int bins = _bins.Length;

        for (int k = 0; k < _ringX.Count; k++)
        {
            double dx = _ringX[k] - ox, dy = _ringY[k] - oy;
            double d2 = dx * dx + dy * dy;
            if (d2 < lo2 || d2 > hi2) continue;
            int b = (int)((Math.Atan2(dy, dx) + Math.PI) * bins / (2.0 * Math.PI));
            if (b < 0) b = 0;
            else if (b >= bins) b = bins - 1;
            _bins[b]++;
        }

        int active = 0;
        foreach (int n in _bins)
            if (n >= NavTuning.SurvivalAngleMinPixels) active++;
        return active;
    }

    /// <summary>
    /// Dò vành từ đám pixel vừa gom, HAI MỨC — xem <see cref="SurvivalRing"/> để biết vì sao phải
    /// tách ra.
    ///
    /// Mức 1 (làm lại mỗi lượt cho tới khi chốt được tâm): dựng biểu đồ số pixel theo bán kính ở
    /// đúng tâm trong config, lấy cửa sổ dày nhất. Cung ngắn vẫn nằm trên đúng đường tròn nên bán
    /// kính này tin được; chỉ cần đủ dày để không dò trúng viền đĩa lõi.
    ///
    /// Mức 2 (chỉ khi vành gần đầy): quét từng độ lệch tâm trong <c>±span</c> và chọn cái làm đám
    /// pixel dồn chặt nhất vào một dải. Với vành gần đầy thì "dồn chặt nhất" đúng là tâm thật; với
    /// một cung ngắn thì không — nó chọn được cái tâm làm cung trông dày lên, và phần trăm phồng
    /// theo. Ngưỡng <see cref="NavTuning.SurvivalCalibMinCoveragePct"/> là chỗ chặn đúng chuyện đó.
    /// </summary>
    private void Calibrate(SurvivalRing ring, string who, double rLo, double rHi, double span)
    {
        int lo = (int)Math.Floor(rLo);
        int hi = (int)Math.Ceiling(rHi);
        int nb = hi - lo + 1;
        if (nb < 3 || _ringX.Count == 0) return;
        if (_hist.Length < nb) _hist = new int[nb];

        int w = Math.Max(1, (int)Math.Round(NavTuning.SurvivalRingHalfWidthRef * _s.Max));
        int step = Math.Max(1, (int)Math.Round(span));
        int minPixels = (int)(NavTuning.SurvivalCalibMinPixels * _s.Area);

        // ---- muc 1: ban kinh, ngay tai tam trong config ----
        var (score0, r0) = FitRadius(0, 0, lo, nb, w);
        if (score0 < minPixels) return;

        ring.R = r0;
        ring.Strength = score0;
        if (!ring.ROk)
        {
            ring.ROk = true;
            _state.Note($"[ĂN UỐNG] dò vành {who}: bán kính {r0:F1}px ({r0 / _s.Max:F1} mốc 1080p), " +
                        $"{score0} điểm ảnh");
        }

        // ---- muc 2: do lech tam, chi khi vanh gan day ----
        double wide = (NavTuning.SurvivalRingHalfWidthRef + NavTuning.SurvivalCenterSearchRef) * _s.Max;
        if (100.0 * Coverage(0, 0, r0, wide) / _bins.Length < NavTuning.SurvivalCalibMinCoveragePct) return;

        int bestScore = 0;
        double bestOx = 0, bestOy = 0, bestR = r0;
        for (int oy = -step; oy <= step; oy++)
        for (int ox = -step; ox <= step; ox++)
        {
            var (sum, r) = FitRadius(ox, oy, lo, nb, w);
            if (sum <= bestScore) continue;
            bestScore = sum;
            bestOx = ox;
            bestOy = oy;
            bestR = r;
        }

        ring.CentreOk = true;
        ring.OffX = bestOx;
        ring.OffY = bestOy;
        ring.R = bestR;
        ring.Strength = bestScore;
        _state.Note($"[ĂN UỐNG] chốt tâm vành {who}: bán kính {bestR:F1}px " +
                    $"({bestR / _s.Max:F1} mốc 1080p), tâm lệch ({bestOx:+0;-0;0},{bestOy:+0;-0;0})px, " +
                    $"{bestScore} điểm ảnh");
    }

    /// <summary>Cửa sổ bán kính dày nhất quanh tâm lệch <c>(ox,oy)</c>: trả về (số pixel, bán kính).</summary>
    private (int score, double r) FitRadius(int ox, int oy, int lo, int nb, int w)
    {
        Array.Clear(_hist, 0, nb);
        for (int k = 0; k < _ringX.Count; k++)
        {
            double dx = _ringX[k] - ox, dy = _ringY[k] - oy;
            int b = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy)) - lo;
            if (b >= 0 && b < nb) _hist[b]++;
        }

        int best = 0;
        double bestR = 0;
        for (int c = 0; c < nb; c++)
        {
            int sum = 0;
            double wsum = 0;
            for (int j = Math.Max(0, c - w); j <= Math.Min(nb - 1, c + w); j++)
            {
                sum += _hist[j];
                wsum += (double)_hist[j] * (j + lo);
            }
            if (sum <= best) continue;
            best = sum;
            bestR = wsum / sum;     // trong tam trong cua so — min hon la lay dung buoc nguyen
        }

        return (best, bestR);
    }

    /// <summary>
    /// Trong dải HSV không. Loại rẻ theo V rồi S trước, chỉ tính hue khi đã qua — đúng khuôn
    /// <c>YellowDotDetector.IsYellow</c>; vòng này chạy vài nghìn lần mỗi lượt quét.
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
