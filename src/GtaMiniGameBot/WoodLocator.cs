namespace GtaMiniGameBot;

/// <summary>Một dòng chữ trắng dò được trong băng quét, toạ độ như của nguồn pixel.</summary>
internal sealed class TextLine
{
    /// <summary>Hộp bao MỰC của nhóm chữ (đã bỏ ô phím ra ngoài).</summary>
    public Rectangle Rect { get; init; }

    /// <summary>Số nhóm cụm trong dòng: 2 = có ô phím đứng trước, 1 = chỉ có chữ.</summary>
    public int Groups { get; init; }

    /// <summary>Chiều cao cả dải hàng, gồm cả ô phím / vòng tiến trình.</summary>
    public int BandH { get; init; }

    public override string ToString() =>
        $"{Rect.Width}×{Rect.Height}@{Rect.X},{Rect.Y} nhóm={Groups} dải={BandH}";
}

/// <summary>Kết quả so mẫu trên một dòng chữ.</summary>
internal sealed class WoodPick
{
    public TextLine Line { get; init; }

    /// <summary>Đúng là prompt "KHAI THÁC" đang sẵn sàng.</summary>
    public bool Ready { get; init; }

    public double Score { get; init; }

    public string Detail => $"{Line}  ncc={Score:F2} → {(Ready ? "SẴN SÀNG" : "–")}";
}

/// <summary>
/// Dò prompt tương tác "[E] KHAI THÁC" / "[40] ĐANG KHAI THÁC" của job thợ mộc.
///
/// Vì sao không khoanh một ô cố định như các ROI cũ: prompt này GẮN VÀO GỐC CÂY trong không gian
/// 3D. Đo trên ảnh chụp thật: cùng một cái cây, hai lần chụp cách nhau hai phút, prompt nằm ở
/// y=841 rồi y=944. Ô cố định hụt ngay lần đầu đổi góc nhìn.
///
/// Vì sao bắt MỰC TRẮNG chứ không bắt khối ô phím:
///   Ô phím là khối ĐEN bo góc, chữ bên trong mới trắng. Mà cảnh chặt cây là rừng lúc đêm —
///   ĐO ĐƯỢC: lọc "khối tối liền nhau" trên ảnh thật trả về đúng cả khung 557×460, tức nền cũng
///   tối y như ô phím. Ngược lại mực trắng thì cả băng quét chỉ có 1523 pixel (ảnh sẵn sàng) và
///   2698 pixel (ảnh đang chặt) — không lẫn vào đâu được.
///
/// Vì sao neo vào MÉP TRÁI NHÓM CHỮ, không neo vào ô phím:
///   Thanh tiến trình 0→100 là VÒNG SÁNG chạy quanh viền ô phím, nên cụm mực của ô phím phình ra
///   theo phần trăm — đo được 34 px rộng lúc sẵn sàng, 50 px lúc đang chặt 40%. Neo vào đó là neo
///   vào cái đang động. Nhóm chữ thì đứng yên: mép trái 327 (sẵn sàng) và 331 (đang chặt).
///
/// Tách ô phím khỏi chữ bằng KHE: đo trên ảnh thật, khe ô-phím→chữ là 31 px còn khe giữa các từ
/// là 10–11 px. Ngưỡng tách nằm giữa hai số đó và được đo lại lúc hiệu chuẩn chứ không gõ cứng.
///
/// Vì sao CHỈ MỘT mẫu là đủ, không cần mẫu riêng cho lúc đang chặt: "ĐANG KHAI THÁC" có chứa
/// "KHAI THÁC" thật, nhưng nhóm chữ lúc đó bắt đầu từ "ĐANG" (đo được x=331 so với x=327 lúc
/// sẵn sàng). Mẫu neo TRÁI nên nó đem so với "ĐANG KHAI" chứ không trượt sang được phần trùng —
/// không khớp, và đúng đó là tín hiệu "đang bận".
/// </summary>
internal sealed class WoodLocator : IDisposable
{
    /// <summary>Quét quanh điểm neo để hút sai số làm tròn. Neo khá chính xác nên không cần rộng.</summary>
    private const int Nudge = 3;

    private readonly WoodConfig _cfg;
    private readonly WoodProfile _p;
    private readonly IPixelSource _band;
    private readonly GrayTemplate _ready;

    public Rectangle BandRegion => _band.Region;

    private WoodLocator(WoodConfig cfg, WoodProfile p, IPixelSource band, GrayTemplate ready)
    {
        _cfg = cfg;
        _p = p;
        _band = band;
        _ready = ready;
    }

    /// <summary>Dò trên MÀN HÌNH THẬT. Null + lý do tiếng Việt nếu chưa đủ ô/mẫu.</summary>
    public static WoodLocator Create(WoodConfig cfg, Screen screen, WoodProfile p, out string problem)
        => Create(cfg, p, band => new RegionReader(FishingConfig.ToAbsolute(screen, band)), out problem);

    /// <summary>
    /// Dò trên một ẢNH TĨNH đã chụp — dùng cho form hiệu chuẩn và <c>--verify-wood</c>.
    /// Chạy đúng đoạn code của đường thật, chỉ đổi nguồn pixel.
    /// </summary>
    public static WoodLocator CreateForBitmap(WoodConfig cfg, WoodProfile p, Bitmap still, out string problem)
        => Create(cfg, p, band => new BitmapRegion(still, band.ToRectangle()), out problem);

    private static WoodLocator Create(WoodConfig cfg, WoodProfile p,
                                      Func<FishingRect, IPixelSource> openBand, out string problem)
    {
        problem = null;
        if (p is null) { problem = "chưa có cấu hình cho màn hình này"; return null; }
        if (!p.Ready.IsSet) { problem = "chưa khoanh prompt “khai thác”"; return null; }
        if (p.TextH < 6) { problem = "chưa đo được cỡ chữ — khoanh lại prompt"; return null; }

        var bandRect = p.ScanBand();
        if (!bandRect.IsSet) { problem = "vùng quét quá nhỏ"; return null; }

        var ready = LoadTemplate(WoodConfig.ReadyTemplatePath(p.Key), "khai thác", out problem);
        if (ready is null) return null;

        IPixelSource band;
        try { band = openBand(bandRect); }
        catch (Exception ex) { problem = "không mở được vùng quét: " + ex.Message; return null; }

        if (ready.Width > band.Region.Width || ready.Height > band.Region.Height)
        {
            band.Dispose();
            problem = "mẫu chữ to hơn vùng quét — khoanh vùng quét rộng hơn";
            return null;
        }

        return new WoodLocator(cfg, p, band, ready);
    }

    private static GrayTemplate LoadTemplate(string path, string name, out string problem)
    {
        problem = null;
        if (!File.Exists(path)) { problem = $"chưa có mẫu chữ “{name}”"; return null; }
        try
        {
            var t = GrayTemplate.FromFile(path);
            if (t.IsFlat) { problem = $"mẫu “{name}” phẳng — khoanh lại lúc prompt đang hiện"; return null; }
            return t;
        }
        catch (Exception ex)
        {
            problem = $"mẫu “{name}” hỏng: {ex.Message}";
            return null;
        }
    }

    // ---------------------------------------------------------------- do dong chu

    private bool IsInk(int b, int g, int r)
    {
        int min = Math.Min(b, Math.Min(g, r));
        if (min < _cfg.InkMinBright) return false;
        int max = Math.Max(b, Math.Max(g, r));
        return max - min <= _cfg.InkSpreadTol;
    }

    /// <summary>
    /// Chụp lại băng quét rồi trả về mọi dòng chữ trắng có kích cỡ hợp lý.
    ///
    /// Lọc theo cỡ là thứ chặn cảnh vật sáng: ban ngày thì trời, đá, thân xe trắng cũng thành mực,
    /// nhưng chúng trải hàng trăm hàng chứ không phải một dải cao ~<see cref="WoodProfile.TextH"/>.
    /// </summary>
    public List<TextLine> FindLines()
    {
        _band.Refresh();
        var mask = _band.MaskBuffer(IsInk);
        int w = _band.Region.Width, h = _band.Region.Height;
        var origin = _band.Region.Location;

        int textH = Math.Max(6, _p.TextH);
        int bandHi = Math.Max(textH + 6, (int)Math.Ceiling(textH * _cfg.LineBandMaxRatio));
        int textLo = Math.Max(4, (int)(textH * 0.60));
        int textHi = (int)Math.Ceiling(textH * 1.60);
        int wLo = Math.Max(6, (int)(_ready.Width * 0.40));
        int wHi = (int)Math.Ceiling(_ready.Width * 4.0);
        int gapSplit = Math.Max(2, _p.GapSplit);

        int rowMax = Math.Max(_cfg.InkRowMin, (int)(w * _cfg.RowMaxFrac));

        var rowCount = new int[h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w, n = 0;
            for (int x = 0; x < w; x++) if (mask[row + x] != 0) n++;
            rowCount[y] = n;
        }

        var lines = new List<TextLine>();
        int y0 = -1, gap = 0;
        for (int y = 0; y <= h; y++)
        {
            bool on = y < h && rowCount[y] >= _cfg.InkRowMin && rowCount[y] <= rowMax;
            if (on) { if (y0 < 0) y0 = y; gap = 0; continue; }
            if (y0 < 0) continue;

            // Chu co net cach roi theo hang (dau mu, chan chu), nen dung ngat dai ngay hang dau
            // trong — gop cac khe nho lai.
            gap++;
            if (gap <= _cfg.RowGapMerge && y < h) continue;

            int y1 = y - gap;
            int bandH = y1 - y0 + 1;
            if (bandH >= textLo && bandH <= bandHi)
            {
                var line = MeasureLine(mask, w, y0, y1, gapSplit, origin);
                if (line is not null &&
                    line.Rect.Height >= textLo && line.Rect.Height <= textHi &&
                    line.Rect.Width >= wLo && line.Rect.Width <= wHi)
                    lines.Add(line);
            }
            y0 = -1;
        }

        // Nhieu dong thi uu tien dong RONG nhat: prompt cua job dai hon moi thu HUD lac vao.
        return lines
            .OrderByDescending(l => l.Rect.Width)
            .Take(Math.Max(1, _cfg.MaxLines))
            .ToList();
    }

    /// <summary>
    /// Trong một dải hàng: gom cụm mực theo cột, tách nhóm ở những khe rộng hơn
    /// <paramref name="gapSplit"/>, rồi lấy NHÓM CUỐI làm nhóm chữ.
    ///
    /// Lấy nhóm cuối chứ không "bỏ nhóm đầu": prompt không có ô phím thì chỉ có một nhóm và nhóm
    /// đó vẫn đúng là chữ.
    /// </summary>
    private static TextLine MeasureLine(byte[] mask, int w, int y0, int y1, int gapSplit, Point origin)
    {
        var colOn = new bool[w];
        for (int x = 0; x < w; x++)
        {
            for (int y = y0; y <= y1; y++)
                if (mask[y * w + x] != 0) { colOn[x] = true; break; }
        }

        int groups = 0, gx0 = -1, gx1 = -1;
        int x0 = -1, gap = 0;
        for (int x = 0; x <= w; x++)
        {
            bool on = x < w && colOn[x];
            if (on) { if (x0 < 0) x0 = x; gap = 0; continue; }
            if (x0 < 0) continue;

            gap++;
            if (gap <= gapSplit && x < w) continue;

            groups++;
            gx0 = x0;
            gx1 = x - gap;
            x0 = -1;
        }
        if (groups == 0 || gx1 < gx0) return null;

        // Siet bien hang RIENG cho nhom chu: dai hang con chua ca vong tien trinh cao hon chu.
        int ty0 = -1, ty1 = -1;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = gx0; x <= gx1; x++)
                if (mask[y * w + x] != 0) { if (ty0 < 0) ty0 = y; ty1 = y; break; }
        }
        if (ty0 < 0) return null;

        return new TextLine
        {
            Rect = new Rectangle(origin.X + gx0, origin.Y + ty0, gx1 - gx0 + 1, ty1 - ty0 + 1),
            Groups = groups,
            BandH = y1 - y0 + 1
        };
    }

    // ---------------------------------------------------------------- phan loai

    /// <summary>
    /// So mẫu tại một dòng chữ. Không đạt ngưỡng là câu trả lời hợp lệ — thà bỏ một khung còn
    /// hơn bấm E giữa lúc đang chặt.
    /// </summary>
    public WoodPick Classify(TextLine line)
    {
        double score = BestScore(line, _ready);
        return new WoodPick { Line = line, Ready = score >= _cfg.NccMin, Score = score };
    }

    /// <summary>Điểm tại một dòng — để chỉnh ngưỡng bằng số thật, không đoán.</summary>
    public string DescribeScores(TextLine line) => Classify(line).Detail;

    /// <summary>
    /// NCC của một mẫu chữ, mép trái-trên canh vào mép trái-trên nhóm chữ, quét quanh ±<see cref="Nudge"/>.
    ///
    /// Kẹp cửa sổ trong băng: <see cref="GrayTemplate.Score"/> chỉ so được hai mảng bằng cỡ, mà
    /// <see cref="RegionReader.GrayBuffer"/> trả 0 cho pixel ngoài vùng — để tràn ra là điểm rác.
    /// </summary>
    private double BestScore(TextLine line, GrayTemplate tpl)
    {
        var band = _band.Region;
        int tw = tpl.Width, th = tpl.Height;
        if (tw > band.Width || th > band.Height) return -1;

        double best = -1;
        for (int ox = -Nudge; ox <= Nudge; ox++)
        for (int oy = -Nudge; oy <= Nudge; oy++)
        {
            int x = Math.Clamp(line.Rect.Left + ox, band.Left, band.Right - tw);
            int y = Math.Clamp(line.Rect.Top + oy, band.Top, band.Bottom - th);
            double s = tpl.Score(_band.GrayBuffer(new Rectangle(x, y, tw, th)));
            if (s > best) best = s;
        }
        return best;
    }

    public void Dispose() => _band?.Dispose();

    // ---------------------------------------------------------------- dung luc khoanh

    /// <summary>Phần chữ tách được từ ô người dùng vừa khoanh.</summary>
    internal sealed class PromptText
    {
        /// <summary>Hộp bao mực của nhóm chữ, toạ độ TRONG ảnh đã cắt.</summary>
        public Rectangle Text { get; init; }

        /// <summary>Ngưỡng tách ô phím khỏi chữ, đo từ chính khe của prompt này.</summary>
        public int GapSplit { get; init; }

        public string Note { get; init; } = "";
    }

    /// <summary>
    /// Tách phần CHỮ ra khỏi ô người dùng vừa khoanh, và đo luôn ngưỡng khe.
    ///
    /// Người dùng chỉ khoanh cả prompt một lần cho mỗi trạng thái; mọi hình học suy ra ở đây chứ
    /// không bắt họ khoanh nhiều lần rồi tự cộng trừ toạ độ.
    ///
    /// Ô phím KHÔNG vào mẫu: bên trong nó là chữ E hay con số đếm ngược đổi liên tục, và quanh nó
    /// là vòng tiến trình đang chạy. Cả hai đều là thứ đang động — nhét vào mẫu NCC là tự dìm
    /// điểm khớp của chính mình.
    /// </summary>
    public static PromptText ExtractText(Bitmap crop, WoodConfig cfg, out string problem)
    {
        problem = null;
        int w = crop.Width, h = crop.Height;
        if (w < 20 || h < 10) { problem = "ô quá nhỏ — khoanh trùm cả ô phím lẫn chữ"; return null; }

        using var src = new BitmapRegion(crop, new Rectangle(0, 0, w, h));
        var mask = src.MaskBuffer((b, g, r) =>
        {
            int min = Math.Min(b, Math.Min(g, r));
            if (min < cfg.InkMinBright) return false;
            return Math.Max(b, Math.Max(g, r)) - min <= cfg.InkSpreadTol;
        });

        // Dai hang co nhieu muc nhat: nguoi dung co the khoanh lo vao mot dong HUD khac ben canh.
        int rowMax = Math.Max(cfg.InkRowMin, (int)(w * cfg.RowMaxFrac));
        int y0 = -1, y1 = -1, bestInk = 0;
        int cy0 = -1, gap = 0, cInk = 0;
        for (int y = 0; y <= h; y++)
        {
            int n = 0;
            if (y < h) { int row = y * w; for (int x = 0; x < w; x++) if (mask[row + x] != 0) n++; }

            bool on = y < h && n >= cfg.InkRowMin && n <= rowMax;
            if (on) { if (cy0 < 0) { cy0 = y; cInk = 0; } cInk += n; gap = 0; continue; }
            if (cy0 < 0) continue;

            gap++;
            if (gap <= cfg.RowGapMerge && y < h) continue;

            if (cInk > bestInk) { bestInk = cInk; y0 = cy0; y1 = y - gap; }
            cy0 = -1;
        }

        if (y0 < 0)
        {
            problem = "không thấy chữ trắng nào trong vùng đã khoanh — khoanh trùm cả chữ, " +
                      "và chụp lúc prompt đang hiện";
            return null;
        }

        // Cum tho (khong gop khe) de DO cac khe: khe rong nhat la o-phim -> chu, khe rong thu hai
        // la khe giua cac tu. Nguong tach nam giua hai so do.
        var runs = ColumnRuns(mask, w, y0, y1, gapMerge: 0);
        if (runs.Count == 0) { problem = "không tách được cụm chữ — khoanh lại"; return null; }

        int gapSplit = MeasureGapSplit(runs, w);
        var groups = ColumnRuns(mask, w, y0, y1, gapSplit);
        var last = groups[^1];

        int ty0 = -1, ty1 = -1;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = last.Start; x <= last.End; x++)
                if (mask[y * w + x] != 0) { if (ty0 < 0) ty0 = y; ty1 = y; break; }
        }
        if (ty0 < 0) { problem = "nhóm chữ rỗng — khoanh lại"; return null; }

        var text = Rectangle.FromLTRB(last.Start, ty0, last.End + 1, ty1 + 1);
        if (text.Width < 12 || text.Height < 6)
        {
            problem = $"nhóm chữ chỉ {text.Width}×{text.Height} — khoanh trùm hết chữ " +
                      "(đừng khoanh riêng ô phím)";
            return null;
        }

        return new PromptText
        {
            Text = text,
            GapSplit = gapSplit,
            Note = $"chữ {text.Width}×{text.Height}@{text.X},{text.Y}  " +
                   $"{groups.Count} nhóm, ngưỡng khe {gapSplit}px"
        };
    }

    private readonly record struct Run(int Start, int End);

    /// <summary>Các dải cột có mực, các dải cách nhau ≤ <paramref name="gapMerge"/> thì nhập một.</summary>
    private static List<Run> ColumnRuns(byte[] mask, int w, int y0, int y1, int gapMerge)
    {
        var colOn = new bool[w];
        for (int x = 0; x < w; x++)
        {
            for (int y = y0; y <= y1; y++)
                if (mask[y * w + x] != 0) { colOn[x] = true; break; }
        }

        var runs = new List<Run>();
        int x0 = -1, gap = 0;
        for (int x = 0; x <= w; x++)
        {
            bool on = x < w && colOn[x];
            if (on) { if (x0 < 0) x0 = x; gap = 0; continue; }
            if (x0 < 0) continue;

            gap++;
            if (gap <= gapMerge && x < w) continue;

            runs.Add(new Run(x0, x - gap));
            x0 = -1;
        }
        return runs;
    }

    /// <summary>
    /// Ngưỡng khe, đặt vừa TRÊN khe rộng thứ hai (khe giữa các từ) chứ không phải giữa nó và khe
    /// rộng nhất (khe ô-phím→chữ).
    ///
    /// Vì sao lệch xuống chứ không lấy trung bình: khe được đo ở khung SẴN SÀNG, nơi cụm đầu là
    /// chữ "E" nhỏ nằm giữa ô phím. Lúc chạy, khung ĐANG CHẶT có vòng tiến trình bọc quanh ô phím
    /// nên cụm đầu rộng ra và khe HỤT LẠI — đo trên ảnh thật là 31 px tụt còn 30 px, nhưng biên độ
    /// đó phụ thuộc độ dày vòng. Ngưỡng thấp thì chỉ cần vượt khe giữa từ (đo được 10–11 px) là đủ
    /// việc, mà lại còn dư chỗ cho vòng phình.
    /// Chỉ có một dải (prompt không có ô phím) thì suy theo bề rộng màn.
    /// </summary>
    private static int MeasureGapSplit(List<Run> runs, int w)
    {
        if (runs.Count < 2) return Math.Clamp(w / 28, 6, 60);

        var gaps = new List<int>();
        for (int i = 1; i < runs.Count; i++) gaps.Add(runs[i].Start - runs[i - 1].End - 1);
        gaps.Sort();

        int g1 = gaps[^1];
        int g2 = gaps.Count >= 2 ? gaps[^2] : 0;
        return Math.Clamp(g2 + Math.Max(2, (g1 - g2) / 4), 6, 60);
    }
}
