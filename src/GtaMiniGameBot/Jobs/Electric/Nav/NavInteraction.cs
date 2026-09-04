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
}
