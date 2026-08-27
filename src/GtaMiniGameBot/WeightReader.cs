using System.Text;
using System.Text.RegularExpressions;

namespace GtaMiniGameBot;

internal sealed class WeightRead
{
    public bool Ok { get; init; }
    /// <summary>Số kg đang mang. Chỉ có nghĩa khi <see cref="Ok"/>.</summary>
    public double Value { get; init; }
    /// <summary>Mẫu số đọc được từ chính ảnh — cái neo, không phải giá trị cấu hình.</summary>
    public double Cap { get; init; }
    /// <summary>Chuỗi đọc được, '?' cho glyph không đủ tự tin.</summary>
    public string Text { get; init; } = "";
    /// <summary>Hỏng vì cổng nào — ghi tên cổng để đọc log là biết sửa gì.</summary>
    public string Reason { get; init; }
    /// <summary>Chi tiết từng glyph, để dán vào log lúc tinh chỉnh.</summary>
    public string Trace { get; init; } = "";

    public override string ToString() =>
        Ok ? $"{Value:0.0}/{Cap:0} KG" : $"đọc hỏng ({Reason}) — “{Text}”";
}

/// <summary>
/// Đọc "7.7/35 KG". Không có thư viện OCR nào ở đây: tách glyph rồi so từng cái với bộ mẫu
/// người dùng dạy.
///
/// Điểm cốt lõi là các cổng kiểm tra, không phải bộ so khớp. Đọc sai còn tệ hơn không đọc
/// được: bot sẽ câu vào cái ba lô đã đầy mà log vẫn trông bình thường. Nên bất kỳ nghi ngờ
/// nào cũng trả về Ok = false, và người gọi rơi về đếm số cá.
/// </summary>
internal sealed class WeightReader : IDisposable
{
    /// <summary>
    /// Phải khớp TRỌN chuỗi, chỉ cho phép '?' phía sau (ký tự không đọc được, ví dụ chữ KG lỡ
    /// lọt vào ô). Nếu chỉ khớp phần đầu thì một ký tự lạ bị nhận nhầm thành chữ số sẽ dính
    /// luôn vào mẫu số — "/60" hoá "/600" — mà vẫn coi là đọc được.
    /// </summary>
    private static readonly Regex Pattern =
        new(@"^(\d{1,3})(?:\.(\d))?/(\d{1,3})\?*$", RegexOptions.Compiled);

    private readonly FishingConfig _cfg;
    private readonly DigitAtlas _atlas;
    private readonly bool _capIsDynamic;
    private readonly RegionReader _region;
    private readonly Rectangle _abs;

    /// <summary>
    /// Neo mẫu số. Ba lô có thể đổi sau upgrade nên lần đọc hợp lệ đầu tiên khóa số này
    /// cho phần còn lại của phiên — các lần sau vẫn phải khớp, đúng tinh thần cổng cũ.
    /// </summary>
    private double _expectedCap;

    private double _last = -1;

    public WeightReader(FishingConfig cfg, Screen screen, FishingRect roi, DigitAtlas atlas,
                        double expectedCap, bool capIsDynamic = false)
    {
        _cfg = cfg;
        _atlas = atlas;
        _expectedCap = expectedCap;
        _capIsDynamic = capIsDynamic;
        _abs = FishingConfig.ToAbsolute(screen, roi);
        _region = new RegionReader(_abs);
    }

    /// <summary>Bộ mẫu chưa đủ thì đừng chạy — sẽ chỉ toàn '?'.</summary>
    public bool AtlasReady => AtlasMissing.Length == 0;

    public string AtlasMissing => _atlas.MissingText(_cfg.BagCapKg, _cfg.TrunkCapKg);

    /// <summary>Quên giá trị lần trước — gọi sau khi đổ cốp, vì lúc đó KG giảm là đúng.</summary>
    public void ResetHistory() => _last = -1;

    public WeightRead Read()
    {
        _region.Refresh();
        var gray = _region.GrayBuffer(_abs);
        var r = Parse(gray, _abs.Width, _abs.Height, _atlas, _cfg, _expectedCap, _capIsDynamic, ref _last);
        if (r.Ok) _expectedCap = r.Cap;
        return r;
    }

    /// <summary>Đọc từ ảnh tĩnh — dùng để thử nguội, không cần đứng trong game.</summary>
    public static WeightRead ReadStill(Bitmap still, FishingRect roi, DigitAtlas atlas,
                                       FishingConfig cfg, double expectedCap, bool capIsDynamic = false)
    {
        var gray = GlyphSeg.GrayOf(still, roi.ToRectangle(), out int w, out int h);
        double ignore = -1;
        return Parse(gray, w, h, atlas, cfg, expectedCap, capIsDynamic, ref ignore);
    }

    /// <summary>
    /// Trần ba lô sau upgrade: số nguyên 15–50, bội của 5. 80 bị loại vì hay là 30 đọc
    /// nhầm 3→8; 39 không phải bước upgrade.
    /// </summary>
    internal static bool IsPlausibleBagCap(double cap)
    {
        int n = (int)Math.Round(cap);
        return Math.Abs(cap - n) <= 0.05 && n is >= 15 and <= 50 && n % 5 == 0;
    }

    private static bool CapAccepted(double cap, double expectedCap, bool capIsDynamic) =>
        Math.Abs(cap - expectedCap) <= 0.5 || (capIsDynamic && IsPlausibleBagCap(cap));

    private static WeightRead Parse(byte[] gray, int w, int h, DigitAtlas atlas,
                                    FishingConfig cfg, double expectedCap, bool capIsDynamic,
                                    ref double last)
    {
        if (gray.Length < w * h || w < 8 || h < 6)
            return new WeightRead { Reason = "vùng quá nhỏ" };

        var bin = GlyphSeg.Binarize(gray, cfg.DigitInkMinGray, out int thr);
        var boxes = GlyphSeg.Segment(bin, w, h, cfg.DigitMinGlyphW, cfg.DigitMinGlyphInk, cfg.DigitMergeGapPx);
        boxes = MergeDotPieces(boxes);
        int tallest = boxes.Count == 0 ? 0 : boxes.Max(b => b.Box.Height);
        boxes = SplitTouching(boxes, bin, gray, w, h, atlas, cfg, tallest);

        var sb = new StringBuilder();
        var trace = new StringBuilder($"ngưỡng={thr} khối={boxes.Count}");
        int dots = 0;

        foreach (var b in boxes)
        {
            var g = ClassifyBox(gray, w, h, b.Box, tallest, atlas, cfg);
            sb.Append(g.Ch);
            if (g.Ch == '.') dots++;
            trace.Append($" | '{g.Ch}' {g.Best:F2}/{(g.Second <= -1 ? "–" : g.Second.ToString("F2"))}" +
                         $" {b.Box.Width}×{b.Box.Height}@{b.Box.X}");
        }

        string text = sb.ToString();

        if (boxes.Count == 0)
            return Fail("không thấy chữ nào", text, trace);

        var m = Pattern.Match(text);
        if (!m.Success)
            return Fail("chuỗi không đúng dạng số.số/số", text, trace);

        // Dung MOT dau cham: mat dau cham thi "27.4" doc ra "274", va do dung la kieu doc sai
        // nguy hiem nhat — van hop le ve moi mat khac.
        if (dots != 1)
            return Fail($"phải có đúng 1 dấu chấm, thấy {dots}", text, trace);

        double whole = double.Parse(m.Groups[1].Value);
        double frac = m.Groups[2].Success ? double.Parse(m.Groups[2].Value) / 10.0 : 0;
        double value = whole + frac;
        double cap = double.Parse(m.Groups[3].Value);

        // Mau so van la cai neo: doc sai mot chu so thi gan nhu khong con dung muc ba lo
        // (30/35/40…) nua. Ba lo upgrade duoc nen cho phep doi muc hop le, roi khoa o Read().
        if (!CapAccepted(cap, expectedCap, capIsDynamic))
            return Fail($"mẫu số {cap:0} khác {expectedCap:0} đã cấu hình", text, trace);

        if (value < 0 || value > cap + 0.05)
            return Fail($"giá trị {value:0.0} nằm ngoài 0..{cap:0}", text, trace);

        if (last >= 0)
        {
            double delta = value - last;
            if (delta < -0.05)
                return Fail($"giảm từ {last:0.0} xuống {value:0.0} mà chưa đổ cốp", text, trace);
            if (delta > cfg.MaxWeightJumpKg)
                return Fail($"nhảy {delta:0.0} kg trong một lần đọc", text, trace);
        }

        last = value;
        return new WeightRead
        {
            Ok = true,
            Value = value,
            Cap = cap,
            Text = text,
            Trace = trace.ToString()
        };
    }

    /// <summary>Cao không quá ngần này so với chữ số cao nhất thì coi là dấu chấm.</summary>
    private const double DotMaxHeightFrac = 0.45;

    /// <summary>
    /// Dấu chấm nhận theo KÍCH THƯỚC, không so mẫu. Nó chỉ vài pixel nên gần như không có cấu
    /// trúc nét để NCC bám vào, và bề rộng đo được nhảy giữa 2 và 5 px tuỳ ngưỡng Otsu của
    /// từng ảnh — dạy mẫu cho nó là dạy một con số ngẫu nhiên. Trong khi đó nó là ký tự thấp
    /// duy nhất trong chuỗi: mọi chữ số và dấu gạch chéo đều cao hết dòng.
    /// </summary>
    internal static bool IsDot(Rectangle box, int tallest) =>
        tallest > 0
        && box.Height <= tallest * DotMaxHeightFrac
        && box.Width <= Math.Max(3, tallest * 0.5);

    internal static GlyphGuess ClassifyBox(byte[] gray, int w, int h, Rectangle box, int tallest,
                                          DigitAtlas atlas, FishingConfig cfg)
        => IsDot(box, tallest)
            ? new GlyphGuess('.', 1.0, -2, box.Width, box.Height)
            : atlas.Classify(gray, w, h, box, cfg);

    /// <summary>
    /// Gộp các mảnh thấp nằm sát nhau. Khử răng cưa để lại một cột lõm ngay giữa dấu chấm là
    /// đủ tách nó thành hai khối, thành ra chuỗi có hai dấu chấm và bị từ chối.
    /// Chỉ gộp khối THẤP với khối THẤP nên không thể vô tình dính hai chữ số vào nhau.
    /// </summary>
    internal static List<GlyphBox> MergeDotPieces(List<GlyphBox> boxes)
    {
        if (boxes.Count < 2) return boxes;

        int tallest = boxes.Max(b => b.Box.Height);
        var outp = new List<GlyphBox>();

        foreach (var b in boxes)
        {
            if (outp.Count > 0
                && IsDot(b.Box, tallest)
                && IsDot(outp[^1].Box, tallest)
                && b.Box.Left - outp[^1].Box.Right <= 2)
            {
                var prev = outp[^1];
                outp[^1] = new GlyphBox(Rectangle.Union(prev.Box, b.Box), prev.Ink + b.Ink);
                continue;
            }
            outp.Add(b);
        }
        return outp;
    }

    /// <summary>
    /// Khử ca hai chữ dính liền: khử răng cưa và kerning làm "0/" thành một khối duy nhất, và
    /// chiếu theo cột không thể tự thấy điều đó — profile không hề chạm đáy giữa hai chữ.
    ///
    /// Chỉ đụng vào khối RỘNG HƠN mọi mẫu đã học, và chỉ chấp nhận nhát cắt khi CẢ HAI nửa đều
    /// nhận ra được chắc chắn. Nhờ vậy cắt sai không thể sinh ra số sai: nửa nào không nhận ra
    /// thì nhát cắt bị loại, khối giữ nguyên và cả lần đọc bị từ chối như cũ.
    /// </summary>
    internal static List<GlyphBox> SplitTouching(List<GlyphBox> boxes, byte[] bin, byte[] gray,
                                                int w, int h, DigitAtlas atlas, FishingConfig cfg,
                                                int tallest)
    {
        if (atlas.Count == 0) return boxes;

        int maxW = atlas.MaxWidth + cfg.DigitWidthTolPx;
        int minW = Math.Max(1, atlas.MinWidth - cfg.DigitWidthTolPx);

        var todo = new Queue<GlyphBox>(boxes);
        var done = new List<GlyphBox>();
        int guard = boxes.Count * 4 + 16;   // chan vong lap vo tan neu cat ra roi lai cat tiep mai

        while (todo.Count > 0 && guard-- > 0)
        {
            var b = todo.Dequeue();

            // Thu cat khi khoi RONG hon moi mau, HOAC khi khong nhan ra duoc.
            // Chi dua vao be rong thi hut ca dinh nhau kieu ".1": dau cham cong chu so ben canh
            // van hep hon chu so rong nhat nen khong co khoi nao trong "rong bat thuong" —
            // nhung no khong nhan ra duoc, va do moi la dau hieu that.
            bool tooWide = b.Box.Width > maxW;
            if (!tooWide && ClassifyBox(gray, w, h, b.Box, tallest, atlas, cfg).Ch != '?')
            {
                done.Add(b);
                continue;
            }

            var cut = BestCut(b, bin, gray, w, h, atlas, cfg, minW, tallest);
            if (cut is null) { done.Add(b); continue; }

            todo.Enqueue(cut.Value.Left);
            todo.Enqueue(cut.Value.Right);
        }
        while (todo.Count > 0) done.Add(todo.Dequeue());

        done.Sort((a, b) => a.Box.X.CompareTo(b.Box.X));
        return done;
    }

    private static (GlyphBox Left, GlyphBox Right)? BestCut(GlyphBox b, byte[] bin, byte[] gray,
                                                            int w, int h, DigitAtlas atlas,
                                                            FishingConfig cfg, int minW, int tallest)
    {
        int lo = b.Box.Left, hi = b.Box.Right - 1;
        int width = hi - lo + 1;

        (GlyphBox L, GlyphBox R)? best = null;
        double bestScore = -2;

        // Thu MOI cot cat duoc, khong loc truoc theo "cho lom nhat" cua profile. Cho noi dau
        // cham beo dinh vao net dung cua so 1, profile NHAY VOT LEN tai ranh gioi (4 -> 19) chu
        // khong lom xuong, nen bo loc lom bo qua dung nhat cat can tim. Khoi hong nhat cung chi
        // rong vai chuc pixel, ma nhanh nay chi chay khi khoi da khong nhan ra duoc.
        for (int i = minW; i <= width - minW; i++)
        {
            var left = GlyphSeg.TightBox(bin, w, h, lo, lo + i - 1);
            var right = GlyphSeg.TightBox(bin, w, h, lo + i, hi);
            if (left is null || right is null) continue;

            var gl = ClassifyBox(gray, w, h, left.Box, tallest, atlas, cfg);
            if (gl.Ch == '?') continue;
            var gr = ClassifyBox(gray, w, h, right.Box, tallest, atlas, cfg);
            if (gr.Ch == '?') continue;

            double s = Math.Min(gl.Best, gr.Best);
            if (s > bestScore) { bestScore = s; best = (left, right); }
        }

        return best;
    }

    private static WeightRead Fail(string reason, string text, StringBuilder trace) =>
        new() { Ok = false, Reason = reason, Text = text, Trace = trace.ToString() };

    public void Dispose() => _region?.Dispose();
}
