using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Một lượt đọc panel vật phẩm (hiện ra khi chuột phải vào một ô kho đồ).
///
/// Hai con số của dòng đầu — "15 ĐƠN VỊ / 26.250 KG" — là thứ duy nhất trong cả game nói thẳng
/// một chồng cá gồm mấy con và nặng bao nhiêu. Không có nó thì kg mỗi con chỉ suy ra được bằng
/// phép trừ KG ba lô, mà phép trừ đó làm tròn tới 0.1 kg và chỉ đúng khi vừa kéo trọn một ô.
/// </summary>
internal sealed class SplitPanelRead
{
    public bool Ok { get; init; }

    /// <summary>Hỏng ở cổng nào. Ghi tên cổng để đọc log là biết sửa gì, y như <see cref="WeightRead"/>.</summary>
    public string Reason { get; init; }

    /// <summary>
    /// Nút ĐANG SÁNG lúc panel vừa mở — tức nút DÙNG, nút ngoài cùng bên trái của dải.
    ///
    /// Đây là cái THƯỚC, không phải cái đích: từ kích thước và vị trí của nó suy ra cả dải ba
    /// nút. Cố ý KHÔNG có thuộc tính "chỗ cần click" trong lớp này — bản đầu có, và cái tên
    /// <c>Click</c> đó khiến người gọi bấm thẳng vào đây, tức bấm vào DÙNG. Chỗ click phải đi
    /// qua <see cref="SplitPanelReader.MiddleButtonCentre"/> và phải được hover xác nhận trước.
    /// </summary>
    public Rectangle Button { get; init; }

    /// <summary>Bề ngang panel suy từ nút trái nhất — dải ba nút chiếm trọn bề ngang panel.</summary>
    public Rectangle Panel { get; init; }

    public int Count { get; init; } = -1;
    public double TotalKg { get; init; } = -1;

    public string Text { get; init; } = "";
    public string Trace { get; init; } = "";

    /// <summary>Kg mỗi con. -1 khi chưa đọc được.</summary>
    public double KgPerUnit => Count > 0 && TotalKg > 0 ? TotalKg / Count : -1;

    /// <summary>Đã có thước để suy ra dải nút. Chưa có nghĩa là chưa được phép bấm gì.</summary>
    public bool HasButton => !Button.IsEmpty;

    public override string ToString() =>
        Ok ? $"{Count} đơn vị / {TotalKg:0.000} kg ({KgPerUnit:0.000} kg mỗi con)"
           : $"đọc hỏng ({Reason})" + (Text.Length > 0 ? $" — “{Text}”" : "");
}

/// <summary>
/// Đọc panel vật phẩm: dò nút TÁCH rồi đọc dòng "n ĐƠN VỊ / x.xxx KG".
///
/// Tách hẳn khỏi <see cref="ItemSplitter"/> và chỉ nhận <see cref="IPixelSource"/> để chạy được
/// cả trên màn hình thật lẫn trên ảnh tĩnh — nghĩa là <c>--verify-split</c> soi được toàn bộ
/// phần suy luận mà không phải đứng trong game, đúng lối đã dùng cho lưới ô và cho KG.
/// </summary>
internal static class SplitPanelReader
{
    /// <summary>
    /// Nền của nút ĐANG SÁNG: xanh lục tươi, kênh G vượt hẳn hai kênh kia.
    ///
    /// ĐỌC KỸ CHỖ NÀY TRƯỚC KHI DÙNG: màu xanh KHÔNG chỉ ra nút nào là nút TÁCH. Nó chỉ nói
    /// "nút này đang sáng" — game tô nút dưới con trỏ, và tô nút DÙNG làm mặc định lúc panel
    /// vừa mở. Bản đầu của lớp này tưởng xanh = TÁCH, nên nó bấm đúng vào DÙNG (ảnh 27/08) và
    /// làm mất cá. Nút TÁCH được xác định bằng THỨ TỰ trong dải, xem
    /// <see cref="MiddleButtonCentre"/>.
    /// </summary>
    public static bool IsButtonGreen(int b, int g, int r) =>
        g > 110 && g > r + 45 && g > b + 35;

    /// <summary>
    /// Tâm nút GIỮA của dải, suy từ nút ngoài cùng bên trái.
    ///
    /// Dải luôn là ba nút chia đều bề ngang panel — DÙNG | TÁCH | VỨT — nên nút giữa là TÁCH.
    /// Đây là chỗ duy nhất trong lớp biết bố cục đó, và nó chỉ là phép tính; việc chứng minh
    /// rằng cái nút ở đó thật sự tồn tại và thật sự là nút giữa thuộc về
    /// <see cref="ButtonCovering"/> cùng chuỗi hover trong <see cref="ItemSplitter"/>.
    /// </summary>
    public static Point MiddleButtonCentre(Rectangle first) =>
        new(first.Left + first.Width + first.Width / 2, first.Top + first.Height / 2);

    /// <summary>Tâm nút thứ ba (VỨT) — chỉ dùng để xác nhận dải có đủ ba nút. KHÔNG bao giờ click.</summary>
    public static Point ThirdButtonCentre(Rectangle first) =>
        new(first.Left + first.Width * 2 + first.Width / 2, first.Top + first.Height / 2);

    /// <summary>Tâm chỗ lẽ ra là nút thứ 0 — phải TRỐNG, đó là cách biết nút kia là trái nhất.</summary>
    public static Point BeforeFirstCentre(Rectangle first) =>
        new(first.Left - first.Width / 2, first.Top + first.Height / 2);

    /// <summary>
    /// Nút đang sáng có PHỦ điểm <paramref name="p"/> không.
    ///
    /// Đây là phép đo nền tảng của cả chuỗi xác nhận: rê con trỏ tới một điểm rồi hỏi câu này.
    /// Trả lời "có" nghĩa là dưới con trỏ đúng là một nút — vì game vừa tô sáng nó. Không cần
    /// biết nút tên gì, và cũng không cần khối xanh là khối duy nhất: nút mặc định có thể còn
    /// sáng cùng lúc, nên hỏi "khối nào phủ điểm này" mới đúng chứ không phải "có mấy khối".
    /// </summary>
    public static bool ButtonCovering(IPixelSource src, Rectangle band, FishingConfig cfg,
                                      int screenW, int screenH, Point p, out Rectangle box)
    {
        box = Rectangle.Empty;

        double expW = screenW * cfg.SplitButtonWidthFrac;
        double expH = screenH * cfg.SplitButtonHeightFrac;
        int minArea = (int)Math.Round(expW * expH * cfg.SplitButtonAreaFracMin);

        foreach (var b in GreenBlobs(src, band, Math.Max(64, minArea), 8))
        {
            if (!b.Box.Contains(p)) continue;
            if (Math.Abs(b.Aspect - cfg.SplitButtonAspect) > cfg.SplitButtonAspectTol) continue;
            if (b.Fill < cfg.SplitButtonFillMin) continue;

            box = b.Box;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Dò nút đang sáng trong <paramref name="band"/> — dùng làm THƯỚC, không phải làm đích.
    ///
    /// Trả về hộp của nút đang sáng lúc panel vừa mở, tức nút DÙNG. Giá trị của nó nằm ở kích
    /// thước và vị trí: từ một nút suy ra được cả dải ba nút. Người gọi TUYỆT ĐỐI không được
    /// click vào hộp này — đó chính là cái lỗi đã làm mất cá.
    ///
    /// Các cửa vẫn giữ nguyên vì chúng lọc ra "một viên nút" chứ không lọc ra tên nút:
    ///   1. đủ lớn so với màn hình (nút chiếm ~1/3 bề ngang panel);
    ///   2. tỉ lệ rộng/cao khớp viên nút;
    ///   3. ĐẶC (chữ trắng khoét vào giữa nhưng nền vẫn kín);
    ///   4. CÁCH BIỆT hẳn khối xanh lớn thứ hai — lúc panel vừa mở chỉ có đúng một nút sáng,
    ///      nên hai khối ngang nhau nghĩa là ta đang nhìn vào cái gì khác.
    /// </summary>
    public static Rectangle FindButton(IPixelSource src, Rectangle band, FishingConfig cfg,
                                       int screenW, int screenH, out string why)
    {
        why = null;
        if (band.Width < 16 || band.Height < 16) { why = "vùng quét quá nhỏ"; return Rectangle.Empty; }

        src.Refresh();
        var mask = new Mask(band.Width, band.Height, src.MaskBuffer(IsButtonGreen));

        // Ky vong kich thuoc nut theo ti le man hinh, khong go cung pixel: cung mot bo so chay
        // duoc ca 1920 lan 2560.
        double expW = screenW * cfg.SplitButtonWidthFrac;
        double expH = screenH * cfg.SplitButtonHeightFrac;
        int minArea = (int)Math.Round(expW * expH * cfg.SplitButtonAreaFracMin);

        var blobs = ImageOps.Blobs(mask, Math.Max(64, minArea));
        if (blobs.Count == 0) { why = "không thấy mảng xanh nào đủ lớn"; return Rectangle.Empty; }

        var ranked = blobs.OrderByDescending(b => b.Area).ToList();
        var top = ranked[0];

        if (ranked.Count > 1 && ranked[1].Area > top.Area * cfg.SplitButtonRivalMax)
        {
            why = $"hai mảng xanh ngang nhau ({top.Area} và {ranked[1].Area}) — " +
                  "không dám đoán cái nào là nút TÁCH";
            return Rectangle.Empty;
        }

        var box = top.Box;
        double aspect = box.Width / (double)Math.Max(1, box.Height);
        if (Math.Abs(aspect - cfg.SplitButtonAspect) > cfg.SplitButtonAspectTol)
        {
            why = $"mảng {box.Width}×{box.Height} có tỉ lệ {aspect:F2}, lệch quá " +
                  $"{cfg.SplitButtonAspectTol:F2} so với {cfg.SplitButtonAspect:F2}";
            return Rectangle.Empty;
        }

        double fill = top.Area / (double)Math.Max(1, box.Width * box.Height);
        if (fill < cfg.SplitButtonFillMin)
        {
            why = $"mảng {box.Width}×{box.Height} chỉ đặc {fill:F2} — nút thì phải kín";
            return Rectangle.Empty;
        }

        // Blob.Box la toa do trong BAND, doi ve toa do cua nguon.
        return new Rectangle(band.X + box.X, band.Y + box.Y, box.Width, box.Height);
    }

    /// <summary>Một mảng xanh tìm được, kèm mọi số đo mà các cửa của <see cref="FindButton"/> dùng.</summary>
    internal sealed record GreenBlob(Rectangle Box, int Area, double Aspect, double Fill);

    /// <summary>
    /// Liệt kê MỌI mảng xanh trong vùng quét, không qua cửa nào — chỉ để chẩn đoán.
    ///
    /// <see cref="FindButton"/> cố ý chỉ trả về "được / không được" kèm một câu lý do, vì lúc
    /// chạy thật thì đó là tất cả những gì cần biết. Nhưng khi nút không dò ra trên máy người
    /// dùng thì câu lý do đó không đủ: phải thấy con số thật để biết cửa nào cắt nhầm, hay
    /// vùng quét vốn dĩ chẳng có mảng xanh nào.
    /// </summary>
    public static List<GreenBlob> GreenBlobs(IPixelSource src, Rectangle band, int minArea, int top)
    {
        src.Refresh();
        var mask = new Mask(band.Width, band.Height, src.MaskBuffer(IsButtonGreen));

        return ImageOps.Blobs(mask, Math.Max(1, minArea))
            .OrderByDescending(b => b.Area)
            .Take(top)
            .Select(b => new GreenBlob(
                new Rectangle(band.X + b.Box.X, band.Y + b.Box.Y, b.Box.Width, b.Box.Height),
                b.Area,
                b.Box.Width / (double)Math.Max(1, b.Box.Height),
                b.Area / (double)Math.Max(1, b.Box.Width * b.Box.Height)))
            .ToList();
    }

    /// <summary>
    /// Đọc dòng "n ĐƠN VỊ / x.xxx KG" của panel đang mở.
    ///
    /// <paramref name="cellTop"/> là mép trên ô vừa chuột phải: panel neo đỉnh theo ô đó, còn
    /// ĐÁY thì trôi theo độ dài phần mô tả loài, nên mọi mốc tính ngược từ nút đều sai. Bề
    /// ngang thì ngược lại — lấy từ nút, vì panel có thể lật sang trái khi ô nằm sát mép phải.
    ///
    /// Quét lệch vài mốc dọc rồi lấy lần đọc hợp lệ đầu tiên. Rẻ (mỗi lượt chỉ là một ô ảnh vài
    /// nghìn pixel) và cần thật: một dòng chữ cao chưa tới 20 px thì lệch 4 px là cụt mất chân chữ.
    /// </summary>
    public static SplitPanelRead ReadLine(IPixelSource src, Rectangle panel, int cellTop,
                                          DigitAtlas atlas, FishingConfig cfg, int screenH)
    {
        var button = Rectangle.Empty;
        int h = Math.Max(10, (int)Math.Round(screenH * cfg.SplitLineHeightFrac));
        int x = panel.Left + (int)Math.Round(panel.Width * cfg.SplitLineLeftFrac);
        int w = Math.Max(24, (int)Math.Round(panel.Width * cfg.SplitLineWidthFrac));
        int step = Math.Max(2, (int)Math.Round(screenH * cfg.SplitLineNudgeFrac));

        SplitPanelRead last = null;
        for (int k = -cfg.SplitLineNudges; k <= cfg.SplitLineNudges; k++)
        {
            int y = cellTop + (int)Math.Round(screenH * cfg.SplitLineTopFrac) + k * step;
            var r = ParseLine(src, new Rectangle(x, y, w, h), atlas, cfg, button, panel);
            if (r.Ok) return r;
            last ??= r;
        }
        return last ?? new SplitPanelRead { Reason = "không quét được dòng nào", Button = button, Panel = panel };
    }

    /// <summary>
    /// Khung panel suy từ chính ô vừa chuột phải.
    ///
    /// Panel NEO theo ô: mép trái lùi ra ngoài mép ô một chút, đỉnh thụt xuống một chút, bề
    /// ngang cố định. Chỉ ĐÁY là trôi — phần mô tả loài cá dài ngắn khác nhau — nên hàm này cố
    /// ý chỉ trả về đúng phần đo được, còn dải nút thì phải đi tìm bằng cách khác.
    ///
    /// Vì sao không suy từ nút như bản trước: lúc panel vừa mở KHÔNG nút nào sáng (đo trên ảnh
    /// chụp 27/08), nên không hề có khối xanh nào để làm mốc. Nút chỉ sáng dưới con trỏ.
    ///
    /// <paramref name="flipped"/> cho trường hợp ô nằm sát mép phải màn: panel hết chỗ nên lật
    /// sang trái, mép PHẢI của nó neo vào mép phải ô.
    /// </summary>
    public static Rectangle PanelFromCell(Rectangle cell, FishingConfig cfg, int screenW, bool flipped)
    {
        int w = Math.Max(40, (int)Math.Round(screenW * cfg.SplitPanelWidthFrac));
        int dx = (int)Math.Round(screenW * cfg.SplitPanelDxFrac);
        int left = flipped ? cell.Right + dx - w : cell.Left - dx;
        return new Rectangle(left, cell.Top, w, cell.Height);
    }

    private static SplitPanelRead ParseLine(IPixelSource src, Rectangle roi, DigitAtlas atlas,
                                            FishingConfig cfg, Rectangle button, Rectangle panel)
    {
        SplitPanelRead Fail(string reason, string text = "", string trace = "") => new()
        {
            Reason = reason, Text = text, Trace = trace, Button = button, Panel = panel
        };

        src.Refresh();
        var gray = src.GrayBuffer(roi);
        int w = roi.Width, h = roi.Height;
        if (gray.Length < w * h) return Fail("vùng đọc nằm ngoài ảnh");

        var bin = GlyphSeg.Binarize(gray, cfg.DigitInkMinGray, out int thr);
        var boxes = GlyphSeg.Segment(bin, w, h, cfg.DigitMinGlyphW, cfg.DigitMinGlyphInk, cfg.DigitMergeGapPx);
        boxes = WeightReader.MergeDotPieces(boxes);
        if (boxes.Count == 0) return Fail("không thấy chữ nào");

        int tallest = boxes.Max(b => b.Box.Height);
        boxes = WeightReader.SplitTouching(boxes, bin, gray, w, h, atlas, cfg, tallest);

        // Gom khoi thanh TU theo khoang ho. Day la cot loi cua ca ham: doc ca dong roi bat regex
        // thi mot chu cai bi nhan nham thanh chu so se dinh lien vao con so that — "15 ĐƠN" ra
        // "150" neu 'Ơ' cham 0. Chu trong mot tu cach nhau vai pixel, hai tu cach nhau ca chuc.
        int gap = Math.Max(4, (int)Math.Round(tallest * cfg.SplitWordGapFrac));
        var words = new List<(string Text, Rectangle Box)>();
        var sb = new StringBuilder();
        var trace = new StringBuilder($"ngưỡng={thr} khối={boxes.Count} hở={gap}");
        Rectangle cur = Rectangle.Empty;
        string curText = "";

        foreach (var b in boxes)
        {
            var g = WeightReader.ClassifyBox(gray, w, h, b.Box, tallest, atlas, cfg);
            sb.Append(g.Ch);
            trace.Append($" | '{g.Ch}' {b.Box.Width}×{b.Box.Height}@{b.Box.X}");

            if (cur.IsEmpty || b.Box.Left - cur.Right <= gap)
            {
                cur = cur.IsEmpty ? b.Box : Rectangle.Union(cur, b.Box);
                curText += g.Ch;
            }
            else
            {
                words.Add((curText, cur));
                cur = b.Box;
                curText = g.Ch.ToString();
                sb.Append(' ');
            }
        }
        if (!cur.IsEmpty) words.Add((curText, cur));

        string text = sb.ToString();
        if (words.Count < 2) return Fail($"chỉ tách được {words.Count} từ", text, trace.ToString());

        // Tu dau tien la so don vi. Khong duyet tim "tu nao toan chu so" — "26.250" cung toan so,
        // va nham hai con so nay voi nhau la tach ra so con sai hoan toan.
        if (!int.TryParse(words[0].Text, out int count))
            return Fail($"từ đầu “{words[0].Text}” không phải số nguyên", text, trace.ToString());

        // Tong kg la tu ngay sau dau '/'. Dau gach cheo co trong bo mau (WeightReader doc "/35"
        // bang chinh no) nen day la moc dang tin nhat trong dong.
        int slash = words.FindIndex(t => t.Text.Contains('/'));
        if (slash < 0 || slash + 1 >= words.Count)
            return Fail("không thấy dấu / ngăn giữa số đơn vị và số kg", text, trace.ToString());

        string kgText = words[slash + 1].Text;
        // Dau '/' co the dinh lien voi so kg thanh mot tu ("/26.250") — cat phan sau dau.
        if (words[slash].Text.Length > 1 && words[slash].Text.IndexOf('/') < words[slash].Text.Length - 1)
            kgText = words[slash].Text[(words[slash].Text.IndexOf('/') + 1)..];

        if (!double.TryParse(kgText, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double kg))
            return Fail($"“{kgText}” không phải số kg", text, trace.ToString());

        if (count < 1 || count > cfg.SplitMaxUnits)
            return Fail($"số đơn vị {count} nằm ngoài 1..{cfg.SplitMaxUnits}", text, trace.ToString());
        if (kg <= 0 || kg > cfg.SplitMaxStackKg)
            return Fail($"tổng {kg:0.000} kg nằm ngoài 0..{cfg.SplitMaxStackKg}", text, trace.ToString());

        double per = kg / count;
        if (per < cfg.SplitMinUnitKg || per > cfg.SplitMaxUnitKg)
            return Fail($"{per:0.000} kg mỗi con nằm ngoài " +
                        $"{cfg.SplitMinUnitKg:0.###}..{cfg.SplitMaxUnitKg:0.###}", text, trace.ToString());

        return new SplitPanelRead
        {
            Ok = true, Count = count, TotalKg = kg, Text = text, Trace = trace.ToString(),
            Button = button, Panel = panel
        };
    }
}

/// <summary>Kết cục một lượt tách.</summary>
internal enum SplitOutcome
{
    /// <summary>Đã tách xong, ô mới đang nằm trong ba lô.</summary>
    Done,
    /// <summary>Cả chồng đã lọt cốp rồi — không cần tách, cứ kéo trọn ô.</summary>
    FitsWhole,
    /// <summary>Không con nào lọt: cốp đầy thật.</summary>
    NothingFits,
    /// <summary>Không làm được (không dò ra nút, đọc hỏng, hết giờ). Đã dọn về màn kho đồ.</summary>
    Failed
}

internal sealed class SplitAttempt
{
    public SplitOutcome Outcome { get; init; }
    public string Why { get; init; } = "";

    /// <summary>Số con đã tách ra. Chỉ có nghĩa khi <see cref="SplitOutcome.Done"/>.</summary>
    public int Units { get; init; }

    /// <summary>Panel đọc được gì — để người gọi học kg mỗi con kể cả khi không tách.</summary>
    public SplitPanelRead Read { get; init; }
}

/// <summary>
/// Tách một chồng vật phẩm cho vừa chỗ trống còn lại của cốp.
///
/// Vì sao phải có: bot kéo NGUYÊN cả ô, nên một ô đã dồn 15 con nặng 26 kg thì cốp còn trống
/// 22 kg là không bao giờ nhận — game hiện "Kho đồ đã đầy!" và chỗ trống cuối cốp bỏ trắng.
/// Người chơi làm tay thì tách ra đúng số con vừa chỗ; đây là bản máy làm của việc đó.
///
/// Toàn bộ lớp này chạy theo một luật: THÀ BỎ LƯỢT TÁCH CÒN HƠN CLICK LIỀU. Nút VỨT nằm ngay
/// cạnh nút TÁCH trên cùng một dải, nên mỗi cú click chỉ được phép đi ra từ một khối pixel đã
/// qua đủ cửa kiểm hình học. Không dò ra thì trả <see cref="SplitOutcome.Failed"/> — bot mất
/// mấy kg chỗ trống, còn hơn mất cả chồng cá.
/// </summary>
internal sealed class ItemSplitter : IDisposable
{
    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly DigitAtlas _atlas;
    private readonly Action<string> _log;
    private readonly Point _park;

    private RegionReader _band;
    private RegionReader _dialog;

    public ItemSplitter(FishingConfig cfg, Screen screen, DigitAtlas atlas, Action<string> log)
    {
        _cfg = cfg;
        _screen = screen;
        _atlas = atlas;
        _log = log ?? (_ => { });

        var b = screen.Bounds;
        _park = new Point(b.Left + 40, b.Top + 40);
    }

    /// <summary>Vùng quét nút TÁCH: trùm cả khung kho đồ, vì panel neo theo ô được chuột phải.</summary>
    private Rectangle Band()
    {
        var b = _screen.Bounds;
        int x = b.Left + (int)Math.Round(b.Width * _cfg.SplitBandLeftFrac);
        int y = b.Top + (int)Math.Round(b.Height * _cfg.SplitBandTopFrac);
        int w = (int)Math.Round(b.Width * _cfg.SplitBandWidthFrac);
        int h = (int)Math.Round(b.Height * _cfg.SplitBandHeightFrac);
        return Rectangle.Intersect(new Rectangle(x, y, w, h), b);
    }

    /// <summary>Vùng quét hộp thoại TÁCH VẬT PHẨM — nó luôn nằm giữa màn.</summary>
    private Rectangle DialogBand()
    {
        var b = _screen.Bounds;
        int w = (int)Math.Round(b.Width * _cfg.SplitDialogWidthFrac);
        int h = (int)Math.Round(b.Height * _cfg.SplitDialogHeightFrac);
        return Rectangle.Intersect(
            new Rectangle(b.Left + b.Width / 2 - w / 2, b.Top + b.Height / 2 - h / 2, w, h), b);
    }

    /// <summary>
    /// Mở panel của một ô rồi đọc "n ĐƠN VỊ / x.xxx KG". Panel để NGUYÊN đang mở — người gọi
    /// hoặc bấm TÁCH tiếp, hoặc gọi <see cref="ClosePanel"/>.
    /// </summary>
    public SplitPanelRead Peek(CellInfo cell, CancellationToken ct)
    {
        var band = Band();
        _band ??= new RegionReader(band);

        SplitPanelRead last = null;
        for (int attempt = 0; attempt <= _cfg.SplitPanelRetries; attempt++)
        {
            // Bam lai o MOI luot, khong chi luot dau. Cu chuot phai co the truot han (con tro
            // chua kip toi o, hoac UI chua chot hover) — luot do panel khong he mo ra, nen doc
            // lai cung mot khung hinh trong bao nhieu lan cung vay.
            InputSender.RightClickAt(cell.Centre, _cfg.MenuMoveSteps, _cfg.DragStepMs,
                                     _cfg.MenuHoverMs, 60);
            Sleep(ct, _cfg.SplitPanelWaitMs);

            // Con tro dang de TREN o vua bam nen o do sang len. Panel nam cho khac va KHONG tat
            // khi chuot roi o (do tren anh chup 27/08), nen doi con tro ra cho trung tinh la an
            // toan, va no giu moi phep do dong nhat voi phan con lai cua bot.
            InputSender.MoveCursorOnly(_park.X, _park.Y);
            Sleep(ct, 120);

            var read = LocatePanel(cell);
            if (read.Ok) return read;
            last = read;
        }
        return last ?? new SplitPanelRead { Reason = "hết lượt quét panel" };
    }

    /// <summary>
    /// Định vị panel ĐANG MỞ bằng chính dòng "n ĐƠN VỊ / x.xxx KG" của nó.
    ///
    /// Vì sao không tính thẳng ra vị trí panel: đã thử, và sai. Panel mọc ra theo một luật của
    /// game mà từ ngoài chỉ đoán được, và một công thức khớp với ô này lại trượt hẳn ra ngoài
    /// panel ở ô khác — lúc đó bot rà nút ở một cột trống trơn rồi kết luận "không có nút nào".
    ///
    /// Nên thay vì đoán, thử dịch ô đọc sang ngang từng nấc và để CHÍNH PHÉP ĐỌC nhận ra chỗ
    /// đúng. Rẻ: mỗi nấc chỉ là một lần chụp vùng nhỏ, không phải di chuột. Và đáng tin: các
    /// cổng trong <see cref="SplitPanelReader.ReadLine"/> vốn đã chặt tay — ra được "20 đơn vị
    /// / 25.000 kg" đúng dạng thì gần như chắc chắn ô đọc đang nằm đúng chỗ, chứ nền panel trơn
    /// hay một mẩu chữ khác không thể tình cờ khớp.
    /// </summary>
    public SplitPanelRead LocatePanel(CellInfo cell)
    {
        var band = Band();
        _band ??= new RegionReader(band);

        int w = _screen.Bounds.Width;
        int step = Math.Max(8, (int)Math.Round(w * _cfg.SplitPanelScanStepFrac));
        int reach = (int)Math.Round(w * _cfg.SplitPanelScanReachFrac);

        SplitPanelRead last = null;

        // Thu vi tri "chuan" truoc (panel mo sang phai o) roi moi toa dan ra hai ben — o dung
        // thuong nam ngay day, nen luot dau da xong, khong phai quet het.
        var basePanel = SplitPanelReader.PanelFromCell(cell.Full, _cfg, w, flipped: false);
        for (int d = 0; d <= reach; d += step)
        foreach (int dx in d == 0 ? new[] { 0 } : new[] { d, -d })
        {
            var panel = new Rectangle(basePanel.X + dx, basePanel.Y,
                                      basePanel.Width, basePanel.Height);
            var read = SplitPanelReader.ReadLine(_band, panel, cell.Full.Top, _atlas, _cfg,
                                                 _screen.Bounds.Height);
            if (read.Ok)
            {
                if (dx != 0) _log($"   panel lệch {dx:+#;-#;0} px so với chỗ tính ra");
                return read;
            }
            last ??= read;
        }
        return last ?? new SplitPanelRead { Reason = "không dò ra panel ở nấc nào" };
    }

    /// <summary>Đóng panel bằng một cú click vào chỗ trống — KHÔNG dùng Esc, Esc đóng cả kho đồ.</summary>
    public void ClosePanel(CancellationToken ct)
    {
        InputSender.LeftClickAt(_park, _cfg.MenuMoveSteps, _cfg.DragStepMs, 80, 60);
        Sleep(ct, _cfg.SplitAfterClickMs);
    }

    /// <summary>
    /// Bảo đảm hộp thoại TÁCH VẬT PHẨM không còn mở. Trả false khi vẫn không đóng được.
    ///
    /// Bỏ qua bước này là hỏng nặng chứ không chỉ mất lượt tách: hộp thoại nằm giữa màn và che
    /// mất lưới ô, nên mọi phép quét sau đó đọc ra sai; rồi <c>TrunkOpener.CloseAll</c> bấm
    /// Esc MỘT lần, cú Esc ấy chỉ đóng hộp thoại, kho đồ vẫn mở, và nó ném "Esc không đóng
    /// được màn hình" — một lỗi trông như lệch trạng thái mà thật ra do chính chỗ này để lại.
    /// </summary>
    public bool CloseDialog(CancellationToken ct)
    {
        if (_dialog is null) return true;

        var band = DialogBand();
        if (!FindDialogOk(band, out var ok, out _)) return true;

        _log("   hộp thoại tách còn mở — bấm HUỶ");
        CancelDialog(ok, ct);
        if (!FindDialogOk(band, out _, out _)) return true;

        _log("   HUỶ không ăn — bấm Esc");
        InputSender.TapKey(0x1B);
        Sleep(ct, _cfg.SplitAfterClickMs);
        return !FindDialogOk(band, out _, out _);
    }

    /// <summary>
    /// Tách ô <paramref name="cell"/> sao cho phần tách ra lọt <paramref name="freeKg"/>.
    ///
    /// Không tự kéo sang cốp: việc đó vẫn là của <c>TrunkDumper.DragOne</c>, chỗ duy nhất trong
    /// repo được phép kéo ô kho đồ và cũng là chỗ giữ mọi cửa xác minh của cú kéo.
    /// </summary>
    public SplitAttempt SplitToFit(CellInfo cell, double freeKg, double marginKg,
                                   double kgPerUnitFallback, CancellationToken ct)
    {
        var read = Peek(cell, ct);
        if (read.Ok) _log($"panel ô #{cell.Index}: {read}");

        // Doc hong van con mot duong: kg moi con da hoc/khai bao tu truoc. Chu so tren panel la
        // co thu BA (khac ca tu so lan mau so cua thanh KG), nen bo mau thieu no la chuyen binh
        // thuong tren mot may moi — khong co duong nay thi lan tach dau tien luon hong.
        double per = read.Ok ? read.KgPerUnit : kgPerUnitFallback;
        if (per <= 0)
        {
            ClosePanel(ct);
            return new SplitAttempt
            {
                Outcome = SplitOutcome.Failed, Read = read,
                Why = read.Reason + " — mà cũng chưa biết loài này mỗi con mấy kg. Hai đường gỡ: " +
                      "điền số vào “Kg mỗi con…”, hoặc chụp màn lúc panel vật phẩm đang mở rồi " +
                      "dạy thêm cỡ chữ số đó (chữ trên panel là cỡ thứ ba, khác cả tử số lẫn " +
                      "mẫu số của thanh KG)"
            };
        }
        if (!read.Ok)
            _log($"không đọc được panel ({read.Reason}) — dùng {per:0.000} kg mỗi con đã biết");

        double budget = freeKg - marginKg;
        int want = (int)Math.Floor(budget / per);

        // Chi ket luan "ca chong lot" khi ĐỌC ĐƯỢC so con. Doan mo thi khong biet chong co bao
        // nhieu con, ma cho nay chi duoc goi sau khi da biet la khong lot — cu tach.
        if (read.Ok && want >= read.Count)
        {
            ClosePanel(ct);
            return new SplitAttempt
            {
                Outcome = SplitOutcome.FitsWhole, Read = read,
                Why = $"cả {read.Count} con ({read.TotalKg:0.0} kg) lọt {budget:0.0} kg còn trống"
            };
        }

        if (want <= 0)
        {
            ClosePanel(ct);
            return new SplitAttempt
            {
                Outcome = SplitOutcome.NothingFits, Read = read,
                Why = $"chỗ trống {budget:0.0} kg không đủ cho một con {per:0.000} kg"
            };
        }

        _log($"tách {want}" + (read.Ok ? $"/{read.Count}" : "") +
             $" con (≈{want * per:0.0} kg) cho vừa {budget:0.0} kg còn trống");

        if (!ClickSplitButton(cell, read.Panel, ct))
        {
            // Hop thoai co the hien ra MUON hon so luot cho — don cho chac, khong thi no nam
            // lai giua man che mat luoi o.
            CloseDialog(ct);
            ClosePanel(ct);
            return new SplitAttempt
            {
                Outcome = SplitOutcome.Failed, Read = read,
                Why = "bấm nút TÁCH mà hộp thoại không hiện"
            };
        }

        if (!EnterAmount(want, ct))
        {
            // Don sach TRUOC khi tra ve: hop thoai con mo la che mat luoi o, va cu Esc don cua
            // TrunkOpener.CloseAll se roi vao no thay vi vao man kho do.
            bool clean = CloseDialog(ct);
            ClosePanel(ct);
            return new SplitAttempt
            {
                Outcome = SplitOutcome.Failed, Read = read,
                Why = "không gõ được số lượng vào hộp thoại" +
                      (clean ? "" : " — VÀ hộp thoại vẫn chưa đóng được")
            };
        }

        return new SplitAttempt { Outcome = SplitOutcome.Done, Units = want, Read = read };
    }

    /// <summary>Bấm nút TÁCH của panel rồi chờ hộp thoại hiện ra.</summary>
    /// <summary>
    /// Rê con trỏ tới <paramref name="p"/> rồi hỏi game: dưới đó có phải một nút không?
    ///
    /// Game tô sáng nút dưới con trỏ, nên câu trả lời do CHÍNH GAME đưa ra chứ không phải do ta
    /// suy từ toạ độ. Đó là điều bản đầu thiếu: nó tính ra một chỗ rồi bấm luôn, không hỏi lại
    /// bao giờ.
    /// </summary>
    private bool ButtonAt(Point p, Rectangle band, CancellationToken ct, out Rectangle box)
    {
        InputSender.MoveCursorOnlySmooth(p.X, p.Y, _cfg.MenuMoveSteps, _cfg.DragStepMs);
        Sleep(ct, _cfg.SplitHoverMs);

        return SplitPanelReader.ButtonCovering(_band, band, _cfg,
                                               _screen.Bounds.Width, _screen.Bounds.Height,
                                               p, out box);
    }

    /// <summary>
    /// Rà dọc giữa panel cho tới khi một nút sáng lên dưới con trỏ.
    ///
    /// Phải rà vì đáy panel trôi theo độ dài phần mô tả loài cá, mà dải nút thì nằm ở đáy. Rà ở
    /// giữa panel theo bề ngang: ba nút chia đều panel nên cột giữa rơi đúng vào nút giữa —
    /// nhưng lệch cột cũng không sao, <see cref="ClickSplitButton"/> tự đếm lại thứ tự.
    /// </summary>
    private bool ScanForButton(Rectangle panel, Rectangle band, CancellationToken ct,
                               out Rectangle box)
    {
        box = Rectangle.Empty;

        int h = _screen.Bounds.Height;
        int x = panel.Left + panel.Width / 2;
        int from = panel.Top + (int)Math.Round(h * _cfg.SplitScanTopFrac);
        int to = panel.Top + (int)Math.Round(h * _cfg.SplitScanBottomFrac);
        int step = Math.Max(6, (int)Math.Round(h * _cfg.SplitScanStepFrac));

        // Ra tu DUOI len: dai nut nam o day panel, nen di tu day len la gap no ngay may buoc
        // dau, con di tu tren xuong thi luot nao cung phai qua het phan mo ta.
        for (int y = to; y >= from; y -= step)
        {
            ct.ThrowIfCancellationRequested();
            if (ButtonAt(new Point(x, y), band, ct, out box))
            {
                _log($"   rà thấy nút {box.Width}×{box.Height} @ {box.X},{box.Y} (rà ở cột {x})");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Bấm nút TÁCH — chỉ sau khi game đã tự xác nhận nút đó là nút GIỮA của một dải ba nút.
    ///
    /// Không có mốc tĩnh nào để bám: lúc panel vừa mở KHÔNG nút nào sáng, và đáy panel thì trôi
    /// theo độ dài mô tả. Nên cách duy nhất đáng tin là hỏi chính game — rê con trỏ tới một chỗ
    /// rồi xem nó có tô sáng nút ở đó không.
    ///
    /// Rà ra được MỘT nút, nhưng chưa biết là nút thứ mấy, nên hỏi tiếp hai bên rồi đếm:
    ///   trái trống + phải có → đang đứng ở nút 1, TÁCH lệch sang phải một nút;
    ///   trái có   + phải có → đang đứng ở nút 2, tức TÁCH luôn;
    ///   trái có   + phải trống → đang đứng ở nút 3, TÁCH lệch sang trái một nút.
    /// Mọi tổ hợp khác (cả hai bên đều trống — dải chỉ có một nút) đều bị từ chối: bố cục không
    /// phải DÙNG | TÁCH | VỨT thì không có căn cứ nào để gọi nút giữa là TÁCH.
    ///
    /// Cuối cùng vẫn hỏi lại ngay trên chỗ sắp bấm. Con trỏ vừa đi một vòng, và cú click chỉ
    /// được phép rơi vào chỗ mà game đang sáng NGAY LÚC ĐÓ — hai nút kẹp bên cạnh là DÙNG (ăn
    /// mất cá) và VỨT (bỏ cá đi), nên ở đây "gần đúng" đồng nghĩa với mất cá.
    /// </summary>
    private bool ClickSplitButton(CellInfo cell, Rectangle panel, CancellationToken ct)
    {
        var mid = LocateSplitButton(cell, panel, ct);
        if (mid is null) return false;

        InputSender.LeftClickAt(mid.Value, 2, _cfg.DragStepMs, _cfg.MenuHoverMs, 60);

        var dialogBand = DialogBand();
        _dialog ??= new RegionReader(dialogBand);

        for (int attempt = 0; attempt <= _cfg.SplitDialogRetries; attempt++)
        {
            Sleep(ct, _cfg.SplitDialogWaitMs);
            InputSender.MoveCursorOnly(_park.X, _park.Y);
            Sleep(ct, 120);

            if (FindDialogOk(dialogBand, out _, out string why)) return true;
            _log("   " + why);
        }
        return false;
    }

    /// <summary>
    /// Tìm tâm nút TÁCH của panel ĐANG MỞ, không bấm gì.
    ///
    /// Tách khỏi <see cref="ClickSplitButton"/> để <c>--verify-split --live</c> chạy được đúng
    /// phần suy luận này trên panel thật rồi in ra "bot sẽ bấm ở đâu" — xem trước được một cú
    /// click mà không phải chịu hậu quả của nó.
    /// </summary>
    public Point? LocateSplitButton(CellInfo cell, Rectangle panel, CancellationToken ct)
    {
        var band = Band();
        _band ??= new RegionReader(band);

        // Panel do LocatePanel dua sang la panel da duoc chinh dong chu xac nhan. Khong co no
        // (doc chu hong) thi danh quay ve cong thuc uoc luong — van hon la khong thu gi.
        if (panel.IsEmpty)
        {
            panel = SplitPanelReader.PanelFromCell(cell.Full, _cfg, _screen.Bounds.Width, false);
            _log("   chưa định vị được panel bằng dòng chữ — rà theo vị trí ước lượng");
        }

        if (!ScanForButton(panel, band, ct, out var found))
        {
            _log("   rà hết chiều cao panel mà không nút nào sáng lên — KHÔNG bấm");
            return null;
        }

        int w = found.Width;
        int cy = found.Top + found.Height / 2;
        bool left = ButtonAt(new Point(found.Left - w / 2, cy), band, ct, out _);
        bool right = ButtonAt(new Point(found.Right + w / 2, cy), band, ct, out _);

        int order = (left, right) switch
        {
            (false, true) => 1,
            (true, true) => 2,
            (true, false) => 3,
            _ => 0
        };
        if (order == 0)
        {
            _log("   nút rà được không có nút nào bên cạnh — dải không phải DÙNG|TÁCH|VỨT, KHÔNG bấm");
            return null;
        }

        var mid = new Point(found.Left + (2 - order) * w + w / 2, cy);
        _log($"   nút rà được là nút thứ {order} của dải → TÁCH ở {mid.X},{mid.Y}");

        if (!ButtonAt(mid, band, ct, out var midBox))
        {
            _log($"   chỗ tính là nút TÁCH ({mid.X},{mid.Y}) không sáng lên — KHÔNG bấm");
            return null;
        }

        _log($"nút TÁCH @ {mid.X},{mid.Y} — nút giữa của dải ba nút, " +
             $"{midBox.Width}×{midBox.Height} đang sáng dưới con trỏ");
        return mid;
    }

    /// <summary>
    /// Nút TÁCH của hộp thoại: khối TRẮNG đặc. Nút HUỶ bên cạnh chỉ có CHỮ trắng trên nền tối,
    /// nên khối của nó vừa nhỏ vừa rỗng và không qua nổi cửa diện tích lẫn cửa độ đặc.
    /// </summary>
    private bool FindDialogOk(Rectangle band, out Rectangle box, out string why)
    {
        box = Rectangle.Empty;
        why = null;

        _dialog.Refresh();
        var mask = new Mask(band.Width, band.Height,
                            _dialog.MaskBuffer((b, g, r) =>
                                r > _cfg.SplitDialogWhiteMin &&
                                Math.Abs(r - g) < 26 && Math.Abs(g - b) < 26));

        var bounds = _screen.Bounds;
        double expW = bounds.Width * _cfg.SplitDialogOkWidthFrac;
        double expH = bounds.Height * _cfg.SplitDialogOkHeightFrac;
        int minArea = (int)Math.Round(expW * expH * _cfg.SplitButtonAreaFracMin);

        var blobs = ImageOps.Blobs(mask, Math.Max(64, minArea));
        if (blobs.Count == 0) { why = "hộp thoại chưa hiện (không thấy nút TÁCH trắng)"; return false; }

        var ranked = blobs.OrderByDescending(b => b.Area).ToList();
        var top = ranked[0];
        if (ranked.Count > 1 && ranked[1].Area > top.Area * _cfg.SplitButtonRivalMax)
        {
            why = $"hai khối trắng ngang nhau ({top.Area} và {ranked[1].Area}) — không dám đoán";
            return false;
        }

        double fill = top.Area / (double)Math.Max(1, top.Box.Width * top.Box.Height);
        if (fill < _cfg.SplitButtonFillMin)
        {
            why = $"khối trắng chỉ đặc {fill:F2} — nút thì phải kín";
            return false;
        }

        box = new Rectangle(band.X + top.Box.X, band.Y + top.Box.Y, top.Box.Width, top.Box.Height);
        return true;
    }

    /// <summary>
    /// Gõ số vào hộp thoại rồi bấm TÁCH.
    ///
    /// Gõ sai số KHÔNG mất cá — cá vẫn nằm nguyên trong ba lô, chỉ chia sai chỗ — nên cửa ở đây
    /// nhẹ hơn cửa của nút TÁCH: đọc lại được mà thấy khác thì HUỶ, đọc không ra thì ghi cảnh
    /// báo rồi đi tiếp.
    /// </summary>
    private bool EnterAmount(int want, CancellationToken ct)
    {
        var band = DialogBand();
        if (!FindDialogOk(band, out var ok, out string why)) { _log("   " + why); return false; }

        var bounds = _screen.Bounds;
        int dy = (int)Math.Round(bounds.Height * _cfg.SplitDialogInputDyFrac);
        var input = new Point(bounds.Left + bounds.Width / 2, ok.Top + ok.Height / 2 - dy);

        InputSender.LeftClickAt(input, _cfg.MenuMoveSteps, _cfg.DragStepMs, 120, 60);
        Sleep(ct, _cfg.SplitAfterClickMs);

        InputSender.ClearNumberField(_cfg.SplitClearTaps);
        InputSender.TypeNumber(want);
        Sleep(ct, _cfg.SplitAfterClickMs);

        int shown = ReadTypedAmount(input, ct);
        if (shown > 0 && shown != want)
        {
            _log($"   ô nhập đang là {shown} chứ không phải {want} — huỷ, không tách");
            CancelDialog(ok, ct);
            return false;
        }
        if (shown <= 0)
            _log("   cảnh báo: không đọc lại được số vừa gõ (thiếu mẫu chữ số?) — vẫn bấm TÁCH");

        InputSender.LeftClickAt(new Point(ok.Left + ok.Width / 2, ok.Top + ok.Height / 2),
                                _cfg.MenuMoveSteps, _cfg.DragStepMs, _cfg.MenuHoverMs, 60);
        Sleep(ct, _cfg.SplitAfterSplitMs);
        InputSender.MoveCursorOnly(_park.X, _park.Y);
        Sleep(ct, 150);

        // Hop thoai DONG chinh la bang chung cu bam da an. Con thay no nghia la cu click roi ra
        // ngoai nut, va di tiep luc nay se quet luoi qua mot tam kinh mo.
        for (int attempt = 0; attempt <= _cfg.SplitDialogRetries; attempt++)
        {
            if (!FindDialogOk(band, out _, out _)) return true;
            _log("   bấm TÁCH rồi mà hộp thoại vẫn còn — chờ thêm");
            Sleep(ct, _cfg.SplitDialogWaitMs);
        }
        return false;
    }

    /// <summary>Đọc lại con số trong ô nhập. -1 = không đọc được (thiếu mẫu, hoặc ô rỗng).</summary>
    private int ReadTypedAmount(Point inputCentre, CancellationToken ct)
    {
        var bounds = _screen.Bounds;
        int w = (int)Math.Round(bounds.Width * _cfg.SplitDialogInputWidthFrac);
        int h = (int)Math.Round(bounds.Height * _cfg.SplitDialogInputHeightFrac);
        var roi = Rectangle.Intersect(
            new Rectangle(inputCentre.X - w / 2, inputCentre.Y - h / 2, w, h), bounds);
        if (roi.Width < 8 || roi.Height < 8) return -1;

        Sleep(ct, 80);
        _dialog.Refresh();

        var gray = _dialog.GrayBuffer(roi);
        var bin = GlyphSeg.Binarize(gray, _cfg.DigitInkMinGray, out _);
        var boxes = GlyphSeg.Segment(bin, roi.Width, roi.Height,
                                     _cfg.DigitMinGlyphW, _cfg.DigitMinGlyphInk, _cfg.DigitMergeGapPx);
        if (boxes.Count == 0) return -1;

        int tallest = boxes.Max(b => b.Box.Height);
        var sb = new StringBuilder();
        foreach (var b in boxes)
        {
            var g = WeightReader.ClassifyBox(gray, roi.Width, roi.Height, b.Box, tallest, _atlas, _cfg);
            if (g.Ch is < '0' or > '9') return -1;   // co ky tu la thi khong dam ket luan gi
            sb.Append(g.Ch);
        }
        return int.TryParse(sb.ToString(), out int n) ? n : -1;
    }

    /// <summary>Bấm HUỶ — nút bên TRÁI nút TÁCH, cùng dải, cách đúng một bề ngang nút.</summary>
    private void CancelDialog(Rectangle ok, CancellationToken ct)
    {
        var cancel = new Point(ok.Left - (int)Math.Round(ok.Width * _cfg.SplitDialogCancelDxFrac),
                               ok.Top + ok.Height / 2);
        InputSender.LeftClickAt(cancel, _cfg.MenuMoveSteps, _cfg.DragStepMs, _cfg.MenuHoverMs, 60);
        Sleep(ct, _cfg.SplitAfterClickMs);
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
    }

    public void Dispose()
    {
        _band?.Dispose();
        _dialog?.Dispose();
        _band = null;
        _dialog = null;
    }
}
