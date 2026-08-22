using System.Text.Json;
using System.Text.Json.Serialization;

namespace GtaMiniGameBot;

/// <summary>
/// Vùng làm việc của job Thợ điện theo từng độ phân giải.
///
/// Dùng lại <see cref="FishingRect"/> như <see cref="WoodProfile"/> đã làm: nó chỉ là hình chữ
/// nhật toạ độ TƯƠNG ĐỐI góc màn, mà <see cref="FishingConfig.ToAbsolute"/> lẫn
/// <see cref="StillCropForm"/> đã nói cùng thứ tiếng đó rồi.
///
/// Khác các job cũ ở một chỗ quan trọng: job này KHÔNG bắt buộc phải khoanh tay. Cả ba vùng dưới
/// đây đều SUY RA được từ độ phân giải, vì:
///   - Panel đi dây là hộp thoại nổi, tìm bằng MÀU chứ không bằng toạ độ (xem <see cref="WireReader"/>).
///   - Bảng Water &amp; Power là HUD toàn màn cố định, và bản Python đã đo ROI ở mốc 1920×1080 rồi
///     nhân tỉ lệ — cách đó chạy đúng ở 2560×1440 mà không cần đo lại.
/// Khoanh tay chỉ là đường CHỮA khi máy người dùng có safezone/HUD scale lạ.
/// </summary>
internal sealed class ElectricProfile
{
    public string Device { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>
    /// Vùng quét tìm panel đi dây. Rỗng thì suy 90% giữa màn.
    ///
    /// Vì sao không quét cả màn như bản Python: chụp 2560×1440 mỗi vòng là 14 MB, mà
    /// <see cref="RegionReader"/> tồn tại chính vì bài học "không bao giờ chụp cả màn". Cắt 5% mỗi
    /// cạnh cũng đồng thời loại minimap góc dưới-trái và HUD góc trên-phải — hai chỗ có sẵn màu
    /// vàng/xanh dễ lẫn với dây.
    /// </summary>
    public FishingRect WireBand { get; set; } = new();

    /// <summary>
    /// ROI bảng Water &amp; Power. Rỗng thì suy từ mốc 1080p <c>(280,140)-(1640,930)</c> của
    /// <c>water_power_solver_core_v13.CFG</c>.
    /// </summary>
    public FishingRect BoardRoi { get; set; } = new();

    /// <summary>
    /// Dải chữ tiêu đề bảng, dùng làm chữ ký xác nhận "bảng đang mở". Rỗng thì suy từ mốc 1080p
    /// <c>(250,5)-(1400,145)</c>.
    /// </summary>
    public FishingRect TitleBand { get; set; } = new();

    [JsonIgnore]
    public string Key => $"{Width}x{Height}";

    /// <summary>
    /// Hệ số quy đổi từ mốc 1920×1080 sang màn này — <c>(sx + sy) / 2</c>, đúng công thức
    /// <c>core_v13</c> dùng (dòng 611). Ở 16:9 thì sx = sy nên nó chỉ là tỉ lệ thẳng: 1.0 ở FHD,
    /// 1.3333 ở 2K. Giữ đúng công thức cũ để mọi hằng số <c>*_1080P</c> đem sang dùng được nguyên.
    /// </summary>
    [JsonIgnore]
    public double Scale => (Sx + Sy) / 2.0;

    [JsonIgnore]
    public double Sx => Width <= 0 ? 1.0 : Width / ElectricConfig.RefW;

    [JsonIgnore]
    public double Sy => Height <= 0 ? 1.0 : Height / ElectricConfig.RefH;

    public void Normalize()
    {
        WireBand ??= new FishingRect();
        BoardRoi ??= new FishingRect();
        TitleBand ??= new FishingRect();
    }

    // ---------------- vung thuc te ----------------

    /// <summary>Vùng quét panel dây: ô đã khoanh, không thì 90% giữa màn.</summary>
    public FishingRect ScanWireBand() => WireBand.IsSet ? WireBand : Inset(0.05);

    /// <summary>ROI bảng: ô đã khoanh, không thì quy đổi mốc 1080p.</summary>
    public FishingRect ScanBoardRoi() => BoardRoi.IsSet ? BoardRoi : FromRef(280, 140, 1640, 930);

    /// <summary>Dải tiêu đề: ô đã khoanh, không thì quy đổi mốc 1080p.</summary>
    public FishingRect ScanTitleBand() => TitleBand.IsSet ? TitleBand : FromRef(250, 5, 1400, 145);

    /// <summary>Quy đổi một ô đo ở 1920×1080 (x1,y1,x2,y2) sang màn này.</summary>
    private FishingRect FromRef(int x1, int y1, int x2, int y2)
    {
        if (Width < 100 || Height < 100) return new FishingRect();

        int rx1 = (int)Math.Round(x1 * Sx), rx2 = (int)Math.Round(x2 * Sx);
        int ry1 = (int)Math.Round(y1 * Sy), ry2 = (int)Math.Round(y2 * Sy);

        rx1 = Math.Clamp(rx1, 0, Width - 1);
        ry1 = Math.Clamp(ry1, 0, Height - 1);
        rx2 = Math.Clamp(rx2, rx1 + 1, Width);
        ry2 = Math.Clamp(ry2, ry1 + 1, Height);

        return new FishingRect { X = rx1, Y = ry1, W = rx2 - rx1, H = ry2 - ry1 };
    }

    private FishingRect Inset(double frac)
    {
        if (Width < 100 || Height < 100) return new FishingRect();
        int dx = (int)Math.Round(Width * frac), dy = (int)Math.Round(Height * frac);
        return new FishingRect { X = dx, Y = dy, W = Width - dx * 2, H = Height - dy * 2 };
    }

    public string Describe()
    {
        var board = ScanBoardRoi();
        var wire = ScanWireBand();
        if (!board.IsSet || !wire.IsSet) return $"{Key} — độ phân giải quá nhỏ, không suy được vùng";

        string how = BoardRoi.IsSet || WireBand.IsSet ? "đã khoanh tay" : "suy từ độ phân giải";
        return $"{Key} — đủ ({how}; bảng {board.W}×{board.H}, quét dây {wire.W}×{wire.H}, tỉ lệ {Scale:F3})";
    }
}

/// <summary>
/// Hằng số minigame ĐI DÂY. Đo từ bản Python <c>wire/wire_auto_solver_v9.py</c>.
///
/// Hai nhóm số ở đây có bản chất khác nhau và đừng trộn:
///   - Màu và toạ độ CHUẨN HOÁ theo bbox panel thì ĐỘC LẬP độ phân giải — 2K và FHD dùng chung.
///   - Chỉ <see cref="PanelMinWidth"/>/<see cref="PanelMinHeight"/> là số pixel, nên nó đo ở mốc
///     1080p rồi nhân <see cref="ElectricProfile.Scale"/>.
/// </summary>
internal sealed class WireSettings
{
    /// <summary>
    /// Sai số cho phép mỗi kênh khi so màu nền panel. Bản Python: <c>panel_color_tolerance = 10</c>.
    /// Hẹp vì nền panel là màu phẳng, không dính ánh sáng cảnh.
    /// </summary>
    public int PanelColorTolerance { get; set; } = 10;

    /// <summary>
    /// Sai số cho phép khi so màu đầu dây / ổ cắm. Bản Python: <c>anchor_color_tolerance = 42</c>.
    /// Rộng gấp bốn lần ngưỡng panel vì dây có gradient và viền chống răng cưa.
    /// </summary>
    public int AnchorColorTolerance { get; set; } = 42;

    /// <summary>Panel nhỏ hơn ngần này (ở mốc 1080p) thì không phải panel. Python: 250.</summary>
    public int PanelMinWidth { get; set; } = 250;

    public int PanelMinHeight { get; set; } = 250;

    /// <summary>
    /// Tỉ lệ rộng/cao để tách 3 dây với 5 dây. Python: <c>profile_aspect_split = 1.18</c> —
    /// dưới ngưỡng là WIRE_3 (panel vuông hơn), trên là WIRE_5 (panel bè ra).
    /// </summary>
    public double ProfileAspectSplit { get; set; } = 1.18;

    /// <summary>Nửa bề rộng ô lấy mẫu tại một slot, theo TỈ LỆ bbox panel. Python: 0.022.</summary>
    public double SlotPatchXFrac { get; set; } = 0.022;

    /// <summary>Nửa bề cao ô lấy mẫu tại một slot. Python: 0.035.</summary>
    public double SlotPatchYFrac { get; set; } = 0.035;

    /// <summary>Ô lấy mẫu phải có ít nhất ngần này pixel đúng màu mới tính là có màu. Python: 18.</summary>
    public int SlotMinColorPixels { get; set; } = 18;

    // ---------------- nguong hinh hoc "day da dinh" ----------------

    /// <summary>
    /// Mức NỞ RA của khối màu tại một ổ cắm, so lúc trước và sau khi game kiểm tra — xem
    /// <see cref="WireReader.GeometryScores"/>. Ổ trống chỉ có cái mấu ngắn; ổ đã nối đúng thì
    /// khối cùng màu nở thành cả sợi cáp uốn.
    ///
    /// Trên <see cref="LockGeomHigh"/> là chắc chắn dính; giữa hai mốc là lửng lơ, bot coi như
    /// KHÔNG dính chứ không đoán. Python: <c>target_lock_geom_low 1.35</c> / <c>high 1.65</c>.
    /// </summary>
    public double LockGeomLow { get; set; } = 1.35;

    public double LockGeomHigh { get; set; } = 1.65;

    /// <summary>Đã dính rồi thì KHÔNG kéo lại. Python: <c>pre_drag_skip_geom_high 1.30</c>.</summary>
    public double PreDragSkipGeom { get; set; } = 1.30;

    /// <summary>Kéo xong đạt ngần này là nhận. Python: <c>post_drag_accept_geom_high 1.30</c>.</summary>
    public double PostDragAcceptGeom { get; set; } = 1.30;

    /// <summary>Đủ cao để tin ngay không cần xác nhận thêm. Python: <c>post_drag_fast_geom_high 1.58</c>.</summary>
    public double PostDragFastGeom { get; set; } = 1.58;

    /// <summary>Cả bộ đã cắm đủ, được phép chờ game check. Python: <c>submit_attach_geom_high 1.42</c>.</summary>
    public double SubmitAttachGeom { get; set; } = 1.42;

    // ---------------- nhip ----------------

    /// <summary>
    /// Nhịp quét lúc ĐANG TÌM panel. Bản Python quét cả màn ở 45 ms; ở đây tách làm hai nhịp vì
    /// vùng quét lúc tìm rộng hơn hẳn lúc giải — chụp vùng to ở 45 ms là làm game giật.
    /// </summary>
    public int SearchPollMs { get; set; } = 220;

    /// <summary>Nhịp quét khi đã khoá được bbox panel. Python: <c>poll_interval_ms = 45</c>.</summary>
    public int SolvePollMs { get; set; } = 45;

    /// <summary>Kéo nhanh: thời lượng và số bước. Python turbo: 24 ms / 3 bước.</summary>
    public int DragMs { get; set; } = 24;

    public int DragSteps { get; set; } = 3;

    /// <summary>Kéo lại kiểu an toàn khi cú nhanh không được nhận. Python: 52 ms / 5 bước.</summary>
    public int RetryDragMs { get; set; } = 52;

    public int RetryDragSteps { get; set; } = 5;

    /// <summary>Số lần kéo lại một dây trước khi bỏ. Python: <c>drag_max_retries = 8</c>.</summary>
    public int DragMaxRetries { get; set; } = 8;

    /// <summary>Chờ tối đa để một cú kéo được game nhận. Python: <c>drag_accept_timeout_ms 2600</c>.</summary>
    public int DragAcceptTimeoutMs { get; set; } = 2600;

    /// <summary>Nghỉ giữa hai dây trong cùng một lượt. Python: <c>between_drag_ms 2</c>.</summary>
    public int BetweenDragMs { get; set; } = 2;

    /// <summary>Nghỉ sau cú kéo CUỐI (cú kích hoạt kiểm tra). Python: <c>after_last_drag_ms 2</c>.</summary>
    public int AfterLastDragMs { get; set; } = 2;

    /// <summary>Chờ trước khi đọc xem cú kéo có dính. Python: <c>drag_attach_verify_ms 4</c>.</summary>
    public int DragAttachVerifyMs { get; set; } = 4;

    /// <summary>Số khung đọc xác nhận sau một cú kéo. Python: <c>post_drag_confirm_frames 2</c>.</summary>
    public int PostDragConfirmFrames { get; set; } = 2;

    /// <summary>Python: <c>post_drag_confirm_gap_ms 3</c>.</summary>
    public int PostDragConfirmGapMs { get; set; } = 3;

    /// <summary>
    /// Số khung đọc THĂM DÒ trước khi kéo lại. Python: <c>pre_retry_probe_frames 2</c> — đọc
    /// không bấm gì, để bắt ca dây dính muộn vài khung và tránh kéo lại cái đã nối.
    /// </summary>
    public int PreRetryProbeFrames { get; set; } = 2;

    /// <summary>Python: <c>pre_retry_probe_gap_ms 6</c>.</summary>
    public int PreRetryProbeGapMs { get; set; } = 6;

    /// <summary>Nghỉ giữa hai lần kéo lại. Python: <c>drag_retry_pause_ms 30</c>.</summary>
    public int DragRetryPauseMs { get; set; } = 30;

    /// <summary>
    /// Sau khi cắm đủ thì đợi ít nhất ngần này mới đọc kết quả — game cần một nhịp mới vẽ lại.
    /// Python: <c>feedback_min_after_submit_ms 205</c>.
    /// </summary>
    public int FeedbackMinMs { get; set; } = 205;

    /// <summary>Python: <c>feedback_timeout_ms 2500</c>.</summary>
    public int FeedbackTimeoutMs { get; set; } = 2500;

    /// <summary>Python: <c>feedback_poll_ms 14</c>.</summary>
    public int FeedbackPollMs { get; set; } = 14;

    /// <summary>
    /// Phải đọc được ngần này khung GIỐNG NHAU liên tiếp mới tin. Python:
    /// <c>feedback_stable_frames 3</c> — game rung màn sau lượt sai, một khung lẻ là rác.
    /// </summary>
    public int FeedbackStableFrames { get; set; } = 3;

    /// <summary>Python: <c>feedback_stable_gap_ms 18</c>.</summary>
    public int FeedbackStableGapMs { get; set; } = 18;

    /// <summary>
    /// Game khoá tương tác một lát sau lượt sai. Python: <c>post_feedback_cooldown_ms 480</c> —
    /// bản Python nói rõ đừng coi khoảng này là thất bại.
    /// </summary>
    public int PostFeedbackCooldownMs { get; set; } = 480;

    /// <summary>
    /// Ca không được phép spam: mọi dây trước vẫn dính, chỉ dây CUỐI rời ra và giữ nguyên thế.
    /// Sau ngần này thì coi như game ĐÃ kiểm tra và dùng phản hồi đó để loại trừ, thay vì cắm lại
    /// đúng phương án vừa sai. Python: <c>submit_assume_check_ms 620</c>.
    /// </summary>
    public int SubmitAssumeCheckMs { get; set; } = 620;

    // ---------------- suy luan phan hoi ----------------

    /// <summary>
    /// Tâm của hàm logistic quy điểm hình học thành xác suất "dây này đúng". Python:
    /// <c>feedback_probability_center 1.50</c> — nằm giữa <see cref="LockGeomLow"/> và
    /// <see cref="LockGeomHigh"/>.
    /// </summary>
    public double FeedbackProbabilityCenter { get; set; } = 1.50;

    /// <summary>Độ dốc của hàm logistic đó. Python: <c>feedback_probability_scale 0.12</c>.</summary>
    public double FeedbackProbabilityScale { get; set; } = 0.12;

    /// <summary>
    /// Khoảng cách log-likelihood tối thiểu giữa phương án nhất và nhì để dám chốt phản hồi.
    /// Chưa đủ cách biệt thì bot KHÔNG đoán — nó dừng, vì đoán sai ở đây là cắm lại đúng phương
    /// án vừa sai và ăn thêm một lần điện giật. Python: <c>feedback_mask_log_margin 0.80</c>.
    /// </summary>
    public double FeedbackMaskLogMargin { get; set; } = 0.80;

    /// <summary>Không thấy panel nữa trong ngần này thì coi như đã xong và dừng.</summary>
    public int PanelGoneMs { get; set; } = 1_500;

    /// <summary>Chưa từng thấy panel nào sau ngần này thì dừng: đứng sai chỗ.</summary>
    public int NoPanelMs { get; set; } = 20_000;

    public void Normalize()
    {
        PanelColorTolerance = Math.Clamp(PanelColorTolerance <= 0 ? 10 : PanelColorTolerance, 1, 80);
        AnchorColorTolerance = Math.Clamp(AnchorColorTolerance <= 0 ? 42 : AnchorColorTolerance, 4, 120);
        PanelMinWidth = Math.Clamp(PanelMinWidth <= 0 ? 250 : PanelMinWidth, 40, 1800);
        PanelMinHeight = Math.Clamp(PanelMinHeight <= 0 ? 250 : PanelMinHeight, 40, 1000);
        ProfileAspectSplit = Math.Clamp(ProfileAspectSplit <= 0 ? 1.18 : ProfileAspectSplit, 0.5, 4.0);

        SlotPatchXFrac = Math.Clamp(SlotPatchXFrac <= 0 ? 0.022 : SlotPatchXFrac, 0.004, 0.20);
        SlotPatchYFrac = Math.Clamp(SlotPatchYFrac <= 0 ? 0.035 : SlotPatchYFrac, 0.004, 0.20);
        SlotMinColorPixels = Math.Clamp(SlotMinColorPixels <= 0 ? 18 : SlotMinColorPixels, 1, 5_000);

        LockGeomLow = Math.Clamp(LockGeomLow <= 0 ? 1.35 : LockGeomLow, 1.01, 6.0);
        LockGeomHigh = Math.Clamp(LockGeomHigh <= 0 ? 1.65 : LockGeomHigh, LockGeomLow, 8.0);
        PreDragSkipGeom = Math.Clamp(PreDragSkipGeom <= 0 ? 1.30 : PreDragSkipGeom, 1.01, 8.0);
        PostDragAcceptGeom = Math.Clamp(PostDragAcceptGeom <= 0 ? 1.30 : PostDragAcceptGeom, 1.01, 8.0);
        PostDragFastGeom = Math.Clamp(PostDragFastGeom <= 0 ? 1.58 : PostDragFastGeom, 1.01, 8.0);
        SubmitAttachGeom = Math.Clamp(SubmitAttachGeom <= 0 ? 1.42 : SubmitAttachGeom, 1.01, 8.0);

        SearchPollMs = Math.Clamp(SearchPollMs <= 0 ? 220 : SearchPollMs, 60, 1_000);
        SolvePollMs = Math.Clamp(SolvePollMs <= 0 ? 45 : SolvePollMs, 10, 400);
        DragMs = Math.Clamp(DragMs <= 0 ? 24 : DragMs, 4, 600);
        DragSteps = Math.Clamp(DragSteps <= 0 ? 3 : DragSteps, 1, 40);
        RetryDragMs = Math.Clamp(RetryDragMs <= 0 ? 52 : RetryDragMs, 4, 1_200);
        RetryDragSteps = Math.Clamp(RetryDragSteps <= 0 ? 5 : RetryDragSteps, 1, 60);
        DragMaxRetries = Math.Clamp(DragMaxRetries <= 0 ? 8 : DragMaxRetries, 1, 40);
        DragAcceptTimeoutMs = Math.Clamp(DragAcceptTimeoutMs <= 0 ? 2_600 : DragAcceptTimeoutMs, 200, 20_000);
        BetweenDragMs = Math.Clamp(BetweenDragMs < 0 ? 2 : BetweenDragMs, 0, 2_000);
        AfterLastDragMs = Math.Clamp(AfterLastDragMs < 0 ? 2 : AfterLastDragMs, 0, 2_000);
        DragAttachVerifyMs = Math.Clamp(DragAttachVerifyMs < 0 ? 4 : DragAttachVerifyMs, 0, 500);
        PostDragConfirmFrames = Math.Clamp(PostDragConfirmFrames <= 0 ? 2 : PostDragConfirmFrames, 1, 10);
        PostDragConfirmGapMs = Math.Clamp(PostDragConfirmGapMs < 0 ? 3 : PostDragConfirmGapMs, 0, 200);
        PreRetryProbeFrames = Math.Clamp(PreRetryProbeFrames <= 0 ? 2 : PreRetryProbeFrames, 1, 10);
        PreRetryProbeGapMs = Math.Clamp(PreRetryProbeGapMs < 0 ? 6 : PreRetryProbeGapMs, 0, 200);
        DragRetryPauseMs = Math.Clamp(DragRetryPauseMs < 0 ? 30 : DragRetryPauseMs, 0, 2_000);

        FeedbackMinMs = Math.Clamp(FeedbackMinMs <= 0 ? 205 : FeedbackMinMs, 30, 3_000);
        FeedbackTimeoutMs = Math.Clamp(FeedbackTimeoutMs <= 0 ? 2_500 : FeedbackTimeoutMs, 300, 20_000);
        FeedbackPollMs = Math.Clamp(FeedbackPollMs <= 0 ? 14 : FeedbackPollMs, 4, 200);
        FeedbackStableFrames = Math.Clamp(FeedbackStableFrames <= 0 ? 3 : FeedbackStableFrames, 1, 12);
        FeedbackStableGapMs = Math.Clamp(FeedbackStableGapMs <= 0 ? 18 : FeedbackStableGapMs, 2, 300);
        PostFeedbackCooldownMs = Math.Clamp(PostFeedbackCooldownMs <= 0 ? 480 : PostFeedbackCooldownMs, 0, 5_000);
        SubmitAssumeCheckMs = Math.Clamp(SubmitAssumeCheckMs <= 0 ? 620 : SubmitAssumeCheckMs, 100, 10_000);

        FeedbackProbabilityCenter = Math.Clamp(
            FeedbackProbabilityCenter <= 0 ? 1.50 : FeedbackProbabilityCenter, 1.01, 8.0);
        FeedbackProbabilityScale = Math.Clamp(
            FeedbackProbabilityScale <= 0 ? 0.12 : FeedbackProbabilityScale, 0.03, 4.0);
        FeedbackMaskLogMargin = Math.Clamp(
            FeedbackMaskLogMargin < 0 ? 0.80 : FeedbackMaskLogMargin, 0.0, 40.0);

        PanelGoneMs = Math.Clamp(PanelGoneMs <= 0 ? 1_500 : PanelGoneMs, 200, 20_000);
        NoPanelMs = Math.Clamp(NoPanelMs <= 0 ? 20_000 : NoPanelMs, 2_000, 300_000);
    }
}

/// <summary>
/// Hằng số minigame BẢNG WATER &amp; POWER. Đo từ <c>water_power_solver_core_v13.Config</c>.
///
/// Mọi số pixel ở đây là mốc 1920×1080 và phải nhân <see cref="ElectricProfile.Scale"/> (hoặc
/// bình phương nó với diện tích) trước khi dùng — đúng như bản Python làm với các hằng
/// <c>*_1080P</c>. Đừng gõ sẵn hai bộ số cho 2K và FHD: bản Python đã chứng minh một bộ + tỉ lệ
/// là đủ, và hai bộ số là hai chỗ để lệch nhau.
/// </summary>
internal sealed class BoardSettings
{
    // ---------------- chu ky tieu de ----------------

    /// <summary>Dải Hue của chữ tiêu đề bảng, quy ước OpenCV (H 0–179). Python: 70–95.</summary>
    public int TitleHueMin { get; set; } = 70;

    public int TitleHueMax { get; set; } = 95;

    /// <summary>Python: <c>TITLE_S_MIN 120</c>, <c>TITLE_V_MIN 90</c>.</summary>
    public int TitleSatMin { get; set; } = 120;

    public int TitleValMin { get; set; } = 90;

    /// <summary>
    /// Số pixel tiêu đề tối thiểu để coi là bảng đang mở, ở mốc 1080p. Python:
    /// <c>TITLE_MIN_PIXELS_1080P 6000</c>, và nó nhân <c>sx*sy</c> (tức diện tích) chứ không phải
    /// nhân tỉ lệ dài.
    /// </summary>
    public int TitleMinPixels { get; set; } = 6_000;

    // ---------------- vach tuong / vat can ----------------

    /// <summary>Bán kính blur khi ước lượng ngưỡng V thích ứng. Python: <c>PANEL_BLUR_PX_1080P 25</c>.</summary>
    public int PanelBlurPx { get; set; } = 25;

    /// <summary>Python: <c>PANEL_CLOSE_PX_1080P 9</c>.</summary>
    public int PanelClosePx { get; set; } = 9;

    /// <summary>Khối nhỏ hơn ngần này (diện tích, mốc 1080p) không phải tường. Python: 3500.</summary>
    public int PanelMinComponentArea { get; set; } = 3_500;

    // ---------------- luoi va duong di ----------------
    //
    // KHONG co "GridPx / NavPaddingPx / PortGapPx / MinRouteLengthPx / MaxRouteLengthPx" o day
    // nua. Ca nam deu la CFG cua nhanh Python DA CHET (core_v10/core_v13), va ca nam deu KHONG
    // duoc doc o bat ky dau — BoardPlanner co GridFallbackRefs/InflationRadiiRef rieng lay tu
    // v75_planner (luoi 12/10/8, no 18..6), BoardReader co PortGapPx rieng.
    //
    // Hai cai cuoi khong chi la rac: chung tung duoc noi vao BoardPlanner va da giet mot ban do
    // that ngay 22/08 (tuyen 4343px bi tu choi vi tran 4000px). Xem ghi chu cuoi BoardPlanner.

    // ---------------- chay tuyen ----------------
    //
    // KHONG co "TargetTolerancePx / AcceptOvershootPx / AbortOvershootPx / FineZonePx /
    // MaxOrthogonalDriftPx / MinExpectedSpeedPxS" o day nua, va day la ly do — de khong ai them
    // lai chung:
    //
    // Sau day TU CHAY. Phim huong chi de RE, khong phai de di, nen "toi dung pixel goc roi dung
    // lai" la viec khong lam duoc, va "vuot qua goc" la chuyen binh thuong chu khong phai loi.
    // Sau con so do ke tren la CFG cua bo dieu khien Python DA BI BO (chung chi con ton tai trong
    // core_v10.py / core_v13.py; chuoi dang chay that v75 -> v71_controller khong doc cai nao).
    // Ban C# dau tien lam theo chung va chet o doan thu hai cua moi tuyen. Xem phan dau BoardBot.
    //
    // Cac nguong cua bo dieu khien moi nam trong BoardBot duoi dang const: chung mo ta hanh vi cua
    // game (do tre input, quang duong can de thay duoc cu re), khong phai khau vi nguoi dung.

    // ---------------- phat hien that bai ----------------
    //
    // KHONG co "FailRedMinPixels / FailCheckRadiusPx" o day nua. Chung dieu khien phep kiem "co dom
    // DO quanh dau day khong" va da giet oan mot luot DANG THANG ngay 22/08 (bang #2, doan 17/19,
    // con 27px la tram). Ly do la cau truc chu khong phai lech nguong: dau noi DICH la mot khoi DO
    // co dinh va tuyen bao gio cung ket thuc o do, nen o quang cuoi moi tuyen no luon nam trong hop
    // kiem ban kinh 140px. Bang truoc do thoat duoc chi vi tuyen it hon mot doan.
    //
    // Va chung cung la CFG cua nhanh Python DA CHET — chi ton tai trong core_v10/core_v13, chi duoc
    // doc boi dynamic_red_near_tip, ma ham do chi duoc goi tu hai cho trong cung file chet do.
    // Chuoi dang chay that (v75 -> v71_controller) KHONG kiem mau do o dau ca.
    //
    // Bay gio va tuong duoc phat hien bang VAT LY: day va tuong thi no ngung chay, va nguong
    // TrackerBlindMs cua BoardBot bat duoc trong 200ms. Xem ghi chu day hon o cho da bo RedNearTip
    // trong BoardBot.cs.

    // ---------------- nhip ----------------

    /// <summary>Nhịp chờ bảng xuất hiện.</summary>
    public int WatchPollMs { get; set; } = 120;

    /// <summary>Phải thấy bảng ngần này khung liên tiếp mới giải. Python: <c>DETECT_STABLE_FRAMES 2</c>.</summary>
    public int DetectStableFrames { get; set; } = 2;

    /// <summary>
    /// Số khung tường gần như y hệt nhau cần có trước khi đóng băng tuyến.
    ///
    /// Bản Python đòi 3, nhưng nó chụp bằng dxcam ở nhịp ~2 ms nên ba khung của nó chỉ trải
    /// khoảng 60–90 ms. Ở đây một khung tốn ~175 ms (đo trên ROI 2K, bản Release), nên HAI khung
    /// đã trải ~350 ms — bằng chứng "bảng đã vẽ xong" mạnh hơn hẳn ba khung cách nhau 30 ms.
    ///
    /// Đây là chỗ đắt nhất trong ngân sách thời gian: sợi dây tự chạy và cú rẽ đầu tiên trên bảng
    /// đo được chỉ cách START 130 px, tức khoảng 0.2–0.5 giây. Mỗi khung đòi thêm là một khung có
    /// thể làm bot trễ chuyến.
    /// </summary>
    public int WallStableFrames { get; set; } = 2;

    /// <summary>Mất bảng ngần này khung thì coi như đã đóng. Python: <c>RESET_MISSING_FRAMES 20</c>.</summary>
    public int ResetMissingFrames { get; set; } = 20;

    /// <summary>Chưa thấy bảng nào sau ngần này thì dừng: đứng sai chỗ.</summary>
    public int NoBoardMs { get; set; } = 20_000;

    public void Normalize()
    {
        TitleHueMin = Math.Clamp(TitleHueMin < 0 ? 70 : TitleHueMin, 0, 179);
        TitleHueMax = Math.Clamp(TitleHueMax <= 0 ? 95 : TitleHueMax, TitleHueMin, 179);
        TitleSatMin = Math.Clamp(TitleSatMin <= 0 ? 120 : TitleSatMin, 0, 255);
        TitleValMin = Math.Clamp(TitleValMin <= 0 ? 90 : TitleValMin, 0, 255);
        TitleMinPixels = Math.Clamp(TitleMinPixels <= 0 ? 6_000 : TitleMinPixels, 100, 2_000_000);

        PanelBlurPx = Math.Clamp(PanelBlurPx <= 0 ? 25 : PanelBlurPx, 3, 200);
        PanelClosePx = Math.Clamp(PanelClosePx <= 0 ? 9 : PanelClosePx, 1, 100);
        PanelMinComponentArea = Math.Clamp(PanelMinComponentArea <= 0 ? 3_500 : PanelMinComponentArea, 50, 500_000);

        WatchPollMs = Math.Clamp(WatchPollMs <= 0 ? 120 : WatchPollMs, 20, 1_000);
        DetectStableFrames = Math.Clamp(DetectStableFrames <= 0 ? 2 : DetectStableFrames, 1, 20);
        WallStableFrames = Math.Clamp(WallStableFrames <= 0 ? 2 : WallStableFrames, 2, 10);
        ResetMissingFrames = Math.Clamp(ResetMissingFrames <= 0 ? 20 : ResetMissingFrames, 1, 200);
        NoBoardMs = Math.Clamp(NoBoardMs <= 0 ? 20_000 : NoBoardMs, 2_000, 300_000);
    }
}

/// <summary>Người dùng muốn tab Điện lo minigame nào.</summary>
internal enum ElectricMode
{
    /// <summary>Chỉ panel đi dây.</summary>
    Wire,

    /// <summary>Chỉ bảng Water &amp; Power.</summary>
    Board,

    /// <summary>Cả hai — cái nào hiện thì giải cái đó.</summary>
    Both
}

/// <summary>
/// Cài đặt job Thợ điện: hai minigame của nghề, panel ĐI DÂY và bảng WATER &amp; POWER.
///
/// Vì sao hai thứ chung một file config mà hằng số lại tách làm hai lớp
/// (<see cref="WireSettings"/> / <see cref="BoardSettings"/>): chúng chung ĐÚNG hai thứ — cửa sổ
/// game và profile màn hình — còn lại không có gì giống nhau, đúng như <see cref="WoodConfig"/> đã
/// lập luận khi từ chối nhập vào <see cref="MinerConfig"/>. Nhưng chúng là MỘT nghề, người dùng
/// bấm một phím và chọn một màn hình, nên tách thành hai file json là bắt họ hiệu chuẩn hai lần
/// cho cùng một cái màn.
/// </summary>
internal sealed class ElectricConfig
{
    /// <summary>
    /// Độ phân giải mốc mà mọi hằng số pixel được đo ở đó. Bằng <c>CFG.REF_W/REF_H</c> của
    /// <c>core_v13</c> và <c>BASE_W/BASE_H</c> của Navigator — cả hai bản Python đều lấy 1920×1080
    /// làm mốc, nên đem số của chúng sang đây dùng được nguyên.
    /// </summary>
    public const double RefW = 1920.0;

    public const double RefH = 1080.0;

    public WireSettings Wire { get; set; } = new();

    public BoardSettings Board { get; set; } = new();

    /// <summary>Minigame nào được bật. Mặc định cả hai — nghề này gặp cả hai màn.</summary>
    public ElectricMode Mode { get; set; } = ElectricMode.Both;

    /// <summary>Chỉ bắn phím/chuột khi tiêu đề cửa sổ foreground chứa chuỗi này.</summary>
    public string WindowMatch { get; set; } = "PlayXGTA";

    /// <summary>Giây đếm ngược để người dùng bày màn hình trước khi chụp ảnh tĩnh.</summary>
    public int ShotCountdownSec { get; set; } = 5;

    public Dictionary<string, ElectricProfile> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        Wire ??= new WireSettings();
        Board ??= new BoardSettings();
        Wire.Normalize();
        Board.Normalize();

        if (!Enum.IsDefined(Mode)) Mode = ElectricMode.Both;
        if (string.IsNullOrWhiteSpace(WindowMatch)) WindowMatch = "PlayXGTA";
        ShotCountdownSec = Math.Clamp(ShotCountdownSec <= 0 ? 5 : ShotCountdownSec, 2, 30);

        Profiles ??= new Dictionary<string, ElectricProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Profiles.Values) p?.Normalize();
    }

    // ---------------- profile ----------------

    public ElectricProfile GetOrCreate(Screen screen)
    {
        var b = screen.Bounds;
        string key = $"{b.Width}x{b.Height}";
        if (!Profiles.TryGetValue(key, out var p) || p is null)
        {
            p = new ElectricProfile { Device = screen.DeviceName, Width = b.Width, Height = b.Height };
            Profiles[key] = p;
        }
        else
        {
            p.Device = screen.DeviceName;
            p.Width = b.Width;
            p.Height = b.Height;
        }
        p.Normalize();
        return p;
    }

    // ---------------- duong dan tai san ----------------

    public static string DefaultPath => Path.Combine(AppPaths.Root, "electric.json");

    public static string ProfileDir(string key) => Path.Combine(AppPaths.Root, "electric", key);

    public static string ShotDir(string key) => Path.Combine(ProfileDir(key), "shots");

    public static string ShotPath(string key, string name) =>
        Path.Combine(ShotDir(key), name + ".png");

    /// <summary>Nơi <c>--verify-wire</c> / <c>--verify-board</c> đổ ảnh trung gian.</summary>
    public static string DebugDir(string key) => Path.Combine(ProfileDir(key), "debug");

    // ---------------- luu / doc ----------------

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public void Save(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* khong ghi duoc thi van chay voi cai dat dang dung */ }
    }

    public static ElectricConfig Load(string path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<ElectricConfig>(File.ReadAllText(path), Opts);
                if (cfg is not null)
                {
                    cfg.Profiles = new Dictionary<string, ElectricProfile>(
                        cfg.Profiles ?? new(), StringComparer.OrdinalIgnoreCase);
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch { /* file hong -> ve mac dinh */ }

        var fresh = new ElectricConfig();
        fresh.Normalize();
        return fresh;
    }
}
