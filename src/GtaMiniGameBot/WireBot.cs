using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum WireStopReason
{
    UserStopped,

    /// <summary>Panel đóng — giải xong.</summary>
    Solved,

    /// <summary>Không thấy panel nào: đứng sai chỗ, hoặc chưa mở minigame.</summary>
    NoPanel,

    /// <summary>Kéo mãi mà UI không nhận. Thường là mất focus, không phải lỗi suy luận.</summary>
    DragExhausted,

    /// <summary>Không đọc chắc được phản hồi. Dừng thay vì cắm lại đúng phương án vừa sai.</summary>
    FeedbackStalled,

    /// <summary>Phản hồi đọc được không khớp bí mật nào — mâu thuẫn dữ liệu.</summary>
    Contradiction,

    InputFailed,
    Error
}

/// <summary>
/// Job Thợ điện, phần minigame ĐI DÂY: cắm n đầu dây vào n ổ cắm khi game không nói màu nào đi
/// với màu nào.
///
/// Vòng chơi: cắm đủ n dây → game kiểm tra → dây đúng dính lại, dây sai rời ra → dùng đúng thông
/// tin đó loại bớt khả năng → cắm lại. <see cref="WirePolicy"/> lo phần chọn nước sao cho số lượt
/// kiểm tra kỳ vọng ít nhất (2.0 lượt với 3 dây, 3.0 với 5 dây); <see cref="WireReader"/> lo phần
/// đọc xem dây nào đã dính.
///
/// Ba luật an toàn, cả ba đều là bài học đã trả giá trong bản Python và đừng bỏ:
///   1. KHÔNG cắm lại một phương án đã sai. Dây rời ra sau khi kiểm tra là PHẢN HỒI, không phải
///      "cú kéo bị tuột" — bản v4 của họ hiểu sai chỗ này và giật điện người chơi liên tục mà
///      không loại được khả năng nào.
///   2. KHÔNG kéo lại một dây đang dính. Trước mỗi cú kéo phải thăm dò trước.
///   3. Đọc không chắc thì DỪNG, không đoán. Đoán sai ở đây tốn đúng một lần điện giật.
///
/// Neo màu đọc MỘT LẦN lúc đầu lượt rồi giữ nguyên: khi một dây đã nối đúng, sợi cáp của nó chạy
/// vòng qua cả panel và cắt ngang các slot khác, nên phân loại lại màu-theo-slot lúc đó là không
/// an toàn. Chỉ HỘP panel mới được dò lại, để màn rung không làm lệch toạ độ.
/// </summary>
internal sealed class WireBot
{
    private readonly ElectricConfig _cfg;
    private readonly Screen _screen;
    private readonly ElectricProfile _profile;
    private readonly Dictionary<int, WirePolicy> _policies = new();

    private CancellationTokenSource _cts;
    private Thread _thread;
    private bool _windowWarned;
    private int _rounds;

    public WireBot(ElectricConfig cfg, Screen screen, ElectricProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
    }

    public bool Running => _thread is { IsAlive: true };

    /// <summary>Số panel đã giải xong trong phiên này.</summary>
    public int Rounds => _rounds;

    public event Action<string> Log;
    public event Action<int> RoundsChanged;
    public event Action<WireStopReason, string> Stopped;

    public void Start()
    {
        if (Running) return;
        _rounds = 0;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "WireBot" };
        _thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>Huỷ rồi CHỜ luồng chết hẳn — xem <see cref="WoodBot.StopAndWait"/>.</summary>
    public void StopAndWait(int ms = 2500)
    {
        _cts?.Cancel();
        var t = _thread;
        if (t is null || !t.IsAlive) return;
        try { t.Join(ms); } catch { }
    }

    public static string TenLyDo(WireStopReason r) => r switch
    {
        WireStopReason.UserStopped => "người dùng bấm dừng",
        WireStopReason.Solved => "giải xong, panel đã đóng",
        WireStopReason.NoPanel => "không thấy panel đi dây",
        WireStopReason.DragExhausted => "UI không nhận cú kéo (thường là game mất focus)",
        WireStopReason.FeedbackStalled => "không đọc chắc được phản hồi của game",
        WireStopReason.Contradiction => "phản hồi đọc được không khớp khả năng nào",
        WireStopReason.InputFailed => "không gửi được chuột vào game",
        _ => "lỗi"
    };

    // ---------------------------------------------------------------- vong ngoai

    private void Run(CancellationToken ct)
    {
        var reason = WireStopReason.UserStopped;
        string message = "người dùng bấm dừng";
        WireReader reader = null;

        try
        {
            reader = WireReader.Open(_cfg, _screen, _profile);
            if (!reader.Configured)
            {
                reason = WireStopReason.Error;
                message = reader.Problem ?? "không mở được vùng quét";
                Emit("không chạy được: " + message);
                return;
            }

            Emit($"quét panel trong {reader.BandRegion.Width}×{reader.BandRegion.Height} " +
                 $"@{reader.BandRegion.X},{reader.BandRegion.Y}  (tỉ lệ {_profile.Scale:F3})");
            Emit($"cửa sổ game phải đang focus ({_cfg.WindowMatch}).");

            var sinceSeen = Stopwatch.StartNew();
            bool everSeen = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (WaitWindow(ct)) sinceSeen.Restart();

                var panel = reader.FindPanel();
                if (panel.IsEmpty)
                {
                    long limit = everSeen ? _cfg.Wire.PanelGoneMs : _cfg.Wire.NoPanelMs;
                    if (sinceSeen.ElapsedMilliseconds >= limit)
                    {
                        reason = everSeen ? WireStopReason.Solved : WireStopReason.NoPanel;
                        message = everSeen
                            ? $"panel đóng — đã giải {_rounds} lượt"
                            : $"{limit / 1000}s không thấy panel đi dây (đứng sai chỗ?)";
                        Emit(message);
                        return;
                    }
                    Sleep(ct, _cfg.Wire.SearchPollMs);
                    continue;
                }

                everSeen = true;
                sinceSeen.Restart();

                var round = reader.ReadRound(panel);
                if (round is null)
                {
                    // Panel dang ve do hoac dang co hoat anh — thu lai chu khong doan.
                    Sleep(ct, _cfg.Wire.SolvePollMs);
                    continue;
                }

                Emit("--- " + round.Describe());
                var (outcome, detail) = SolveRound(reader, round, ct);

                if (outcome == WireStopReason.Solved)
                {
                    _rounds++;
                    RoundsChanged?.Invoke(_rounds);
                    Emit($"xong lượt #{_rounds}. chờ panel tiếp theo.");
                    sinceSeen.Restart();
                    Sleep(ct, _cfg.Wire.SearchPollMs);
                    continue;
                }

                reason = outcome;
                message = detail;
                Emit("dừng an toàn: " + detail);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            reason = WireStopReason.UserStopped;
            message = "người dùng bấm dừng";
        }
        catch (InvalidOperationException ex)
        {
            // InputSender.Send nem cai nay khi SendInput khong lot — thuong la game chay quyen
            // Admin con app thi khong. Thong diep cua no da noi ro cach sua.
            reason = WireStopReason.InputFailed;
            message = ex.Message;
            Emit(message);
        }
        catch (Exception ex)
        {
            reason = WireStopReason.Error;
            message = ex.Message;
            Emit("lỗi: " + message);
        }
        finally
        {
            reader?.Dispose();
            try { InputSender.LeftUp(); } catch { }
            HeldKeys.ReleaseAll();
            Stopped?.Invoke(reason, message);
        }
    }

    // ---------------------------------------------------------------- mot luot

    private (WireStopReason Outcome, string Detail) SolveRound(
        WireReader reader, WireRound round, CancellationToken ct)
    {
        int n = round.Count;
        if (!_policies.TryGetValue(n, out var policy))
        {
            var sw = Stopwatch.StartNew();
            policy = new WirePolicy(n);
            _policies[n] = policy;

            var (cost, _) = policy.Choose(policy.AllCandidates(), 0);
            Emit($"bảng {n} dây: kỳ vọng {cost:F3} lượt kiểm tra (dựng {sw.ElapsedMilliseconds} ms)");
        }

        var th = new WirePolicy.FeedbackThresholds
        {
            Low = _cfg.Wire.LockGeomLow,
            High = _cfg.Wire.LockGeomHigh,
            Center = _cfg.Wire.FeedbackProbabilityCenter,
            Scale = _cfg.Wire.FeedbackProbabilityScale,
            Margin = _cfg.Wire.FeedbackMaskLogMargin
        };

        // Moc SACH cua ca luot: dung de nhan lai day da dinh o cac lan thu sau.
        var (cleanOk, clean) = reader.ReadTargetBlobs(round, round.Panel);
        if (!cleanOk) return (WireStopReason.Solved, "panel đóng ngay khi bắt đầu");

        var candidates = policy.AllCandidates();
        int fixedMask = 0;
        int full = (1 << n) - 1;
        int lastGuess = -1;
        var fixedPairs = new Dictionary<string, string>();

        // n+1 luot la du: ky vong 2.0 voi 3 day va 3.0 voi 5 day, con n+1 la chan tren de bot
        // khong quay mai khi mat doc sai mot cach he thong.
        int maxAttempts = n + 1;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            WaitWindow(ct);

            var live = reader.FindPanel();
            if (live.IsEmpty) return (WireStopReason.Solved, "panel đóng — xong");

            var (beforeOk, before) = reader.ReadTargetBlobs(round, live);
            if (!beforeOk) return (WireStopReason.Solved, "panel đóng — xong");

            // Dong bo trang thai VAT LY: mot day cua luot truoc van dinh ro rang nhung khung
            // phan hoi luc do lung lo nen chua duoc chot. Nhan lai o day, truoc khi chon nuoc
            // moi — neu khong thi bot se keo lai chinh cai day dang noi dung.
            if (lastGuess >= 0)
            {
                var prev = policy.Permutation(lastGuess);
                var sync = WireReader.GeometryScores(clean, before);
                int recovered = 0;
                for (int i = 0; i < n; i++)
                {
                    if (((fixedMask >> i) & 1) != 0) continue;
                    if (sync[prev[i]] >= _cfg.Wire.LockGeomHigh) recovered |= 1 << i;
                }

                if (recovered != 0)
                {
                    var names = new List<string>();
                    for (int i = 0; i < n; i++)
                    {
                        if (((recovered >> i) & 1) == 0) continue;
                        fixedPairs[round.Sources[i]] = round.Targets[prev[i]];
                        names.Add($"{round.Sources[i]}→{round.Targets[prev[i]]}");
                    }

                    // FilterKnownCorrect, KHONG phai Filter: o day bot chi QUAN SAT thay vai soi
                    // con dinh, con "chua thay dinh" khong phai bang chung la sai. Xem doc cua
                    // WirePolicy.FilterKnownCorrect.
                    candidates = policy.FilterKnownCorrect(candidates, lastGuess, recovered);
                    fixedMask |= recovered;
                    Emit("giữ dây đã nối, không kéo lại: " + string.Join(" | ", names));

                    if (candidates.Count == 0)
                        return (WireStopReason.Contradiction,
                                "trạng thái dây thật mâu thuẫn với mọi khả năng còn lại");
                    if (fixedMask == full) return (WireStopReason.Solved, "tất cả dây đã đúng");
                }
            }

            var activeIdx = new List<int>();
            for (int i = 0; i < n; i++)
                if (((fixedMask >> i) & 1) == 0) activeIdx.Add(i);
            if (activeIdx.Count == 0) return (WireStopReason.Solved, "tất cả dây đã đúng");

            double expected;
            int guessIdx;
            try { (expected, guessIdx) = policy.Choose(candidates, fixedMask); }
            catch (InvalidOperationException ex) { return (WireStopReason.Contradiction, ex.Message); }

            var guess = policy.Permutation(guessIdx);
            var plan = activeIdx.Select(i => $"{round.Sources[i]}→{round.Targets[guess[i]]}");
            Emit($"lượt {attempt}: {candidates.Count} khả năng, kỳ vọng còn {expected:F2} — cắm {string.Join(" | ", plan)}");

            // Moi day TRU day cuoi: cam va xac nhan, co retry co chan tren.
            for (int k = 0; k < activeIdx.Count - 1; k++)
            {
                int i = activeIdx[k];
                var attach = DragVerified(reader, round, before, i, guess[i], ct);

                if (attach == AttachResult.Closed) return (WireStopReason.Solved, "panel đóng trong lúc kéo");
                if (attach == AttachResult.NotAttached)
                    return (WireStopReason.DragExhausted,
                            $"{round.Sources[i]}→{round.Targets[guess[i]]} kéo {_cfg.Wire.DragMaxRetries} lần vẫn không dính");

                Sleep(ct, _cfg.Wire.BetweenDragMs);
            }

            // Day CUOI kich hoat kiem tra. Cam DUNG MOT LAN cho phuong an nay: neu game bao sai
            // thi cac day roi ra la phan hoi, khong phai co de cam lai.
            int lastI = activeIdx[^1];
            var livePanel = reader.FindPanel();
            if (livePanel.IsEmpty) return (WireStopReason.Solved, "panel đóng — xong");

            WaitWindow(ct);
            Drag(livePanel, round, lastI, guess[lastI], retry: false);
            Sleep(ct, _cfg.Wire.AfterLastDragMs);
            ParkCursor(livePanel);

            var fb = WaitFeedback(reader, round, before, policy, candidates, fixedMask,
                                  guessIdx, activeIdx, th, ct);

            if (fb.Closed)
            {
                foreach (int i in activeIdx) fixedPairs[round.Sources[i]] = round.Targets[guess[i]];
                return (WireStopReason.Solved, "panel đóng sau khi cắm — xong");
            }
            if (fb.Mask is null)
                return (WireStopReason.FeedbackStalled,
                        $"đọc phản hồi không chắc ({fb.How}); KHÔNG cắm lại phương án cũ");

            int mask = fb.Mask.Value;
            var locked = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (((mask >> i) & 1) == 0) continue;
                fixedPairs[round.Sources[i]] = round.Targets[guess[i]];
                locked.Add(round.Sources[i]);
            }

            var next = policy.Filter(candidates, fixedMask, guessIdx, mask);
            if (next.Count == 0)
                return (WireStopReason.Contradiction, "phản hồi khả thi mà vẫn hết khả năng — dừng cho an toàn");

            candidates = next;
            fixedMask |= mask;
            lastGuess = guessIdx;

            Emit($"phản hồi: đúng {(locked.Count > 0 ? string.Join(", ", locked) : "không có")} " +
                 $"({fb.How}) — còn {candidates.Count} khả năng");

            if (fixedMask == full) return (WireStopReason.Solved, "tất cả dây đã đúng");

            // Game khoa tuong tac mot lat sau luot sai — cho no het roi hay cam tiep.
            Sleep(ct, _cfg.Wire.PostFeedbackCooldownMs);
        }

        return (WireStopReason.FeedbackStalled, $"hết {maxAttempts} lượt mà chưa cắm đúng hết");
    }

    // ---------------------------------------------------------------- keo mot day

    private enum AttachResult { Attached, NotAttached, Closed }

    /// <summary>
    /// Cắm một dây KHÔNG PHẢI dây cuối, rồi xác nhận nó đã dính.
    ///
    /// Trước mỗi cú kéo đều thăm dò trước, kể cả cú đầu: nếu ổ đích đã đang mang cáp thì không
    /// chạm chuột. Bản Python gọi đây là chốt chống-kéo-trùng, và nó cần thật — cáp có thể dính
    /// muộn vài khung, mà kéo lại một dây đang nối đúng thì làm rơi nó ra.
    /// </summary>
    private AttachResult DragVerified(WireReader reader, WireRound round,
                                      Blob?[] before, int i, int j, CancellationToken ct)
    {
        var w = _cfg.Wire;
        var deadline = Stopwatch.StartNew();
        double last = 0.0;

        // (panel con khong, da dinh chua). Doc `frames` khung roi lay trung vi.
        (bool Closed, bool Attached) Probe(int frames, int gapMs)
        {
            var vals = new List<double>(frames);
            var rect = reader.PanelRect.IsEmpty ? round.Panel : reader.PanelRect;

            for (int f = 0; f < frames; f++)
            {
                if (f > 0) Sleep(ct, gapMs);
                var (present, blob) = reader.ProbeTarget(round, rect, j);
                if (!present) return (true, false);
                vals.Add(WireReader.GeometryScore(before[j], blob));
            }

            if (vals.Count == 0) return (false, false);

            last = Median(vals);
            int above = vals.Count(v => v >= w.PostDragAcceptGeom);
            bool strong = vals.Max() >= w.PostDragFastGeom;

            // Trung vi la chot chinh. Mot khung RAT manh cung tinh, nhung chi khi da co it nhat
            // mot khung khac tren nguong thuong va khung cuoi khong tut han ve 1.0.
            bool ok = last >= w.PostDragAcceptGeom || (strong && above >= 1 && vals[^1] >= 1.18);
            return (false, ok);
        }

        var pre = Probe(1, 0);
        if (pre.Closed) return AttachResult.Closed;
        if (pre.Attached && last >= w.PreDragSkipGeom)
        {
            Emit($"{round.Sources[i]}→{round.Targets[j]} đã nối sẵn, không kéo thừa ({last:F2})");
            return AttachResult.Attached;
        }

        for (int k = 1; k <= w.DragMaxRetries; k++)
        {
            ct.ThrowIfCancellationRequested();
            if (deadline.ElapsedMilliseconds >= w.DragAcceptTimeoutMs) break;

            if (k > 1)
            {
                var late = Probe(w.PreRetryProbeFrames, w.PreRetryProbeGapMs);
                if (late.Closed) return AttachResult.Closed;
                if (late.Attached && last >= w.PreDragSkipGeom)
                {
                    Emit($"{round.Sources[i]}→{round.Targets[j]} dính muộn ({last:F2}) — bỏ lần kéo thừa");
                    return AttachResult.Attached;
                }
            }

            // Do lai hop panel sat truoc khi bam: man rung thi toa do cu tro ra ngoai panel.
            var livePanel = reader.FindPanel();
            if (livePanel.IsEmpty) return AttachResult.Closed;

            WaitWindow(ct);
            Drag(livePanel, round, i, j, retry: k > 1);
            Sleep(ct, w.DragAttachVerifyMs);

            var after = Probe(w.PostDragConfirmFrames, w.PostDragConfirmGapMs);
            if (after.Closed) return AttachResult.Closed;
            if (after.Attached)
            {
                if (k > 1) Emit($"{round.Sources[i]}→{round.Targets[j]} dính sau {k} lần kéo ({last:F2})");
                return AttachResult.Attached;
            }

            // KHONG keo lai ngay. Cho ve/nhan input mot nhip roi doc lai mot lan nua, khong bam gi.
            Sleep(ct, Math.Min(60, w.DragRetryPauseMs));
            var settled = Probe(w.PreRetryProbeFrames, w.PreRetryProbeGapMs);
            if (settled.Closed) return AttachResult.Closed;
            if (settled.Attached)
            {
                Emit($"{round.Sources[i]}→{round.Targets[j]} đã nối ({last:F2}) — không kéo lần {k + 1}");
                return AttachResult.Attached;
            }

            Emit($"{round.Sources[i]}→{round.Targets[j]} chưa dính ({last:F2}), kéo lại {k}/{w.DragMaxRetries}");
            Sleep(ct, Math.Min(140, w.DragRetryPauseMs + 8 * Math.Min(k, 8)));
        }

        return AttachResult.NotAttached;
    }

    private void Drag(Rectangle panel, WireRound round, int i, int j, bool retry)
    {
        var from = WireRound.PointIn(panel, round.SourceFrac[i]);
        var to = WireRound.PointIn(panel, round.TargetFrac[j]);

        var w = _cfg.Wire;
        int steps = retry ? w.RetryDragSteps : w.DragSteps;
        int ms = retry ? w.RetryDragMs : w.DragMs;
        int hold = retry ? 3 : 1;

        InputSender.DragEased(from, to, steps, ms, preDownMs: hold, downHoldMs: hold, preUpMs: hold);
    }

    /// <summary>
    /// Đưa con trỏ ra ngoài panel trước khi game kiểm tra, để nó không hover lên dây nào — con
    /// trỏ đứng trên một đầu dây có thể làm game vẽ trạng thái hover và mắt đọc thành "đã nở ra".
    /// </summary>
    private static void ParkCursor(Rectangle panel)
    {
        InputSender.MoveCursorOnly(Math.Max(5, panel.Left - 35), Math.Max(5, panel.Top - 35));
    }

    // ---------------------------------------------------------------- doc phan hoi

    /// <summary>
    /// Chờ và đọc phản hồi sau cú cắm cuối.
    ///
    /// Chỗ khó không phải đọc điểm mà là biết game ĐÃ kiểm tra hay chưa. Ba bằng chứng, lấy theo
    /// bản Python:
    ///   1. Một dây đã xác nhận dính TRƯỚC khi cắm cú cuối giờ rời ra → chắc chắn đã kiểm tra.
    ///   2. Dây cuối đã từng dính rồi giờ có dây nào rời ra → đã kiểm tra.
    ///   3. Ca không được phép spam: mọi dây trước vẫn dính, chỉ dây cuối rời — chờ đủ
    ///      <see cref="WireSettings.SubmitAssumeCheckMs"/> thì coi như đã kiểm tra và cú cuối sai.
    ///      Bản v4 của họ gọi ca này là "input lock" rồi cắm lại mãi.
    /// </summary>
    private (bool Closed, int? Mask, string How) WaitFeedback(
        WireReader reader, WireRound round, Blob?[] before,
        WirePolicy policy, IReadOnlyList<int> candidates, int fixedMask,
        int guessIdx, List<int> activeIdx, WirePolicy.FeedbackThresholds th,
        CancellationToken ct)
    {
        var w = _cfg.Wire;
        var guess = policy.Permutation(guessIdx);
        int lastI = activeIdx[^1];

        var rows = new List<double[]>();
        var sw = Stopwatch.StartNew();
        bool lastWasHigh = false;
        bool checkEvidence = false;
        string how = "chưa đủ bằng chứng game đã kiểm tra";

        while (sw.ElapsedMilliseconds < w.FeedbackTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            var rect = reader.PanelRect.IsEmpty ? round.Panel : reader.PanelRect;
            var (present, now) = reader.ReadTargetBlobs(round, rect);
            if (!present) return (true, null, "panel đóng");

            var scores = WireReader.GeometryScores(before, now);

            // scoreBySource[i] = diem cua o cam ma day i vua cam vao.
            var bySource = new double[round.Count];
            for (int i = 0; i < round.Count; i++) bySource[i] = scores[guess[i]];

            rows.Add(bySource);
            if (rows.Count > w.FeedbackStableFrames) rows.RemoveAt(0);

            long elapsed = sw.ElapsedMilliseconds;
            if (bySource[lastI] >= w.SubmitAttachGeom) lastWasHigh = true;

            bool anyPreLow = activeIdx.Take(activeIdx.Count - 1).Any(i => bySource[i] <= w.LockGeomLow);
            bool anyActiveLow = activeIdx.Any(i => bySource[i] <= w.LockGeomLow);

            if (elapsed >= w.FeedbackMinMs && anyPreLow) checkEvidence = true;
            if (elapsed >= w.FeedbackMinMs && lastWasHigh && anyActiveLow) checkEvidence = true;

            if (elapsed >= w.SubmitAssumeCheckMs && activeIdx.Count > 1)
            {
                bool preAllHigh = activeIdx.Take(activeIdx.Count - 1)
                                           .All(i => bySource[i] >= w.LockGeomHigh);
                if (preAllHigh && bySource[lastI] <= w.LockGeomLow) checkEvidence = true;
            }

            if (checkEvidence && rows.Count >= w.FeedbackStableFrames)
            {
                var med = MedianRows(rows, round.Count);
                var (mask, margin, method) = policy.InferResponse(candidates, fixedMask, guessIdx, med, th);
                if (mask is not null)
                    return (false, mask, $"{method}, cách biệt {margin:F2}");

                how = $"{method} (cách biệt {margin:F2})";
            }

            Sleep(ct, Math.Max(w.FeedbackPollMs, w.FeedbackStableGapMs));
        }

        return (false, null, how);
    }

    private static double[] MedianRows(List<double[]> rows, int n)
    {
        var outp = new double[n];
        var col = new List<double>(rows.Count);
        for (int i = 0; i < n; i++)
        {
            col.Clear();
            foreach (var r in rows) col.Add(r[i]);
            outp[i] = Median(col);
        }
        return outp;
    }

    private static double Median(List<double> vals)
    {
        if (vals.Count == 0) return 0.0;
        var sorted = vals.ToArray();
        Array.Sort(sorted);
        int m = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2.0;
    }

    // ---------------------------------------------------------------- chung

    /// <summary>
    /// Chặn tới khi game là cửa sổ foreground. Trả về true nếu đã PHẢI chờ — người gọi dùng nó để
    /// reset mốc thời gian, vì thời gian alt-tab không phải thời gian bot không thấy panel.
    /// Cùng khuôn <see cref="WoodBot"/>: job này cũng không giữ phím nào nên chặn là an toàn.
    /// </summary>
    private bool WaitWindow(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) return false;

        bool waited = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var title = Native.ForegroundTitle();
            if (title.Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase))
            {
                if (_windowWarned)
                {
                    Emit("game đã focus lại — chạy tiếp");
                    _windowWarned = false;
                }
                return waited;
            }

            if (!_windowWarned)
            {
                Emit($"tạm dừng: chưa focus “{_cfg.WindowMatch}” (đang focus: “{title}”)");
                _windowWarned = true;
            }
            waited = true;
            Sleep(ct, 200);
        }
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
    }

    private void Emit(string line) => Log?.Invoke(line);
}
