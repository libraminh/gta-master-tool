using System.Diagnostics;

namespace GtaMiniGameBot;

internal enum FishingStopReason
{
    UserStopped,
    MissingRegions,
    TrunkDump,
    Error,
    /// <summary>Cốp đầy và ba lô cũng đầy — hết chỗ chứa, phiên đã chạy hết mức. Không phải lỗi.</summary>
    BagFull,
    /// <summary>
    /// Game báo "không có cá nào phù hợp với cần và độ sâu câu của bạn" nhiều lượt liên tiếp.
    /// Không phải lỗi của bot mà là sai trang bị / sai chỗ câu — người dùng phải đổi cần hoặc
    /// đổi chỗ, câu tiếp bao nhiêu cũng vô ích.
    /// </summary>
    NoFishMatch,
    /// <summary>
    /// Game báo "Khu vực này hiện không có cá để câu" nhiều lượt liên tiếp. Cùng loại với
    /// <see cref="NoFishMatch"/> — khác ở chỗ phải đổi CHỖ CÂU chứ không phải đổi cần.
    /// </summary>
    NoFishArea,
    /// <summary>
    /// Game báo "Bạn không đứng gần mặt nước" nhiều lượt liên tiếp. Nhân vật đã rời mép nước
    /// thật — thả lại bao nhiêu cũng vô ích cho tới khi có người lái xe/đi bộ lại chỗ câu.
    /// </summary>
    NoWater
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
/// Game liên tục báo "không câu được ở đây" — hoặc "không có cá nào phù hợp với cần và độ sâu",
/// hoặc "khu vực này hiện không có cá để câu". Ném từ trong vòng câu để cắt phiên; khác
/// <see cref="BagFullException"/> ở chỗ đây KHÔNG phải kết thúc bình thường: chẳng có con cá nào
/// được câu, và tình trạng chỉ hết khi người dùng đổi cần hoặc đổi chỗ.
///
/// Mang theo <see cref="Reason"/> thay vì tách thành hai lớp exception: hai tình huống này đi
/// chung toàn bộ đường xử lý (chung chuỗi đếm, chung trần thử lại), chỉ khác câu chữ báo ra —
/// tách lớp thì phải nhân đôi cả catch arm cho đúng một dòng khác biệt.
/// </summary>
internal sealed class NoFishMatchException : Exception
{
    public FishingStopReason Reason { get; }

    public NoFishMatchException(FishingStopReason reason, string message) : base(message)
        => Reason = reason;
}

/// <summary>
/// Game báo "Bạn không đứng gần mặt nước" nhiều lượt liên tiếp. Cùng vai với
/// <see cref="NoFishMatchException"/>: cắt phiên khi tình trạng đã rõ là không tự khỏi. Khác ở
/// chỗ vài lượt đầu tiên là chuyện BÌNH THƯỜNG sau mỗi lần đổ cốp — chỉ chuỗi dài mới là sự cố.
/// </summary>
internal sealed class NoWaterException : Exception
{
    public NoWaterException(string message) : base(message) { }
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
    /// <summary>Số lượt đổ cốp THÀNH CÔNG của phiên — chỉ đếm DumpResult.Ok, cho tin Discord.</summary>
    private int _dumpsDone;
    private int _released;
    private int _sold;

    /// <summary>KG ba lô lần cân trước, chỉ dùng ở chặng cuối phiên. -1 = chưa cân lần nào.</summary>
    private double _endgameLastKg = -1;
    /// <summary>Mấy con liên tiếp mà KG ba lô không nhúc nhích.</summary>
    private int _endgameFlat;

    // ---------------- trang thai cho UI ----------------
    // Moi thu duoi day chi luong bot ghi, va chi doc lai luc dong goi FishingState —
    // cung mot luong, nen khong can lock. UI nhan ban copy bat bien qua event.
    private FishingPhase _phase = FishingPhase.Idle;
    private readonly Stopwatch _phaseSw = new();
    private readonly Stopwatch _sessionSw = new();
    private long _lastPublishMs = -1;

    private int _casts;
    private int _bites;
    private int _rejects;
    /// <summary>Số lần thả câu bị chặn vì "không đứng gần mặt nước". Đếm riêng khỏi _rejects.</summary>
    private int _noWater;
    /// <summary>Số lần thấy "không có cá phù hợp" cả phiên — chỉ để vẽ thanh tỉ lệ.</summary>
    private int _noFish;
    /// <summary>
    /// Số lần thấy "không có cá phù hợp" LIÊN TIẾP. Đây mới là con số quyết định lúc nào dừng:
    /// đếm tổng thì một lần đọc nhầm lúc 9 giờ sáng sẽ cộng dồn với một lần lúc 3 giờ chiều rồi
    /// cắt ngang một phiên hoàn toàn khoẻ mạnh. Đặt lại về 0 ngay khi có cá cắn.
    /// </summary>
    private int _noFishStreak;
    /// <summary>
    /// Số lần thấy "không đứng gần mặt nước" LIÊN TIẾP, cùng vai với <see cref="_noFishStreak"/>.
    /// Cũng đặt lại về 0 khi có cá cắn.
    /// </summary>
    private int _noWaterStreak;
    private int _castMissed;
    /// <summary>Số lần thả lại vì thanh câu đã hiện rồi tắt giữa chừng. Đếm riêng khỏi _biteTimeouts.</summary>
    private int _barGoneRecasts;
    private int _biteTimeouts;
    private int _fightTimeouts;
    private int _castRetries;

    /// <summary>KG ba lô lần cân gần nhất, chỉ để hiện lên UI. -1 = chưa cân.</summary>
    private double _lastBagKg = -1;
    /// <summary>Trần ba lô lần cân gần nhất (mẫu số trên UI). -1 = chưa cân.</summary>
    private double _lastBagCap = -1;

    /// <summary>Fill thanh câu đọc được gần nhất, cho badge overlay. -1 = chưa đọc được.</summary>
    private double _lastFill = -1;

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

    /// <summary>
    /// Pha + so dem, phat khi DOI PHA (khong phai moi tick).
    ///
    /// Vi sao can event rieng: SnapshotReady chi ban tu vong lap chinh va hai vong
    /// cho nut, con MaybeDump / Dump / PeekBagWeight / WatchBagUntilFull khong he
    /// goi reader.Read(). Nghia la suot 10-30 giay do cop, UI khong nhan gi ca va
    /// dung nguyen so cu.
    /// </summary>
    public event Action<FishingState> StateChanged;

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
        FishingStopReason.NoFishMatch => "không có cá hợp cần và độ sâu",
        FishingStopReason.NoFishArea => "khu vực này hết cá",
        FishingStopReason.NoWater => "không đứng gần mặt nước",
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

            // Tuy chon, va phai la tuy chon: moi may dang dung deu chua co no-water.png. Thieu
            // thi thong bao "khong dung gan mat nuoc" van bat duoc, chi la phai cho het
            // CastConfirmMs (4 s) qua duong "tha cau truot" nhu truoc gio.
            if (_profile.Reject.IsSet)
                Emit(reader.NoWaterTemplateProblem is { } np
                    ? $"“không đứng gần mặt nước”: chưa dùng được ({np}) — vẫn chờ " +
                      $"{_cfg.CastConfirmMs} ms rồi thả lại như cũ"
                    : $"“không đứng gần mặt nước”: nhận ở ncc ≥ {_cfg.NoWaterNccMin:F2}, thấy là " +
                      $"thả lại sau {_cfg.RejectRecastMs} ms — quá {_cfg.NoWaterRetries} lần liên " +
                      "tiếp thì báo Discord rồi dừng phiên");

            // Cung la tuy chon, cung ly do. Khac o cho: thieu mau nay thi bot khong chi cham hon
            // ma quay vong VO TAN — nen dong log phai noi ro cai gia, khong chi noi "chua co mau".
            if (_profile.Reject.IsSet)
                Emit(reader.NoFishTemplateProblem is { } fnp
                    ? $"“không có cá phù hợp”: chưa dùng được ({fnp}) — sai cần/độ sâu sẽ quay " +
                      "vòng mãi mà không ai báo"
                    : $"“không có cá phù hợp”: nhận ở ncc ≥ {_cfg.NoFishNccMin:F2}, thấy là báo " +
                      $"Discord rồi thả lại {_cfg.NoFishRetries} lần, vẫn bị thì dừng phiên");

            // Chung chuoi dem va chung tran voi "khong co ca phu hop" — chi khac cau chu bao ra.
            if (_profile.Reject.IsSet)
                Emit(reader.NoFishAreaTemplateProblem is { } fap
                    ? $"“khu vực hết cá”: chưa dùng được ({fap}) — hồ cạn cá sẽ quay vòng mãi mà " +
                      "không ai báo"
                    : $"“khu vực hết cá”: nhận ở ncc ≥ {_cfg.NoFishAreaNccMin:F2}, dùng chung " +
                      "ngân sách thử lại với “không có cá phù hợp”");

            // Luot chet bat bang pixel HUD — xem chu thich o FishingConfig.BarGoneFrames.
            // Luon bat, khong co cong tac: da do thuc te va thay the han tinh nang
            // "mat can cau" (pose dung yen) cu.
            Emit($"“thanh tắt sớm”: thanh đã hiện rồi tắt {_cfg.BarGoneFrames} khung liên tiếp " +
                 $"là thả lại sau {_cfg.RejectRecastMs} ms");

            if (!_profile.Keep.IsSet)
                Emit("cảnh báo: chưa khoanh CẤT VÀO — sau khi câu được sẽ chỉ bấm 4, không nhận cá");
            else if (reader.KeepTemplateProblem is { } kp)
                Emit("cảnh báo: CẤT VÀO — sẽ click ô cố định, không dò được (" + kp + ")");
            else
                Emit($"dò CẤT VÀO trong vùng {reader.KeepBandRegion.Width}×{reader.KeepBandRegion.Height} " +
                     $"@ {reader.KeepBandRegion.X},{reader.KeepBandRegion.Y}, màu nền nút " +
                     $"#{reader.KeepColor.R:X2}{reader.KeepColor.G:X2}{reader.KeepColor.B:X2} ±{_cfg.KeepColorTol}");

            EmitReleasePlan();
            EmitSellPlan();

            Emit($"bắt đầu. chờ cắn {_cfg.WaitBiteMs} ms, giữ S tối đa {_cfg.FightTimeoutMs} ms, " +
                 $"xong khi fill ≥ {_cfg.DoneFill01:0.00}");
            Emit(_cfg.CastConfirmMs > 0
                ? $"xác minh thả câu: thanh không hiện sau {_cfg.CastConfirmMs} ms thì thả lại " +
                  $"(tối đa {_cfg.CastConfirmRetries} lần)"
                : "xác minh thả câu: TẮT — thả trượt sẽ phải chờ hết thời gian chờ cắn");
            Emit($"{HotkeyText.Job()} = bật/tắt. Cửa sổ game phải đang focus (" + _cfg.WindowMatch + ").");
            Emit($"mỗi lần 4 sẽ bấm Space sau {_cfg.CastSpaceDelayMs} ms — tắt hotkey 4 trong AutoHotkey.");

            SetUpDumper();

            // Bao mo phien de nguoi di vang biet bot da vao ca. Khong ping — tin dung phien
            // moi dang rung dien thoai, va no co the den chi vai phut sau.
            DiscordNotifier.NotifyInfo(_cfg, "🎣 Bắt đầu phiên câu",
                "đổ cốp: " + (_dumper is null ? "tắt" : "bật"), Emit);

            _sessionSw.Restart();
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

            // So do "thanh tat giua chung", dem lai tung luot cho — de doi chieu bang so that
            // khi can chinh BarGoneFrames.
            //
            //   barGoneFrames  : so khung LIEN TIEP thanh tat SAU khi da tung hien (debounce)
            //   barGoneMax     : chuoi dai nhat trong luot cho — luot cau KHOE ma so nay cham
            //                    nguong BarGoneFrames nghia la nguong dang qua thap
            //   barGoneFirstMs : lan dau thanh tat tinh tu luc tha. -1 = chua tat lan nao.
            int barGoneFrames = 0, barGoneMax = 0;
            long barGoneFirstMs = -1;

            // Rong khi thanh chua tat lan nao — dong "ca can" cua luot khoe binh thuong
            // giu nguyen hinh dang cu, chu nay chi moc len khi co gi dang xem.
            string BarNote() =>
                barGoneFirstMs < 0
                    ? ""
                    : $"  [thanh tắt đầu {barGoneFirstMs} ms · dài nhất {barGoneMax} khung]";

            // Khoi reset nay truoc day duoc lap y nguyen bon lan — bon cho phai giu
            // dong bo bang tay. Local function sua thang bien ben ngoai duoc, nen gop
            // lai duoc ma khong phai doi bien thanh field.
            //
            // keepRetries: nhanh "tha truot" khong reset castRetries (no phai cong don
            // toi CastConfirmRetries) va cung khong can dat sawCastHud = false, vi
            // sawCastHud == false chinh la dieu kien kich hoat nhanh do.
            void EnterWaiting(bool keepRetries = false)
            {
                biteFrames = 0;
                barGoneFrames = 0;
                barGoneMax = 0;
                barGoneFirstMs = -1;
                sawCastHud = false;
                if (!keepRetries) castRetries = 0;
                _castRetries = castRetries;
                waitSw.Restart();
                ignoreFailUntil = DateTime.UtcNow.AddMilliseconds(_cfg.CastCooldownMs);
                SetPhase(FishingPhase.WaitingForBite);
            }

            EnterWaiting();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                WaitWindow(ct);

                var snap = reader.Read();
                SnapshotReady?.Invoke(snap);
                _lastFill = snap.BlueFill01;
                Heartbeat();

                if (!fighting)
                {
                    // Thanh da hien roi tat = day da roi khoi nuoc. Chi dem SAU khi thanh tung
                    // hien, nen khong can grace rieng: animation gio can truoc do thanh chua
                    // hien, va luc do sawCastHud con false.
                    if (snap.UiOpen)
                    {
                        sawCastHud = true;
                        barGoneFrames = 0;
                    }
                    else if (sawCastHud)
                    {
                        barGoneFrames++;
                        if (barGoneFirstMs < 0) barGoneFirstMs = waitSw.ElapsedMilliseconds;
                        if (barGoneFrames > barGoneMax) barGoneMax = barGoneFrames;
                    }

                    if (snap.FishBite) biteFrames++;
                    else biteFrames = 0;

                    if (biteFrames >= _cfg.BiteDebounceFrames)
                    {
                        _bites++;
                        // Ca can duoc nghia la day dang o duoi nuoc VA can/do sau deu hop — ca hai
                        // chuoi hong da dut, khong duoc mang so cu sang tinh tiep.
                        _noFishStreak = 0;
                        _noWaterStreak = 0;
                        Emit($"cá cắn (ncc={snap.FishScore:F3}) — giữ S" + BarNote());
                        HoldS();
                        fighting = true;
                        sawHud = snap.UiOpen;
                        fightSw.Restart();
                        SetPhase(FishingPhase.Fighting);
                        continue;
                    }

                    // HAI thong bao "khong cau duoc o day": "khong co ca nao phu hop voi can va do
                    // sau cau cua ban" (thu ba) va "khu vuc nay hien khong co ca de cau" (thu tu).
                    // Ca hai ve tren cung o do.
                    //
                    // Phai xet TRUOC hai nhanh kia. Bon mau cham tren cung mot vung anh, neu mot
                    // khung nao do khop nhieu mau thi thu tu if quyet dinh ai thang — ma xu nham
                    // thanh "che moi" hay "xa nuoc" la quay vong vo tan, dung cai bay ma nhanh nay
                    // sinh ra de tranh.
                    //
                    // Khac han hai cai kia o BAN CHAT: che moi va xa nuoc tu het sau mot cu tha
                    // lai. Sai can / het ca thi tha lai bao nhieu lan cung ra dung thong bao do.
                    //
                    // CHUNG mot chuoi dem cho ca hai, khong tach: neu game doi qua lai giua hai
                    // thong bao ma dem rieng thi khong chuoi nao cham tran, bot quay vong mai.
                    bool noFishOk = (snap.NoFishNotice || snap.NoFishAreaNotice)
                                    && !snap.UiOpen && DateTime.UtcNow >= ignoreFailUntil;
                    if (noFishOk)
                    {
                        // Ca hai cung khop thi mot trong hai la duong tinh gia. Uu tien "khu vuc
                        // het ca": loi khuyen "di cho khac" van dung ke ca khi that ra la sai can,
                        // con nguoc lai thi khong.
                        bool area = snap.NoFishAreaNotice;
                        string what = area ? "khu vực hết cá" : "không có cá phù hợp";
                        double ncc = area ? snap.NoFishAreaScore : snap.NoFishScore;

                        _noFish++;
                        _noFishStreak++;

                        // Lan dau: bao Discord NGAY, khong ping. Bot con dang thu lai — neu may cu
                        // sau chay duoc thi day la dau vet duy nhat cho biet vua co chuyen. Khong
                        // ping vi tin dung phien (co ping) co the den chi 3 giay sau do.
                        if (_noFishStreak == 1 && _cfg.NoFishRetries > 0)
                            DiscordNotifier.NotifyAlert(_cfg,
                                area ? "Khu vực này hiện không có cá để câu"
                                     : "Không có cá phù hợp với cần và độ sâu",
                                $"đang thử câu lại {_cfg.NoFishRetries} lần nữa", Emit);

                        if (_noFishStreak > _cfg.NoFishRetries)
                            throw new NoFishMatchException(
                                area ? FishingStopReason.NoFishArea : FishingStopReason.NoFishMatch,
                                $"thấy \"{what}\" {_noFishStreak} lần liên tiếp — " +
                                (area ? "đi chỗ khác câu" : "đổi cần hoặc đổi chỗ câu"));

                        Emit($"{what} (ncc={ncc:F3}) — câu lại " +
                             $"({_noFishStreak}/{_cfg.NoFishRetries + 1})");
                        Sleep(ct, _cfg.RejectRecastMs);
                        Cast(ct, area ? "câu lại (khu vực hết cá)" : "câu lại (sai cần/độ sâu)",
                            waitRelease: false);
                        EnterWaiting();
                        continue;
                    }

                    bool rejectOk = snap.FailNotice && !snap.UiOpen && DateTime.UtcNow >= ignoreFailUntil;
                    if (rejectOk)
                    {
                        _rejects++;
                        Emit($"chê mồi (ncc={snap.RejectScore:F3}, HUD đóng) — câu lại");
                        Sleep(ct, _cfg.RejectRecastMs);
                        Cast(ct, "câu lại", waitRelease: false);
                        EnterWaiting();
                        continue;
                    }

                    // "Ban khong dung gan mat nuoc" — game ve o DUNG o thong bao che moi. Hay gap
                    // ngay sau khi do cop: nhan vat vua quay lai, game chua kip ghi nhan vi tri
                    // moi. Do la race thoang qua, khong phai loi vi tri that: nghi mot nhip roi
                    // tha lai la duoc, khong can quay them.
                    //
                    // Truoc day duong nay roi vao nhanh castMissed va phai cho tron CastConfirmMs
                    // (4 s). Do trong log: cu tha ngay sau do cop truot 77% (155/201) so voi nen
                    // chung 8% — gan nhu toan bo la cai nay.
                    //
                    // Dem rieng, log rieng, khong gop vao _rejects: hai nguyen nhan khac nhau ma
                    // chung mot dong log thi lan sau doc log lai khong phan biet duoc.
                    //
                    // Co tran dung nhu nhanh "sai can": neu nhan vat THAT SU roi mep nuoc (xe bi
                    // day di, bi keo, dung sai cho sau khi do cop) thi thong bao khong bao gio het
                    // va truoc day bot quay vong ~1 lan/1.5 s cho toi khi co nguoi phat hien.
                    bool noWaterOk = snap.NoWaterNotice && !snap.UiOpen && DateTime.UtcNow >= ignoreFailUntil;
                    if (noWaterOk)
                    {
                        _noWater++;
                        _noWaterStreak++;

                        // Bao o lan THU HAI, khong phai lan dau nhu nhanh "sai can". Lan dau la
                        // chuyen binh thuong: 77% cu tha ngay sau do cop dinh cai nay roi tu khoi.
                        // Chuoi reset moi khi co ca can, nen bao o lan dau la moi luot do cop mot
                        // tin Discord — vai chuc tin mot phien. Lan thu hai moi la bat thuong.
                        if (_noWaterStreak == 2 && _cfg.NoWaterRetries >= 2)
                            DiscordNotifier.NotifyAlert(_cfg,
                                "Không đứng gần mặt nước",
                                $"thả lại một lần rồi vẫn bị — đang thử thêm " +
                                $"{_cfg.NoWaterRetries - 1} lần nữa", Emit);

                        if (_noWaterStreak > _cfg.NoWaterRetries)
                            throw new NoWaterException(
                                $"thấy \"không đứng gần mặt nước\" {_noWaterStreak} lần liên tiếp — " +
                                "nhân vật đã rời mép nước");

                        Emit($"không đứng gần mặt nước (ncc={snap.NoWaterScore:F3}, HUD đóng) — " +
                             $"câu lại ({_noWaterStreak}/{_cfg.NoWaterRetries + 1})");
                        Sleep(ct, _cfg.RejectRecastMs);
                        Cast(ct, "câu lại (không gần nước)", waitRelease: false);
                        // EnterWaiting dat lai ignoreFailUntil = +CastCooldownMs, nen nhanh nay tu
                        // gioi han ~1 lan thu moi 1.5 s.
                        EnterWaiting();
                        continue;
                    }

                    // Thanh câu đã hiện rồi tắt, không thông báo nào khớp => cú thả đã chết.
                    // Đặt SAU bốn nhánh thông báo bên trên: chúng cũng làm thanh tắt nhưng biết
                    // rõ NGUYÊN NHÂN — debounce BarGoneFrames chính là cửa sổ để mẫu của chúng
                    // khớp trước. Mẫu trượt thì nhánh này thành lưới an toàn: vẫn thả lại, chỉ
                    // khác counter và câu chữ. Không cần xét ignoreFailUntil: chỉ đếm sau khi
                    // thanh đã từng hiện, tức cú thả đã ăn thật rồi.
                    bool barGone = sawCastHud && barGoneFrames >= _cfg.BarGoneFrames;
                    if (barGone)
                    {
                        _barGoneRecasts++;
                        Emit($"thanh câu tắt {barGoneFrames} khung liên tiếp " +
                             $"({waitSw.ElapsedMilliseconds} ms, không thông báo nào khớp) — " +
                             "cú thả đã chết, câu lại");
                        Sleep(ct, _cfg.RejectRecastMs);
                        // waitRelease: false — không hề giữ S, chờ AfterReleaseMs là thời gian
                        // chết vô ích. Giống nhánh chê mồi.
                        Cast(ct, "câu lại (thanh tắt)", waitRelease: false);
                        EnterWaiting();
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
                        _castMissed++;
                        Emit($"thanh câu không hiện sau {_cfg.CastConfirmMs} ms — thả câu trượt, " +
                             $"thả lại (lần {castRetries}/{_cfg.CastConfirmRetries})");
                        Cast(ct, "thả lại (trượt)", waitRelease: false);
                        EnterWaiting(keepRetries: true);
                        continue;
                    }

                    if (waitSw.ElapsedMilliseconds >= _cfg.WaitBiteMs)
                    {
                        Emit($"hết {_cfg.WaitBiteMs} ms không cắn — câu lại" +
                             $" (thanh={(sawCastHud ? "đã mở" : "chưa mở lần nào")}" +
                             $" fill={snap.BlueFill01 * 100:0.0}% cá={snap.FishScore:F3}" +
                             $" chê={snap.RejectScore:F3} nước={snap.NoWaterScore:F3}" +
                             $" saicần={snap.NoFishScore:F3} hếtcá={snap.NoFishAreaScore:F3})" +
                             BarNote());
                        _biteTimeouts++;
                        Cast(ct, "câu lại (timeout)");
                        EnterWaiting();
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
                        EnterWaiting();
                        continue;
                    }

                    if (fightSw.ElapsedMilliseconds >= _cfg.FightTimeoutMs)
                    {
                        _fightTimeouts++;
                        Emit($"giữ S quá {_cfg.FightTimeoutMs} ms — nhả và câu lại");
                        Cast(ct, "câu lại (timeout kéo)");
                        fighting = false;
                        sawHud = false;
                        EnterWaiting();
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
        catch (NoFishMatchException ex)
        {
            reason = ex.Reason;
            message = ex.Message;
            Emit("dừng: " + ex.Message);
        }
        catch (NoWaterException ex)
        {
            reason = FishingStopReason.NoWater;
            message = ex.Message;
            Emit("dừng: " + ex.Message);
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
            _sessionSw.Stop();
            // Phat pha Stopped TRUOC khi _dumper bi don, khong thi so kg cuoi cung
            // cua phien khong con cho nao lay ra.
            SetPhase(FishingPhase.Stopped);
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
            // Chua khoanh CẤT VÀO thi khong ai bam cat ca — con nay khong tinh la bat duoc.
            Cast(ct, "thả câu", waitRelease: false);
            return;
        }

        SetPhase(FishingPhase.WaitingForKeep);
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
                // Không thả mù: lệch sang THẢ RA khi không biết chỗ nút là bấm nhầm BÁN NGAY.
                var abs = FishingConfig.ToAbsolute(_screen, _profile.Keep);
                Emit($"không dò được nút trong {_cfg.WaitKeepMs} ms — click ô đã khoanh");
                ClickKeep(new Point(abs.Left + abs.Width / 2, abs.Top + abs.Height / 2), ct);
            }

            AfterKept(reader, ct);
            return;
        }

        Emit($"thấy nút {found.KeepRect.Width}×{found.KeepRect.Height} @ {found.KeepRect.X},{found.KeepRect.Y}" +
             $"  dens={found.KeepDensity:F2}  ncc={found.KeepScore:F3}");

        if (TryAutoRelease(found, reader, ct))
            return;

        if (TryAutoSell(found, reader, ct))
            return;

        SetPhase(FishingPhase.ClickingKeep);
        ClickKeep(found.KeepClick, ct);
        RetryClicks(reader, found.KeepRect, CatchClick.Keep, ct);
        AfterKept(reader, ct);
    }

    /// <summary>
    /// Chỉ thả khi dò được CẤT VÀO thật (biết chỗ hàng nút) và tên khớp danh sách.
    /// Không chắc / thiếu mẫu → false, bên gọi cất vào như cũ.
    /// </summary>
    private bool TryAutoRelease(FishingSnapshot found, FishingReader reader, CancellationToken ct)
    {
        if (_cfg.AutoReleaseEnabled != true) return false;
        if (_profile.AutoReleaseItems is not { Count: > 0 }) return false;

        var guess = CatchIdentifier.Identify(_cfg, _screen, _profile, _profile.AutoReleaseItems);
        if (guess.Name is null)
        {
            Emit("thả: " + (guess.Note ?? "không nhận được tên"));
            return false;
        }

        Emit($"thả {guess.Name} (ncc={guess.Score:F2})");
        SetPhase(FishingPhase.ClickingRelease);
        ClickRelease(ReleasePoint(found), ct);
        RetryClicks(reader, found.KeepRect, CatchClick.Release, ct);

        _released++;
        try { SnapshotReady?.Invoke(reader.Read()); } catch { }
        Sleep(ct, _cfg.AfterKeepCastMs);
        Cast(ct, "thả câu", waitRelease: false);
        return true;
    }

    /// <summary>
    /// Chỉ bán khi đã dò được hàng nút và tên khớp danh sách bán.
    /// Thả Ra đã xét trước — loài nằm cả hai danh sách không vào đây.
    /// </summary>
    private bool TryAutoSell(FishingSnapshot found, FishingReader reader, CancellationToken ct)
    {
        if (_cfg.AutoSellEnabled != true) return false;
        if (_profile.AutoSellItems is not { Count: > 0 }) return false;

        var guess = CatchIdentifier.Identify(_cfg, _screen, _profile, _profile.AutoSellItems);
        if (guess.Name is null)
        {
            Emit("giữ — " + (guess.Note ?? "không nhận được tên"));
            return false;
        }

        Emit($"bán {guess.Name} (ncc={guess.Score:F2})");
        SetPhase(FishingPhase.ClickingSell);
        ClickSell(SellPoint(found), ct);
        RetryClicks(reader, found.KeepRect, CatchClick.Sell, ct);

        _sold++;
        try { SnapshotReady?.Invoke(reader.Read()); } catch { }
        Sleep(ct, _cfg.AfterKeepCastMs);
        Cast(ct, "thả câu", waitRelease: false);
        return true;
    }

    private void AfterKept(FishingReader reader, CancellationToken ct)
    {
        // Dem ca o DAY, khong o trong MaybeDump. O do no nam sau chot `_dumper is null`
        // nen chi dem khi bat do cop — tuc con so "ca phien nay" bien mat hoan toan
        // khi nguoi dung tat do cop. Cho nay la duong da chac chan co cu click cat ca.
        _catches++;
        _catchesSinceDump++;

        // Con dau tien cua phien: bao mot tieng cho biet moi thu da vao guong — thoi gian cho
        // toi con dau cung la thuoc do suc khoe cua cho cau. Khong ping, cung ly do NotifyAlert.
        if (_catches == 1)
            DiscordNotifier.NotifyInfo(_cfg, "🐟 Bắt được con cá đầu tiên",
                "sau " + DiscordNotifier.FormatDuration(_sessionSw.ElapsedMilliseconds) +
                " từ lúc bắt đầu", Emit, good: true);

        try { SnapshotReady?.Invoke(reader.Read()); } catch { }

        MaybeDump(ct);

        // Mặc định 0 — xem chú thích AfterKeepCastMs. Chờ ở đây là cách phòng hờ cho việc
        // animation cất cá nuốt mất phím 4; cách bắt sau khi đã trượt nằm ở vòng lặp chính.
        Sleep(ct, _cfg.AfterKeepCastMs);
        Cast(ct, "thả câu", waitRelease: false);
    }

    private enum CatchClick { Keep, Release, Sell }

    private void RetryClicks(FishingReader reader, Rectangle anchor, CatchClick action, CancellationToken ct)
    {
        for (int i = 0; i < _cfg.KeepClickRetries; i++)
        {
            var still = WaitForKeepGone(reader, anchor, ct);
            if (still is null) break;
            Emit($"nút vẫn còn sau {_cfg.KeepGoneMs} ms — click lại (lần {i + 1}/{_cfg.KeepClickRetries})");
            switch (action)
            {
                case CatchClick.Keep: ClickKeep(still.KeepClick, ct); break;
                case CatchClick.Release: ClickRelease(ReleasePoint(still), ct); break;
                default: ClickSell(SellPoint(still), ct); break;
            }
            anchor = still.KeepRect;
        }
    }

    private Point ReleasePoint(FishingSnapshot snap)
    {
        var r = snap.KeepRect;
        int gap = _cfg.ReleaseGapPx;
        int x = r.Right + gap + r.Width / 2;
        int y = snap.KeepClick.IsEmpty ? r.Top + r.Height / 2 : snap.KeepClick.Y;
        return new Point(x, y);
    }

    private Point SellPoint(FishingSnapshot snap)
    {
        var r = snap.KeepRect;
        int gap = _cfg.ReleaseGapPx;
        int x = r.Right + 2 * gap + r.Width + r.Width / 2;
        int y = snap.KeepClick.IsEmpty ? r.Top + r.Height / 2 : snap.KeepClick.Y;
        return new Point(x, y);
    }

    private void EmitReleasePlan()
    {
        if (_cfg.AutoReleaseEnabled != true)
        {
            Emit("tự thả: tắt");
            return;
        }

        var items = _profile.AutoReleaseItems ?? new List<string>();
        if (items.Count == 0)
        {
            Emit("tự thả: bật nhưng chưa chọn loại — mọi con sẽ cất vào");
            return;
        }

        int have = items.Count(n => FishingConfig.HasCatchTitleTemplate(_profile.Key, n));
        Emit($"tự thả: {string.Join(", ", items)} ({have}/{items.Count} có mẫu tên)");
        if (!_profile.CatchTitle.IsSet)
            Emit("tự thả: chưa khoanh ô tên cá — sẽ cất vào như cũ (mở Loại thả ra để khoanh)");
        else if (have == 0)
            Emit("tự thả: chưa có mẫu tên — sẽ cất vào như cũ (chụp mẫu lúc panel đang hiện)");
    }

    private void EmitSellPlan()
    {
        if (_cfg.AutoSellEnabled != true)
        {
            Emit("tự bán: tắt");
            return;
        }

        var items = _profile.AutoSellItems ?? new List<string>();
        if (items.Count == 0)
        {
            Emit("tự bán: bật nhưng chưa chọn loại — không bán ngay");
            return;
        }

        int have = items.Count(n => FishingConfig.HasCatchTitleTemplate(_profile.Key, n));
        Emit($"tự bán: {string.Join(", ", items)} ({have}/{items.Count} có mẫu tên)");
        if (!_profile.CatchTitle.IsSet)
            Emit("tự bán: chưa khoanh ô tên cá — sẽ cất vào như cũ (mở Loại bán ngay để khoanh)");
        else if (have == 0)
            Emit("tự bán: chưa có mẫu tên — sẽ cất vào như cũ (chụp mẫu lúc panel đang hiện)");
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
        Emit(_cfg.ScanRetries > 0
            ? $"icon tải chậm: ô trống mà lệch ≥ {_cfg.CellFaintStdMin:F1} thì coi như đang tải, " +
              $"quét lại tối đa {_cfg.ScanRetries} lượt cách nhau {_cfg.ScanRetryGapMs} ms"
            : "quét lại khi icon tải chậm: TẮT (ScanRetries = 0)");
        Emit($"không thấy ô cá nào thì mở cốp lại, đủ {_cfg.NoFishTries} lượt mới dừng phiên");
    }

    /// <summary>
    /// Chỗ DUY NHẤT được phép đổ cốp: sau khi nút CẤT VÀO đã tắt và trước cú thả câu kế tiếp.
    /// Không bao giờ chen vào lúc đang giữ S kéo cá — mất cá là nhẹ, giữ S suốt cả lượt đổ cốp
    /// mới là hỏng.
    /// </summary>
    private void MaybeDump(CancellationToken ct)
    {
        if (_dumper is null) return;

        // _catches / _catchesSinceDump da tang o CollectThenCast, ngay truoc khi vao day.
        // Truoc kia chung tang o chinh cho nay, tuc nam sau chot `_dumper is null`.

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
            SetPhase(FishingPhase.CheckingWeight);
            var w = _dumper.PeekBagWeight(ct);
            if (w.Ok)
            {
                _lastBagKg = w.Value;
                _lastBagCap = w.Cap;
                double full = _cfg.BagDumpKg(w.Cap);
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

                // Chua den nguong do — quay lai cho can, khong thi UI ket o "Cân ba lô".
                if (!bagFull && !wontFit && !byCount && !tightNow)
                {
                    SetPhase(FishingPhase.WaitingForBite);
                    return;
                }
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
        SetPhase(FishingPhase.Dumping);

        // Thu lai NGAY chu khong doi con ca sau: vao duoc tan day nghia la ba lo dang sat tran,
        // cau them mot con nua thi no co the khong cat vao duoc va KG dung yen. Luot thu lai mo
        // lai cop tu dau nen no vao lai duoc ca tu mot trang thai lech.
        DumpResult r;
        while (true)
        {
            r = _dumper.Dump(ct);
            _catchesSinceDump = 0;
            // Cop vua doi, phat lai ngay de so kg tren UI khong tre mot luot.
            Publish(force: true);

            // Bo dem strike nam trong TrunkDumper, khong nhan ban o day: no con dung strike de
            // biet co nen quay mat khoi xe hay khong, hai cho dem rieng la lech nhau.
            if (r != DumpResult.NothingToMove || _dumper.NoFishGivenUp) break;

            Emit($"mở cốp lại thử một lượt nữa sau {_cfg.DumpRetryGapMs} ms " +
                 "(nhân vật còn hướng vào xe)");
            Sleep(ct, _cfg.DumpRetryGapMs);
        }

        if (r == DumpResult.Ok)
        {
            _dumpsDone++;
            Emit("--- đổ xong, câu tiếp ---");
            // Nguoi di vang biet chu trinh do cop van song. Khong ping, nhu moi tin Info.
            DiscordNotifier.NotifyInfo(_cfg, $"📦 Đã đổ cốp thành công — {_dumpsDone} lần",
                _dumper.TrunkFreeKg >= 0
                    ? $"cốp còn trống {_dumper.TrunkFreeKg:F1}/{_cfg.TrunkCapKg:F0} kg"
                    : null, Emit, good: true);
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
        throw new TrunkStepException(_dumper.ByIcon
            ? $"mở cốp {_cfg.NoFishTries} lượt mà không nhận ra ô nào là cá — xem mấy dòng " +
              "“bỏ qua” trong log: “RÕ nhưng không có trong danh sách cá” thì vào Vật phẩm & cá " +
              "tích thêm loài đó; “không rõ” thì loài đó chưa có icon trong bộ mẫu; “như đang " +
              "tải icon” thì nới ScanRetries / ScanRetryGapMs cho đường truyền chậm; còn “trống” " +
              "trơn thì hạ ngưỡng ô trống"
            : $"mở cốp {_cfg.NoFishTries} lượt mà mọi ô chứa cá đã khai báo đều trống — " +
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
        SetPhase(FishingPhase.EndgameWeighing);
        var w = _dumper.PeekBagWeight(ct);
        if (w.Ok)
        {
            _lastBagKg = w.Value;
            _lastBagCap = w.Cap;
        }
        if (!w.Ok)
        {
            Emit("cốp đầy — lần cân này hỏng, câu tiếp rồi cân lại");
            SetPhase(FishingPhase.WaitingForBite);
            return;
        }

        double stopKg = _cfg.BagStopKg(w.Cap);
        if (w.Value >= stopKg)
            throw new BagFullException(
                $"cốp đầy và ba lô đã {w.Value:F1}/{w.Cap:F0} kg — xong phiên, đi bán cá");

        // Ba lo dung lai duoi nguong van la ba lo day: con ca ke tiep nang hon cho con lai thi
        // game khong cho cat, KG dung yen va nguong dung khong bao gio toi. Khong bat cai nay
        // thi bot cau den sang van thay "chua du nguong dung".
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

        Emit($"cốp đầy — ba lô {w.Value:F1}/{w.Cap:F0} kg, câu tiếp tới {stopKg:F1} kg");
        SetPhase(FishingPhase.WaitingForBite);
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

    private void ClickRelease(Point p, CancellationToken ct)
    {
        WaitWindow(ct);
        Emit($"click THẢ RA @ {p.X},{p.Y}");
        InputSender.MoveCursorOnlySmooth(p.X, p.Y, _cfg.KeepMoveSteps);
        Sleep(ct, _cfg.KeepHoverMs);
        InputSender.LeftDown();
        Sleep(ct, 60);
        InputSender.LeftUp();
    }

    private void ClickSell(Point p, CancellationToken ct)
    {
        WaitWindow(ct);
        Emit($"click BÁN NGAY @ {p.X},{p.Y}");
        InputSender.MoveCursorOnlySmooth(p.X, p.Y, _cfg.KeepMoveSteps);
        Sleep(ct, _cfg.KeepHoverMs);
        InputSender.LeftDown();
        Sleep(ct, 60);
        InputSender.LeftUp();
    }

    /// <summary>
    /// Mot cua duy nhat cho moi cu tha — ca bay cho goi deu qua day, nen dat pha
    /// Casting va dem _casts o day la du, khong phai rai ra bay cho.
    /// </summary>
    private void Cast(CancellationToken ct, string why, bool waitRelease = true)
    {
        SetPhase(FishingPhase.Casting);
        _casts++;
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

    // ---------------------------------------------------------------- trang thai

    private void SetPhase(FishingPhase p)
    {
        _phase = p;
        _phaseSw.Restart();
        Publish(force: true);
    }

    /// <summary>
    /// Goi moi tick vong lap. Chi thuc su phat neu da qua 250 ms — de dong ho trong
    /// pha co nhich, ma khong nhan doi luu luong BeginInvoke o nhip PollMs.
    /// </summary>
    private void Heartbeat() => Publish(force: false);

    private void Publish(bool force)
    {
        var h = StateChanged;
        if (h is null) return;

        long now = _sessionSw.ElapsedMilliseconds;
        if (!force && _lastPublishMs >= 0 && now - _lastPublishMs < 250) return;
        _lastPublishMs = now;

        var d = _dumper;
        h(new FishingState
        {
            Phase = _phase,
            PhaseMs = _phaseSw.ElapsedMilliseconds,
            SessionMs = now,

            Casts = _casts,
            Bites = _bites,
            Rejects = _rejects,
            NoWater = _noWater,
            NoFish = _noFish,
            CastMissed = _castMissed,
            BarGoneRecasts = _barGoneRecasts,
            BiteTimeouts = _biteTimeouts,
            FightTimeouts = _fightTimeouts,
            Catches = _catches,
            CatchesSinceDump = _catchesSinceDump,
            Released = _released,
            Sold = _sold,
            CastRetries = _castRetries,
            CastConfirmRetries = _cfg.CastConfirmRetries,
            Fill01 = _lastFill,

            // Copy ra day chu khong phoi _dumper: no bi set null trong finally cua
            // luong bot, UI giu tham chieu la dua.
            BagKg = _lastBagKg,
            BagCapKg = _lastBagCap > 0 ? _lastBagCap : _cfg.BagCapKg,
            PendingFishKg = d is null || _lastBagKg < 0 ? -1 : d.PendingFishKg(_lastBagKg),
            TrunkFreeKg = d?.TrunkFreeKg ?? -1,
            TrunkCapKg = _cfg.TrunkCapKg,
            TrunkFullStrikes = d?.TrunkFullStrikes ?? 0,
            TrunkFullTries = _cfg.TrunkFullTries,
            TrunkFull = d?.TrunkFull ?? false,
            OcrHealthy = d?.OcrHealthy ?? true,
            DumpOn = d is not null
        });
    }
}
