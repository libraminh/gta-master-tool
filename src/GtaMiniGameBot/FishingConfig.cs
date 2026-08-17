using System.Text.Json;
using System.Text.Json.Serialization;

namespace GtaMiniGameBot;

/// <summary>
/// ROI câu cá theo từng độ phân giải. Tọa độ tương đối góc trên-trái của màn,
/// không phải tọa độ ảo cả desktop — rút/đổi màn không làm vỡ ô đã khoanh.
/// </summary>
internal sealed class FishingRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    [JsonIgnore]
    public bool IsSet => W >= 8 && H >= 8;

    public Rectangle ToRectangle() => new(X, Y, W, H);

    public static FishingRect FromRelative(Rectangle r) => new()
    {
        X = r.X, Y = r.Y, W = r.Width, H = r.Height
    };
}

/// <summary>
/// Lưới ô kho đồ: người dùng khoanh MỘT hình chữ nhật trùm cả lưới rồi gõ số cột/hàng,
/// từng ô suy ra bằng phép chia — khoanh 25 ô một tay vừa lâu vừa lệch.
/// </summary>
internal sealed class GridSpec
{
    public FishingRect Area { get; set; } = new();
    public int Cols { get; set; }
    public int Rows { get; set; }

    [JsonIgnore]
    public bool IsSet => Area.IsSet && Cols > 0 && Rows > 0;

    [JsonIgnore]
    public int Count => Cols * Rows;

    /// <summary>
    /// Ô thứ <paramref name="index"/> (trái→phải rồi trên→dưới), toạ độ TƯƠNG ĐỐI góc màn.
    /// Chia theo mốc tích luỹ chứ không nhân bề rộng ô, để sai số làm tròn không dồn về cuối hàng.
    /// </summary>
    public Rectangle CellRelative(int index)
    {
        if (!IsSet || index < 0 || index >= Count) return Rectangle.Empty;
        int c = index % Cols, r = index / Cols;
        int x0 = Area.X + c * Area.W / Cols;
        int x1 = Area.X + (c + 1) * Area.W / Cols;
        int y0 = Area.Y + r * Area.H / Rows;
        int y1 = Area.Y + (r + 1) * Area.H / Rows;
        return new Rectangle(x0, y0, x1 - x0, y1 - y0);
    }

    public Rectangle Cell(Screen screen, int index)
    {
        var r = CellRelative(index);
        if (r.IsEmpty) return r;
        var o = screen.Bounds.Location;
        return new Rectangle(o.X + r.X, o.Y + r.Y, r.Width, r.Height);
    }

    /// <summary>Ô đã co vào mỗi cạnh để bỏ viền ô và vệt sáng khi rê chuột.</summary>
    public Rectangle CellInset(Screen screen, int index, double insetFrac)
    {
        var r = Cell(screen, index);
        if (r.IsEmpty) return r;
        int dx = (int)Math.Round(r.Width * insetFrac);
        int dy = (int)Math.Round(r.Height * insetFrac);
        var inner = Rectangle.Inflate(r, -dx, -dy);
        return inner.Width < 4 || inner.Height < 4 ? r : inner;
    }

    public Point CellCenter(Screen screen, int index)
    {
        var r = Cell(screen, index);
        return r.IsEmpty ? Point.Empty : new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    /// <summary>Cols/Rows = 0 nghĩa là chia cho 0 — json cũ thiếu field sẽ rơi đúng vào đó.</summary>
    public void Normalize(int defCols, int defRows)
    {
        Area ??= new FishingRect();
        if (Cols is <= 0 or > 20) Cols = defCols;
        if (Rows is <= 0 or > 20) Rows = defRows;
    }
}

/// <summary>
/// Một ô luôn chứa cá. Người dùng tự khai báo thay vì để bot nhận icon.
/// Đánh đổi có ý thức: bot kéo BẤT KỲ thứ gì nằm trong ô này mà không hỏi lại.
/// </summary>
internal sealed class FishSlot
{
    public const string GridHotbar = "hotbar";
    public const string GridBag = "bag";

    public string Grid { get; set; } = GridHotbar;
    public int Index { get; set; }

    [JsonIgnore]
    public bool IsValid => Index >= 0 && Grid is GridHotbar or GridBag;

    [JsonIgnore]
    public string Label => (Grid == GridBag ? "ba lô" : "phím nhanh") + " #" + Index;
}

internal sealed class FishingProfile
{
    public string Device { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public FishingRect Bar { get; set; } = new();
    public FishingRect Fish { get; set; } = new();
    public FishingRect Reject { get; set; } = new();
    public FishingRect Keep { get; set; } = new();

    // ---------------- đổ cá vào cốp xe ----------------

    public bool TrunkDumpEnabled { get; set; }

    /// <summary>Trùm CẢ chuỗi "27.4/30 KG" — mẫu số đọc được chính là cái neo chống đọc sai.</summary>
    public FishingRect BagWeight { get; set; } = new();
    public FishingRect TrunkWeight { get; set; } = new();

    /// <summary>Chữ "BA LÔ" / "CỐP PHƯƠNG TIỆN" — dùng để biết màn hình nào đang mở.</summary>
    public FishingRect BagHeader { get; set; } = new();
    public FishingRect TrunkHeader { get; set; } = new();

    /// <summary>Tuỳ chọn: dấu hiệu menu tạm dừng, để bắt ca Esc bấm nhầm lúc lệch trạng thái.</summary>
    public FishingRect PauseMarker { get; set; } = new();

    /// <summary>Vùng quét menu radial. Rỗng thì suy từ tâm màn — xem <see cref="AltSearchBand"/>.</summary>
    public FishingRect AltBand { get; set; } = new();
    public FishingRect AltInteract { get; set; } = new();
    public FishingRect AltTrunk { get; set; } = new();
    public FishingRect AltFuel { get; set; } = new();

    public GridSpec Hotbar { get; set; } = new();
    public GridSpec Bag { get; set; } = new();
    public GridSpec Trunk { get; set; } = new();

    /// <summary>
    /// Các ô chứa cá. Cố ý KHÔNG có mặc định: đoán hộ một ô rồi kéo đồ trong đó đi là việc
    /// không được làm ngầm. Khai báo nhiều ô để phòng khi cá tràn sang ô kế vì đầy chồng.
    /// </summary>
    public List<FishSlot> FishSlots { get; set; } = new();

    /// <summary>
    /// Vùng quét cao trùm mọi vị trí nút CẤT VÀO có thể trượt tới. Chưa khoanh thì
    /// suy từ <see cref="Keep"/> — xem <see cref="KeepSearchBand"/>.
    /// </summary>
    public FishingRect KeepBand { get; set; } = new();

    [JsonIgnore]
    public string Key => $"{Width}x{Height}";

    /// <summary>
    /// Vùng quét thật dùng khi dò nút. Ô người dùng khoanh được ưu tiên; không có thì
    /// nới ô Keep xuống ~4 dòng chữ (tên cá dài đẩy hàng nút xuống, không đẩy lên).
    /// </summary>
    public FishingRect KeepSearchBand()
    {
        if (KeepBand.IsSet) return KeepBand;
        if (!Keep.IsSet) return new FishingRect();

        int side = Math.Max(12, (int)Math.Round(Width * 0.02));
        int up = Math.Max(16, (int)Math.Round(Height * 0.03));
        int down = Math.Max(80, (int)Math.Round(Height * 0.16));

        int x = Math.Max(0, Keep.X - side);
        int y = Math.Max(0, Keep.Y - up);
        int w = Math.Min(Width - x, Keep.W + side * 2);
        int h = Math.Min(Height - y, Keep.H + up + down);
        return new FishingRect { X = x, Y = y, W = w, H = h };
    }

    /// <summary>
    /// Vùng quét menu radial. Menu vẽ quanh TÂM MÀN HÌNH nên suy được, không bắt khoanh tay.
    /// </summary>
    public FishingRect AltSearchBand()
    {
        if (AltBand.IsSet) return AltBand;
        if (Width < 200 || Height < 200) return new FishingRect();

        int w = (int)Math.Round(Width * 0.46);
        int h = (int)Math.Round(Height * 0.38);
        return new FishingRect
        {
            X = Math.Max(0, Width / 2 - w / 2),
            Y = Math.Max(0, Height / 2 - h / 2),
            W = Math.Min(Width, w),
            H = Math.Min(Height, h)
        };
    }

    /// <summary>Json cũ thiếu field thì về null/0 — đưa lại mặc định trước khi ai đó chia cho Cols.</summary>
    public void Normalize()
    {
        Bar ??= new FishingRect();
        Fish ??= new FishingRect();
        Reject ??= new FishingRect();
        Keep ??= new FishingRect();
        KeepBand ??= new FishingRect();
        BagWeight ??= new FishingRect();
        TrunkWeight ??= new FishingRect();
        BagHeader ??= new FishingRect();
        TrunkHeader ??= new FishingRect();
        PauseMarker ??= new FishingRect();
        AltBand ??= new FishingRect();
        AltInteract ??= new FishingRect();
        AltTrunk ??= new FishingRect();
        AltFuel ??= new FishingRect();

        Hotbar ??= new GridSpec();
        Bag ??= new GridSpec();
        Trunk ??= new GridSpec();
        Hotbar.Normalize(1, 5);
        Bag.Normalize(5, 5);
        Trunk.Normalize(5, 5);

        FishSlots ??= new List<FishSlot>();
        FishSlots.RemoveAll(s => s is null || !s.IsValid);
    }

    /// <summary>Thiếu gì để bật được đổ cốp — hiện thẳng trên panel thay vì để bot chết giữa chừng.</summary>
    public string DescribeTrunkGaps()
    {
        var missing = new List<string>();
        if (!BagWeight.IsSet) missing.Add("số KG ba lô");
        if (!BagHeader.IsSet) missing.Add("chữ BA LÔ");
        if (!TrunkHeader.IsSet) missing.Add("chữ CỐP");
        if (!AltInteract.IsSet) missing.Add("nút Tương tác");
        if (!AltTrunk.IsSet) missing.Add("nút Cốp xe");
        if (!AltFuel.IsSet) missing.Add("nút Bơm nhiên liệu");
        if (!Hotbar.IsSet) missing.Add("lưới hotbar");
        if (!Bag.IsSet) missing.Add("lưới ba lô");
        if (!Trunk.IsSet) missing.Add("lưới cốp");
        if (FishSlots is not { Count: > 0 }) missing.Add("ô chứa cá");

        if (missing.Count == 0)
            return PauseMarker.IsSet ? "đủ cấu hình đổ cốp" : "đủ cấu hình đổ cốp (chưa khoanh menu tạm dừng)";
        return "thiếu " + string.Join(", ", missing);
    }

    public string DescribeGaps()
    {
        var missing = new List<string>();
        if (!Bar.IsSet) missing.Add("thanh");
        if (!Fish.IsSet) missing.Add("cá");
        if (!Reject.IsSet) missing.Add("thông báo");
        if (!Keep.IsSet) missing.Add("CẤT VÀO");
        if (missing.Count == 0)
            return KeepBand.IsSet ? $"{Key} — đủ 4 ô + vùng quét" : $"{Key} — đủ 4 ô, vùng quét tự suy";
        if (missing.Count == 4) return $"{Key} — chưa khoanh";
        return $"{Key} — thiếu {string.Join(", ", missing)}";
    }
}

internal sealed class FishingConfig
{
    public Dictionary<string, FishingProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double FishNccMin { get; set; } = 0.75;
    public double RejectNccMin { get; set; } = 0.75;

    /// <summary>
    /// Sai lệch cho phép mỗi kênh màu khi dò nền nút CẤT VÀO. Đo trên 3 ảnh panel thật
    /// (nền nút ≈ #223D41): 16–26 đều dò đúng, dưới 14 thì cắt mất phần dưới nút vì nền
    /// nút có gradient nhẹ, từ 28 thì mask lan sang nền panel — chỉ cách nút ~27 mỗi
    /// kênh — làm dải hàng dính liền rồi bắt sai khối.
    /// </summary>
    public int KeepColorTol { get; set; } = 20;
    /// <summary>Tỉ lệ pixel đúng màu tối thiểu trong ô nút vừa dò được.</summary>
    public double KeepDensityMin { get; set; } = 0.55;

    /// <summary>
    /// NCC tối thiểu với keep.png thì mới coi khối màu dò được là nút thật. Số âm = tắt.
    ///
    /// Đo trên log 17/08: nút THẬT chỉ đạt 0.402–0.420 (mẫu lệch scale so với panel thật),
    /// nên đừng nâng lên 0.7 như <see cref="FishNccMin"/> — sẽ chặn sạch nút thật rồi đẩy
    /// mọi lượt vào nhánh click mù. Đây chỉ là lớp chặn phụ; cửa chính là
    /// <see cref="KeepAnchorTolPx"/>.
    /// </summary>
    public double KeepNccMin { get; set; } = 0.30;

    /// <summary>Click lại tối đa mấy lần nếu nút vẫn còn sau <see cref="KeepGoneMs"/>.</summary>
    public int KeepClickRetries { get; set; } = 2;

    /// <summary>
    /// Click lại chỉ được phép khi khối dò được còn nằm quanh chỗ nút vừa click, lệch tâm
    /// tối đa ngần này pixel. 0 = không bao giờ click lại.
    ///
    /// Vì sao cần: dò nút thuần theo màu trên cả dải cao 608px, nên khi panel đã tắt nó
    /// hay bắt nhầm mảng tối khác rồi click thẳng vào thế giới game — trong GTA đó là cú
    /// đấm vào người đứng cạnh. Log 17/08 có hai lần như vậy, lệch 100px và 174px.
    /// Nút cao 77px và panel thật đứng yên trong lúc chờ, nên 40 là rộng rãi.
    /// </summary>
    public int KeepAnchorTolPx { get; set; } = 40;

    public int WaitBiteMs { get; set; } = 25_000;

    /// <summary>
    /// Thả câu xong, chờ ngần này mà thanh câu vẫn chưa hiện thì coi như cú thả TRƯỢT
    /// (animation cất cá nuốt mất phím 4, rồi Space thành lệnh nhảy) và thả lại luôn,
    /// khỏi đứng chờ hết <see cref="WaitBiteMs"/>. 0 = tắt, chờ mù như cũ.
    ///
    /// Đo log 17/08: cú thả ngay sau khi cất cá hụt 26% (34/132), trong khi câu lại lúc
    /// nhân vật đứng rảnh chỉ hụt 6% (13/209). Mỗi lần hụt tốn trọn 25 s.
    /// </summary>
    public int CastConfirmMs { get; set; } = 4_000;

    /// <summary>
    /// Thả lại tối đa mấy lần trước khi chịu thua và chờ theo <see cref="WaitBiteMs"/>.
    /// Trần này là dây an toàn: nếu thanh câu vì lý do nào đó không dò được lúc chờ, bot
    /// chỉ thừa vài cú thả rồi chạy y như cũ, chứ không kẹt vòng lặp thả câu vô hạn.
    /// </summary>
    public int CastConfirmRetries { get; set; } = 2;

    /// <summary>
    /// Chờ sau khi click CẤT VÀO rồi mới bấm 4 + space, để animation cất cá chạy xong.
    ///
    /// Mặc định 0. Khi đã có <see cref="CastConfirmMs"/> thì cú trượt chỉ còn tốn ~4 s,
    /// nên bắt CẢ 132 vòng chờ phòng hờ 1.2 s (158 s) hoá ra đắt hơn là để 34 cú trượt
    /// tự lộ rồi thả lại (136 s). Chỉ nâng lên 1200–2500 nếu log vẫn cho thấy nhóm
    /// "sau cất cá" trượt nhiều hơn hẳn nhóm còn lại.
    /// </summary>
    public int AfterKeepCastMs { get; set; } = 0;

    public int FightTimeoutMs { get; set; } = 40_000;
    public int AfterReleaseMs { get; set; } = 1_200;
    public int CastCooldownMs { get; set; } = 1_500;
    /// <summary>Sau phím 4, chờ rồi bấm Space (thay combo AutoHotkey).</summary>
    public int CastSpaceDelayMs { get; set; } = 200;
    /// <summary>Sau khi thấy chê mồi, chờ rồi mới bấm 4. 0 = ngay.</summary>
    public int RejectRecastMs { get; set; } = 100;
    public int PollMs { get; set; } = 100;
    public int BiteDebounceFrames { get; set; } = 3;
    public double DoneFill01 { get; set; } = 0.95;
    public int WaitKeepMs { get; set; } = 8_000;

    /// <summary>
    /// Hết <see cref="WaitKeepMs"/> mà không dò ra nút thì có click mù vào tâm ô đã khoanh
    /// không. Bật để không mất con cá tên ngắn (panel hiện quá nhanh, dò không kịp);
    /// tắt nếu vẫn còn bị đấm người sau khi câu.
    ///
    /// Riêng trường hợp thiếu mẫu/vùng thì KHÔNG BAO GIỜ click mù, bất kể cờ này —
    /// lúc đó mọi lượt câu đều trượt, thành đấm liên tục.
    ///
    /// Nullable vì json cũ thiếu field sẽ ra false, mà mặc định phải là true —
    /// <see cref="Normalize"/> phân biệt "thiếu" với "người dùng tắt hẳn".
    /// </summary>
    public bool? BlindKeepClick { get; set; }

    public int KeepAppearMs { get; set; } = 400;
    public int KeepGoneMs { get; set; } = 1_500;
    public int KeepHoverMs { get; set; } = 320;
    public int KeepMoveSteps { get; set; } = 8;
    public string WindowMatch { get; set; } = "PlayXGTA";

    // ============ đổ cá vào cốp xe ============

    // -- ngưỡng đổ --
    /// <summary>Mấy con cá mới mở Tab đọc KG một lần. Đọc mỗi con thì che màn quá nhiều.</summary>
    public int WeightCheckEveryCatches { get; set; } = 5;
    public double BagCapKg { get; set; } = 30.0;
    public double TrunkCapKg { get; set; } = 60.0;
    /// <summary>Còn thiếu bao nhiêu kg nữa mới đầy thì đã đi đổ — chừa chỗ cho một con cá to.</summary>
    public double DumpMarginKg { get; set; } = 3.0;
    /// <summary>Lưới an toàn khi OCR hỏng: cứ bấy nhiêu con là đổ, không cần biết KG.</summary>
    public int CatchesPerDumpFallback { get; set; } = 20;
    /// <summary>
    /// Trần cứng: đổ sau mỗi bấy nhiêu con dù ba lô còn nhẹ. 0 = tắt.
    /// Cắt nhỏ mỗi lượt kéo, vì thứ làm hỏng không phải cốp đầy mà là MỘT CỤM quá nặng —
    /// một cụm 13 con nặng 22.7 kg thì cốp còn 9.9 kg là chắc chắn không lọt.
    /// </summary>
    public int DumpEveryCatches { get; set; }
    /// <summary>Đổ xong KG phải giảm ít nhất bấy nhiêu, không thì coi như kéo trượt.</summary>
    public double MinDropKg { get; set; } = 0.5;
    /// <summary>Hỏng liên tiếp bấy nhiêu lần thì bỏ hẳn OCR cho phần còn lại của phiên.</summary>
    public int WeightOcrFailMax { get; set; } = 3;
    /// <summary>Hai lần đọc liên tiếp lệch quá bấy nhiêu kg = đọc sai, không phải câu được cá to.</summary>
    public double MaxWeightJumpKg { get; set; } = 5.0;

    // -- đọc chữ số --
    public double DigitNccMin { get; set; } = 0.80;
    /// <summary>Cách biệt tối thiểu giữa chữ số nhất và nhì. Sát nhau = đoán mò, thà báo hỏng.</summary>
    public double DigitMarginMin { get; set; } = 0.08;
    /// <summary>
    /// Chỉ so hai glyph khi bề rộng lệch trong ngần này px — "1" và "8" nhờ vậy không lẫn.
    /// 1 px là quá chặt: chính bề rộng khối cũng nhích một pixel khi ngưỡng Otsu đổi giữa hai
    /// lần chụp, nên mẫu "3" học ở lần này trượt mất chữ 3 ở lần sau.
    /// </summary>
    public int DigitWidthTolPx { get; set; } = 2;
    public int DigitMinGlyphW { get; set; } = 2;
    public int DigitMinGlyphInk { get; set; } = 6;
    /// <summary>
    /// Hai cụm cách nhau không quá ngần này cột trống thì gộp làm một glyph.
    /// Mặc định 0 (không gộp): ở cỡ chữ này khe giữa hai chữ số cũng chỉ 1–2 px, gộp bừa là
    /// "27" dính thành một khối. Chỉ nâng lên khi thấy một chữ số bị tách đôi.
    /// </summary>
    public int DigitMergeGapPx { get; set; }
    /// <summary>Sàn cứng cho ngưỡng Otsu: nền panel tối nên đừng để ngưỡng tụt xuống nhiễu.</summary>
    public int DigitInkMinGray { get; set; } = 90;

    // -- menu radial --
    public int MenuColorTol { get; set; } = 22;
    public double MenuDensityMin { get; set; } = 0.50;
    public double MenuNccMin { get; set; } = 0.70;
    /// <summary>"Cốp xe" phải hơn "Bơm nhiên liệu" ngần này mới dám click — so sánh, không phải ngưỡng.</summary>
    public double MenuNccMargin { get; set; } = 0.06;
    public int MenuHoverMs { get; set; } = 200;
    public int MenuMoveSteps { get; set; } = 12;
    public int MenuClickRetries { get; set; } = 2;
    public int AltMenuAppearMs { get; set; } = 250;
    public int AltMenuWaitMs { get; set; } = 1_500;
    public int AltRetries { get; set; } = 2;
    public int AltRetryGapMs { get; set; } = 400;
    /// <summary>
    /// Trần cứng cho thời gian giữ Alt. Trong lúc Alt còn xuống thì phím tắt dừng bot (đăng ký
    /// không modifier) KHÔNG nổ, nên phải có đồng hồ tự nhả kể cả khi luồng bot treo.
    /// </summary>
    public int AltMaxHoldMs { get; set; } = 4_000;

    // -- thời gian màn hình --
    public int TabToggleMs { get; set; } = 900;
    public int TabWaitMs { get; set; } = 2_500;
    public int TrunkOpenMs { get; set; } = 3_000;
    public int EscCloseMs { get; set; } = 1_500;
    public int AfterEscMs { get; set; } = 300;
    public int AfterDumpMs { get; set; } = 600;
    /// <summary>
    /// Giữ S bao lâu sau khi đóng cốp, để nhân vật quay mặt lại. Tương tác với xe làm nhân vật
    /// xoay về phía xe, mà thả câu thì phải hướng ra hồ — không quay lại thì phím 4 vô tác dụng
    /// và bot câu hụt cả phiên mà log vẫn trông bình thường. 0 = tắt.
    /// </summary>
    public int AfterDumpTurnMs { get; set; } = 450;
    public int DumpRetryGapMs { get; set; } = 1_500;
    public int MaxDumpMs { get; set; } = 60_000;

    // -- kéo thả --
    public int DragGrabMs { get; set; } = 140;
    public int DragMoveSteps { get; set; } = 20;
    public int DragStepMs { get; set; } = 10;
    public int DragDropHoverMs { get; set; } = 180;
    public int DragSettleMs { get; set; } = 350;
    public int DragRetries { get; set; } = 2;
    public int MaxDragsPerDump { get; set; } = 12;
    /// <summary>
    /// Kéo bằng SetCursorPos thay vì SendInput. Mặc định bật: SendInput bị game đọc thành lệnh
    /// xoay camera, mà lần đổ cốp sau còn cần camera hướng đúng vào xe. Bật tắt được phòng khi
    /// UI kho đồ không nhận ra cú kéo nếu thiếu sự kiện chuột.
    /// </summary>
    public bool DragCursorOnly { get; set; } = true;

    // -- ô lưới --
    public double CellInsetFrac { get; set; } = 0.15;
    /// <summary>Phần góc dưới-phải bị bỏ khỏi phép đo: chỗ game vẽ số lượng.</summary>
    public double BadgeFrac { get; set; } = 0.42;
    /// <summary>
    /// Độ lệch chuẩn xám tối đa để coi là ô trống. Hiệu chỉnh được, không đoán.
    /// Ô trống là mảng phẳng; icon thì lắm chi tiết. Đo trên ảnh thật: trống 0.5–1.7,
    /// có đồ 10.7–55.4.
    /// </summary>
    public double CellEmptyStdMax { get; set; } = 6.0;
    public double HeaderNccMin { get; set; } = 0.70;
    public int ShotCountdownSec { get; set; } = 5;

    /// <summary>Json cũ thiếu field thì về 0 — khôi phục mặc định, không đoán timeout = 0.</summary>
    public void Normalize()
    {
        Profiles ??= new Dictionary<string, FishingProfile>(StringComparer.OrdinalIgnoreCase);
        if (FishNccMin <= 0) FishNccMin = 0.75;
        if (RejectNccMin <= 0) RejectNccMin = 0.75;
        if (KeepColorTol <= 0) KeepColorTol = 20;
        if (KeepDensityMin <= 0 || KeepDensityMin > 1) KeepDensityMin = 0.55;
        if (KeepNccMin == 0 || KeepNccMin > 1) KeepNccMin = 0.30;   // <0 = cố ý tắt, giữ nguyên
        if (KeepClickRetries < 0) KeepClickRetries = 2;
        if (KeepAnchorTolPx < 0) KeepAnchorTolPx = 40;
        if (WaitBiteMs <= 0) WaitBiteMs = 25_000;
        if (CastConfirmMs < 0) CastConfirmMs = 4_000;        // 0 = cố ý tắt, giữ nguyên
        if (CastConfirmRetries < 0) CastConfirmRetries = 2;
        if (AfterKeepCastMs < 0) AfterKeepCastMs = 0;
        if (FightTimeoutMs <= 0) FightTimeoutMs = 40_000;
        if (AfterReleaseMs <= 0) AfterReleaseMs = 1_200;
        if (CastCooldownMs <= 0) CastCooldownMs = 1_500;
        if (CastSpaceDelayMs < 0) CastSpaceDelayMs = 200;
        if (RejectRecastMs < 0) RejectRecastMs = 100;
        if (PollMs <= 0) PollMs = 100;
        if (BiteDebounceFrames <= 0) BiteDebounceFrames = 3;
        if (DoneFill01 <= 0 || DoneFill01 > 1) DoneFill01 = 0.95;
        if (WaitKeepMs <= 0) WaitKeepMs = 8_000;
        BlindKeepClick ??= true;
        if (KeepAppearMs <= 0) KeepAppearMs = 400;
        if (KeepGoneMs < 0) KeepGoneMs = 1_500;
        if (KeepHoverMs <= 0) KeepHoverMs = 320;
        if (KeepMoveSteps <= 0) KeepMoveSteps = 8;
        if (string.IsNullOrWhiteSpace(WindowMatch)) WindowMatch = "PlayXGTA";

        if (WeightCheckEveryCatches <= 0) WeightCheckEveryCatches = 5;
        if (BagCapKg <= 0) BagCapKg = 30.0;
        if (TrunkCapKg <= 0) TrunkCapKg = 60.0;
        if (DumpMarginKg <= 0) DumpMarginKg = 3.0;
        if (CatchesPerDumpFallback <= 0) CatchesPerDumpFallback = 20;
        if (DumpEveryCatches < 0) DumpEveryCatches = 0;
        if (MinDropKg <= 0) MinDropKg = 0.5;
        if (WeightOcrFailMax <= 0) WeightOcrFailMax = 3;
        if (MaxWeightJumpKg <= 0) MaxWeightJumpKg = 5.0;

        if (DigitNccMin is <= 0 or > 1) DigitNccMin = 0.80;
        if (DigitMarginMin is < 0 or > 1) DigitMarginMin = 0.08;
        // San cung 2, khong phai 0: khong co UI nao chinh so nay nen json cu chi co the la mac
        // dinh cu (1) — ma 1 la qua chat, xem chu thich o khai bao.
        if (DigitWidthTolPx < 2) DigitWidthTolPx = 2;
        if (DigitMinGlyphW <= 0) DigitMinGlyphW = 2;
        if (DigitMinGlyphInk <= 0) DigitMinGlyphInk = 6;
        if (DigitMergeGapPx < 0) DigitMergeGapPx = 0;
        if (DigitInkMinGray is <= 0 or > 250) DigitInkMinGray = 90;

        if (MenuColorTol <= 0) MenuColorTol = 22;
        if (MenuDensityMin is <= 0 or > 1) MenuDensityMin = 0.50;
        if (MenuNccMin is <= 0 or > 1) MenuNccMin = 0.70;
        if (MenuNccMargin is < 0 or > 1) MenuNccMargin = 0.06;
        if (MenuHoverMs <= 0) MenuHoverMs = 200;
        if (MenuMoveSteps <= 0) MenuMoveSteps = 12;
        if (MenuClickRetries < 0) MenuClickRetries = 2;
        if (AltMenuAppearMs < 0) AltMenuAppearMs = 250;
        if (AltMenuWaitMs <= 0) AltMenuWaitMs = 1_500;
        if (AltRetries < 0) AltRetries = 2;
        if (AltRetryGapMs < 0) AltRetryGapMs = 400;
        if (AltMaxHoldMs <= 0) AltMaxHoldMs = 4_000;

        if (TabToggleMs <= 0) TabToggleMs = 900;
        if (TabWaitMs <= 0) TabWaitMs = 2_500;
        if (TrunkOpenMs <= 0) TrunkOpenMs = 3_000;
        if (EscCloseMs <= 0) EscCloseMs = 1_500;
        if (AfterEscMs < 0) AfterEscMs = 300;
        if (AfterDumpMs < 0) AfterDumpMs = 600;
        if (AfterDumpTurnMs < 0) AfterDumpTurnMs = 450;
        if (DumpRetryGapMs < 0) DumpRetryGapMs = 1_500;
        if (MaxDumpMs <= 0) MaxDumpMs = 60_000;

        if (DragGrabMs <= 0) DragGrabMs = 140;
        if (DragMoveSteps <= 0) DragMoveSteps = 20;
        if (DragStepMs <= 0) DragStepMs = 10;
        if (DragDropHoverMs <= 0) DragDropHoverMs = 180;
        if (DragSettleMs <= 0) DragSettleMs = 350;
        if (DragRetries < 0) DragRetries = 2;
        if (MaxDragsPerDump <= 0) MaxDragsPerDump = 12;

        if (CellInsetFrac is < 0 or > 0.4) CellInsetFrac = 0.15;
        if (BadgeFrac is <= 0 or >= 0.9) BadgeFrac = 0.42;
        // Chan tren 8: json cu co the dang giu 12.0 cua ban truoc, ma o co do thap nhat do duoc
        // la 10.7 — de nguyen 12 la o do bi doc thanh o trong roi tha ca de len, tuc HOAN DOI.
        if (CellEmptyStdMax is <= 0 or > 8) CellEmptyStdMax = 6.0;
        if (HeaderNccMin is <= 0 or > 1) HeaderNccMin = 0.70;
        if (ShotCountdownSec <= 0) ShotCountdownSec = 5;

        foreach (var p in Profiles.Values) p?.Normalize();
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultPath =>
        Path.Combine(AppPaths.Root, "fishing.json");

    public static string ProfileDir(string key) =>
        Path.Combine(AppPaths.Root, "fishing", key);

    public static string BarPreviewPath(string key) => Path.Combine(ProfileDir(key), "bar.png");
    public static string FishTemplatePath(string key) => Path.Combine(ProfileDir(key), "fish.png");
    public static string RejectTemplatePath(string key) => Path.Combine(ProfileDir(key), "reject.png");
    public static string KeepTemplatePath(string key) => Path.Combine(ProfileDir(key), "keep.png");
    public static string KeepBandPreviewPath(string key) => Path.Combine(ProfileDir(key), "keep-band.png");

    // ---------------- đổ cá vào cốp xe ----------------

    /// <summary>
    /// Ảnh tĩnh chụp cả màn game. Phải chụp tĩnh rồi khoanh trên ảnh, không khoanh trực tiếp
    /// được: menu radial cần giữ Alt và tắt ngay khi game mất focus.
    /// </summary>
    public static string ShotDir(string key) => Path.Combine(ProfileDir(key), "shots");
    public static string ShotPath(string key, string name) => Path.Combine(ShotDir(key), name + ".png");

    /// <summary>Mẫu NCC cắt ra từ ảnh tĩnh: nhãn nút menu, chữ tiêu đề cột.</summary>
    public static string TrunkTemplatePath(string key, string name) =>
        Path.Combine(ProfileDir(key), "trunk", name + ".png");

    public static string DigitDir(string key) => Path.Combine(ProfileDir(key), "digits");
    public static string DigitPath(string key, string cls) => Path.Combine(DigitDir(key), cls + ".png");
    public static string DigitUnknownDir(string key) => Path.Combine(DigitDir(key), "unknown");

    public static string OcrDebugDir(string key) => Path.Combine(ProfileDir(key), "debug-ocr");
    public static string InvDebugDir(string key) => Path.Combine(ProfileDir(key), "debug-inv");

    /// <summary>Tên file cho một ký tự — '.' và '/' không đặt tên file được.</summary>
    public static string DigitClassName(char c) => c switch
    {
        '.' => "dot",
        '/' => "slash",
        >= '0' and <= '9' => "d" + c,
        _ => null
    };

    public static char DigitClassChar(string cls) => cls switch
    {
        "dot" => '.',
        "slash" => '/',
        { Length: 2 } s when s[0] == 'd' && s[1] is >= '0' and <= '9' => s[1],
        _ => '\0'
    };

    public void Save(string path = null)
    {
        path ??= DefaultPath;
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }

    public static FishingConfig Load(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<FishingConfig>(File.ReadAllText(path), Opts);
                if (cfg is not null)
                {
                    cfg.Profiles = new Dictionary<string, FishingProfile>(
                        cfg.Profiles ?? new(), StringComparer.OrdinalIgnoreCase);
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch { /* file hong -> config rong, user khoanh lai */ }
        return new FishingConfig();
    }

    public FishingProfile GetOrCreate(Screen screen)
    {
        var b = screen.Bounds;
        string key = $"{b.Width}x{b.Height}";
        if (!Profiles.TryGetValue(key, out var p) || p is null)
        {
            p = new FishingProfile
            {
                Device = screen.DeviceName,
                Width = b.Width,
                Height = b.Height
            };
            Profiles[key] = p;
        }
        else
        {
            p.Device = screen.DeviceName;
            p.Width = b.Width;
            p.Height = b.Height;
        }
        return p;
    }

    public FishingProfile TryGet(Screen screen)
    {
        string key = $"{screen.Bounds.Width}x{screen.Bounds.Height}";
        return Profiles.TryGetValue(key, out var p) ? p : null;
    }

    /// <summary>
    /// Tìm màn đang gắn: ưu tiên DeviceName đã khoanh, không có thì khớp WxH.
    /// </summary>
    public static Screen ResolveScreen(FishingProfile profile)
    {
        if (profile is null) return null;
        var all = Screen.AllScreens;
        var byDevice = all.FirstOrDefault(s =>
            string.Equals(s.DeviceName, profile.Device, StringComparison.OrdinalIgnoreCase));
        if (byDevice is not null) return byDevice;
        return all.FirstOrDefault(s =>
            s.Bounds.Width == profile.Width && s.Bounds.Height == profile.Height);
    }

    public static Rectangle ToAbsolute(Screen screen, FishingRect r)
    {
        var o = screen.Bounds.Location;
        return new Rectangle(o.X + r.X, o.Y + r.Y, r.W, r.H);
    }

    public static Screen Prefer2kOrPrimary()
    {
        var twoK = Screen.AllScreens.FirstOrDefault(s =>
            s.Bounds.Width == 2560 && s.Bounds.Height == 1440);
        return twoK ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }
}
