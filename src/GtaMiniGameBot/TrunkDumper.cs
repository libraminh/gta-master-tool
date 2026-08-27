using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum DumpResult
{
    Ok,
    NothingToMove,
    /// <summary>Chỗ cá không lọt cốp nữa. Không phải lỗi — là lúc chuyển sang chất đầy ba lô.</summary>
    TrunkFull
}

/// <summary>
/// Đổ cá từ ba lô sang cốp xe: mở cốp → tìm ô cá → kéo sang ô trống → đóng lại.
///
/// Mọi bước đều xác nhận bằng pixel trước khi đi tiếp, và không bước nào thả một món đồ vào ô
/// chưa chứng minh là trống — thả nhầm vào ô có đồ có thể là HOÁN ĐỔI, tức kéo ngược đồ trong
/// cốp vào ba lô và làm ba lô nặng thêm, đúng cái đang cố tránh.
///
/// Cốp đầy KHÔNG phải lỗi: nó là kết cục bình thường của mọi phiên câu. Ba lối phát hiện
/// (biết trước không lọt / hết ô trống / kéo hỏng lúc cốp đã chật) đều trả
/// <see cref="DumpResult.TrunkFull"/> chứ không ném, và mỗi lần như vậy ghi một strike. Đủ
/// <see cref="FishingConfig.TrunkFullTries"/> strike thì <see cref="TrunkFull"/> bật và người
/// gọi thôi mở cốp. Kéo lọt được một ô là xoá hết strike — cốp vừa nhận đồ thì nó chưa đầy.
/// </summary>
internal sealed class TrunkDumper : IDisposable
{
    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;
    private readonly Action<string> _log;

    private readonly TrunkOpener _opener;
    private readonly WeightReader _bagWeight;
    private readonly WeightReader _trunkWeight;
    private readonly GridScanner _hotbar;
    private readonly GridScanner _bag;
    /// <summary>Hàng "TRÊN NGƯỜI". null = người dùng chưa khoanh vùng đó.</summary>
    private readonly GridScanner _pockets;
    private readonly GridScanner _trunk;

    /// <summary>
    /// Các lưới được quét tìm cá, ĐÚNG thứ tự quét. Giữ ở một chỗ để thứ tự không lệch giữa
    /// vòng quét và phần ghi log. Lưới chưa khoanh bị lọc ra ngay từ đây.
    /// </summary>
    private readonly (string Label, GridScanner Scanner)[] _sources;
    private readonly Point _park;
    private readonly ItemCatalog _catalog;
    private readonly HashSet<string> _fishItems;

    /// <summary>Null khi người dùng tắt tính năng tách.</summary>
    private readonly ItemSplitter _splitter;

    private int _ocrFails;
    private int _trunkFullStrikes;
    private int _noFishStrikes;

    /// <summary>
    /// Đã dùng lượt tách của phiên này chưa.
    ///
    /// Chỉ tách MỘT lần: tách xong là cốp còn chưa đủ chỗ cho một con nữa, nên lần tách sau chỉ
    /// đẻ thêm ô lẻ trong ba lô mà không thêm được kg nào vào cốp. Đặt cờ ngay khi bắt đầu chứ
    /// không đợi kết quả — hỏng kiểu gì cũng không thử lại, để một panel đọc mãi không ra không
    /// biến thành vòng chuột phải bất tận.
    /// </summary>
    private bool _splitUsed;

    public bool OcrHealthy { get; private set; } = true;
    public string AtlasMissing => _bagWeight?.AtlasMissing ?? "";

    /// <summary>Cốp còn trống bao nhiêu kg, đo lần mở cốp gần nhất. -1 = chưa biết.</summary>
    public double TrunkFreeKg { get; private set; } = -1;

    /// <summary>
    /// KG ba lô lúc KHÔNG có cá, học được ngay sau mỗi lần đổ sạch. -1 = chưa biết.
    /// Có nó mới suy ra được chỗ cá đang có nặng bao nhiêu, tức mới biết trước là có lọt cốp
    /// hay không thay vì kéo rồi mới thấy hỏng.
    /// </summary>
    public double BagBaseKg { get; private set; } = -1;

    /// <summary>Chỗ cá đang có nặng bao nhiêu kg. -1 = chưa đủ dữ liệu để biết.</summary>
    public double PendingFishKg(double bagNow) =>
        BagBaseKg < 0 || bagNow < 0 ? -1 : Math.Max(0, bagNow - BagBaseKg);

    /// <summary>Đã hỏng đủ số lượt để kết luận cốp đầy hẳn — đừng mở cốp nữa.</summary>
    public bool TrunkFull => _trunkFullStrikes >= _cfg.TrunkFullTries;

    /// <summary>Đã hỏng mấy lượt, để người gọi ghi log "lượt 1/2".</summary>
    public int TrunkFullStrikes => _trunkFullStrikes;

    /// <summary>
    /// Đã mở cốp đủ số lượt mà không thấy ô cá nào — thôi thử lại, để người gọi dừng phiên.
    /// Kéo lọt được một ô là xoá hết strike, y như <see cref="TrunkFull"/>.
    /// </summary>
    public bool NoFishGivenUp => _noFishStrikes >= _cfg.NoFishTries;

    /// <summary>Mở cốp mà không thấy cá mấy lượt rồi.</summary>
    public int NoFishStrikes => _noFishStrikes;

    private TrunkDumper(FishingConfig cfg, Screen screen, FishingProfile profile, Action<string> log,
                        TrunkOpener opener, WeightReader weight, WeightReader trunkWeight,
                        GridScanner hotbar, GridScanner bag, GridScanner pockets,
                        GridScanner trunk, ItemCatalog catalog, DigitAtlas atlas)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _log = log;
        _opener = opener;
        _bagWeight = weight;
        _trunkWeight = trunkWeight;
        _hotbar = hotbar;
        _bag = bag;
        _pockets = pockets;
        _trunk = trunk;
        _catalog = catalog;

        // Thu tu: hai luoi NHO truoc, ba lo sau. Moi lan do bi chan tren boi MaxDragsPerDump
        // (12), ma ba lo co 25 o — de ba lo truoc thi mot ba lo day co the an het luot keo va
        // bo doi hai hang nho hoi qua nhieu lan do lien tiep, dung cai dang di sua.
        _sources = new[] { ("phím nhanh", hotbar), ("trên người", pockets), ("ba lô", bag) }
            .Where(s => s.Item2 is not null)
            .ToArray();
        _fishItems = new HashSet<string>(profile.FishItems ?? new List<string>(),
                                         StringComparer.OrdinalIgnoreCase);

        // Cho do chuot trung tinh truoc moi lan chup kiem tra: o duoi con tro duoc ve sang hon,
        // quen buoc nay la moi phep do deu nhiem.
        var b = screen.Bounds;
        _park = new Point(b.Left + 40, b.Top + 40);

        _splitter = cfg.SplitEnabled ? new ItemSplitter(cfg, screen, atlas, log) : null;
    }

    public static TrunkDumper Create(FishingConfig cfg, Screen screen, FishingProfile p,
                                     Action<string> log, out string problem)
    {
        var opener = TrunkOpener.Create(cfg, screen, p, log, out problem);
        if (opener is null) return null;

        if (!p.Hotbar.IsSet || !p.Bag.IsSet || !p.Trunk.IsSet)
        {
            opener.Dispose();
            problem = "chưa khoanh đủ ba lưới (phím nhanh, ba lô, cốp)";
            return null;
        }
        if (!p.BagWeight.IsSet)
        {
            opener.Dispose();
            problem = "chưa khoanh ô số KG ba lô";
            return null;
        }
        // Hai duong deu chap nhan duoc: nhan ca theo icon, hoac tin may o da khai bao. Khong
        // co duong nao thi dung han — bot khong duoc phep tu chon o de keo.
        var catalog = ItemCatalog.Load(cfg);
        bool byIcon = catalog.Count > 0 && p.FishItems is { Count: > 0 };
        if (!byIcon && p.FishSlots is not { Count: > 0 })
        {
            opener.Dispose();
            problem = catalog.Count > 0
                ? "chưa tích vật phẩm nào là cá, cũng chưa chọn ô chứa cá"
                : "chưa chọn ô chứa cá (hoặc trích icon từ game rồi tích loài cá)";
            return null;
        }
        log(byIcon
            ? $"nhận cá theo icon: {catalog.Count} vật phẩm trong bộ mẫu, " +
              $"{p.FishItems.Count} loại được tính là cá — quét phím nhanh, trên người và " +
              "ba lô, không cần ô khai báo"
            : $"nhận cá theo ô khai báo ({p.FishSlots.Count} ô)");

        // Bay: cac o khai bao nam im khi che do icon dang bat, nhung mat bo icon (doi
        // ItemCachePath, xoa thu muc items) la bot lang le roi ve che do nay va keo BAT KY thu
        // gi trong nhung o do. Khong tu xoa — do la du lieu nguoi dung — chi noi ra.
        if (byIcon && p.FishSlots is { Count: > 0 })
            log($"ô chứa cá đã khai báo ({string.Join(", ", p.FishSlots.Select(s => s.Label))}) " +
                "chỉ dùng khi mất bộ icon — kiểm lại nếu nó không còn là ô cá");

        var atlas = DigitAtlas.Load(p.Key);
        var weight = new WeightReader(cfg, screen, p.BagWeight, atlas, cfg.BagCapKg, capIsDynamic: true);
        var trunkWeight = p.TrunkWeight.IsSet
            ? new WeightReader(cfg, screen, p.TrunkWeight, atlas, cfg.TrunkCapKg)
            : null;
        if (trunkWeight is null)
            log("chưa khoanh ô số KG cốp — bot sẽ không biết cốp còn trống bao nhiêu");

        // CO Y de luoi "trên người" ngoai phep kiem bat buoc o tren: moi cau hinh dang co deu
        // thieu vung nay, chan cung o day la tat do cop cua tat ca nguoi dung.
        if (!p.Pockets.IsSet)
            log("chưa khoanh lưới TRÊN NGƯỜI — cá rơi vào hàng đó bot sẽ không thấy");

        return new TrunkDumper(cfg, screen, p, log, opener, weight, trunkWeight,
            new GridScanner(cfg, screen, p.Hotbar),
            new GridScanner(cfg, screen, p.Bag),
            p.Pockets.IsSet ? new GridScanner(cfg, screen, p.Pockets) : null,
            new GridScanner(cfg, screen, p.Trunk),
            catalog, atlas);
    }

    // ---------------------------------------------------------------- đọc KG

    /// <summary>
    /// Mở Tab, đọc KG, đóng Tab. Cố ý dùng Tab cả hai chiều: đường Esc và cái bẫy menu tạm
    /// dừng đi kèm chỉ tồn tại trong luồng cốp xe, nơi bắt buộc phải dùng Esc.
    /// </summary>
    public WeightRead PeekBagWeight(CancellationToken ct)
    {
        var st = _opener.ReadState();
        if (st.AnyOpen)
        {
            _log("có màn hình mở sẵn khi định đọc KG (" + st + ") — bỏ lần đọc này");
            return new WeightRead { Reason = "lệch trạng thái" };
        }

        InputSender.TapKey(0x09);
        try
        {
            if (!WaitState(s => s.BagOpen, _cfg.TabWaitMs, ct, out string last))
                return new WeightRead { Reason = "Tab không mở được kho đồ (" + last + ")" };

            InputSender.MoveCursorOnly(_park.X, _park.Y);
            Sleep(ct, 150);

            var r = _bagWeight.Read();
            if (r.Ok) { _ocrFails = 0; }
            else
            {
                _ocrFails++;
                _log($"đọc KG hỏng ({r.Reason}) — “{r.Text}”");
                _log("   " + r.Trace);
                if (_ocrFails >= _cfg.WeightOcrFailMax && OcrHealthy)
                {
                    OcrHealthy = false;
                    _log($"đọc KG hỏng {_ocrFails} lần liên tiếp — chuyển hẳn sang đếm cá " +
                         $"(mỗi {_cfg.CatchesPerDumpFallback} con đổ một lần)");
                }
            }
            return r;
        }
        finally
        {
            InputSender.TapKey(0x09);
            try { WaitState(s => !s.AnyOpen, _cfg.TabWaitMs, ct, out _); } catch { }
        }
    }

    public void ResetWeightHistory() => _bagWeight.ResetHistory();

    // ---------------------------------------------------------------- đổ

    /// <summary>
    /// Ném <see cref="TrunkStepException"/> nếu cả hai lượt đều không xong.
    ///
    /// <see cref="DumpResult.TrunkFull"/> KHÔNG đi qua đường thử lại này: cốp chật thì lượt hai
    /// đọc ra đúng mấy con số của lượt một và chỉ tổ kéo thêm một chùm nữa vào chỗ không có.
    /// Hai lượt thử của nó đếm ở tầng trên, cách nhau ít nhất một con cá.
    /// </summary>
    public DumpResult Dump(CancellationToken ct)
    {
        try
        {
            return DumpOnce(ct);
        }
        catch (TrunkStepException ex)
        {
            _log("lượt 1 hỏng: " + ex.Message);
            _log($"thu dọn rồi thử lại một lượt nữa sau {_cfg.DumpRetryGapMs} ms");
            Recover(ct);
            Sleep(ct, _cfg.DumpRetryGapMs);
            return DumpOnce(ct);
        }
    }

    private DumpResult DumpOnce(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _opener.Open(ct);

        InputSender.MoveCursorOnly(_park.X, _park.Y);
        Sleep(ct, 200);

        double before = ReadBagWeightNow();
        MeasureTrunkFree();

        double fishKg = PendingFishKg(before);
        _log($"ba lô {before:F1} kg" +
             (fishKg >= 0 ? $" (chỗ cá ≈ {fishKg:F1} kg)" : " (chưa biết chỗ cá nặng bao nhiêu)") +
             (TrunkFreeKg >= 0 ? $", cốp còn trống {TrunkFreeKg:F1} kg" : ", chưa đọc được KG cốp"));

        // Biet truoc la khong lot thi KHONG keo thu. Cu keo hong lam game hien thong bao do
        // "Kho do da day" va bot chi biet la "keo that bai", khong phan biet duoc voi keo truot.
        //
        // Nhung khong lot KHONG con la het chuyen: cho trong cuoi cung cua cop van nhoi duoc
        // bang mot chong ca da tach nho. TrySplitTail lam viec do roi moi ket luan.
        if (fishKg >= 0 && TrunkFreeKg >= 0 && fishKg > TrunkFreeKg)
            return TrySplitTail(
                $"cốp còn {TrunkFreeKg:F1} kg mà chỗ cá đang có {fishKg:F1} kg", ct);

        int moved = 0;
        string fullWhy = null;

        while (moved < _cfg.MaxDragsPerDump)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.ElapsedMilliseconds > _cfg.MaxDumpMs)
                throw new TrunkStepException($"đổ cốp quá {_cfg.MaxDumpMs} ms — dừng");

            var source = NextFish(ct, out string scanNote);
            if (scanNote is not null) _log(scanNote);
            if (source is null) break;

            var dest = NextEmptyTrunkCell(ct);
            if (dest is null) { fullWhy = "cốp không còn ô trống nào"; break; }

            if (!DragOne(source.Value.Scanner, source.Value.Cell, dest, ct))
            {
                // Cop con rong ranh ma keo hong thi KHONG phai chuyen day cop — do la loi that
                // (mat focus, luoi khoanh lech) va phai bao ra, khong duoc nuot thanh "cop day".
                if (!CouldBeFull(fishKg))
                    throw new TrunkStepException("kéo cá vào cốp thất bại");

                fullWhy = TrunkFreeKg >= 0
                    ? $"kéo không được, cốp chỉ còn {TrunkFreeKg:F1} kg"
                    : "kéo không được, mà chưa khoanh ô KG cốp nên không loại trừ được là cốp đầy";
                break;
            }

            moved++;
        }

        if (moved == 0 && fullWhy is null)
        {
            _noFishStrikes++;

            // Cau nay phai theo dung che do dang chay. Truoc day luon in ban cua che do khai
            // bao o, ke ca khi dang nhan theo icon — doc log la di tim "o da khai bao" trong
            // khi bot khong he dung o nao ca.
            _log((ByIcon
                     ? "quét hết phím nhanh, trên người và ba lô mà không ô nào nhận ra là cá"
                     : "mọi ô chứa cá đã khai báo đều đang trống") +
                 $" — lượt hỏng {_noFishStrikes}/{_cfg.NoFishTries}");
            _opener.CloseAll(ct);

            // Chi quay mat khoi xe khi da thoi thu lai. Con luot nua thi phai GIU nguyen huong
            // vao xe: TurnBack giu S de nhan vat xoay ra ho, ma mo cop lai thi can camera huong
            // vao xe — quay ra roi thi luot thu lai chet ngay o "khong hien menu Alt".
            if (NoFishGivenUp) TurnBack(ct);
            return DumpResult.NothingToMove;
        }

        if (moved > 0)
        {
            // Xoa lich su NGAY TRUOC lan doc nay. Keo xong thi KG chac chan giam, ma WeightReader
            // co cong chan "giam ma chua do cop" — de nguyen thi no tu choi dung lan doc quan
            // trong nhat, va BagBaseKg khong bao gio hoc duoc. Do chinh la thu da xay ra suot:
            // log ghi "ba lo 8.7 -> -1.0 kg" roi moi luot do deu keu "chua biet cho ca nang bao
            // nhieu", keo theo ca cua chan "khong lot cop" lan che do do tung con deu chet.
            _bagWeight.ResetHistory();

            double after = ReadBagWeightNow();
            if (before >= 0 && after >= 0 && before - after < _cfg.MinDropKg)
                _log($"cảnh báo: kéo {moved} ô nhưng KG chỉ giảm {before - after:F1} " +
                     $"(chờ ít nhất {_cfg.MinDropKg:F1}) — " +
                     (ByIcon
                         ? "ô vừa kéo có thể không phải cá, xem lại danh sách đã tích"
                         : "nhiều khả năng cá đã tràn sang một ô chưa khai báo, " +
                           "vào Chọn ô chứa cá thêm ô đó"));
            else if (after >= 0)
            {
                // Moi o ca da khai bao deu trong, nen can nang bay gio CHINH LA can nang khong co ca.
                BagBaseKg = after;
            }

            MeasureTrunkFree();
            _log($"đã kéo {moved} ô sang cốp, ba lô {before:F1} → {after:F1} kg" +
                 (TrunkFreeKg >= 0 ? $", cốp còn trống {TrunkFreeKg:F1} kg" : ""));

            // Cop vua nhan do thi no chua day: xoa strike cu di. Neu ngay sau do van dung tuong,
            // ConcludeTrunkFull ben duoi ghi lai tu strike 1 — dung, vi day la lan chan moi.
            _trunkFullStrikes = 0;
            // Keo duoc thi ro rang van nhan ra ca, dot "khong thay ca" truoc do khong con tinh.
            _noFishStrikes = 0;
        }

        if (fullWhy is not null) return TrySplitTail(fullWhy, ct);

        _opener.CloseAll(ct);
        Sleep(ct, _cfg.AfterDumpMs);
        TurnBack(ct);
        _bagWeight.ResetHistory();
        return DumpResult.Ok;
    }

    /// <summary>
    /// Cú kéo hỏng vừa rồi có thể là do cốp chật không? Chỉ trả false khi cốp RỘNG RÃI hơn chỗ
    /// cá một cách chắc chắn — lúc đó lỗi nằm ở chỗ khác và phải báo ra thay vì đổ cho cốp.
    /// </summary>
    private bool CouldBeFull(double fishKg) =>
        TrunkFreeKg < 0 || fishKg < 0 || TrunkFreeKg <= fishKg + _cfg.DumpMarginKg;

    /// <summary>
    /// Lưới cuối trước khi kết luận cốp đầy: tách một chồng cá cho vừa chỗ trống rồi kéo nốt.
    ///
    /// Vì sao đặt ở ĐÂY chứ không xen vào vòng kéo: kéo trọn ô luôn rẻ hơn (một cú kéo, không
    /// chuột phải, không hộp thoại), nên cứ để vòng trên vét sạch những ô còn lọt đã. Tách chỉ
    /// giải quyết đúng phần thừa cuối cùng — chỗ mà trước giờ bị bỏ trắng.
    ///
    /// Mọi đường hỏng đều rơi về <see cref="ConcludeTrunkFull"/> với lý do gốc kèm lý do phụ.
    /// Không đường nào ném: cốp đầy vẫn là kết cục bình thường của một phiên câu, và tách hỏng
    /// không được phép biến nó thành lỗi.
    /// </summary>
    private DumpResult TrySplitTail(string why, CancellationToken ct)
    {
        if (_splitter is null || _splitUsed) return ConcludeTrunkFull(why, ct);
        _splitUsed = true;

        MeasureTrunkFree();
        if (TrunkFreeKg < 0)
            return ConcludeTrunkFull(why + "; chưa đọc được KG cốp nên không tách được", ct);

        // Xem con o trong da, truoc khi bo cong doc panel: tach ra ma khong co cho tha thi cong
        // coc, va con de lai mot o le trong ba lo.
        var dest = NextEmptyTrunkCell(ct);
        if (dest is null)
            return ConcludeTrunkFull(why + "; cốp không còn ô trống để nhận phần tách", ct);

        var source = NextFish(ct, out string scanNote, out string species);
        if (scanNote is not null) _log(scanNote);
        if (source is null)
            return ConcludeTrunkFull(why + "; không còn ô cá nào để tách", ct);

        var before = OccupiedSnapshot();
        var attempt = _splitter.SplitToFit(source.Value.Cell, TrunkFreeKg, _cfg.DumpMarginKg,
                                           _cfg.KgPerUnitOf(species), ct);
        LearnKgPerUnit(species, attempt.Read);

        // Panel bao ca chong LOT tron thi keo thang, khong tach gi ca.
        //
        // Ca nay xay ra that va truoc gio bi bo lo: cua chan tren dem TONG cho ca cua ba luoi,
        // nen mot cum 26 kg chan het luot do du trong do co o chi 5 kg thua suc lot 22 kg con
        // lai. Bay gio co panel noi ro tung o nang bao nhieu, khong con phai doan theo tong nua.
        if (attempt.Outcome == SplitOutcome.FitsWhole)
        {
            // Chua tach gi thi chua tieu luot tach — de danh cho luc that su can.
            _splitUsed = false;

            var whole = NextEmptyTrunkCell(ct);
            if (whole is null)
                return ConcludeTrunkFull($"{why}; {attempt.Why} nhưng cốp hết ô trống", ct);

            if (!DragOne(source.Value.Scanner, source.Value.Cell, whole, ct))
                return ConcludeTrunkFull($"{why}; {attempt.Why} mà kéo vẫn không vào", ct);

            _bagWeight.ResetHistory();
            MeasureTrunkFree();
            _trunkFullStrikes = 0;
            _log($"{attempt.Why} — đã kéo trọn ô" +
                 (TrunkFreeKg >= 0 ? $", cốp còn trống {TrunkFreeKg:F1} kg" : ""));

            _opener.CloseAll(ct);
            Sleep(ct, _cfg.AfterDumpMs);
            TurnBack(ct);
            _bagWeight.ResetHistory();
            return DumpResult.Ok;
        }

        if (attempt.Outcome != SplitOutcome.Done)
            return ConcludeTrunkFull($"{why}; {attempt.Why}", ct);

        // O moi la o VUA XUAT HIEN, khong phai "o trong dau tien". Game co the tha phan tach vao
        // bat ky cho nao con trong, va doan sai o thi cu keo sau do lai loi mot mon do khac di.
        var fresh = NewCell(before, ct);
        if (fresh is null)
            return ConcludeTrunkFull(
                $"{why}; đã tách {attempt.Units} con nhưng không nhận ra ô mới nằm đâu", ct);

        // Cua kiem chat nhat cua ca tinh nang: hoi lai chinh o vua tach xem no dung la bay
        // nhieu con va nang bay nhieu kg khong. Doc duoc ma lech thi thoi, dung keo.
        var check = _splitter.Peek(fresh.Value.Cell, ct);
        _splitter.ClosePanel(ct);

        if (check.Ok && check.Count != attempt.Units)
            return ConcludeTrunkFull(
                $"{why}; ô mới có {check.Count} con chứ không phải {attempt.Units} — không kéo", ct);
        if (check.Ok && check.TotalKg > TrunkFreeKg)
            return ConcludeTrunkFull(
                $"{why}; ô mới nặng {check.TotalKg:F1} kg mà cốp chỉ còn {TrunkFreeKg:F1} — không kéo", ct);
        if (!check.Ok)
            _log($"cảnh báo: không đọc lại được ô vừa tách ({check.Reason}) — vẫn kéo thử");

        // Doc lai o trong: panel vua che mat luoi cop mot luc, va o dich chon truoc do co the
        // khong con dung nua.
        dest = NextEmptyTrunkCell(ct);
        if (dest is null)
            return ConcludeTrunkFull($"{why}; tách xong thì cốp hết ô trống", ct);

        if (!DragOne(fresh.Value.Scanner, fresh.Value.Cell, dest, ct))
            return ConcludeTrunkFull(
                $"{why}; đã tách {attempt.Units} con nhưng kéo vẫn không vào", ct);

        _bagWeight.ResetHistory();
        double after = ReadBagWeightNow();
        MeasureTrunkFree();
        _log($"đã nhồi nốt {attempt.Units} con vào cốp" +
             (after >= 0 ? $", ba lô còn {after:F1} kg" : "") +
             (TrunkFreeKg >= 0 ? $", cốp còn trống {TrunkFreeKg:F1} kg" : ""));

        // Van la "cop day": cho con lai khong du cho mot con nua. Ghi strike nhu moi lan khac de
        // tang tren dem dung — chi khac la lan nay cop da duoc vet sach truoc khi dong so.
        return ConcludeTrunkFull($"đã tách và nhồi nốt {attempt.Units} con, {why}", ct);
    }

    /// <summary>
    /// Ghi lại kg mỗi con vừa đọc được từ panel, để lần sau đọc hỏng vẫn tách được.
    ///
    /// Chỉ học khi BIẾT loài — chế độ ô khai báo cố ý không nhìn icon nên nó không biết trong ô
    /// là con gì, mà ghi một con số vào ô "không rõ loài" thì lần sau đem áp cho loài khác.
    /// Ghi đè giá trị cũ: panel là nguồn chính xác nhất đang có, và cá cùng loài thì cùng cân.
    /// </summary>
    private void LearnKgPerUnit(string species, SplitPanelRead read)
    {
        if (species is null || read is not { Ok: true }) return;

        double per = read.KgPerUnit;
        if (per < _cfg.SplitMinUnitKg || per > _cfg.SplitMaxUnitKg) return;

        if (_cfg.KgPerUnit.TryGetValue(species, out double old) && Math.Abs(old - per) < 0.0005) return;

        _cfg.KgPerUnit[species] = per;
        _log($"ghi nhớ {species} = {per:0.000} kg mỗi con" +
             (old > 0 ? $" (trước ghi {old:0.000})" : ""));
        try { _cfg.Save(); } catch (Exception ex) { _log("lưu cấu hình lỗi: " + ex.Message); }
    }

    /// <summary>Tập ô ĐANG CÓ ĐỒ của mọi lưới nguồn — chụp trước khi tách để nhận ra ô mới.</summary>
    private HashSet<(string Grid, int Index)> OccupiedSnapshot()
    {
        var set = new HashSet<(string, int)>();
        foreach (var (label, scanner) in _sources)
        foreach (var c in scanner.ScanScreen())
            if (c.State != CellState.Empty) set.Add((label, c.Index));
        return set;
    }

    /// <summary>
    /// Ô vừa mọc thêm so với <paramref name="before"/>. Null khi không có, hoặc khi có NHIỀU HƠN
    /// MỘT — nhiều ô mới nghĩa là không biết ô nào là phần vừa tách, mà không biết thì không kéo.
    /// </summary>
    private (GridScanner Scanner, CellInfo Cell)? NewCell(HashSet<(string, int)> before,
                                                          CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var found = new List<(GridScanner, CellInfo)>();
            foreach (var (label, scanner) in _sources)
            foreach (var c in scanner.ScanScreen())
                if (c.State != CellState.Empty && !before.Contains((label, c.Index)))
                    found.Add((scanner, c));

            if (found.Count == 1) return found[0];
            if (found.Count > 1)
            {
                _log($"sau khi tách thấy {found.Count} ô mới — không dám đoán ô nào là phần vừa tách");
                return null;
            }

            if (attempt >= _cfg.ScanRetries) return null;
            _log($"chưa thấy ô mới sau khi tách, quét lại sau {_cfg.ScanRetryGapMs} ms " +
                 $"(lượt {attempt + 1}/{_cfg.ScanRetries})");
            Sleep(ct, _cfg.ScanRetryGapMs);
        }
    }

    /// <summary>
    /// Ghi một strike "cốp không nhận nữa", dọn màn hình rồi quay mặt lại như một lượt đổ bình
    /// thường — bot còn câu tiếp, nên vẫn phải trả nhân vật về đúng tư thế hướng ra hồ.
    /// </summary>
    private DumpResult ConcludeTrunkFull(string why, CancellationToken ct)
    {
        _trunkFullStrikes++;
        _log($"cốp không nhận thêm ({why}) — lượt hỏng {_trunkFullStrikes}/{_cfg.TrunkFullTries}" +
             (TrunkFull
                 ? ". KẾT LUẬN CỐP ĐẦY: thôi mở cốp, câu tiếp cho đầy ba lô rồi dừng"
                 : ". Lượt đổ sau sẽ đo lại — dọn bớt cốp bây giờ là bot đổ tiếp được"));

        _opener.CloseAll(ct);
        Sleep(ct, _cfg.AfterDumpMs);
        TurnBack(ct);
        _bagWeight.ResetHistory();
        return DumpResult.TrunkFull;
    }

    /// <summary>
    /// Giữ S một nhịp cho nhân vật quay mặt lại về phía hồ.
    ///
    /// Tương tác với xe làm nhân vật xoay về phía xe. Thả câu thì phải hướng ra hồ, nên không
    /// quay lại là phím 4 rơi vào hư không — mà bot không có cách nào tự thấy điều đó: HUD câu
    /// không mở, không có cá cắn, và nó chỉ lặng lẽ câu hụt hết lượt này tới lượt khác.
    /// </summary>
    private void TurnBack(CancellationToken ct)
    {
        if (_cfg.AfterDumpTurnMs <= 0) return;

        try
        {
            InputSender.KeyDown(HeldKeys.VK_S);
            Sleep(ct, _cfg.AfterDumpTurnMs);
        }
        finally
        {
            try { InputSender.KeyUp(HeldKeys.VK_S); } catch { }
        }
        _log($"giữ S {_cfg.AfterDumpTurnMs} ms để quay mặt khỏi xe");
        Sleep(ct, 250);
    }

    /// <summary>KG ba lô đọc ngay trên màn cốp — ô số nằm đúng chỗ cũ. -1 nếu không đọc được.</summary>
    private double ReadBagWeightNow()
    {
        if (!OcrHealthy) return -1;
        var r = _bagWeight.Read();
        // Noi ro vi sao hong. Truoc day cho nay nuot lang le, nen log chi hien "-1.0 kg" va
        // khong ai lan ra duoc rang cong chan giam moi la thu dang tu choi.
        if (!r.Ok) _log($"không đọc được KG ba lô ({r.Reason}) — “{r.Text}”");
        return r.Ok ? r.Value : -1;
    }

    /// <summary>Đọc KG cốp và cập nhật chỗ trống. Chỉ gọi được khi cốp đang mở.</summary>
    private void MeasureTrunkFree()
    {
        if (_trunkWeight is null) return;

        var r = _trunkWeight.Read();
        if (!r.Ok)
        {
            _log($"không đọc được KG cốp ({r.Reason}) — “{r.Text}”");
            return;
        }
        // Doc doc lap tung lan, khong rang buoc don dieu: cot cop chi len chu khong xuong,
        // nhung nguoi choi co the tu lay do ra giua chung.
        _trunkWeight.ResetHistory();
        TrunkFreeKg = Math.Max(0, r.Cap - r.Value);
    }

    /// <summary>Một lượt quét: thấy cá chưa, và lưới có dấu hiệu đang tải icon không.</summary>
    private sealed class ScanPass
    {
        public (GridScanner Scanner, CellInfo Cell)? Fish;
        public string Note;

        /// <summary>
        /// Loài nhận ra ở ô sắp kéo. null ở chế độ ô khai báo — ở đó bot cố ý không nhìn icon,
        /// nên nó thật sự không biết trong ô là con gì.
        /// </summary>
        public string Species;
        /// <summary>Vì sao nghĩ là đang tải. null = không có dấu hiệu, quét lại cũng vô ích.</summary>
        public string Loading;
        public readonly List<string> Skipped = new();
    }

    /// <summary>
    /// Ô cá tiếp theo cần kéo.
    ///
    /// Hai chế độ. Có bộ icon moi từ cache game và người dùng đã tích vật phẩm nào là cá thì
    /// quét CẢ phím nhanh lẫn ba lô rồi nhận cá theo icon — cá nằm đâu cũng thấy. Chưa có thì
    /// quay về lối cũ: chỉ tin mấy ô người dùng khai báo, không nhìn icon.
    ///
    /// Quét NHIỀU LƯỢT khi lưới trông như đang tải icon. Icon kho đồ tải từ server, mỗi ô một
    /// ảnh riêng nên chúng về lệch nhịp nhau — mở panel lên có thể phải chờ 500 ms – 1 s mới đủ
    /// hình. Quét đúng một lượt là ăn trọn một khung hình nửa vời: log 21/08 23:04 đọc cả 5 ô
    /// phím nhanh thành trống (lệch 4.5–4.7, ngưỡng 6.2) rồi dừng cả phiên, mà 2 phút sau cùng
    /// con cá đó ở cùng ô đó lại nhận ra `carp 0.94`.
    ///
    /// Quét lại an toàn nhờ sàn <see cref="FishingConfig.ItemNccMin"/> đã có: icon vẽ dở chấm
    /// 0.41–0.49, không đời nào lọt sàn 0.70. Nên lượt quét thêm không thể kéo bừa một ô đang
    /// tải — nó chỉ có thể thấy thêm cá, hoặc không thấy gì.
    /// </summary>
    private (GridScanner Scanner, CellInfo Cell)? NextFish(CancellationToken ct, out string note)
        => NextFish(ct, out note, out _);

    private (GridScanner Scanner, CellInfo Cell)? NextFish(CancellationToken ct, out string note,
                                                           out string species)
    {
        ScanPass pass = null;

        for (int attempt = 0; ; attempt++)
        {
            InputSender.MoveCursorOnly(_park.X, _park.Y);
            Sleep(ct, 120);

            pass = ByIcon ? ScanByIcon() : ScanBySlot();

            // Thay ca thi thoi cho: kéo luon. Het luot cung thoi. Va khong con dau hieu dang
            // tai thi cho them cung vay thoi — o "khong ro" vi hai mau icon giong nhau qua thi
            // cho bao lau cung khong ro ra duoc, xem ItemLoadingScoreMax.
            if (pass.Fish is not null || pass.Loading is null || attempt >= _cfg.ScanRetries)
                break;

            _log($"lưới như đang tải icon ({pass.Loading}) — quét lại sau " +
                 $"{_cfg.ScanRetryGapMs} ms (lượt {attempt + 1}/{_cfg.ScanRetries})");
            Sleep(ct, _cfg.ScanRetryGapMs);
        }

        // Xa danh sach bo qua CUA LUOT CUOI thoi. Xa moi luot thi mot lan do in ra ba ban sao
        // cua cung mot danh sach 35 dong, khong con doc duoc.
        Flush(pass.Skipped);
        if (pass.Fish is null && pass.Loading is not null)
            _log($"vẫn như đang tải sau {_cfg.ScanRetries + 1} lượt quét ({pass.Loading}) — " +
                 "đường truyền tải ảnh chậm thì nới ScanRetries / ScanRetryGapMs");

        note = pass.Note;
        species = pass.Species;
        return pass.Fish;
    }

    /// <summary>Có đủ bộ icon và danh sách cá thì mới nhận diện được; thiếu một trong hai là về lối cũ.</summary>
    public bool ByIcon => _catalog is { Count: > 0 } && _fishItems.Count > 0;

    /// <summary>
    /// Quét mọi ô đang có đồ, kéo ô đầu tiên nhận ra là cá.
    ///
    /// Ô không nhận ra thì ĐỂ YÊN. Đó là lựa chọn có chủ ý: đoán bừa một ô lạ rồi kéo đi có
    /// thể là ném cả cần câu, mồi hay tiền vào cốp, mà thứ mất đi thì không kéo ngược lại được.
    /// </summary>
    private ScanPass ScanByIcon()
    {
        // MOI o bi bo qua deu phai co ly do. Truoc day chi o "khong ro" duoc ghi, con hai
        // duong kia im lang: o doc ra trong, va o nhan RO nhung khong phai ca. Hau qua that:
        // 19/08 co 5 con ca o phim 6 khong duoc keo, ma log khong he co dong nao ve o do, nen
        // khong cach nao biet no bi doc thanh trong hay bi nhan thanh mot mon KHONG phai ca.
        var pass = new ScanPass();

        foreach (var (label, scanner) in _sources)
        {
            foreach (var (cell, gray) in scanner.ScanScreenPixels())
            {
                if (cell is null) continue;
                if (cell.IsEmpty)
                {
                    // In ca so do va nguong: o co do ma bi doc thanh trong thi thay ngay.
                    pass.Skipped.Add($"{label} #{cell.Index} " +
                                     (cell.Faint ? "trống NHƯNG LỆCH CAO — như đang tải icon " : "trống ") +
                                     $"(lệch={cell.Std:F1} ≤ {_cfg.CellEmptyStdMax:F1})");
                    // Rong that cho 0.4-2.2, tai do cho 3.7-5.9 — hai dai tach han nhau.
                    if (cell.Faint)
                        pass.Loading ??= $"{label} #{cell.Index} lệch={cell.Std:F1}";
                    continue;
                }

                var guess = _catalog.Classify(gray, cell.Rect.Width, cell.Rect.Height);

                // Hoi "co phai ca" chu khong hoi "loai gi" — xem ItemGuess.FishName.
                string fishName = guess.FishName(_fishItems, _cfg.ItemNccMin);
                if (fishName is null)
                {
                    pass.Skipped.Add(guess.Name is null
                        ? $"{label} #{cell.Index} {guess}"
                        : $"{label} #{cell.Index} {guess.Name} {guess.Score:F2} — RÕ nhưng " +
                          "không có trong danh sách cá");
                    // Diem thap han la icon con dang ve. Diem cao ma bi loai vi cach biet thi
                    // KHONG phai — do la hai mau icon giong nhau, cho them vo ich.
                    if (guess.Score < _cfg.ItemLoadingScoreMax)
                        pass.Loading ??= $"{label} #{cell.Index} điểm {guess.Score:F2}";
                    continue;
                }

                pass.Note = guess.Name is null
                    ? $"kéo {label} #{cell.Index} — {guess.Best} {guess.Score:F2}, lẫn với " +
                      $"{guess.Runner} {guess.RunnerScore:F2} — cả hai đều là cá nên vẫn kéo"
                    : $"kéo {label} #{cell.Index} — {guess}";
                pass.Fish = (scanner, cell);
                pass.Species = fishName;
                return pass;
            }
        }

        return pass;
    }

    /// <summary>
    /// Xả danh sách ô bị bỏ qua. Gom lại rồi mới ghi chứ không ghi ngay lúc gặp: ô nào cũng
    /// bị bỏ qua cho tới khi gặp con cá, nên ghi ngay sẽ đẩy dòng "kéo …" xuống dưới một đống
    /// dòng phụ, khó đọc.
    /// </summary>
    private void Flush(List<string> skipped)
    {
        foreach (string s in skipped) _log("   bỏ qua: " + s);
        skipped.Clear();
    }

    /// <summary>
    /// Ô cá đầu tiên trong danh sách khai báo mà đang có đồ. Không dò icon: người dùng đã cam
    /// kết ô đó luôn là cá, nên ở đây chỉ cần biết ô rỗng hay không.
    /// </summary>
    private ScanPass ScanBySlot()
    {
        var pass = new ScanPass();

        foreach (var slot in _profile.FishSlots)
        {
            var scanner = ScannerFor(slot.Grid);
            if (scanner is null)
            {
                // Nguoi dung KHAI BAO o nay, nen im lang bo qua la sai — noi ro vi sao.
                pass.Note = $"ô {slot.Label} nằm ở lưới chưa khoanh — bỏ qua";
                continue;
            }
            if (slot.Index < 0 || slot.Index >= scanner.Count)
            {
                pass.Note = $"ô {slot.Label} nằm ngoài lưới ({scanner.Count} ô) — bỏ qua";
                continue;
            }

            var cell = scanner.ScanCell(slot.Index);
            if (cell is null) continue;
            if (cell.IsEmpty)
            {
                // Che do nay khong nhin icon nen khong co diem NCC de dua vao — chi con do lech.
                if (cell.Faint)
                    pass.Loading ??= $"ô {slot.Label} lệch={cell.Std:F1}";
                continue;
            }

            pass.Note = $"kéo ô {slot.Label} (màu={cell.Chroma:F3} lệch={cell.Std:F1})";
            pass.Fish = (scanner, cell);
            return pass;
        }
        return pass;
    }

    private GridScanner ScannerFor(string grid) => grid switch
    {
        FishSlot.GridHotbar => _hotbar,
        FishSlot.GridBag => _bag,
        FishSlot.GridPockets => _pockets,
        _ => null
    };

    /// <summary>
    /// Ô trống đầu tiên trong cốp để thả cá vào. Null = cốp không còn ô trống.
    ///
    /// Bỏ qua ô "nhạt" — cùng cái bẫy tải icon ở bên ba lô, nhưng đầu này hậu quả nặng hơn:
    /// ô cốp ĐANG CÓ đồ mà icon chưa về thì đọc ra trống, và cửa xác minh trong
    /// <see cref="DragOne"/> bắt không được vì nó kiểm "đích != trống" — ô đó vốn đã có đồ từ
    /// đầu nên điều kiện luôn đúng. Kéo vào là hoán đổi: lôi ngược đồ trong cốp về ba lô, rồi
    /// vì cú kéo bị tính là hỏng mà kết luận sai thành "cốp đầy" và thôi đổ cả phiên.
    ///
    /// Hết lượt mà chỉ còn ô nhạt thì vẫn nhận, có cảnh báo. Lượt cuối này giữ nguyên nết cũ
    /// có chủ ý: nếu một ô cốp trống thật mà đo được lệch cao (nền panel chỗ đó không phẳng
    /// chẳng hạn) thì bỏ hẳn nó đi là tự bịt đường đổ cốp mãi mãi, hỏng nặng hơn cái đang sửa.
    /// </summary>
    private CellInfo NextEmptyTrunkCell(CancellationToken ct)
    {
        CellInfo faint = null;

        for (int attempt = 0; ; attempt++)
        {
            faint = null;
            foreach (var c in _trunk.ScanScreen())
            {
                if (c.State != CellState.Empty) continue;
                if (!c.Faint) return c;
                faint ??= c;
            }

            if (faint is null || attempt >= _cfg.ScanRetries) break;

            _log($"cốp không có ô nào trống hẳn, sớm nhất là #{faint.Index} lệch={faint.Std:F1} " +
                 $"— như đang tải icon, quét lại sau {_cfg.ScanRetryGapMs} ms " +
                 $"(lượt {attempt + 1}/{_cfg.ScanRetries})");
            Sleep(ct, _cfg.ScanRetryGapMs);
        }

        if (faint is not null)
            _log($"cảnh báo: thả vào cốp #{faint.Index} dù lệch={faint.Std:F1} cao đáng ngờ — " +
                 "hết lượt quét mà không có ô nào trống hẳn");
        return faint;
    }

    /// <summary>
    /// Kéo một ô. Chỉ tính là xong khi ô nguồn ĐÃ TRỐNG và ô đích ĐÃ CÓ ĐỒ — thiếu một trong
    /// hai thì không phân biệt được "đã chuyển" với "nhấc lên rồi thả lại" hay "bị hoán đổi".
    /// </summary>
    private bool DragOne(GridScanner srcGrid, CellInfo src, CellInfo dest, CancellationToken ct)
    {
        var destCell = dest;
        for (int attempt = 1; attempt <= _cfg.DragRetries + 1; attempt++)
        {
            _log($"kéo #{src.Index} → cốp #{destCell.Index}  ({src.Centre.X},{src.Centre.Y} → " +
                 $"{destCell.Centre.X},{destCell.Centre.Y})  lần {attempt}");

            int slow = attempt - 1;
            InputSender.DragSmooth(
                src.Centre, destCell.Centre,
                _cfg.DragMoveSteps,
                _cfg.DragStepMs,
                _cfg.DragGrabMs + slow * _cfg.DragGrabMs / 2,
                _cfg.DragDropHoverMs + slow * _cfg.DragDropHoverMs / 2,
                _cfg.DragCursorOnly);

            Sleep(ct, _cfg.DragSettleMs);
            InputSender.MoveCursorOnly(_park.X, _park.Y);
            Sleep(ct, 150);

            var srcNow = srcGrid.ScanScreen().FirstOrDefault(c => c.Index == src.Index);
            var dstNow = _trunk.ScanScreen().FirstOrDefault(c => c.Index == destCell.Index);
            bool ok = srcNow is { State: CellState.Empty } && dstNow is not null && dstNow.State != CellState.Empty;
            if (ok) return true;

            _log($"   chưa chuyển được — nguồn={srcNow?.State}, đích={dstNow?.State}");
            if (attempt > _cfg.DragRetries) break;

            // Doi o dich khac: mot o "trong" ma tha vao khong duoc thi van de co the o o dich,
            // khong phai o cu keo.
            // Uu tien o trong HAN. Doi sang mot o nhat la doi tu mot o co the dang co do sang
            // mot o khac cung the — thua ra mot cu keo hoan doi nua.
            var free = _trunk.ScanScreen()
                .Where(c => c.State == CellState.Empty && c.Index != destCell.Index)
                .ToList();
            var other = free.FirstOrDefault(c => !c.Faint) ?? free.FirstOrDefault();
            if (other is not null)
            {
                destCell = other;
                _log($"   đổi ô đích sang cốp #{destCell.Index}");
            }
        }
        return false;
    }

    /// <summary>Thu dọn sau khi hỏng: nhả hết, Esc nếu đang có màn hình mở.</summary>
    private void Recover(CancellationToken ct)
    {
        HeldKeys.ReleaseAll();
        try
        {
            var st = _opener.ReadState();
            if (st.AnyOpen) _opener.CloseAll(ct);
        }
        catch (Exception ex) { _log("thu dọn: " + ex.Message); }
    }

    private bool WaitState(Func<ScreenState, bool> done, int timeoutMs, CancellationToken ct, out string last)
    {
        var sw = Stopwatch.StartNew();
        int hold = 0;
        last = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var st = _opener.ReadState();
            last = st.ToString();
            hold = done(st) ? hold + 1 : 0;
            if (hold >= 2) return true;
            Sleep(ct, _cfg.PollMs);
        }
        return false;
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
    }

    public void Dispose()
    {
        _opener?.Dispose();
        _bagWeight?.Dispose();
        _trunkWeight?.Dispose();
        _hotbar?.Dispose();
        _bag?.Dispose();
        _pockets?.Dispose();
        _trunk?.Dispose();
        _splitter?.Dispose();
    }
}
