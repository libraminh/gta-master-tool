using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum StopReason
{
    UserStopped,
    InventoryFullNoIncrement,
    InventoryFullNoReset,
    PanelClosed,
    HoldTimeout,
    MaxCyclesReached,
    Error
}

/// <summary>Ket cuc cua viec doi game mo chu ky moi.</summary>
internal enum ResetOutcome
{
    DaReset,
    PanelBienMat,
    HetThoiGian
}

/// <summary>
/// Vong lap cay Gieng Khoan Dau.
///
/// Dieu khien theo TRANG THAI, khong phat lai theo thoi gian:
/// doc man hinh -> thanh nao chua trang thi giu no -> nha khi no trang.
/// Nho vay len cap (thoi gian khai thac giam) bot tu chay nhanh theo,
/// khong phai sua mot dong config nao.
/// </summary>
internal sealed class OilWellBot
{
    private sealed class BotStop(StopReason reason, string message) : Exception(message)
    {
        public StopReason Reason { get; } = reason;
    }

    private readonly BotConfig _cfg;
    private readonly Random _rng = new();
    private readonly Stopwatch _sinceCarReset = new();
    private CancellationTokenSource _cts;
    private Thread _thread;
    private bool _marginWarned;

    /// <summary>Bao nhieu giay ke tu lan len-xuong xe gan nhat.</summary>
    public int SecondsSinceCarReset => (int)_sinceCarReset.Elapsed.TotalSeconds;

    public OilWellBot(BotConfig cfg) => _cfg = cfg;

    /// <summary>Them nhieu ngau nhien vao toa do va nhip nghi.</summary>
    public bool Jitter { get; set; }

    public bool Running => _thread is { IsAlive: true };

    public event Action<string> Log;
    public event Action<Snapshot> SnapshotReady;
    public event Action<int, int> Progress;              // (chu ky, thung)
    public event Action<StopReason, string> Stopped;

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "OilWellBot" };
        _thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>Ten tieng Viet cua ly do dung, de hien cho nguoi dung.</summary>
    public static string TenLyDo(StopReason r) => r switch
    {
        StopReason.UserStopped => "người dùng bấm dừng",
        StopReason.InventoryFullNoIncrement => "kho đầy (số thùng không tăng)",
        StopReason.InventoryFullNoReset => "kho đầy (game không mở chu kỳ mới)",
        StopReason.PanelClosed => "panel bị đóng",
        StopReason.HoldTimeout => "giữ quá lâu mà thanh không đầy",
        StopReason.MaxCyclesReached => "đã xong số chu kỳ đặt trước",
        _ => "lỗi"
    };

    // -------------------------------------------------------------------

    private void Run(CancellationToken ct)
    {
        var reason = StopReason.UserStopped;
        string message = "người dùng bấm dừng";

        using var reader = new MiniGameReader(_cfg);
        try
        {
            var first = reader.Read();
            SnapshotReady?.Invoke(first);

            if (!first.PanelOpen)
                throw new BotStop(StopReason.PanelClosed,
                    $"panel chưa mở — 4 thanh nổi lên thấp nhất {first.PanelProminence:F1}, " +
                    $"cần ≥ {_cfg.PanelBarProminenceMin}. Hãy đứng ở giàn khoan và bấm E trước.");

            int greenPrev = first.GreenCount;
            int barrels = 0, cycles = 0, holdsSinceGain = 0;
            int barCount = _cfg.BarX.Length;
            _marginWarned = false;
            _sinceCarReset.Restart();

            Emit($"bắt đầu. pixel xanh ban đầu = {greenPrev}, nhiễu ngẫu nhiên: {(Jitter ? "bật" : "tắt")}");
            Emit($"ngưỡng: đầy ≥ {_cfg.FullThreshold}, coi là đã reset < {_cfg.ResetThreshold}, " +
                 $"panel mở khi nổi lên ≥ {_cfg.PanelBarProminenceMin}");
            Emit($"thân thanh đọc ở y = {_cfg.BarYTop}…{_cfg.BarYBottom}, {_cfg.BarSamples} điểm mẫu");
            Emit(_cfg.CarResetEnabled
                ? $"reset xe: BẬT, mỗi {_cfg.CarResetEverySec}s, chèn giữa hai chu kỳ"
                : "reset xe: TẮT");

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var snap = reader.Read();
                SnapshotReady?.Invoke(snap);

                if (!snap.PanelOpen)
                {
                    if (!PanelStaysClosed(reader, ct, ref snap)) continue;
                    throw new BotStop(StopReason.PanelClosed,
                        $"panel bị đóng giữa lúc chạy — nổi lên {snap.PanelProminence:F1}, " +
                        $"cần ≥ {_cfg.PanelBarProminenceMin}");
                }

                // Chot an toan quan trong nhat: game khong o foreground thi TUYET DOI
                // khong bam, neu khong bot se nhan giu chuot vao the gioi game
                // (tuc la ban sung / dam lien tuc) hoac click bua vao Windows.
                if (!GameIsForeground())
                {
                    Emit("game không ở foreground — tạm ngừng");
                    Thread.Sleep(600);
                    continue;
                }

                int todo = snap.TodoIndex;
                if (todo >= 0)
                {
                    // Chen reset xe DUNG O RANH GIOI CHU KY: thanh dau tien la viec
                    // ke tiep va chua thanh nao chay. Nhu vay khong bao gio cat ngang
                    // mot thanh dang chay (nha giua duong la mat sach tien trinh).
                    if (_cfg.CarResetEnabled && todo == 0 && snap.NoneFull
                        && _sinceCarReset.Elapsed.TotalSeconds >= _cfg.CarResetEverySec)
                    {
                        DoCarReset(reader, ct);
                        _sinceCarReset.Restart();
                        continue;                 // doc lai trang thai tu dau
                    }

                    HoldUntilFull(reader, todo, ct);

                    var (green, _) = reader.RefreshCounter();
                    if (!reader.PanelOpen())
                    {
                        var s2 = reader.Read();
                        if (PanelStaysClosed(reader, ct, ref s2))
                            throw new BotStop(StopReason.PanelClosed, "panel đóng ngay sau khi giữ xong");
                    }

                    if (green != greenPrev)
                    {
                        barrels++;
                        holdsSinceGain = 0;
                        Emit($"+1 thùng (tổng {barrels}). pixel xanh {greenPrev} → {green}");
                        greenPrev = green;
                        Progress?.Invoke(cycles, barrels);
                    }
                    else holdsSinceGain++;

                    // Neu giu xong ca 2 chu ky day ma con so khong nhich => kho day.
                    if (holdsSinceGain >= barCount * _cfg.StopAfterStaleCycles)
                        throw new BotStop(StopReason.InventoryFullNoIncrement,
                            $"đã giữ xong {holdsSinceGain} thanh mà số thùng không tăng — kho đầy");

                    Sleep(_cfg.BetweenPositionsMs);
                }
                else
                {
                    // Ca 4 thanh trang = het chu ky. Doi game reset ve xam.
                    // KHONG duoc bo qua buoc nay: thanh da xong van GIU MAU TRANG
                    // toi het chu ky, lao vao kiem tra ngay se thay trang cu va
                    // tuong da xong -> nha tay, khong duoc gi, lap vo nghia mai mai.
                    switch (WaitForReset(reader, ct))
                    {
                        case ResetOutcome.PanelBienMat:
                        {
                            var s3 = reader.Read();
                            if (PanelStaysClosed(reader, ct, ref s3))
                                throw new BotStop(StopReason.PanelClosed,
                                    "panel biến mất ngay sau khi xong 4 thanh — chưa kịp mở chu kỳ mới");
                            continue;
                        }
                        case ResetOutcome.HetThoiGian:
                            throw new BotStop(StopReason.InventoryFullNoReset,
                                $"cả 4 thanh đầy mà {_cfg.ResetWaitMs} ms không reset — kho đầy");
                    }

                    cycles++;
                    Progress?.Invoke(cycles, barrels);
                    Emit($"hết chu kỳ {cycles}");

                    if (_cfg.MaxCycles > 0 && cycles >= _cfg.MaxCycles)
                        throw new BotStop(StopReason.MaxCyclesReached, $"đã xong {cycles} chu kỳ theo cài đặt");

                    Sleep(_cfg.BetweenCyclesMs);
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = StopReason.UserStopped;
            message = "người dùng bấm dừng";
        }
        catch (BotStop bs)
        {
            reason = bs.Reason;
            message = bs.Message;
        }
        catch (Exception ex)
        {
            reason = StopReason.Error;
            message = ex.Message;
        }
        finally
        {
            // Chan chuot bi ket o trang thai dang nhan, bat ke thoat kieu gi.
            try { InputSender.LeftUp(); } catch { }

            // Chup bang chung khi dung vi loi. Khong chup khi nguoi dung tu bam dung
            // hay khi da xong so chu ky - hai truong hop do khong co gi de truy.
            if (_cfg.DebugDumpEnabled && reason is not (StopReason.UserStopped or StopReason.MaxCyclesReached))
            {
                try
                {
                    string dir = reader.DumpEvidence(
                        AppPaths.DebugDumps,
                        $"{TenLyDo(reason)} — {message}", _cfg.DebugDumpKeep);
                    Emit($"đã lưu bằng chứng vào: {dir}");
                }
                catch (Exception ex) { Emit($"không lưu được bằng chứng: {ex.Message}"); }
            }

            Emit($"DỪNG — {TenLyDo(reason)}: {message}");
            Stopped?.Invoke(reason, message);
        }
    }

    // -------------------------------------------------------------------

    /// <summary>
    /// Panel co THAT SU dong khong, hay chi nhay tat mot nhip.
    /// Truoc day mot lan doc thay dong la dung luon - neu panel nhay tat mot nhip
    /// luc game trao thung thi bot chet giua mot luot cay 30 phut.
    /// Tra ve true = dong that, nen dung.
    /// </summary>
    private bool PanelStaysClosed(MiniGameReader reader, CancellationToken ct, ref Snapshot snap)
    {
        int reads = Math.Max(1, _cfg.PanelClosedGraceReads);
        for (int i = 1; i < reads; i++)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(_cfg.PanelClosedGraceIntervalMs);

            snap = reader.Read();
            SnapshotReady?.Invoke(snap);
            if (snap.PanelOpen)
            {
                Emit($"panel nháy tắt rồi mở lại (lần đọc thứ {i + 1}) — chạy tiếp");
                return false;
            }
        }
        return true;
    }

    private void HoldUntilFull(MiniGameReader reader, int index, CancellationToken ct)
    {
        int attempts = Math.Max(1, _cfg.PressRetries) + 1;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            // Lan cuoi: giu "mu" het MaxHoldMs, khong bo giua.
            // Ly do: neu game KHONG ve thanh chay dan (chi doi mau mot phat luc xong)
            // thi khong co tin hieu "dang chay" nao de doi -> khong duoc phep thu lai
            // mai, vi nhu vay se nha tay lien tuc va khong bao gio xong duoc.
            bool blind = attempt == attempts;
            if (TryHold(reader, index, ct, blind, attempt)) return;
        }
        throw new BotStop(StopReason.HoldTimeout,
            $"thanh {index + 1}: nhấn {attempts} lần vẫn không chạy");
    }

    /// <summary>Tra ve true neu thanh da day. false = cu nhan khong an, nen thu lai.</summary>
    private bool TryHold(MiniGameReader reader, int index, CancellationToken ct, bool blind, int attempt)
    {
        // 1. Bao dam chuot DANG NHA truoc khi roi khoi bieu tuong cu.
        InputSender.LeftUp();
        Thread.Sleep(_cfg.ReleaseSettleMs);

        // 2. Di chuyen tung buoc nho, khong teleport.
        int x = _cfg.BarX[index] + JitterPx();
        int y = _cfg.ClickY + JitterPx();
        InputSender.MoveSmooth(x, y, _cfg.MoveSteps);

        // 3. Phat mot su kien NHA CHUOT NGAY TAI VI TRI MOI.
        // Day la chi tiet quyet dinh: giao dien game chi nhan cu nhan sau khi da
        // thay chuot duoc NHA tren chinh bieu tuong do. Truoc khi co dong nay,
        // cu nhan dau tien o MOI thanh deu truot, phai cho ~2.2s moi an.
        InputSender.LeftUp();

        // 4. Cho game cap nhat trang thai hover truoc khi nhan.
        Thread.Sleep(_cfg.HoverSettleMs);

        reader.RefreshBars();
        var baseline = reader.BarSamples(index);
        WarnIfMarginTight(index, baseline);

        var sw = Stopwatch.StartNew();
        InputSender.LeftDown();
        try
        {
            bool moving = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(_cfg.PollMs);

                reader.RefreshBars();
                var now = reader.BarSamples(index);
                int fullCount = now.Count(v => v >= _cfg.FullThreshold);

                if (fullCount == now.Length)
                {
                    // In ca mang mau LUC DAY: day la so can de chot nguong.
                    // Thanh day dung ra phai la 255 bao hoa - neu no chi dat ~230 thi
                    // khong the nang FullThreshold len 245 duoc.
                    Emit($"thanh {index + 1} ĐẦY sau {sw.ElapsedMilliseconds} ms  " +
                         $"mẫu=[{string.Join(",", now)}]");
                    return true;
                }

                if (!moving && Deviates(baseline, now, 15))
                {
                    moving = true;
                    Emit($"thanh {index + 1} bắt đầu chạy sau {sw.ElapsedMilliseconds} ms " +
                         $"(đã trắng {fullCount}/{now.Length}, mẫu=[{string.Join(",", now)}])");
                }

                // Cu nhan khong an: khong co gi doi sau PressCheckMs -> hover lai va thu lai.
                if (!moving && !blind && sw.ElapsedMilliseconds > _cfg.PressCheckMs)
                {
                    Emit($"thanh {index + 1}: nhấn lần {attempt} KHÔNG ĂN — " +
                         $"{sw.ElapsedMilliseconds} ms không đổi gì, mẫu=[{string.Join(",", now)}]. " +
                         "Hover lại rồi thử lại.");
                    return false;
                }

                if (sw.ElapsedMilliseconds > _cfg.MaxHoldMs)
                    throw new BotStop(StopReason.HoldTimeout,
                        $"thanh {index + 1} không đầy sau {_cfg.MaxHoldMs} ms " +
                        $"(đã trắng {fullCount}/{now.Length}, mẫu=[{string.Join(",", now)}])");
            }
        }
        finally
        {
            InputSender.LeftUp();
            Thread.Sleep(_cfg.ReleaseSettleMs);
        }
    }

    /// <summary>
    /// Canh bao khi thanh dang RONG ma da doc gan sat nguong "day".
    /// Thanh la lop phu ban trong suot (~51 + 0.8*nen), nen o gieng co nen sang
    /// thanh rong co the doc thanh day -> bot nha tay ngay, khong duoc thung nao.
    /// Bien loi am tham do thanh mot dong nhin thay duoc.
    /// </summary>
    private void WarnIfMarginTight(int index, int[] baseline)
    {
        if (_marginWarned || baseline.Length == 0) return;

        int hi = baseline.Max();
        if (hi < _cfg.FullThreshold - _cfg.MarginWarnGap) return;

        _marginWarned = true;
        Emit($"CẢNH BÁO biên hẹp: thanh {index + 1} lúc RỖNG đã đọc tới {hi}, " +
             $"mà ngưỡng đầy là {_cfg.FullThreshold} (mẫu=[{string.Join(",", baseline)}]). " +
             "Nền ở giàn khoan này quá sáng — dễ đọc thanh rỗng thành đầy.");
    }

    /// <summary>Co diem mau nao lech khoi luc chua nhan qua nguong khong.</summary>
    private static bool Deviates(int[] baseline, int[] now, int delta)
    {
        for (int i = 0; i < baseline.Length && i < now.Length; i++)
            if (Math.Abs(now[i] - baseline[i]) > delta) return true;
        return false;
    }

    /// <summary>
    /// Doi game mo chu ky moi.
    ///
    /// PHAI kiem panel truoc khi kiem thanh. Panel bien mat thi 4 vi tri thanh doc ra
    /// dia hinh trong - deu duoi nguong - nen neu khong kiem panel thi ham nay bao ngay
    /// "da reset, het chu ky" va che mat nguyen nhan that. Do la loi da lam moi log
    /// truoc day sai lech.
    /// </summary>
    private ResetOutcome WaitForReset(MiniGameReader reader, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < _cfg.ResetWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(_cfg.PollMs * 2);

            var s = reader.Read();
            SnapshotReady?.Invoke(s);

            if (!s.PanelOpen) return ResetOutcome.PanelBienMat;

            for (int i = 0; i < s.BarMin.Length; i++)
                if (s.BarMin[i] < _cfg.ResetThreshold) return ResetOutcome.DaReset;
        }
        return ResetOutcome.HetThoiGian;
    }

    // ---------------- reset dong ho thue xe ----------------

    /// <summary>
    /// ESC dong panel -> F len xe -> F xuong xe -> E mo panel lai.
    /// Hai buoc panel la VONG KIN; hai cu F la VONG HO (cho theo thoi gian do duoc).
    /// </summary>
    private void DoCarReset(MiniGameReader reader, CancellationToken ct)
    {
        Emit($"=== reset xe thuê (đã {SecondsSinceCarReset}s kể từ lần trước) ===");

        // Hai buoc lien quan toi PANEL van VONG KIN: tin hieu panel la tin hieu dang
        // tin nhat trong du an.
        //
        // Hai cu F thi VONG HO. Ly do: ca hai cach do "dang trong xe" deu do.
        // Dem pixel gan-trang bi anh sang keo di; NCC thi bi nen lot qua vi dong ho
        // ban trong suot (mau hieu chuan co nen dat toi, den luc chay lai co nha ton
        // xam sang dung sau -> ncc tut tu 0.958 xuong 0.71).
        Gate(reader, ct, "đóng panel (ESC)", _cfg.VkEsc,
             expectBefore: _ => true,
             done:         s => !s.PanelOpen);

        TapAndSleep(ct, "lên xe (F)", _cfg.VkVehicle, _cfg.AfterEnterCarMs, reader);
        TapAndSleep(ct, "xuống xe (F)", _cfg.VkVehicle, _cfg.AfterExitCarMs, reader);

        Gate(reader, ct, "mở panel (E)", _cfg.VkInteract,
             expectBefore: _ => true,
             done:         s => s.PanelOpen);

        // xac nhan 4 thanh doc ra gia tri hop le sau khi panel mo lai
        reader.RefreshBars();
        for (int i = 0; i < _cfg.BarX.Length; i++)
        {
            int m = reader.BarMin(i);
            if (m < 0)
                throw new BotStop(StopReason.Error,
                    $"panel mở lại rồi nhưng thanh {i + 1} đọc ra {m} — vùng đọc nằm ngoài màn hình?");
        }
        Emit("=== reset xe xong, 4 thanh đọc được bình thường ===");
    }

    /// <summary>
    /// Bam mot phim roi ngu co dinh - khong kiem trang thai.
    /// Van doc va IN ncc de sau nay truy duoc: neu co lan nao bam truot thi log cho
    /// biet luc do man hinh dang the nao.
    /// </summary>
    private void TapAndSleep(CancellationToken ct, string what, int vk, int sleepMs, MiniGameReader reader)
    {
        ct.ThrowIfCancellationRequested();
        if (!GameIsForeground())
            throw new BotStop(StopReason.Error, $"{what}: game không ở foreground — dừng để không bấm bừa");

        var before = reader.Read();
        InputSender.TapKey((ushort)vk);
        Emit($"  {what}: đã bấm, chờ {sleepMs} ms  (trước khi bấm: ncc={before.CarScore:F3}, " +
             $"thanh nổi lên={before.PanelProminence:F1})");

        // ngu theo tung khuc de bam F9 la dung duoc ngay, khong phai cho het 4 giay
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < sleepMs)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(Math.Min(150, (int)Math.Max(1, sleepMs - sw.ElapsedMilliseconds)));
        }

        var after = reader.Read();
        Emit($"  {what}: chờ xong  (ncc={after.CarScore:F3}, thanh nổi lên={after.PanelProminence:F1})");
    }

    /// <summary>
    /// Bam mot phim roi doi man hinh dat trang thai mong doi.
    /// Neu trang thai DA dat san thi khong bam - quan trong voi ESC, vi bam ESC
    /// lan hai luc khong con panel se mo menu tam dung cua game.
    /// </summary>
    private void Gate(MiniGameReader reader, CancellationToken ct, string what, int vk,
                      Func<Snapshot, bool> expectBefore, Func<Snapshot, bool> done)
    {
        int attempts = Math.Max(0, _cfg.GateRetries) + 1;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (!GameIsForeground())
                throw new BotStop(StopReason.Error, $"{what}: game không ở foreground — dừng để không bấm bừa");

            // TUYET DOI khong bam khi animation dang chay.
            // F la phim BAT/TAT: bam giua luc dang treo len xe se huy dong tac,
            // va lan bam sau lai lam nguoc lai -> lech pha ca chuoi.
            var pre = WaitStable(reader, ct);
            SnapshotReady?.Invoke(pre);

            if (done(pre))
            {
                Emit($"  {what}: đã đạt sẵn, không bấm  (ncc={pre.CarScore:F3})");
                return;
            }

            // Ngu canh phai dung TRUOC khi bam. Doc ra khac = phep do sai.
            if (!expectBefore(pre))
                throw new BotStop(StopReason.Error,
                    $"{what}: trạng thái trước khi bấm trái với ngữ cảnh — đang đọc “{pre.StateName}” " +
                    $"(ncc={pre.CarScore:F3}, thanh nổi lên={pre.PanelProminence:F1}). " +
                    "Rất có thể phép đo sai chứ không phải game đang ở trạng thái đó.");

            InputSender.TapKey((ushort)vk);

            var trace = new List<string>();
            long nextTraceAt = 0;
            var sw = Stopwatch.StartNew();
            int holdOk = 0;

            while (sw.ElapsedMilliseconds < _cfg.GateTimeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(120);

                var s = reader.Read();
                SnapshotReady?.Invoke(s);

                if (sw.ElapsedMilliseconds >= nextTraceAt && trace.Count < 40)
                {
                    trace.Add($"{sw.ElapsedMilliseconds}ms ncc={s.CarScore:F2} thanh={s.PanelProminence:F0}");
                    nextTraceAt = sw.ElapsedMilliseconds + 500;
                }

                // Doi dieu kien giu duoc 2 lan doc lien tiep, tranh an mot khoanh khac thoang qua.
                if (done(s))
                {
                    if (++holdOk >= 2)
                    {
                        Emit($"  {what}: đạt sau {sw.ElapsedMilliseconds} ms  ({s.StateName})");
                        Thread.Sleep(_cfg.AfterKeyDelayMs);
                        return;
                    }
                }
                else holdOk = 0;
            }

            var last = reader.Read();
            Emit($"  {what}: KHÔNG đạt sau {_cfg.GateTimeoutMs} ms (lần {attempt}/{attempts}) " +
                 $"— đang ở “{last.StateName}” (ncc={last.CarScore:F3}, thanh nổi lên={last.PanelProminence:F1})");
            Emit($"     diễn biến: {string.Join("  |  ", trace)}");
        }

        throw new BotStop(StopReason.Error,
            $"reset xe thất bại ở bước “{what}” — dừng hẳn để không bấm tiếp bước sau");
    }

    /// <summary>
    /// Doi cho trang thai doc duoc khong con thay doi = khong con animation dang chay.
    /// Day la thu chan cu bam-giua-animation, nguyen nhan lam du mot buoc F.
    /// </summary>
    private Snapshot WaitStable(MiniGameReader reader, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var last = reader.Read();
        int same = 1;

        while (sw.ElapsedMilliseconds < _cfg.StableWaitMaxMs)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(150);

            var s = reader.Read();
            // Chi theo PanelOpen. KHONG theo trang thai xe: ncc dao dong theo nen
            // phia sau dong ho nen no co the nhay qua nhay lai, lam vong nay
            // khong bao gio thay "on dinh".
            same = s.PanelOpen == last.PanelOpen ? same + 1 : 1;
            last = s;
            if (same >= Math.Max(1, _cfg.StableReads)) return s;
        }

        Emit($"     (cảnh báo: trạng thái chưa ổn định sau {_cfg.StableWaitMaxMs} ms — " +
             $"đang ở “{last.StateName}”)");
        return last;
    }

    private bool GameIsForeground()
    {
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) return true;
        return Native.ForegroundTitle().Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase);
    }

    private int JitterPx() => Jitter ? _rng.Next(-3, 4) : 0;

    private void Sleep(int ms)
    {
        if (Jitter) ms += _rng.Next(-ms / 4, ms / 2);
        Thread.Sleep(Math.Max(0, ms));
    }

    private void Emit(string s) => Log?.Invoke(s);
}
