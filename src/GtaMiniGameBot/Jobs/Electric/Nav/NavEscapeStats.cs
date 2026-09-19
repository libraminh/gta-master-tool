using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Bộ đếm thoát kẹt — STATIC vì <see cref="NavBot"/> bị tạo mới mỗi lượt (mỗi lần đi tới một máy là
/// một đối tượng khác), nên số liệu để trong nó chỉ sống được một chuyến và không so A/B được.
///
/// Đây là dụng cụ ĐO của PR này: bật/tắt <see cref="NavSettings.EscapeLadderEnabled"/> hai buổi ≥ 20
/// phút rồi so hai dòng <c>[TỔNG KẸT PHIÊN]</c>. Số quan trọng nhất là <c>giây/cú</c> và <c>tỉ lệ
/// thoát ở bậc 0–1</c> (thoát rẻ), rồi tới <c>số lần tới bậc chót</c> (thang đã thua, phải nhảy KET1).
///
/// Mọi truy cập đi qua một khoá: <see cref="NavBot"/> chạy trên luồng riêng còn <see cref="ElectricBot"/>
/// đọc tổng kết trên luồng điều phối.
/// </summary>
internal static class NavEscapeStats
{
    /// <summary>
    /// Số bậc của thang (0..3) — xem <see cref="NavEscapeLadder"/>. Từ 19/09 còn bốn bậc: bậc trượt
    /// ngang thuần bị xoá vì chỉ cứu 1/40 đợt trên hai buổi thử thật.
    /// </summary>
    public const int RungCount = 4;

    /// <summary>Bậc chót (KET1 cũ) — thang đã thua ở mọi bậc rẻ hơn.</summary>
    public const int LastRung = RungCount - 1;

    /// <summary>Một bộ đếm độc lập: một cho LƯỢT hiện tại, một cho cả PHIÊN.</summary>
    public sealed class Counters
    {
        /// <summary>Cú kẹt được watchdog xác nhận (đếm cả khi tắt thang — đây là số so A/B chính).</summary>
        public int Stuck;

        /// <summary>Đợt kẹt mở ra.</summary>
        public int Episodes;

        /// <summary>Số lần kẹt lại CÙNG CHỖ nên nhập vào đợt đang có thay vì mở đợt mới.</summary>
        public int Joins;

        /// <summary>Đợt bị huỷ giữa chừng (đầu nối hiện lại và đang tiến, reset nghề…).</summary>
        public int Cancelled;

        /// <summary>Số lần thang phải leo tới bậc chót (KET1 cũ) — thang đã thua ở mọi bậc rẻ hơn.</summary>
        public int RungLast;

        /// <summary>Số cú KET1 chạy ĐỘC LẬP (tắt thang, hoặc nguồn LIGHTNING_*).</summary>
        public int Ket1Runs;

        public readonly int[] Attempts = new int[RungCount];
        public readonly int[] Success = new int[RungCount];

        /// <summary>Tổng giây nằm trong thang/KET1.</summary>
        public double TotalS;

        /// <summary>Đợt dài nhất (giây).</summary>
        public double LongestS;

        public void Clear()
        {
            Stuck = Episodes = Joins = Cancelled = RungLast = Ket1Runs = 0;
            Array.Clear(Attempts);
            Array.Clear(Success);
            TotalS = 0;
            LongestS = 0;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"kẹt {Stuck} · đợt {Episodes}");
            if (Joins > 0) sb.Append($" (kẹt lại cùng chỗ {Joins})");
            sb.Append($" · {TotalS:F1}s thoát kẹt");
            if (Stuck > 0) sb.Append($" ({TotalS / Stuck:F2}s/cú)");
            if (LongestS > 0) sb.Append($" · đợt dài nhất {LongestS:F1}s");

            var rungs = new List<string>();
            for (int k = 0; k < RungCount; k++)
                if (Attempts[k] > 0) rungs.Add($"b{k} {Success[k]}/{Attempts[k]}");
            if (rungs.Count > 0) sb.Append(" · thoát/thử " + string.Join(" ", rungs));

            if (RungLast > 0) sb.Append($" · tới bậc chót (KET1): {RungLast}");
            if (Cancelled > 0) sb.Append($" · huỷ giữa chừng {Cancelled}");
            if (Ket1Runs > 0) sb.Append($" · KET1 rời {Ket1Runs}");
            return sb.ToString();
        }
    }

    private static readonly object Lock = new();
    private static readonly Counters TripC = new();
    private static readonly Counters SessionC = new();

    /// <summary>Gọi ở đầu mỗi lượt (<c>NavBot.Run</c>).</summary>
    public static void ResetTrip()
    {
        lock (Lock) TripC.Clear();
    }

    /// <summary>Gọi ở đầu phiên (<c>ElectricBot.Run</c>) — cũng xoá luôn bộ đếm lượt.</summary>
    public static void ResetSession()
    {
        lock (Lock)
        {
            SessionC.Clear();
            TripC.Clear();
        }
    }

    /// <summary>Watchdog xác nhận một cú kẹt. Trả số thứ tự trong PHIÊN để in <c>[KẸT] #n</c>.</summary>
    public static int NoteStuck()
    {
        lock (Lock)
        {
            TripC.Stuck++;
            return ++SessionC.Stuck;
        }
    }

    /// <summary>Mở đợt. <paramref name="joined"/> = kẹt lại cùng chỗ nên nhập vào đợt cũ.</summary>
    public static void NoteEpisode(bool joined)
    {
        lock (Lock)
        {
            if (joined) { TripC.Joins++; SessionC.Joins++; }
            else { TripC.Episodes++; SessionC.Episodes++; }
        }
    }

    /// <summary>Bắt đầu thử một bậc.</summary>
    public static void NoteRung(int rung)
    {
        if (rung < 0 || rung >= RungCount) return;
        lock (Lock)
        {
            TripC.Attempts[rung]++;
            SessionC.Attempts[rung]++;
            if (rung == LastRung) { TripC.RungLast++; SessionC.RungLast++; }
        }
    }

    /// <summary>Đợt đóng vì ĐÃ THOÁT ở bậc <paramref name="rung"/>.</summary>
    public static void NoteEscaped(int rung, double seconds)
    {
        lock (Lock)
        {
            if (rung >= 0 && rung < RungCount) { TripC.Success[rung]++; SessionC.Success[rung]++; }
            AddTime(seconds);
        }
    }

    /// <summary>Đợt bị huỷ giữa chừng.</summary>
    public static void NoteCancelled(double seconds)
    {
        lock (Lock)
        {
            TripC.Cancelled++;
            SessionC.Cancelled++;
            AddTime(seconds);
        }
    }

    /// <summary>Một cú KET1 chạy ngoài thang (tắt thang, hoặc nguồn LIGHTNING_*) đã xong.</summary>
    public static void NoteKet1(double seconds)
    {
        lock (Lock)
        {
            TripC.Ket1Runs++;
            SessionC.Ket1Runs++;
            AddTime(seconds);
        }
    }

    /// <summary>Phải gọi TRONG <see cref="Lock"/>.</summary>
    private static void AddTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        TripC.TotalS += seconds;
        SessionC.TotalS += seconds;
        if (seconds > TripC.LongestS) TripC.LongestS = seconds;
        if (seconds > SessionC.LongestS) SessionC.LongestS = seconds;
    }

    public static string TripSummary()
    {
        lock (Lock) return TripC.Describe();
    }

    public static string SessionSummary()
    {
        lock (Lock) return SessionC.Describe();
    }

    /// <summary>Có gì để in không — phiên không kẹt lần nào thì khỏi làm bẩn log.</summary>
    public static bool SessionHasData
    {
        get { lock (Lock) return SessionC.Stuck > 0 || SessionC.Episodes > 0 || SessionC.Ket1Runs > 0; }
    }
}
