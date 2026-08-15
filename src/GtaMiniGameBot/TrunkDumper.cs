using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum DumpResult
{
    Ok,
    NothingToMove
}

/// <summary>
/// Đổ cá từ ba lô sang cốp xe: mở cốp → tìm ô cá → kéo sang ô trống → đóng lại.
///
/// Mọi bước đều xác nhận bằng pixel trước khi đi tiếp, và không bước nào thả một món đồ vào ô
/// chưa chứng minh là trống — thả nhầm vào ô có đồ có thể là HOÁN ĐỔI, tức kéo ngược đồ trong
/// cốp vào ba lô và làm ba lô nặng thêm, đúng cái đang cố tránh.
/// </summary>
internal sealed class TrunkDumper : IDisposable
{
    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;
    private readonly Action<string> _log;

    private readonly TrunkOpener _opener;
    private readonly WeightReader _bagWeight;
    private readonly GridScanner _hotbar;
    private readonly GridScanner _bag;
    private readonly GridScanner _trunk;
    private readonly Point _park;

    private int _ocrFails;

    public bool OcrHealthy { get; private set; } = true;
    public string AtlasMissing => _bagWeight?.AtlasMissing ?? "";

    private TrunkDumper(FishingConfig cfg, Screen screen, FishingProfile profile, Action<string> log,
                        TrunkOpener opener, WeightReader weight,
                        GridScanner hotbar, GridScanner bag, GridScanner trunk)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
        _log = log;
        _opener = opener;
        _bagWeight = weight;
        _hotbar = hotbar;
        _bag = bag;
        _trunk = trunk;

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
        if (p.FishSlots is not { Count: > 0 })
        {
            opener.Dispose();
            problem = "chưa chọn ô chứa cá";
            return null;
        }

        var atlas = DigitAtlas.Load(p.Key);
        var weight = new WeightReader(cfg, screen, p.BagWeight, atlas, cfg.BagCapKg);

        return new TrunkDumper(cfg, screen, p, log, opener, weight,
            new GridScanner(cfg, screen, p.Hotbar),
            new GridScanner(cfg, screen, p.Bag),
            new GridScanner(cfg, screen, p.Trunk));
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

    /// <summary>Ném <see cref="TrunkStepException"/> nếu cả hai lượt đều không xong.</summary>
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

        double before = ReadTrunkScreenWeight();
        int moved = 0;

        while (moved < _cfg.MaxDragsPerDump)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.ElapsedMilliseconds > _cfg.MaxDumpMs)
                throw new TrunkStepException($"đổ cốp quá {_cfg.MaxDumpMs} ms — dừng");

            var source = NextFish(out string scanNote);
            if (scanNote is not null) _log(scanNote);
            if (source is null) break;

            var dest = NextEmptyTrunkCell();
            if (dest is null)
                throw new TrunkStepException("cốp xe không còn ô trống — đã đầy?");

            if (!DragOne(source.Value.Scanner, source.Value.Cell, dest, ct))
                throw new TrunkStepException("kéo cá vào cốp thất bại");

            moved++;
        }

        if (moved == 0)
        {
            _log("mọi ô chứa cá đã khai báo đều đang trống");
            _opener.CloseAll(ct);
            return DumpResult.NothingToMove;
        }

        double after = ReadTrunkScreenWeight();
        if (before >= 0 && after >= 0 && before - after < _cfg.MinDropKg)
            _log($"cảnh báo: kéo {moved} ô nhưng KG chỉ giảm {before - after:F1} " +
                 $"(chờ ít nhất {_cfg.MinDropKg:F1}) — nhiều khả năng cá đã tràn sang một ô " +
                 "chưa khai báo, vào Chọn ô chứa cá thêm ô đó");

        _log($"đã kéo {moved} ô sang cốp, KG {before:F1} → {after:F1}");
        _opener.CloseAll(ct);
        Sleep(ct, _cfg.AfterDumpMs);
        _bagWeight.ResetHistory();
        return DumpResult.Ok;
    }

    /// <summary>KG ba lô đọc ngay trên màn cốp — ô số nằm đúng chỗ cũ. -1 nếu không đọc được.</summary>
    private double ReadTrunkScreenWeight()
    {
        if (!OcrHealthy) return -1;
        var r = _bagWeight.Read();
        return r.Ok ? r.Value : -1;
    }

    /// <summary>
    /// Ô cá đầu tiên trong danh sách khai báo mà đang có đồ. Không dò icon: người dùng đã cam
    /// kết ô đó luôn là cá, nên ở đây chỉ cần biết ô rỗng hay không.
    /// </summary>
    private (GridScanner Scanner, CellInfo Cell)? NextFish(out string note)
    {
        note = null;
        InputSender.MoveCursorOnly(_park.X, _park.Y);
        Thread.Sleep(120);

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
        _hotbar?.Dispose();
        _bag?.Dispose();
        _trunk?.Dispose();
    }
}
