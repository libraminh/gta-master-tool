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
    private readonly GridScanner _trunk;
    private readonly Point _park;
    private readonly ItemCatalog _catalog;
    private readonly HashSet<string> _fishItems;

    private int _ocrFails;
    private int _trunkFullStrikes;

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

    private TrunkDumper(FishingConfig cfg, Screen screen, FishingProfile profile, Action<string> log,
                        TrunkOpener opener, WeightReader weight, WeightReader trunkWeight,
                        GridScanner hotbar, GridScanner bag, GridScanner trunk,
                        ItemCatalog catalog)
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
        _trunk = trunk;
        _catalog = catalog;
        _fishItems = new HashSet<string>(profile.FishItems ?? new List<string>(),
                                         StringComparer.OrdinalIgnoreCase);

        // Cho do chuot trung tinh truoc moi lan chup kiem tra: o duoi con tro duoc ve sang hon,
        // quen buoc nay la moi phep do deu nhiem.
        var b = screen.Bounds;
        _park = new Point(b.Left + 40, b.Top + 40);
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
              $"{p.FishItems.Count} loại được tính là cá — quét cả ba lô, không cần ô khai báo"
            : $"nhận cá theo ô khai báo ({p.FishSlots.Count} ô)");

        var atlas = DigitAtlas.Load(p.Key);
        var weight = new WeightReader(cfg, screen, p.BagWeight, atlas, cfg.BagCapKg);
        var trunkWeight = p.TrunkWeight.IsSet
            ? new WeightReader(cfg, screen, p.TrunkWeight, atlas, cfg.TrunkCapKg)
            : null;
        if (trunkWeight is null)
            log("chưa khoanh ô số KG cốp — bot sẽ không biết cốp còn trống bao nhiêu");

        return new TrunkDumper(cfg, screen, p, log, opener, weight, trunkWeight,
            new GridScanner(cfg, screen, p.Hotbar),
            new GridScanner(cfg, screen, p.Bag),
            new GridScanner(cfg, screen, p.Trunk),
            catalog);
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
        if (fishKg >= 0 && TrunkFreeKg >= 0 && fishKg > TrunkFreeKg)
            return ConcludeTrunkFull(
                $"cốp còn {TrunkFreeKg:F1} kg mà chỗ cá đang có {fishKg:F1} kg", ct);

        int moved = 0;
        string fullWhy = null;

        while (moved < _cfg.MaxDragsPerDump)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.ElapsedMilliseconds > _cfg.MaxDumpMs)
                throw new TrunkStepException($"đổ cốp quá {_cfg.MaxDumpMs} ms — dừng");

            var source = NextFish(out string scanNote);
            if (scanNote is not null) _log(scanNote);
            if (source is null) break;

            var dest = NextEmptyTrunkCell();
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
            _log("mọi ô chứa cá đã khai báo đều đang trống");
            _opener.CloseAll(ct);
            TurnBack(ct);
            return DumpResult.NothingToMove;
        }

        if (moved > 0)
        {
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
        }

        if (fullWhy is not null) return ConcludeTrunkFull(fullWhy, ct);

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

    /// <summary>
    /// Ô cá tiếp theo cần kéo.
    ///
    /// Hai chế độ. Có bộ icon moi từ cache game và người dùng đã tích vật phẩm nào là cá thì
    /// quét CẢ phím nhanh lẫn ba lô rồi nhận cá theo icon — cá nằm đâu cũng thấy. Chưa có thì
    /// quay về lối cũ: chỉ tin mấy ô người dùng khai báo, không nhìn icon.
    /// </summary>
    private (GridScanner Scanner, CellInfo Cell)? NextFish(out string note)
    {
        note = null;
        InputSender.MoveCursorOnly(_park.X, _park.Y);
        Thread.Sleep(120);

        return ByIcon ? NextFishByIcon(out note) : NextFishBySlot(out note);
    }

    /// <summary>Có đủ bộ icon và danh sách cá thì mới nhận diện được; thiếu một trong hai là về lối cũ.</summary>
    public bool ByIcon => _catalog is { Count: > 0 } && _fishItems.Count > 0;

    /// <summary>
    /// Quét mọi ô đang có đồ, kéo ô đầu tiên nhận ra là cá.
    ///
    /// Ô không nhận ra thì ĐỂ YÊN. Đó là lựa chọn có chủ ý: đoán bừa một ô lạ rồi kéo đi có
    /// thể là ném cả cần câu, mồi hay tiền vào cốp, mà thứ mất đi thì không kéo ngược lại được.
    /// </summary>
    private (GridScanner Scanner, CellInfo Cell)? NextFishByIcon(out string note)
    {
        note = null;
        var unknown = new List<string>();

        foreach (var (label, scanner) in new[] { ("phím nhanh", _hotbar), ("ba lô", _bag) })
        {
            foreach (var (cell, gray) in scanner.ScanScreenPixels())
            {
                if (cell is null || cell.IsEmpty) continue;

                var guess = _catalog.Classify(gray, cell.Rect.Width, cell.Rect.Height);
                if (guess.Name is null)
                {
                    unknown.Add($"{label} #{cell.Index} {guess}");
                    continue;
                }
                if (!_fishItems.Contains(guess.Name)) continue;

                foreach (string u in unknown) _log("   bỏ qua ô không rõ: " + u);
                note = $"kéo {label} #{cell.Index} — {guess}";
                return (scanner, cell);
            }
        }

        foreach (string u in unknown) _log("   bỏ qua ô không rõ: " + u);
        return null;
    }

    /// <summary>
    /// Ô cá đầu tiên trong danh sách khai báo mà đang có đồ. Không dò icon: người dùng đã cam
    /// kết ô đó luôn là cá, nên ở đây chỉ cần biết ô rỗng hay không.
    /// </summary>
    private (GridScanner Scanner, CellInfo Cell)? NextFishBySlot(out string note)
    {
        note = null;

        foreach (var slot in _profile.FishSlots)
        {
            var scanner = ScannerFor(slot.Grid);
            if (scanner is null) continue;
            if (slot.Index < 0 || slot.Index >= scanner.Count)
            {
                note = $"ô {slot.Label} nằm ngoài lưới ({scanner.Count} ô) — bỏ qua";
                continue;
            }

            var cell = scanner.ScanCell(slot.Index);
            if (cell is null || cell.IsEmpty) continue;

            note = $"kéo ô {slot.Label} (màu={cell.Chroma:F3} lệch={cell.Std:F1})";
            return (scanner, cell);
        }
        return null;
    }

    private GridScanner ScannerFor(string grid) => grid switch
    {
        FishSlot.GridHotbar => _hotbar,
        FishSlot.GridBag => _bag,
        _ => null
    };

    /// <summary>Null = cốp không còn ô trống.</summary>
    private CellInfo NextEmptyTrunkCell()
    {
        foreach (var c in _trunk.ScanScreen())
            if (c.State == CellState.Empty) return c;
        return null;
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
            var other = _trunk.ScanScreen()
                .FirstOrDefault(c => c.State == CellState.Empty && c.Index != destCell.Index);
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
        _trunk?.Dispose();
    }
}
