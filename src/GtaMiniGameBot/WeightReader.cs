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
/// Đọc "27.4/30 KG". Không có thư viện OCR nào ở đây: tách glyph rồi so từng cái với bộ mẫu
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
    private readonly double _expectedCap;
    private readonly RegionReader _region;
    private readonly Rectangle _abs;

    private double _last = -1;

    public WeightReader(FishingConfig cfg, Screen screen, FishingRect roi, DigitAtlas atlas, double expectedCap)
    {
        _cfg = cfg;
        _atlas = atlas;
        _expectedCap = expectedCap;
        _abs = FishingConfig.ToAbsolute(screen, roi);
        _region = new RegionReader(_abs);
    }

    /// <summary>Bộ mẫu chưa đủ 12 ký tự thì đừng chạy — sẽ chỉ toàn '?'.</summary>
    public bool AtlasReady => _atlas.MissingText().Length == 0;

    public string AtlasMissing => _atlas.MissingText();

    /// <summary>Quên giá trị lần trước — gọi sau khi đổ cốp, vì lúc đó KG giảm là đúng.</summary>
    public void ResetHistory() => _last = -1;

    public WeightRead Read()
    {
        _region.Refresh();
        var gray = _region.GrayBuffer(_abs);
        return Parse(gray, _abs.Width, _abs.Height, _atlas, _cfg, _expectedCap, ref _last);
    }

    /// <summary>Đọc từ ảnh tĩnh — dùng để thử nguội, không cần đứng trong game.</summary>
    public static WeightRead ReadStill(Bitmap still, FishingRect roi, DigitAtlas atlas,
                                       FishingConfig cfg, double expectedCap)
    {
        var gray = GlyphSeg.GrayOf(still, roi.ToRectangle(), out int w, out int h);
        double ignore = -1;
        return Parse(gray, w, h, atlas, cfg, expectedCap, ref ignore);
    }

    private static WeightRead Parse(byte[] gray, int w, int h, DigitAtlas atlas,
                                    FishingConfig cfg, double expectedCap, ref double last)
    {
        if (gray.Length < w * h || w < 8 || h < 6)
            return new WeightRead { Reason = "vùng quá nhỏ" };

        var bin = GlyphSeg.Binarize(gray, cfg.DigitInkMinGray, out int thr);
        var boxes = GlyphSeg.Segment(bin, w, h, cfg.DigitMinGlyphW, cfg.DigitMinGlyphInk, cfg.DigitMergeGapPx);
        boxes = SplitTouching(boxes, bin, gray, w, h, atlas, cfg);

        var sb = new StringBuilder();
        var trace = new StringBuilder($"ngưỡng={thr} khối={boxes.Count}");
        int dots = 0;

        foreach (var b in boxes)
        {
            var g = atlas.Classify(gray, w, h, b.Box, cfg);
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

        // Mau so la cai neo: doc sai mot chu so thi gan nhu chac chan no khong con bang 30 nua.
        if (Math.Abs(cap - expectedCap) > 0.5)
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

    /// <summary>
    /// Khử ca hai chữ dính liền: khử răng cưa và kerning làm "0/" thành một khối duy nhất, và
    /// chiếu theo cột không thể tự thấy điều đó — profile không hề chạm đáy giữa hai chữ.
    ///
    /// Chỉ đụng vào khối RỘNG HƠN mọi mẫu đã học, và chỉ chấp nhận nhát cắt khi CẢ HAI nửa đều
    /// nhận ra được chắc chắn. Nhờ vậy cắt sai không thể sinh ra số sai: nửa nào không nhận ra
    /// thì nhát cắt bị loại, khối giữ nguyên và cả lần đọc bị từ chối như cũ.
    /// </summary>
    private static List<GlyphBox> SplitTouching(List<GlyphBox> boxes, byte[] bin, byte[] gray,
                                                int w, int h, DigitAtlas atlas, FishingConfig cfg)
    {
        if (atlas.Count == 0) return boxes;

        int maxW = atlas.MaxWidth + cfg.DigitWidthTolPx;
        int minW = Math.Max(1, atlas.MinWidth - cfg.DigitWidthTolPx);
        if (maxW <= 0 || !boxes.Any(b => b.Box.Width > maxW)) return boxes;

        var todo = new Queue<GlyphBox>(boxes);
        var done = new List<GlyphBox>();
        int guard = boxes.Count * 4 + 16;   // chan vong lap vo tan neu cat ra roi lai cat tiep mai

        while (todo.Count > 0 && guard-- > 0)
        {
            var b = todo.Dequeue();
            if (b.Box.Width <= maxW) { done.Add(b); continue; }

            var cut = BestCut(b, bin, gray, w, h, atlas, cfg, minW);
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
                                                            FishingConfig cfg, int minW)
    {
        int lo = b.Box.Left, hi = b.Box.Right - 1;
        var ink = GlyphSeg.ColumnInk(bin, w, h, lo, hi);

        (GlyphBox L, GlyphBox R)? best = null;
        double bestScore = -2;

        for (int i = minW; i <= ink.Length - minW; i++)
        {
            // Chi thu tai cho tham cuc bo: cat giua than mot chu so thi hai nua deu khong nhan ra,
            // thu het moi cot chi ton thoi gian.
            if (i > 0 && i < ink.Length - 1 && (ink[i] > ink[i - 1] || ink[i] > ink[i + 1])) continue;

            var left = GlyphSeg.TightBox(bin, w, h, lo, lo + i - 1);
            var right = GlyphSeg.TightBox(bin, w, h, lo + i, hi);
            if (left is null || right is null) continue;

            var gl = atlas.Classify(gray, w, h, left.Box, cfg);
            if (gl.Ch == '?') continue;
            var gr = atlas.Classify(gray, w, h, right.Box, cfg);
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
