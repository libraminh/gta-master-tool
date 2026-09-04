using System.Drawing.Imaging;
using System.Drawing.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Tự kiểm phần TÁCH CHỒNG CÁ mà không cần vào game.
///
/// Hai lớp kiểm, và lớp thứ hai mới là lý do file này tồn tại:
///   1. Đường ống — dựng một panel giả đúng bố cục game (dòng "n ĐƠN VỊ / x.xxx KG" ở đỉnh,
///      dải ba nút ở đáy với nút giữa xanh), rồi đòi bộ đọc ra đúng hai con số và đúng tâm nút.
///   2. CỬA AN TOÀN — nút VỨT nằm ngay cạnh nút TÁCH, nên mỗi ca dưới đây dựng một tình huống
///      mà bộ dò PHẢI từ chối: không có nút, nút sai tỉ lệ, nút rỗng ruột, hai mảng xanh ngang
///      nhau. Ca nào mà bộ dò vẫn trả về một hình chữ nhật là một cú click liều trong game.
///
/// Chạy: GtaMiniGameBot.exe --verify-split [đường-dẫn-ảnh-chụp-panel.png]
/// Có ảnh thật thì đọc thêm trên ảnh đó — đấy là dữ liệu duy nhất mang phông và màu thật.
/// </summary>
internal static class VerifySplit
{
    private const string Key = "selftest-split";

    // Man hinh gia: dung 2560x1440 cho khop voi cac ti le mac dinh trong FishingConfig.
    private const int ScreenW = 2560;
    private const int ScreenH = 1440;

    // Bo cuc panel do tren anh chup that 2560x1440 (debug-split, 27/08). Do lai sau khi biet
    // panel neo theo O chu khong theo nut: mep trai lui ra ngoai o 10 px, dinh thut xuong 5 px,
    // be ngang co dinh 474, dai ba nut cao 125 nam sat DAY panel.
    private const int CellTop = 661;          // o duoc chuot phai: ba lo hang 4
    private const int PanelX = 467;
    private const int PanelTop = CellTop + 5;
    private const int PanelW = 474;
    private const int ButtonW = PanelW / 3;
    private const int ButtonH = 125;
    private const int PanelH = 450;

    /// <summary>Dòng "n ĐƠN VỊ / x.xxx KG" nằm dưới mép trên ô bấy nhiêu px — khớp SplitLineTopFrac.</summary>
    private const int LineDy = 32;

    private static readonly Color Green = Color.FromArgb(58, 203, 95);
    private static readonly Color PanelBg = Color.FromArgb(25, 25, 25);

    public static int Run(string[] args)
    {
        if (args.Length > 1 && args[1].Equals("--live", StringComparison.OrdinalIgnoreCase))
            return Live();

        Console.WriteLine("== tự kiểm tách chồng cá ==");

        string dir = FishingConfig.DigitDir(Key);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        var cfg = new FishingConfig();
        cfg.Normalize();

        if (!Teach(cfg)) { Console.WriteLine("không dạy đủ bộ mẫu chữ số — dừng"); return 1; }
        var atlas = DigitAtlas.Load(Key);
        Console.WriteLine($"bộ mẫu: {atlas.Count} mẫu");

        int fail = 0;

        Console.WriteLine();
        Console.WriteLine("-- đọc panel --");
        fail += Expect(cfg, atlas, "15 ĐƠN VỊ / 26.250 KG", ok: true, count: 15, kg: 26.250) ? 0 : 1;
        fail += Expect(cfg, atlas, "1 ĐƠN VỊ / 1.750 KG", ok: true, count: 1, kg: 1.750) ? 0 : 1;
        fail += Expect(cfg, atlas, "45 ĐƠN VỊ / 78.750 KG", ok: true, count: 45, kg: 78.750) ? 0 : 1;
        // Ca quan trong nhat cua bo doc: chu cai dau tu bi cham NHAM thanh chu so. Doc ca dong
        // roi bat regex thi "15 0ƠN" dinh thanh "150" va bot se tach gap muoi lan so con can.
        // Gom khoi thanh TU chan duoc, va day la ca chung minh dieu do.
        fail += Expect(cfg, atlas, "15 0ƠN VỊ / 26.250 KG", ok: true, count: 15, kg: 26.250) ? 0 : 1;
        fail += Expect(cfg, atlas, "ĐƠN VỊ / KG", ok: false) ? 0 : 1;
        fail += Expect(cfg, atlas, "15 ĐƠN VỊ", ok: false) ? 0 : 1;
        // Dong voi con so vo ly phai bi tu choi. Roi o cong nao cung duoc — cai phai chung minh
        // la khong co duong nao de mot con ca 199 kg di ra thanh mot phep chia that.
        fail += Expect(cfg, atlas, "1 ĐƠN VỊ / 199.000 KG", ok: false) ? 0 : 1;

        Console.WriteLine();
        Console.WriteLine("-- tính số con cần tách --");
        fail += Plan(26.250, 15, free: 22.3, margin: 3.0, want: 11) ? 0 : 1;
        fail += Plan(26.250, 15, free: 30.0, margin: 3.0, want: 15) ? 0 : 1;   // ca chong lot tron
        fail += Plan(26.250, 15, free: 3.5, margin: 3.0, want: 0) ? 0 : 1;     // ca cop day han

        Console.WriteLine();
        Console.WriteLine("-- suy ra nút TÁCH từ nút đang sáng --");
        fail += Layout() ? 0 : 1;
        fail += Hover(cfg) ? 0 : 1;

        Console.WriteLine();
        Console.WriteLine("-- cửa an toàn (mọi ca dưới đây PHẢI từ chối) --");
        fail += Reject(cfg, "không có nút nào", b => { }) ? 0 : 1;
        fail += Reject(cfg, "nút quá bẹt", b => Fill(b, new Rectangle(600, 700, 400, 40), Green)) ? 0 : 1;
        fail += Reject(cfg, "nút quá vuông", b => Fill(b, new Rectangle(600, 700, 130, 130), Green)) ? 0 : 1;
        // Khung XANH dung ti le, dung du to, nhung rong ruot — chi co cua "phai dac" chan duoc.
        // Co y ve to hon nut that: khoet ruot mot hinh co nut that thi phan con lai tut xuong
        // duoi san dien tich va bi cua kia loai truoc, tuc cua do dac khong he duoc thu.
        fail += Reject(cfg, "khung rỗng ruột", b =>
        {
            Fill(b, new Rectangle(600, 700, 300, 220), Green);
            Fill(b, new Rectangle(630, 730, 240, 160), PanelBg);
        }) ? 0 : 1;
        fail += Reject(cfg, "hai mảng xanh ngang nhau", b =>
        {
            Fill(b, new Rectangle(600, 700, ButtonW, ButtonH), Green);
            Fill(b, new Rectangle(900, 700, ButtonW, ButtonH), Green);
        }) ? 0 : 1;

        try { Directory.Delete(Path.Combine(AppPaths.Root, "fishing", Key), recursive: true); } catch { }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "TẤT CẢ ĐẠT (ảnh tự vẽ)" : $"HỎNG {fail} ca (ảnh tự vẽ)");

        string shot = args.Length > 1 ? args[1] : null;
        if (shot is not null) RealShot(shot, cfg);

        return fail == 0 ? 0 : 2;
    }

    /// <summary>
    /// Soi panel THẬT đang mở trong game.
    ///
    /// Chạy: <c>GtaMiniGameBot.exe --verify-split --live</c> — đếm ngược rồi tự chụp, nên người
    /// dùng chỉ cần chuột phải vào ô cá và để yên tay.
    ///
    /// Chụp HAI lần có chủ ý: lần đầu giữ nguyên con trỏ nơi vừa bấm, lần sau dời con trỏ ra
    /// góc màn rồi chụp lại. Đó là phép thử cho nghi vấn quan trọng nhất khi nút không dò ra —
    /// panel này là menu ngữ cảnh đứng yên, hay là bảng chú giải tắt ngay khi chuột rời ô? Nếu
    /// là vế sau thì mọi thứ bot làm sau cú chuột phải đều đang nhìn vào một màn hình trống, và
    /// không ngưỡng màu nào chữa được.
    /// </summary>
    private static int Live()
    {
        var cfg = FishingConfig.Load();
        var screen = Screen.PrimaryScreen;
        if (screen is null) { Console.WriteLine("không thấy màn hình"); return 2; }

        var bounds = screen.Bounds;
        string key = $"{bounds.Width}x{bounds.Height}";
        Console.WriteLine($"== soi panel thật ({key}) ==");
        Console.WriteLine("Chuyển sang game NGAY: mở kho đồ, CHUỘT PHẢI vào ô cá, rồi để yên tay.");

        // San 12 giay chu khong dung ShotCountdownSec (5): cac che do chup khac chi doi nguoi
        // dung bam mot phim, con o day ho phai alt-tab sang game, mo kho do VA chuot phai dung o.
        for (int s = Math.Max(12, cfg.ShotCountdownSec); s > 0; s--)
        {
            Console.WriteLine($"  chụp sau {s}…");
            Thread.Sleep(1000);
        }

        // Ghi lai vi tri con tro NGAY BAY GIO, truoc khi doi no di: nguoi dung vua chuot phai
        // vao o ca, nen day chinh la o dang mo panel.
        Native.GetCursorPos(out var cp);
        var cursor = new Point(cp.x, cp.y);

        var band = Band(cfg, bounds);
        Console.WriteLine($"vùng quét: {band.Width}×{band.Height} @ {band.X},{band.Y}");
        Console.WriteLine($"con trỏ đang ở {cursor.X},{cursor.Y}");

        string dir = Path.Combine(FishingConfig.ProfileDir(key), "debug-split");
        Directory.CreateDirectory(dir);

        using (var shot = RegionPicker.Capture(bounds))
        {
            RegionPicker.SavePng(shot, Path.Combine(dir, "1-con-tro-tai-cho.png"));
            Report("con trỏ NGUYÊN chỗ vừa bấm", shot, band, cfg, bounds);
        }

        InputSender.MoveCursorOnly(bounds.Left + 40, bounds.Top + 40);
        Thread.Sleep(400);

        using (var shot = RegionPicker.Capture(bounds))
        {
            RegionPicker.SavePng(shot, Path.Combine(dir, "2-con-tro-da-doi-di.png"));
            Report("con trỏ ĐÃ DỜI ra góc màn", shot, band, cfg, bounds);
        }

        Console.WriteLine();
        Console.WriteLine("Hai ảnh vừa chụp nằm ở: " + dir);
        DryRun(cfg, screen, cursor);
        return 0;
    }

    /// <summary>
    /// Chạy ĐÚNG phép định vị mà bot dùng, trên panel thật, rồi in ra chỗ nó sẽ bấm — và không
    /// bấm gì cả.
    ///
    /// Đây là thứ đáng giá nhất trong cả chế độ này: hai nút kẹp bên cạnh TÁCH là DÙNG (ăn mất
    /// cá) và VỨT (bỏ cá đi), nên xem trước được một cú click mà không phải chịu hậu quả của nó
    /// là cách duy nhất kiểm tra tử tế.
    /// </summary>
    private static void DryRun(FishingConfig cfg, Screen screen, Point cursor)
    {
        Console.WriteLine();
        Console.WriteLine("-- thử định vị nút TÁCH (KHÔNG bấm) --");

        var profile = cfg.GetOrCreate(screen);
        if (profile is null) { Console.WriteLine("  chưa có hồ sơ cho màn hình này"); return; }

        // O duoc chuot phai chinh la o con tro dang nam tren — nguoi dung vua bam vao do.
        var cell = CellUnder(cfg, screen, profile, cursor, out string gridLabel);
        if (cell is null)
        {
            Console.WriteLine($"  con trỏ ở {cursor.X},{cursor.Y} không nằm trong lưới nào " +
                              "(phím nhanh / ba lô / trên người) — bỏ bước này");
            return;
        }
        Console.WriteLine($"  ô vừa chuột phải: {gridLabel} #{cell.Index} " +
                          $"@ {cell.Rect.X},{cell.Rect.Y} {cell.Rect.Width}×{cell.Rect.Height}");

        using var splitter = new ItemSplitter(cfg, screen, DigitAtlas.Load(profile.Key),
                                              s => Console.WriteLine("  " + s));

        // Dinh vi panel bang chinh dong chu truoc, y het luc chay that.
        var read = splitter.LocatePanel(cell);
        Console.WriteLine(read.Ok
            ? $"  đọc được panel: {read} — khung panel {read.Panel.Width}×{read.Panel.Height} " +
              $"@ {read.Panel.X},{read.Panel.Y}"
            : $"  chưa đọc được dòng “n ĐƠN VỊ / x.xxx KG” ({read.Reason})");

        var mid = splitter.LocateSplitButton(cell, read.Ok ? read.Panel : Rectangle.Empty,
                                             CancellationToken.None);

        Console.WriteLine(mid is null
            ? "  → KHÔNG định vị được nút TÁCH, nên bot sẽ bỏ lượt tách (không bấm gì)"
            : $"  → bot sẽ bấm TÁCH ở {mid.Value.X},{mid.Value.Y}");
    }

    /// <summary>Ô của lưới nào đang nằm dưới điểm <paramref name="p"/>. Null nếu không lưới nào.</summary>
    private static CellInfo CellUnder(FishingConfig cfg, Screen screen, FishingProfile profile,
                                      Point p, out string label)
    {
        foreach (var (name, grid) in new[] { ("phím nhanh", profile.Hotbar),
                                             ("ba lô", profile.Bag),
                                             ("trên người", profile.Pockets) })
        {
            if (!grid.IsSet) continue;

            using var scanner = new GridScanner(cfg, screen, grid);
            foreach (var c in scanner.ScanScreen())
            {
                if (!c.Rect.Contains(p)) continue;
                label = name;
                return c;
            }
        }
        label = null;
        return null;
    }

    private static Rectangle Band(FishingConfig cfg, Rectangle bounds)
    {
        int x = bounds.Left + (int)Math.Round(bounds.Width * cfg.SplitBandLeftFrac);
        int y = bounds.Top + (int)Math.Round(bounds.Height * cfg.SplitBandTopFrac);
        int w = (int)Math.Round(bounds.Width * cfg.SplitBandWidthFrac);
        int h = (int)Math.Round(bounds.Height * cfg.SplitBandHeightFrac);
        return Rectangle.Intersect(new Rectangle(x, y, w, h), bounds);
    }

    /// <summary>In mọi mảng xanh thấy được rồi mới in phán quyết — thứ tự đó mới chẩn đoán được.</summary>
    private static void Report(string label, Bitmap shot, Rectangle band, FishingConfig cfg,
                               Rectangle bounds)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {label} --");

        // Anh chup ca man nen toa do anh trung toa do man hinh, tru phan offset man phu.
        var inImage = new Rectangle(band.X - bounds.Left, band.Y - bounds.Top, band.Width, band.Height);
        using var src = new BitmapRegion(shot, inImage);

        double expW = bounds.Width * cfg.SplitButtonWidthFrac;
        double expH = bounds.Height * cfg.SplitButtonHeightFrac;
        int minArea = (int)Math.Round(expW * expH * cfg.SplitButtonAreaFracMin);
        Console.WriteLine($"  nút chờ ≈ {expW:0}×{expH:0} px, sàn diện tích {minArea}, " +
                          $"tỉ lệ {cfg.SplitButtonAspect:0.00}±{cfg.SplitButtonAspectTol:0.00}, " +
                          $"đặc ≥ {cfg.SplitButtonFillMin:0.00}");

        // San 200 px: du nho de thay ca nhung manh xanh vun (icon, vien), du lon de khong in ra
        // hang tram dong nhieu.
        var blobs = SplitPanelReader.GreenBlobs(src, inImage, 200, 6);
        if (blobs.Count == 0)
        {
            Console.WriteLine("  KHÔNG có mảng xanh nào ≥ 200 px trong vùng quét");
        }
        else
        {
            foreach (var b in blobs)
                Console.WriteLine($"  xanh {b.Box.Width,4}×{b.Box.Height,-4} @ {b.Box.X},{b.Box.Y}" +
                                  $"  diện tích {b.Area,6}  tỉ lệ {b.Aspect:0.00}  đặc {b.Fill:0.00}" +
                                  (b.Area < minArea ? "   ← dưới sàn diện tích" : ""));
        }

        var button = SplitPanelReader.FindButton(src, inImage, cfg, bounds.Width, bounds.Height,
                                                 out string why);
        if (button.IsEmpty)
        {
            Console.WriteLine("  → KHÔNG dò ra nút đang sáng: " + why);
            return;
        }

        // Nut sang la THUOC chu khong phai dich. In ca ba moc suy ra tu no de doi chieu bang mat
        // voi anh chup: mocgiữa phai roi dung vao nut TÁCH.
        var mid = SplitPanelReader.MiddleButtonCentre(button);
        var third = SplitPanelReader.ThirdButtonCentre(button);
        var before = SplitPanelReader.BeforeFirstCentre(button);

        Console.WriteLine($"  → nút đang sáng {button.Width}×{button.Height} @ {button.X},{button.Y}");
        Console.WriteLine($"     suy ra: trước-nút-đầu {before.X},{before.Y} (phải TRỐNG) · " +
                          $"TÁCH {mid.X},{mid.Y} · nút ba {third.X},{third.Y}");
        Console.WriteLine("     (bot còn rê chuột lên từng mốc để game tự xác nhận trước khi bấm)");
    }

    /// <summary>
    /// Đọc trên ảnh chụp THẬT. Không tính vào đạt/hỏng — bộ mẫu chữ số của người dùng có thể
    /// còn thiếu cỡ chữ của panel — nhưng đây là chỗ duy nhất thấy được phông và màu thật.
    /// </summary>
    private static void RealShot(string path, FishingConfig defaults)
    {
        Console.WriteLine();
        Console.WriteLine("== đọc thử trên ảnh chụp thật ==");

        if (!File.Exists(path)) { Console.WriteLine("không thấy ảnh: " + path); return; }

        using var still = new Bitmap(path);
        Console.WriteLine($"ảnh: {path}  {still.Width}×{still.Height}");

        var cfg = FishingConfig.Load();
        string key = $"{still.Width}x{still.Height}";
        var atlas = DigitAtlas.Load(key);
        Console.WriteLine($"bộ mẫu {key}: {atlas.Count} mẫu");

        var band = new Rectangle(0, 0, still.Width, still.Height);
        using var src = new BitmapRegion(still, band);

        var button = SplitPanelReader.FindButton(src, band, cfg, still.Width, still.Height, out string why);
        if (button.IsEmpty) { Console.WriteLine("  không dò ra nút TÁCH: " + why); return; }

        Console.WriteLine($"  nút TÁCH: {button.Width}×{button.Height} @ {button.X},{button.Y}" +
                          $"  tâm {button.Left + button.Width / 2},{button.Top + button.Height / 2}");

        // Chua biet o nao duoc chuot phai, nen quet dan tu dinh nut nguoc len — dong chu nam
        // dau do phia tren, va cua kiem trong ParseLine se loai het cac moc sai.
        var profile = cfg.Profiles.TryGetValue(key, out var p) ? p : null;
        for (int top = button.Top - 500; top < button.Top; top += 20)
        {
            var r = SplitPanelReader.ReadLine(src, button, top, atlas, cfg, still.Height);
            if (!r.Ok) continue;
            Console.WriteLine($"  đọc được ở mép ô {top}: {r}");
            Console.WriteLine($"      {r.Trace}");
            return;
        }
        Console.WriteLine("  dò ra nút nhưng không đọc được dòng “n ĐƠN VỊ / x.xxx KG” ở mốc nào" +
                          (profile is null ? " (chưa có hồ sơ cho cỡ màn này)" : ""));
    }

    /// <summary>Dựng panel giả có dòng chữ <paramref name="line"/> rồi đòi đọc ra đúng hai số.</summary>
    private static bool Expect(FishingConfig cfg, DigitAtlas atlas, string line, bool ok,
                               int count = 0, double kg = 0)
    {
        using var bmp = Panel(line);
        var band = new Rectangle(0, 0, bmp.Width, bmp.Height);
        using var src = new BitmapRegion(bmp, band);

        // O đuoc chuot phai, dung vi tri ma Panel() ve panel quanh no. Khung panel suy tu O chu
        // khong tu nut — luc panel vua mo khong nut nao sang, nen khong co nut nao de suy ca.
        var cell = new Rectangle(PanelX + (int)Math.Round(ScreenW * cfg.SplitPanelDxFrac),
                                 CellTop, 120, 120);
        var panel = SplitPanelReader.PanelFromCell(cell, cfg, ScreenW, flipped: false);

        var r = SplitPanelReader.ReadLine(src, panel, CellTop, atlas, cfg, ScreenH);
        bool pass = r.Ok == ok
                    && (!ok || (r.Count == count && Math.Abs(r.TotalKg - kg) < 0.0005));

        Console.WriteLine($"{(pass ? "  ok  " : "  SAI ")} “{line}” → {r}");
        if (!pass) Console.WriteLine("        " + r.Trace);
        return pass;
    }

    /// <summary>
    /// Ba mốc suy ra từ nút trái phải rơi đúng vào ba nút của dải.
    ///
    /// Phép tính bé xíu nhưng là chỗ quyết định bấm vào nút nào, nên nó phải có ca riêng: lệch
    /// một bề rộng nút sang trái là bấm DÙNG (ăn mất cá), sang phải là bấm VỨT (bỏ cá đi).
    /// </summary>
    private static bool Layout()
    {
        var first = new Rectangle(PanelX, PanelTop + PanelH - ButtonH, ButtonW, ButtonH);
        int cy = first.Top + first.Height / 2;

        var mid = SplitPanelReader.MiddleButtonCentre(first);
        var third = SplitPanelReader.ThirdButtonCentre(first);
        var before = SplitPanelReader.BeforeFirstCentre(first);

        // Tam thuc cua tung nut trong dai ba nut chia deu.
        int wantMid = PanelX + ButtonW + ButtonW / 2;
        int wantThird = PanelX + ButtonW * 2 + ButtonW / 2;
        int wantBefore = PanelX - ButtonW / 2;

        bool pass = mid == new Point(wantMid, cy)
                    && third == new Point(wantThird, cy)
                    && before == new Point(wantBefore, cy);

        Console.WriteLine($"{(pass ? "  ok  " : "  SAI ")} nút trái @ {first.X} rộng {ButtonW} → " +
                          $"giữa {mid.X} (chờ {wantMid}), thứ ba {third.X} (chờ {wantThird}), " +
                          $"trước-nút-đầu {before.X} (chờ {wantBefore})");
        return pass;
    }

    /// <summary>
    /// Phép hỏi "dưới điểm này có nút đang sáng không" — nền tảng của chuỗi xác nhận.
    ///
    /// Dựng ảnh với nút GIỮA sáng, tức đúng cảnh sau khi bot rê con trỏ lên nút giữa, rồi đòi:
    /// hỏi ở nút giữa phải ra CÓ, hỏi ở hai nút kia phải ra KHÔNG. Ca cuối là ca đáng giá nhất —
    /// nó chứng minh phép hỏi không trả lời "có" cho một điểm chỉ vì đâu đó trên màn có mảng xanh.
    /// </summary>
    private static bool Hover(FishingConfig cfg)
    {
        using var bmp = new Bitmap(ScreenW, ScreenH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(16, 32, 28));

        int by = PanelTop + PanelH - ButtonH;
        Fill(bmp, new Rectangle(PanelX, by, ButtonW * 3, ButtonH), Color.FromArgb(38, 38, 38));
        Fill(bmp, new Rectangle(PanelX + ButtonW, by, ButtonW, ButtonH), Green);

        var band = new Rectangle(0, 0, ScreenW, ScreenH);
        using var src = new BitmapRegion(bmp, band);

        var first = new Rectangle(PanelX, by, ButtonW, ButtonH);
        var mid = SplitPanelReader.MiddleButtonCentre(first);
        var third = SplitPanelReader.ThirdButtonCentre(first);
        var atFirst = new Point(first.Left + first.Width / 2, first.Top + first.Height / 2);

        bool onMid = SplitPanelReader.ButtonCovering(src, band, cfg, ScreenW, ScreenH, mid, out _);
        bool onFirst = SplitPanelReader.ButtonCovering(src, band, cfg, ScreenW, ScreenH, atFirst, out _);
        bool onThird = SplitPanelReader.ButtonCovering(src, band, cfg, ScreenW, ScreenH, third, out _);

        bool pass = onMid && !onFirst && !onThird;
        Console.WriteLine($"{(pass ? "  ok  " : "  SAI ")} nút giữa đang sáng → " +
                          $"hỏi ở giữa={(onMid ? "CÓ" : "không")} (chờ CÓ), " +
                          $"ở nút trái={(onFirst ? "CÓ" : "không")} (chờ không), " +
                          $"ở nút ba={(onThird ? "CÓ" : "không")} (chờ không)");
        return pass;
    }

    /// <summary>Phép tính số con cần tách — đúng công thức <see cref="ItemSplitter.SplitToFit"/> dùng.</summary>
    private static bool Plan(double totalKg, int count, double free, double margin, int want)
    {
        double per = totalKg / count;
        int got = (int)Math.Floor((free - margin) / per);
        got = Math.Clamp(got, 0, count);

        bool pass = got == want;
        Console.WriteLine($"{(pass ? "  ok  " : "  SAI ")} {count} con × {per:0.000} kg, " +
                          $"cốp còn {free:0.0} − lề {margin:0.0} → tách {got} (chờ {want})");
        return pass;
    }

    /// <summary>Vẽ một tình huống rồi đòi bộ dò TỪ CHỐI. Trả về true khi nó từ chối đúng.</summary>
    private static bool Reject(FishingConfig cfg, string label, Action<Bitmap> draw)
    {
        using var bmp = new Bitmap(ScreenW, ScreenH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(16, 32, 28));
        draw(bmp);

        var band = new Rectangle(0, 0, bmp.Width, bmp.Height);
        using var src = new BitmapRegion(bmp, band);

        var button = SplitPanelReader.FindButton(src, band, cfg, ScreenW, ScreenH, out string why);
        bool pass = button.IsEmpty;
        Console.WriteLine($"{(pass ? "  ok  " : "  NGUY")} {label} → " +
                          (pass ? "từ chối: " + why
                                : $"VẪN CLICK vào {button.Width}×{button.Height} @ {button.X},{button.Y}"));
        return pass;
    }

    /// <summary>
    /// Panel vật phẩm giả, đúng bố cục thật: dòng số ở đỉnh, dải ba nút ở đáy, nút giữa xanh.
    /// Ảnh to bằng cả màn hình để mọi tỉ lệ trong cấu hình được dùng đúng như lúc chạy thật.
    /// </summary>
    private static Bitmap Panel(string line)
    {
        var bmp = new Bitmap(ScreenW, ScreenH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(16, 32, 28));

        using (var bg = new SolidBrush(PanelBg))
            g.FillRectangle(bg, PanelX, PanelTop, PanelW, PanelH);

        // Nut SANG la nut TRAI (DÙNG), dung nhu game ve luc panel vua mo. Bo cuc nay moi la
        // bo cuc that: ban dau file nay ve nut GIUA sang, va vi the no xac nhan cho mot gia
        // dinh sai — "xanh = TÁCH" — trong khi trong game bot bam thang vao DÙNG.
        int by = PanelTop + PanelH - ButtonH;
        using (var dim = new SolidBrush(Color.FromArgb(38, 38, 38)))
        {
            g.FillRectangle(dim, PanelX + ButtonW, by, ButtonW, ButtonH);
            g.FillRectangle(dim, PanelX + ButtonW * 2, by, ButtonW, ButtonH);
        }
        using (var green = new SolidBrush(Green))
            g.FillRectangle(green, PanelX, by, ButtonW, ButtonH);

        // Chu tren nut: mau TRANG khoet vao giua vien xanh, dung nhu game — cua "phai dac" o
        // FindButton phai chiu duoc chuyen do.
        DrawText(g, "DÙNG", PanelX + 26, by + 40, 20F, Color.White);

        if (line.Length > 0)
            DrawText(g, line, PanelX + 26, CellTop + LineDy, 15F, Color.FromArgb(240, 240, 235));
        return bmp;
    }

    private static void DrawText(Graphics g, string text, int x, int y, float size, Color color)
    {
        using var font = new Font("Consolas", size, FontStyle.Bold);
        using var brush = new SolidBrush(color);
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        g.DrawString(text, font, brush, x, y);
    }

    private static void Fill(Bitmap bmp, Rectangle r, Color c)
    {
        using var g = Graphics.FromImage(bmp);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, r);
    }

    /// <summary>Dạy đủ bộ chữ số bằng chính phông dùng để vẽ panel — xem <see cref="VerifyOcr"/>.</summary>
    private static bool Teach(FishingConfig cfg)
    {
        const string all = "0123456789./";
        using var bmp = new Bitmap(600, 60, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(18, 24, 26));
            DrawText(g, all, 8, 8, 15F, Color.FromArgb(240, 240, 235));
        }

        var gray = GlyphSeg.GrayOf(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), out int w, out int h);
        var bin = GlyphSeg.Binarize(gray, cfg.DigitInkMinGray, out _);
        var boxes = GlyphSeg.Segment(bin, w, h, cfg.DigitMinGlyphW, cfg.DigitMinGlyphInk, cfg.DigitMergeGapPx);
        if (boxes.Count != all.Length)
        {
            Console.WriteLine($"dạy “{all}”: tách ra {boxes.Count} khối, cần {all.Length}");
            return false;
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i].Box;
            var crop = GlyphSeg.Crop(gray, w, h, b.X, b.Y, b.Width, b.Height);
            DigitAtlas.SaveGlyph(Key, all[i], crop, b.Width, b.Height, overwrite: true);
        }
        return true;
    }
}
