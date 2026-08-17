using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum FishingStopReason
{
    UserStopped,
    MissingRegions,
    TrunkDump,
    Error,
    /// <summary>Cốp đầy và ba lô cũng đầy — hết chỗ chứa, phiên đã chạy hết mức. Không phải lỗi.</summary>
    BagFull
}

/// <summary>
/// Hết chỗ chứa: cốp đầy, ba lô cũng đầy. Ném ra để cắt vòng câu từ chỗ sâu trong
/// <see cref="FishingBot.MaybeDump"/>, y như <see cref="TrunkStepException"/> — khác ở chỗ đây
/// là kết thúc BÌNH THƯỜNG của một phiên, nên UI báo xanh chứ không báo đỏ.
/// </summary>
internal sealed class BagFullException : Exception
{
    public BagFullException(string message) : base(message) { }
}

/// <summary>
/// Vòng câu: Tap 4 + Space → chờ cá cắn → giữ S → nhả khi đầy / HUD tắt → 4 lại.
/// FailNotice chỉ recast khi HUD đóng (ô thông báo dễ false-positive lúc đang kéo).
/// </summary>
internal sealed class FishingBot
{
    private const ushort VK_4 = 0x34;
    private const ushort VK_S = 0x53;
    private const ushort VK_SPACE = 0x20;

    private readonly FishingConfig _cfg;
    private readonly Screen _screen;
    private readonly FishingProfile _profile;
    private CancellationTokenSource _cts;
    private Thread _thread;
    private bool _holdingS;
    private bool _windowWarned;

    private TrunkDumper _dumper;
    private int _catches;
    private int _catchesSinceDump;

    /// <summary>KG ba lô lần cân trước, chỉ dùng ở chặng cuối phiên. -1 = chưa cân lần nào.</summary>
    private double _endgameLastKg = -1;
    /// <summary>Mấy con liên tiếp mà KG ba lô không nhúc nhích.</summary>
    private int _endgameFlat;

    public FishingBot(FishingConfig cfg, Screen screen, FishingProfile profile)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = profile;
    }

    public bool Running => _thread is { IsAlive: true };

    public event Action<string> Log;
    public event Action<FishingSnapshot> SnapshotReady;
    public event Action<FishingStopReason, string> Stopped;

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "FishingBot" };
        _thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>
    /// Huỷ rồi CHỜ luồng bot chết hẳn. <see cref="Stop"/> chỉ báo CTS và trả về ngay, nên nếu
    /// người gọi nhả phím ngay sau đó thì luồng bot còn sống vẫn kịp bấm lại — phím kẹt xuống
    /// dù panel đã báo "đã dừng". Hết thời gian chờ thì thôi, không treo UI.
    /// </summary>
    public void StopAndWait(int ms = 1500)
    {
        _cts?.Cancel();
        var t = _thread;
        if (t is null || !t.IsAlive) return;
        try { t.Join(ms); } catch { }
    }

    public static string TenLyDo(FishingStopReason r) => r switch
    {
        FishingStopReason.UserStopped => "người dùng bấm dừng",
        FishingStopReason.MissingRegions => "chưa khoanh thanh / cá",
        FishingStopReason.TrunkDump => "đổ cốp thất bại",
        FishingStopReason.BagFull => "cốp đầy, ba lô đầy — đi bán cá",
        _ => "lỗi"
    };

    private void Run(CancellationToken ct)
    {
        var reason = FishingStopReason.UserStopped;
        string message = "người dùng bấm dừng";

        try
        {
            if (_profile is null || !_profile.Bar.IsSet || !_profile.Fish.IsSet)
                throw new InvalidOperationException("cần khoanh thanh và cá trước khi chạy");

            using var reader = new FishingReader(_cfg, _screen, _profile);
            if (reader.FishTemplateProblem is { } fp)
                throw new InvalidOperationException("mẫu cá: " + fp);

            if (!_profile.Reject.IsSet)
                Emit("cảnh báo: chưa khoanh thông báo — recast chỉ theo timeout");
            else if (reader.RejectTemplateProblem is { } rp)
                Emit("cảnh báo: mẫu thông báo — recast chỉ theo timeout (" + rp + ")");

            if (!_profile.Keep.IsSet)
                Emit("cảnh báo: chưa khoanh CẤT VÀO — sau khi câu được sẽ chỉ bấm 4, không nhận cá");
            else if (reader.KeepTemplateProblem is { } kp)
                Emit("cảnh báo: CẤT VÀO — sẽ click ô cố định, không dò được (" + kp + ")");
            else
                Emit($"dò CẤT VÀO trong vùng {reader.KeepBandRegion.Width}×{reader.KeepBandRegion.Height} " +
                     $"@ {reader.KeepBandRegion.X},{reader.KeepBandRegion.Y}, màu nền nút " +
                     $"#{reader.KeepColor.R:X2}{reader.KeepColor.G:X2}{reader.KeepColor.B:X2} ±{_cfg.KeepColorTol}");

            Emit($"bắt đầu. chờ cắn {_cfg.WaitBiteMs} ms, giữ S tối đa {_cfg.FightTimeoutMs} ms, " +
                 $"xong khi fill ≥ {_cfg.DoneFill01:0.00}");
            Emit(_cfg.CastConfirmMs > 0
                ? $"xác minh thả câu: thanh không hiện sau {_cfg.CastConfirmMs} ms thì thả lại " +
                  $"(tối đa {_cfg.CastConfirmRetries} lần)"
                : "xác minh thả câu: TẮT — thả trượt sẽ phải chờ hết thời gian chờ cắn");
            Emit($"{HotkeyText.Job()} = bật/tắt. Cửa sổ game phải đang focus (" + _cfg.WindowMatch + ").");
            Emit($"mỗi lần 4 sẽ bấm Space sau {_cfg.CastSpaceDelayMs} ms — tắt hotkey 4 trong AutoHotkey.");

            SetUpDumper();

            Cast(ct, "thả câu");

            int biteFrames = 0;
            bool fighting = false;
            bool sawHud = false;

            // Thanh câu hiện = dây đang dưới nước = cú thả câu đã ăn. Không thấy nó sau
            // CastConfirmMs thì cú thả trượt, thả lại luôn thay vì chờ hết WaitBiteMs.
            bool sawCastHud = false;
            int castRetries = 0;

            var waitSw = Stopwatch.StartNew();
            var fightSw = new Stopwatch();
            var ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                WaitWindow(ct);

                var snap = reader.Read();
                SnapshotReady?.Invoke(snap);

                if (!fighting)
                {
                    if (snap.UiOpen) sawCastHud = true;

                    if (snap.FishBite) biteFrames++;
                    else biteFrames = 0;

                    if (biteFrames >= _cfg.BiteDebounceFrames)
                    {
                        Emit($"cá cắn (ncc={snap.FishScore:F3}) — giữ S");
                        HoldS();
                        fighting = true;
                        sawHud = snap.UiOpen;
                        fightSw.Restart();
                        continue;
                    }

                    bool rejectOk = snap.FailNotice && !snap.UiOpen && DateTime.UtcNow >= ignoreFailUntil;
                    if (rejectOk)
                    {
                        Emit($"chê mồi (ncc={snap.RejectScore:F3}, HUD đóng) — câu lại");
                        Sleep(ct, _cfg.RejectRecastMs);
                        Cast(ct, "câu lại", waitRelease: false);
                        biteFrames = 0;
                        sawCastHud = false;
                        castRetries = 0;
                        waitSw.Restart();
                        ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                        continue;
                    }

                    // Thanh câu chưa từng hiện => dây chưa xuống nước, cú thả vừa rồi trượt.
                    // Bắt sớm ở đây để khỏi đứng chờ trọn WaitBiteMs cho một cú thả không tồn tại.
                    // BarConfigured là bắt buộc: chưa khoanh thanh thì UiOpen luôn false và
                    // lượt nào cũng bị kết luận trượt. Panel chặn Start khi thiếu, nhưng điều
                    // kiện đó ở xa nên chốt lại ngay tại chỗ dùng.
                    bool castMissed = !sawCastHud
                                      && snap.BarConfigured
                                      && _cfg.CastConfirmMs > 0
                                      && castRetries < _cfg.CastConfirmRetries
                                      && waitSw.ElapsedMilliseconds >= _cfg.CastConfirmMs;
                    if (castMissed)
                    {
                        castRetries++;
                        Emit($"thanh câu không hiện sau {_cfg.CastConfirmMs} ms — thả câu trượt, " +
                             $"thả lại (lần {castRetries}/{_cfg.CastConfirmRetries})");
                        Cast(ct, "thả lại (trượt)", waitRelease: false);
                        biteFrames = 0;
                        waitSw.Restart();
                        ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                        continue;
                    }

                    if (waitSw.ElapsedMilliseconds >= _cfg.WaitBiteMs)
                    {
                        Emit($"hết {_cfg.WaitBiteMs} ms không cắn — câu lại" +
                             $" (thanh={(sawCastHud ? "đã mở" : "chưa mở lần nào")}" +
                             $" fill={snap.BlueFill01 * 100:0.0}% cá={snap.FishScore:F3}" +
                             $" chê={snap.RejectScore:F3})");
                        Cast(ct, "câu lại (timeout)");
                        biteFrames = 0;
                        sawCastHud = false;
                        castRetries = 0;
                        waitSw.Restart();
                        ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                    }
                }
                else
                {
                    if (snap.UiOpen) sawHud = true;

                    bool full = snap.BlueFill01 >= _cfg.DoneFill01;
                    bool hudGone = sawHud && !snap.UiOpen;
                    if (full || hudGone)
                    {
                        Emit(full
                            ? $"xong — fill {snap.BlueFill01 * 100:0.0}%"
                            : "xong — HUD tắt");
                        CollectThenCast(reader, ct);
                        fighting = false;
                        sawHud = false;
                        biteFrames = 0;
                        sawCastHud = false;
                        castRetries = 0;
                        waitSw.Restart();
                        ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                        continue;
                    }

                    if (fightSw.ElapsedMilliseconds >= _cfg.FightTimeoutMs)
                    {
                        Emit($"giữ S quá {_cfg.FightTimeoutMs} ms — nhả và câu lại");
                        Cast(ct, "câu lại (timeout kéo)");
                        fighting = false;
                        sawHud = false;
                        biteFrames = 0;
                        sawCastHud = false;
                        castRetries = 0;
                        waitSw.Restart();
                        ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                    }
                }

                Sleep(ct, _cfg.PollMs);
            }
        }
        catch (OperationCanceledException)
        {
            reason = FishingStopReason.UserStopped;
            message = "người dùng bấm dừng";
        }
        catch (InvalidOperationException ex)
        {
            reason = FishingStopReason.MissingRegions;
            message = ex.Message;
            Emit(message);
        }
        catch (BagFullException ex)
        {
            reason = FishingStopReason.BagFull;
            message = ex.Message;
            Emit("--- xong phiên: " + ex.Message + " ---");
        }
        catch (TrunkStepException ex)
        {
            reason = FishingStopReason.TrunkDump;
            message = ex.Message;
            Emit("dừng vì đổ cốp: " + ex.Message);
        }
        catch (Exception ex)
        {
            reason = FishingStopReason.Error;
            message = ex.Message;
            Emit("lỗi: " + ex.Message);
        }
        finally
        {
            ReleaseS();
            HeldKeys.ReleaseAll();
            _dumper?.Dispose();
            _dumper = null;
            Stopped?.Invoke(reason, message);
        }
    }

    private void CollectThenCast(FishingReader reader, CancellationToken ct)
    {
        ReleaseS();
        Sleep(ct, _cfg.KeepAppearMs);

        if (!_profile.Keep.IsSet)
        {
            Cast(ct, "thả câu", waitRelease: false);
            return;
        }

        var found = WaitForKeep(reader, out bool configured, ct);
        if (found is null)
        {
            if (!configured)
            {
                // Thiếu mẫu/vùng thì lượt nào cũng trượt, click mù sẽ thành đấm liên tục.
                Emit("thiếu mẫu/vùng CẤT VÀO — bỏ qua, không click mù (vào Cấu hình khoanh lại)");
            }
            else if (_cfg.BlindKeepClick != true)
            {
                Emit($"không dò được nút trong {_cfg.WaitKeepMs} ms — bỏ qua (BlindKeepClick tắt)");
            }
            else
            {
                // Không dò được: về cách cũ, click ô đã khoanh. Đúng với con cá tên ngắn.
                var abs = FishingConfig.ToAbsolute(_screen, _profile.Keep);
                Emit($"không dò được nút trong {_cfg.WaitKeepMs} ms — click ô đã khoanh");
                ClickKeep(new Point(abs.Left + abs.Width / 2, abs.Top + abs.Height / 2), ct);
            }
        }
        else
        {
            Emit($"thấy nút {found.KeepRect.Width}×{found.KeepRect.Height} @ {found.KeepRect.X},{found.KeepRect.Y}" +
                 $"  dens={found.KeepDensity:F2}  ncc={found.KeepScore:F3}");
            ClickKeep(found.KeepClick, ct);

            var anchor = found.KeepRect;
            for (int i = 0; i < _cfg.KeepClickRetries; i++)
            {
                var still = WaitForKeepGone(reader, anchor, ct);
                if (still is null) break;
                Emit($"nút vẫn còn sau {_cfg.KeepGoneMs} ms — click lại (lần {i + 1}/{_cfg.KeepClickRetries})");
                ClickKeep(still.KeepClick, ct);
                anchor = still.KeepRect;
            }
        }

        try { SnapshotReady?.Invoke(reader.Read()); } catch { }
        MaybeDump(ct);

        // Mặc định 0 — xem chú thích AfterKeepCastMs. Chờ ở đây là cách phòng hờ cho việc
        // animation cất cá nuốt mất phím 4; cách bắt sau khi đã trượt nằm ở vòng lặp chính.
        Sleep(ct, _cfg.AfterKeepCastMs);
        Cast(ct, "thả câu", waitRelease: false);
    }

    private void SetUpDumper()
    {
        if (!_profile.TrunkDumpEnabled) return;

        _dumper = TrunkDumper.Create(_cfg, _screen, _profile, Emit, out string problem);
        if (_dumper is null)
        {
            Emit("KHÔNG bật được đổ cốp: " + problem + " — vẫn câu bình thường");
            return;
        }

        string missing = _dumper.AtlasMissing;
        Emit(missing.Length == 0
            ? $"đổ cốp: bật. Kiểm tra KG mỗi {_cfg.WeightCheckEveryCatches} con, " +
              $"đổ khi ≥ {_cfg.BagCapKg - _cfg.DumpMarginKg:F1} kg hoặc khi chỗ cá sắp không lọt cốp"
            : $"đổ cốp: bật, nhưng thiếu mẫu chữ số {missing} — chạy theo đếm cá " +
              $"(mỗi {_cfg.CatchesPerDumpFallback} con)");

        if (_cfg.DumpEveryCatches > 0)
            Emit($"đổ cốp: trần cứng mỗi {_cfg.DumpEveryCatches} con, dù ba lô còn nhẹ");

        Emit(_cfg.TrunkTightKg > 0
            ? $"cốp còn trống ≤ {_cfg.TrunkTightKg:F0} kg thì đổ sau MỖI con, để cụm cá đủ nhỏ " +
              "mà lọt nốt chỗ trống cuối"
            : "dồn đổ khi cốp sắp đầy: TẮT (TrunkTightKg = 0)");
        Emit($"cốp đầy hẳn thì thôi mở cốp, câu tiếp tới khi ba lô ≥ {_cfg.BagFullStopKg:F1} kg " +
             "rồi dừng phiên");
    }

    /// <summary>
    /// Chỗ DUY NHẤT được phép đổ cốp: sau khi nút CẤT VÀO đã tắt và trước cú thả câu kế tiếp.
    /// Không bao giờ chen vào lúc đang giữ S kéo cá — mất cá là nhẹ, giữ S suốt cả lượt đổ cốp
    /// mới là hỏng.
    /// </summary>
    private void MaybeDump(CancellationToken ct)
    {
        if (_dumper is null) return;

        _catches++;
        _catchesSinceDump++;

        // Cop day roi thi khong con gi de do: chi con viec chat day ba lo roi dung.
        if (_dumper.TrunkFull) { WatchBagUntilFull(ct); return; }

        // Tran cung theo so con: cat nho moi luot keo. Thu lam hong khong phai cop day ma la
        // MOT CUM qua nang — cum 13 con nang 22.7 kg thi cop con 9.9 kg la chac chan khong lot.
        bool byCount = _cfg.DumpEveryCatches > 0 && _catchesSinceDump >= _cfg.DumpEveryCatches;

        // Cop sap day thi nhin lai sau MOI con. Cho phi trong cop bang dung can nang cum ca
        // khong lot duoc, ma cum to bao nhieu la do khoang cach giua hai lan nhin: nhin sau 5
        // con thi cum 8.75 kg va cop con 5 kg la bo trang 5 kg, nhin sau moi con thi cum chi
        // 1.75 kg va cop chi phi dung mot con.
        bool tight = _cfg.TrunkTightKg > 0
                     && _dumper.TrunkFreeKg >= 0
                     && _dumper.TrunkFreeKg <= _cfg.TrunkTightKg;

        int every = tight ? 1 : Math.Max(1, _cfg.WeightCheckEveryCatches);
        if (!byCount && _catches % every != 0) return;

        if (_dumper.OcrHealthy)
        {
            var w = _dumper.PeekBagWeight(ct);
            if (w.Ok)
            {
                double full = _cfg.BagCapKg - _cfg.DumpMarginKg;
                double fishKg = _dumper.PendingFishKg(w.Value);
                double free = _dumper.TrunkFreeKg;

                bool bagFull = w.Value >= full;
                // Do TRUOC khi cho ca vuot qua cho trong cua cop: qua roi thi cum ca khong con
                // lot vao dau duoc nua va chuyen di ban ca la bat buoc.
                bool wontFit = fishKg >= 0 && free >= 0 && fishKg >= free - _cfg.DumpMarginKg;
                // Che do don: co ca la do, khong doi ba lo nang. Chinh viec do som moi giu duoc
                // cum nho. fishKg < 0 (chua biet) thi cu de duong cu quyet dinh.
                bool tightNow = tight && fishKg > 0;

                Emit($"ba lô {w.Value:F1}/{w.Cap:F0} kg" +
                     (fishKg >= 0 ? $", chỗ cá ≈ {fishKg:F1} kg" : "") +
                     (free >= 0 ? $", cốp còn {free:F1} kg" : "") +
                     $"  (đổ khi ≥ {full:F1} kg" + (wontFit ? ", hoặc sắp không lọt cốp" : "") +
                     (tightNow ? ", cốp sắp đầy nên đổ từng con" : "") + ")");

                if (!bagFull && !wontFit && !byCount && !tightNow) return;
            }
            else if (!byCount && _catchesSinceDump < _cfg.CatchesPerDumpFallback)
            {
                return;   // doc hong nhung chua toi nguong dem ca — cau tiep
            }
        }
        else if (!byCount && _catchesSinceDump < _cfg.CatchesPerDumpFallback)
        {
            return;
        }

        Emit("--- đổ cá vào cốp ---");
        var r = _dumper.Dump(ct);
        _catchesSinceDump = 0;

        if (r == DumpResult.Ok)
        {
            Emit("--- đổ xong, câu tiếp ---");
            return;
        }

        if (r == DumpResult.TrunkFull)
        {
            Emit(_dumper.TrunkFull
                ? "--- cốp đầy, từ giờ chỉ chất vào ba lô ---"
                : "--- lượt này cốp không nhận, câu tiếp rồi thử lại một lượt nữa ---");
            return;
        }

        // Khong thay o ca nao ma ba lo van bao gan day: khong the cau tiep, se cau vao cai
        // ba lo day ma log van trong binh thuong.
        throw new TrunkStepException(
            "ba lô gần đầy nhưng mọi ô chứa cá đã khai báo đều trống — " +
            "cá nằm ở ô khác, vào Chọn ô chứa cá thêm ô đó");
    }

    /// <summary>
    /// Chặng cuối phiên: cốp đã đầy nên không mở cốp nữa, chỉ canh ba lô đầy tới đâu rồi dừng.
    ///
    /// Cân sau MỖI con chứ không giãn ra như lúc còn đổ cốp: chặng này chỉ dài vài con, và
    /// ngưỡng dừng nằm sát trần nên đo thưa là câu lố vào cái ba lô đã hết chỗ.
    /// </summary>
    private void WatchBagUntilFull(CancellationToken ct)
    {
        // Khong do duoc thi dung luon. Cau tiep luc nay la cau mu: khong con cop de do, cung
        // khong biet ba lo con bao nhieu cho — ca cat khong vao ma log van trong binh thuong.
        if (!_dumper.OcrHealthy)
            throw new BagFullException(
                "cốp đầy mà đọc KG ba lô cũng hỏng — dừng cho chắc, đi bán cá rồi bật lại");

        // Mot lan doc hong khong dung phien: PeekBagWeight tu dem, hong lien tiep du nhieu thi
        // OcrHealthy tat va cua tren dung ho. Dung ngay o lan hong dau la vut ca phien vi mot
        // khung hinh xau.
        var w = _dumper.PeekBagWeight(ct);
        if (!w.Ok)
        {
            Emit("cốp đầy — lần cân này hỏng, câu tiếp rồi cân lại");
            return;
        }

        if (w.Value >= _cfg.BagFullStopKg)
            throw new BagFullException(
                $"cốp đầy và ba lô đã {w.Value:F1}/{w.Cap:F0} kg — xong phiên, đi bán cá");

        // Ba lo dung lai duoi nguong van la ba lo day: con ca ke tiep nang hon cho con lai thi
        // game khong cho cat, KG dung yen va nguong dung khong bao gio toi. Khong bat cai nay
        // thi bot cau den sang van thay "chua du 29 kg".
        if (_endgameLastKg >= 0 && w.Value <= _endgameLastKg + 0.05)
        {
            _endgameFlat++;
            if (_endgameFlat >= 2)
                throw new BagFullException(
                    $"câu thêm {_endgameFlat} con mà ba lô vẫn {w.Value:F1}/{w.Cap:F0} kg — " +
                    "cá không cất vào được nữa, xong phiên, đi bán cá");
        }
        else _endgameFlat = 0;
        _endgameLastKg = w.Value;

        Emit($"cốp đầy — ba lô {w.Value:F1}/{w.Cap:F0} kg, câu tiếp tới {_cfg.BagFullStopKg:F1} kg");
    }

    /// <summary>
    /// Chờ dò được nút, tối đa <see cref="FishingConfig.WaitKeepMs"/>. Null = không thấy.
    /// Panel hiện chậm hay nhanh tùy con cá nên không thể click theo một mốc thời gian cố định.
    /// <paramref name="configured"/> false = thiếu mẫu/vùng, khác hẳn với "chờ mãi không thấy":
    /// bên gọi phải cấm click mù, vì lượt nào cũng sẽ trượt.
    /// </summary>
    private FishingSnapshot WaitForKeep(FishingReader reader, out bool configured, CancellationToken ct)
    {
        configured = true;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            WaitWindow(ct);

            var snap = reader.Read();
            SnapshotReady?.Invoke(snap);

            if (snap.KeepVisible) return snap;
            if (!snap.KeepConfigured)                   // thiếu mẫu/vùng — poll thêm cũng vô ích
            {
                configured = false;
                return null;
            }
            if (sw.ElapsedMilliseconds >= _cfg.WaitKeepMs) return null;

            Sleep(ct, _cfg.PollMs);
        }
    }

    /// <summary>
    /// Chờ nút tắt sau khi click, tối đa <see cref="FishingConfig.KeepGoneMs"/>.
    /// Null = đã tắt; khác null = vẫn còn, kèm toạ độ mới để click lại.
    ///
    /// <paramref name="anchor"/> là ô nút của cú click vừa rồi. Khối dò được mà nhảy ra xa
    /// quá <see cref="FishingConfig.KeepAnchorTolPx"/> thì đó không còn là cái nút cũ nữa —
    /// panel đã tắt và bộ dò màu đang bắt nhầm thứ khác trong dải quét. Click theo nó là
    /// click thẳng vào thế giới game, tức là đấm người đứng cạnh.
    /// </summary>
    private FishingSnapshot WaitForKeepGone(FishingReader reader, Rectangle anchor, CancellationToken ct)
    {
        if (_cfg.KeepGoneMs <= 0 || _cfg.KeepAnchorTolPx <= 0) return null;

        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var snap = reader.Read();
            SnapshotReady?.Invoke(snap);

            if (!snap.KeepVisible) return null;
            if (!NearAnchor(snap.KeepRect, anchor))
            {
                Emit($"nút “còn” nhưng lệch chỗ @ {snap.KeepRect.X},{snap.KeepRect.Y}" +
                     $" (nút cũ @ {anchor.X},{anchor.Y}) — coi như đã tắt, bỏ click lại");
                return null;
            }
            if (sw.ElapsedMilliseconds >= _cfg.KeepGoneMs) return snap;

            Sleep(ct, _cfg.PollMs);
        }
    }

    /// <summary>Tâm hai ô cách nhau trong <see cref="FishingConfig.KeepAnchorTolPx"/> pixel.</summary>
    private bool NearAnchor(Rectangle hit, Rectangle anchor)
    {
        if (hit.IsEmpty || anchor.IsEmpty) return false;
        int tol = _cfg.KeepAnchorTolPx;
        int dx = (hit.Left + hit.Width / 2) - (anchor.Left + anchor.Width / 2);
        int dy = (hit.Top + hit.Height / 2) - (anchor.Top + anchor.Height / 2);
        return Math.Abs(dx) <= tol && Math.Abs(dy) <= tol;
    }

    /// <summary>
    /// Rê chuột bằng MoveCursorOnly chứ không MoveSmooth: MoveSmooth bắn kèm
    /// MOUSEEVENTF_MOVE mà GTA đọc thành lệnh xoay camera (xem InputSender.MoveCursorOnly),
    /// nên mỗi lần cất cá là góc nhìn bị kéo lệch một nhát. TrunkOpener và DragSmooth đã
    /// chuyển từ trước, chỗ này sót lại.
    /// </summary>
    private void ClickKeep(Point p, CancellationToken ct)
    {
        WaitWindow(ct);
        Emit($"click CẤT VÀO @ {p.X},{p.Y}");
        InputSender.MoveCursorOnlySmooth(p.X, p.Y, _cfg.KeepMoveSteps);
        Sleep(ct, _cfg.KeepHoverMs);
        InputSender.LeftDown();
        Sleep(ct, 60);
        InputSender.LeftUp();
    }

    private void Cast(CancellationToken ct, string why, bool waitRelease = true)
    {
        ReleaseS();
        if (waitRelease)
            Sleep(ct, _cfg.AfterReleaseMs);
        WaitWindow(ct);
        InputSender.TapKey(VK_4);
        Sleep(ct, _cfg.CastSpaceDelayMs);
        InputSender.TapKey(VK_SPACE);
        Emit("bấm 4 + space — " + why);
    }

    private void HoldS()
    {
        if (_holdingS) return;
        InputSender.KeyDown(VK_S);
        _holdingS = true;
    }

    private void ReleaseS()
    {
        if (!_holdingS) return;
        try { InputSender.KeyUp(VK_S); } catch { }
        _holdingS = false;
    }

    private void WaitWindow(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.WindowMatch)) return;
        while (!ct.IsCancellationRequested)
        {
            var title = Native.ForegroundTitle();
            if (title.Contains(_cfg.WindowMatch, StringComparison.OrdinalIgnoreCase))
            {
                _windowWarned = false;
                return;
            }
            if (!_windowWarned)
            {
                Emit($"chờ cửa sổ “{_cfg.WindowMatch}” (đang focus: “{title}”) — click vào game");
                _windowWarned = true;
            }
            Sleep(ct, 200);
        }
        ct.ThrowIfCancellationRequested();
    }

    private static void Sleep(CancellationToken ct, int ms)
    {
        if (ms <= 0) return;
        if (ct.WaitHandle.WaitOne(ms))
            throw new OperationCanceledException();
    }

    private void Emit(string line) => Log?.Invoke(line);
}
