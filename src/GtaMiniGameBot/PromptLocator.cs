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

/// <summary>
/// Mọi con số của bộ dò prompt. Gom lại một chỗ để <see cref="PromptLocator"/> không phải biết
/// job nào đang gọi nó — thợ mộc lấy từ <see cref="WoodConfig"/>, thợ điện lấy từ
/// <see cref="NavSettings"/>.
/// </summary>
internal sealed record PromptTuning
{
    /// <summary>Kênh TỐI NHẤT phải sáng từ đây trở lên thì mới tính là mực.</summary>
    public int InkMinBright { get; init; } = 200;

    /// <summary>Độ lệch cho phép giữa kênh sáng nhất và tối nhất — chặn màu có sắc.</summary>
    public int InkSpreadTol { get; init; } = 45;

    /// <summary>Một hàng phải có ít nhất ngần này pixel mực mới được tính là hàng có chữ.</summary>
    public int InkRowMin { get; init; } = 3;

    /// <summary>Hàng nào mực chiếm quá tỉ lệ này bề rộng băng thì là cảnh vật, không phải chữ.</summary>
    public double RowMaxFrac { get; init; } = 0.50;

    /// <summary>Khe dọc nhỏ hơn ngần này hàng thì nhập vào cùng một dòng (dấu mũ, chân chữ).</summary>
    public int RowGapMerge { get; init; } = 2;

    /// <summary>Dải hàng cao quá <c>TextH ×</c> ngần này thì không phải một dòng chữ.</summary>
    public double LineBandMaxRatio { get; init; } = 8.0;

    /// <summary>Giữ lại nhiều nhất ngần này dòng, ưu tiên dòng rộng nhất.</summary>
    public int MaxLines { get; init; } = 3;

    /// <summary>Ngưỡng NCC để coi là khớp mẫu.</summary>
    public double NccMin { get; init; } = 0.62;

    /// <summary>Chiều cao mực của nhóm chữ, đo lúc hiệu chuẩn.</summary>
    public int TextH { get; init; }

    /// <summary>Khe rộng từ đây trở lên thì tách ô phím khỏi chữ, đo lúc hiệu chuẩn.</summary>
    public int GapSplit { get; init; }

    /// <summary>
    /// So mẫu trên MẶT NẠ MỰC thay vì trên ảnh xám.
    ///
    /// Vì sao job Thợ điện cần: NCC trên ảnh xám so cả NỀN nằm giữa các nét chữ. Prompt gắn vào
    /// vật thể 3D nên nó xuất hiện lúc trên bê tông trắng nắng, lúc trên tủ điện xám tối — cùng
    /// một dòng chữ mà nền khác nhau thì điểm rớt thảm. Đo được trên ảnh tự vẽ: mẫu cắt lúc sau
    /// lưng có cột sáng, đem dò ở chỗ nền tối ra <c>ncc=0.26</c>, trong khi chữ khớp hoàn hảo.
    ///
    /// Mặt nạ mực thì độc lập nền theo đúng định nghĩa: ngưỡng mực đã tách chữ HUD khỏi thế giới
    /// rồi, nên chỉ còn so hình dạng chữ với hình dạng chữ. Điểm là IoU (giao trên hợp).
    ///
    /// Job Thợ mộc giữ nguyên đường xám: rừng đêm nền tối đều, nó đang chạy tốt, và
    /// <c>--verify-wood</c> là hàng rào không cho đổi bừa.
    /// </summary>
    public bool MatchOnInk { get; init; }
}

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
/// Dò prompt tương tác kiểu <c>[E] CHỮ</c> của FiveM — phần dùng chung, không biết job nào.
///
/// Lõi này tách ra từ <see cref="WoodLocator"/> khi job Thợ điện cần đúng bộ dò đó cho prompt
/// <c>[E] TƯƠNG TÁC</c> (người chơi xác nhận hai prompt giống hệt nhau). Mọi lý lẽ dưới đây đo
/// được trên ảnh thật của job thợ mộc, và tấm chụp trạm điện cho thấy chúng đúng y như vậy:
///
/// Vì sao bắt MỰC TRẮNG chứ không bắt khối ô phím:
///   Ô phím là khối TỐI bo góc, chữ bên trong mới trắng. Cảnh chặt cây là rừng lúc đêm — ĐO ĐƯỢC:
///   lọc "khối tối liền nhau" trên ảnh thật trả về đúng cả khung 557×460, tức nền cũng tối y như
///   ô phím. Ngược lại mực trắng thì cả băng quét chỉ có 1523 pixel (ảnh sẵn sàng) và 2698 pixel
///   (ảnh đang chặt) — không lẫn vào đâu được. Đây cũng là chỗ bản Python
///   (<c>simple_e_white_threshold 175</c> + <c>keycap_min_fill 0.42</c>, tức đi tìm một khối
///   TRẮNG ĐẶC) làm sai với UI này.
///
/// Vì sao neo vào MÉP TRÁI NHÓM CHỮ, không neo vào ô phím:
///   Thanh tiến trình 0→100 là VÒNG SÁNG chạy quanh viền ô phím, nên cụm mực của ô phím phình ra
///   theo phần trăm — đo được 34 px rộng lúc sẵn sàng, 50 px lúc đang chặt 40%. Neo vào đó là neo
///   vào cái đang động. Nhóm chữ thì đứng yên: mép trái 327 (sẵn sàng) và 331 (đang chặt).
///
/// Tách ô phím khỏi chữ bằng KHE: đo trên ảnh thật, khe ô-phím→chữ là 31 px còn khe giữa các từ
/// là 10–11 px. Ngưỡng tách nằm giữa hai số đó và được đo lại lúc hiệu chuẩn chứ không gõ cứng.
///
/// Vì sao dùng BĂNG QUÉT rộng chứ không một ô cố định: prompt GẮN VÀO VẬT THỂ trong không gian
/// 3D. Đo trên ảnh chụp thật: cùng một cái cây, hai lần chụp cách nhau hai phút, prompt nằm ở
/// y=841 rồi y=944. Ô cố định hụt ngay lần đầu đổi góc nhìn.
/// </summary>
internal sealed class PromptLocator : IDisposable
{
    /// <summary>Quét quanh điểm neo để hút sai số làm tròn. Neo khá chính xác nên không cần rộng.</summary>
    private const int Nudge = 3;

    private readonly PromptTuning _t;
    private readonly IPixelSource _band;
    private readonly GrayTemplate _tpl;

    /// <summary>Mực của mẫu, chỉ dựng khi <see cref="PromptTuning.MatchOnInk"/>.</summary>
    private readonly byte[] _tplInk;

    private readonly int _tplInkCount;

    /// <summary>Mặt nạ mực của lần <see cref="FindLines"/> gần nhất, để <see cref="Match"/> dùng lại.</summary>
    private byte[] _mask;

    public PromptLocator(PromptTuning tuning, IPixelSource band, GrayTemplate tpl)
    {
        _t = tuning;
        _band = band;
        _tpl = tpl;

        if (!tuning.MatchOnInk) return;

        // Mau luu duoi dang PNG xam nen R=G=B: do lech kenh bang 0, chi con nguong sang.
        _tplInk = new byte[tpl.Data.Length];
        int n = 0;
        for (int i = 0; i < tpl.Data.Length; i++)
        {
            if (tpl.Data[i] < tuning.InkMinBright) continue;
            _tplInk[i] = 1;
            n++;
        }
        _tplInkCount = n;
    }

    /// <summary>Số pixel mực trong mẫu — 0 nghĩa là mẫu vô dụng ở chế độ so mực.</summary>
    public int TemplateInkCount => _tplInkCount;

    public Rectangle BandRegion => _band.Region;

    public int TemplateWidth => _tpl.Width;

    public int TemplateHeight => _tpl.Height;

    // ---------------------------------------------------------------- do dong chu

    private bool IsInk(int b, int g, int r)
    {
        int min = Math.Min(b, Math.Min(g, r));
        if (min < _t.InkMinBright) return false;
        int max = Math.Max(b, Math.Max(g, r));
        return max - min <= _t.InkSpreadTol;
    }

    /// <summary>
    /// Chụp lại băng quét rồi trả về mọi dòng chữ trắng có kích cỡ hợp lý.
    ///
    /// Lọc theo cỡ là thứ chặn cảnh vật sáng: ban ngày thì trời, đá, thân xe trắng cũng thành mực,
    /// nhưng chúng trải hàng trăm hàng chứ không phải một dải cao ~<see cref="PromptTuning.TextH"/>.
    /// </summary>
    public List<TextLine> FindLines()
    {
        _band.Refresh();
        var mask = _band.MaskBuffer(IsInk);
        _mask = mask;
        int w = _band.Region.Width, h = _band.Region.Height;
        var origin = _band.Region.Location;

        int textH = Math.Max(6, _t.TextH);
        int bandHi = Math.Max(textH + 6, (int)Math.Ceiling(textH * _t.LineBandMaxRatio));
        int textLo = Math.Max(4, (int)(textH * 0.60));
        int textHi = (int)Math.Ceiling(textH * 1.60);
        int wLo = Math.Max(6, (int)(_tpl.Width * 0.40));
        int wHi = (int)Math.Ceiling(_tpl.Width * 4.0);
        int gapSplit = Math.Max(2, _t.GapSplit);

        int rowMax = Math.Max(_t.InkRowMin, (int)(w * _t.RowMaxFrac));

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
            bool on = y < h && rowCount[y] >= _t.InkRowMin && rowCount[y] <= rowMax;
            if (on) { if (y0 < 0) y0 = y; gap = 0; continue; }
            if (y0 < 0) continue;

            // Chu co net cach roi theo hang (dau mu, chan chu), nen dung ngat dai ngay hang dau
            // trong — gop cac khe nho lai.
            gap++;
            if (gap <= _t.RowGapMerge && y < h) continue;

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
            .Take(Math.Max(1, _t.MaxLines))
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

    // ---------------------------------------------------------------- so mau

    /// <summary>Có khớp mẫu ở dòng này không, kèm điểm để chỉnh ngưỡng bằng số thật.</summary>
    public (bool Hit, double Score) Match(TextLine line)
    {
        double score = BestScore(line);
        return (score >= _t.NccMin, score);
    }

    /// <summary>
    /// NCC của mẫu chữ, mép trái-trên canh vào mép trái-trên nhóm chữ, quét quanh ±<see cref="Nudge"/>.
    ///
    /// Kẹp cửa sổ trong băng: <see cref="GrayTemplate.Score"/> chỉ so được hai mảng bằng cỡ, mà
    /// <see cref="RegionReader.GrayBuffer"/> trả 0 cho pixel ngoài vùng — để tràn ra là điểm rác.
    /// </summary>
    private double BestScore(TextLine line)
    {
        var band = _band.Region;
        int tw = _tpl.Width, th = _tpl.Height;
        if (tw > band.Width || th > band.Height) return -1;

        double best = -1;
        for (int ox = -Nudge; ox <= Nudge; ox++)
        for (int oy = -Nudge; oy <= Nudge; oy++)
        {
            int x = Math.Clamp(line.Rect.Left + ox, band.Left, band.Right - tw);
            int y = Math.Clamp(line.Rect.Top + oy, band.Top, band.Bottom - th);

            double s = _t.MatchOnInk
                ? InkScore(x - band.Left, y - band.Top, tw, th)
                : _tpl.Score(_band.GrayBuffer(new Rectangle(x, y, tw, th)));

            if (s > best) best = s;
        }
        return best;
    }

    /// <summary>IoU giữa mực của mẫu và mực của băng tại một vị trí. Toạ độ TRONG băng.</summary>
    private double InkScore(int x0, int y0, int tw, int th)
    {
        if (_mask is null || _tplInk is null || _tplInkCount == 0) return -1;

        int bw = _band.Region.Width;
        int inter = 0, union = 0;

        for (int y = 0; y < th; y++)
        {
            int trow = y * tw, brow = (y0 + y) * bw + x0;
            for (int x = 0; x < tw; x++)
            {
                bool a = _tplInk[trow + x] != 0;
                bool b = _mask[brow + x] != 0;
                if (a && b) inter++;
                if (a || b) union++;
            }
        }

        return union == 0 ? 0 : (double)inter / union;
    }

    public void Dispose() => _band?.Dispose();

    // ---------------------------------------------------------------- dung luc khoanh

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
    public static PromptText ExtractText(Bitmap crop, PromptTuning t, out string problem)
    {
        problem = null;
        int w = crop.Width, h = crop.Height;
        if (w < 20 || h < 10) { problem = "ô quá nhỏ — khoanh trùm cả ô phím lẫn chữ"; return null; }

        using var src = new BitmapRegion(crop, new Rectangle(0, 0, w, h));
        var mask = src.MaskBuffer((b, g, r) =>
        {
            int min = Math.Min(b, Math.Min(g, r));
            if (min < t.InkMinBright) return false;
            return Math.Max(b, Math.Max(g, r)) - min <= t.InkSpreadTol;
        });

        // Dai hang co nhieu muc nhat: nguoi dung co the khoanh lo vao mot dong HUD khac ben canh.
        //
        // Nhung "nhieu muc nhat" mot minh thi KHONG du, va day la ca do duoc: canh cua job Thợ điện
        // la tram dien GIUA TRUA, cot be tong trang nang cung dat nguong muc. Mot cai cot chay doc
        // het o khoanh gop nhieu muc hon ca dong chu, nen no thang — va TextH do ra bang chieu cao
        // ca o, keo theo bo do song tu choi dung dong chu that vi "chu qua thap".
        //
        // Nen loc VONG MOT theo chieu cao: dai nao cao hon MOT PHAN cua o khoanh thi khong phai
        // dong chu, vi nguoi dung khoanh trum prompt chu khong khoanh trum canh vat. Khong con dai
        // nao dat thi moi ha xuong luat cu — o khoanh sat rat thi chinh dong chu cung chiem gan
        // het chieu cao, va luc do khong co dai nao khac de nham.
        int rowMax = Math.Max(t.InkRowMin, (int)(w * t.RowMaxFrac));
        int tallCap = Math.Max(6, (int)(h * 0.60));

        int y0 = FindBand(mask, w, h, t, rowMax, tallCap, out int y1);
        if (y0 < 0) y0 = FindBand(mask, w, h, t, rowMax, h, out y1);

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

    /// <summary>
    /// Dải hàng nhiều mực nhất mà KHÔNG cao quá <paramref name="maxBandH"/>.
    /// Trả về hàng đầu, hoặc −1 nếu không có dải nào đạt.
    /// </summary>
    private static int FindBand(byte[] mask, int w, int h, PromptTuning t, int rowMax, int maxBandH,
                                out int endRow)
    {
        int y0 = -1, bestInk = 0;
        int cy0 = -1, gap = 0, cInk = 0;
        endRow = -1;

        for (int y = 0; y <= h; y++)
        {
            int n = 0;
            if (y < h) { int row = y * w; for (int x = 0; x < w; x++) if (mask[row + x] != 0) n++; }

            bool on = y < h && n >= t.InkRowMin && n <= rowMax;
            if (on) { if (cy0 < 0) { cy0 = y; cInk = 0; } cInk += n; gap = 0; continue; }
            if (cy0 < 0) continue;

            gap++;
            if (gap <= t.RowGapMerge && y < h) continue;

            int end = y - gap;
            if (end - cy0 + 1 <= maxBandH && cInk > bestInk)
            {
                bestInk = cInk;
                y0 = cy0;
                endRow = end;
            }
            cy0 = -1;
        }

        return y0;
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
