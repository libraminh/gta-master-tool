namespace GtaMiniGameBot;

/// <summary>
/// Cổng E song song với điều hướng: đếm prompt theo snapshot thật, chỉ arm khi đang tiếp cận
/// điểm vàng, và tách settle ngắn khỏi thời gian chờ panel (không khóa W).
/// </summary>
internal static class NavInteraction
{
    public const string Settle = "SETTLE";
    public const string Watch = "WATCH";

    /// <summary>
    /// Một frame prompt. Cùng <paramref name="seq"/> không tăng streak — scanner world chậm hơn
    /// vòng nav 25 ms nên hai tick có thể nhìn một snapshot.
    /// </summary>
    public static bool NotePrompt(bool visible, int seq, ref int lastSeq, ref int streak, ref int absent,
                                 ref bool consumed, double now, double retryUntil)
    {
        if (seq == lastSeq)
            return visible && streak >= NavTuning.SimplePromptStableFrames;

        lastSeq = seq;
        if (visible)
        {
            streak++;
            absent = 0;
        }
        else
        {
            streak = 0;
            absent++;
            if (absent >= NavTuning.SimplePromptRearmAbsentFrames && now >= retryUntil)
                consumed = false;
        }
        return visible && streak >= NavTuning.SimplePromptStableFrames;
    }

    /// <summary>
    /// Bằng chứng tiếp cận cuối — tái sử dụng ngưỡng shield/pass-through/world takeover, cộng
    /// <see cref="NavTuning.InteractionArmDistPx"/>. Prompt đơn độc không đủ.
    /// </summary>
    public static bool ApproachReady(double dist, double rel, double px, string quality, double confidence,
                                    bool worldPresent, double worldConf, double worldArea,
                                    string lastState, double shieldUntil, double now)
    {
        bool aligned = double.IsFinite(rel) && Math.Abs(rel) <= NavTuning.ArrivalShieldEntryAngleDeg;
        bool nearDot = double.IsFinite(dist)
                       && dist <= NavTuning.InteractionArmDistPx * px
                       && aligned
                       && confidence >= NavTuning.ArrivalShieldMinConf
                       && quality != "PREDICT_ONLY";
        if (nearDot) return true;
        if (now <= shieldUntil && aligned) return true;

        if (lastState is not null
            && (lastState.StartsWith("RAM_V63_PASS_THROUGH", StringComparison.Ordinal)
                || lastState.StartsWith("RAM_PASS_THROUGH", StringComparison.Ordinal)
                || lastState.StartsWith("WORLD_TRIGGER_COAST", StringComparison.Ordinal)
                || lastState.StartsWith("ARC_ARRIVAL_COAST", StringComparison.Ordinal)))
            return true;

        return worldPresent
               && worldConf >= NavTuning.WorldInstantTakeoverConf
               && worldArea >= NavTuning.WorldInstantTakeoverMinArea;
    }

    public static bool RetryReady(double now, double retryUntil) => now >= retryUntil;

    public static bool IsApproachState(string state) =>
        state is not null
        && (state.StartsWith("RAM_V63_PASS_THROUGH", StringComparison.Ordinal)
            || state.StartsWith("RAM_PASS_THROUGH", StringComparison.Ordinal)
            || state.StartsWith("WORLD_TRIGGER_COAST", StringComparison.Ordinal)
            || state.StartsWith("ARC_ARRIVAL_COAST", StringComparison.Ordinal));

    /// <summary>
    /// Prompt công việc ổn định khi đang SEARCH360 / vừa mất đích — được bấm E dù không còn dist/rel.
    /// </summary>
    public static bool LostTargetArm(bool workPromptStable, string lastState, int search360Round)
    {
        if (!workPromptStable) return false;
        if (search360Round > 0) return true;
        return lastState is not null && lastState.StartsWith("SEARCH360", StringComparison.Ordinal);
    }

    /// <summary>
    /// Sau xin việc NPC hiện cùng HUD <c>[E] TƯƠNG TÁC</c>. Khiên chặn mọi E (kể cả
    /// <see cref="LostTargetArm"/>); hết khiên thì vẫn chặn lost-arm cho tới khi sát điểm vàng.
    /// </summary>
    public static bool PostJobBlocksWorldE(bool shield, bool needYellow, bool lostArm, bool approachReady)
    {
        if (shield) return true;
        return needYellow && lostArm && !approachReady;
    }

    /// <summary>Prompt rộng hoặc ROI chặt đều nghĩa là còn đứng cạnh NPC.</summary>
    public static bool PostJobPromptHoldsShield(bool wideVisible, bool workVisible) =>
        wideVisible || workVisible;

    /// <summary>
    /// Gỡ khiên khi prompt đã tắt đủ lâu: đã thấy thì ≥ minGuard + clearFrames;
    /// chưa thấy thì ≥ noPromptTimeout + clearFrames.
    /// </summary>
    public static bool PostJobClearShield(bool seen, int absentFrames, double elapsed,
                                         int clearFrames, double minGuardS, double noPromptTimeoutS)
    {
        if (absentFrames < clearFrames) return false;
        if (seen) return elapsed >= minGuardS;
        return elapsed >= noPromptTimeoutS;
    }

    /// <summary>
    /// Sau E nhanh: bảng nghề mở trong khi minimap còn điểm vàng → đi ngang NPC, ESC.
    /// Đang reset nghề thì JobRecovery giữ bảng.
    /// </summary>
    public static bool AfterEEscAccidentalNpc(bool inJobRecovery, bool yellowVisible) =>
        !inJobRecovery && yellowVisible;

    /// <summary>Bảng nghề mở, không còn điểm vàng, chưa trong recovery → vào WaitBoard.</summary>
    public static bool AfterEEnterOpenBoard(bool inJobRecovery, bool yellowVisible) =>
        !inJobRecovery && !yellowVisible;
}

/// <summary>
/// Cổng ngắt panel toàn cục: hai (hoặc ba khi reset nghề) hit <c>PanelVisible</c> độc lập.
/// Bảng xin/nghỉ nghề (3 nút cyan) huỷ ứng viên ngay — không giao cho bộ giải nước/điện.
/// </summary>
internal sealed class NavPanelInterrupt
{
    private int _streak;

    public int Streak => _streak;

    public void Reset() => _streak = 0;

    public bool Note(bool visible, bool npcBoard)
    {
        if (npcBoard || !visible)
        {
            Reset();
            return false;
        }
        _streak++;
        return true;
    }

    public bool Confirmed(bool jobRecovery) =>
        _streak >= (jobRecovery ? NavTuning.PanelInterruptJobHits : NavTuning.PanelInterruptHits);
}
