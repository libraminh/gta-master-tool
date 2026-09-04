namespace GtaMiniGameBot;

/// <summary>
/// Bộ chọn nước đoán cho minigame đi dây.
///
/// Bài toán: game bốc một phép GÁN 1-1 ẩn giữa n đầu dây và n ổ cắm (n = 3 hoặc 5). Mỗi lần cắm
/// đủ n dây rồi bấm kiểm tra, game chỉ trả lời được đúng một thứ: NHỮNG DÂY NÀO đúng chỗ — chúng
/// dính lại, số còn lại rời ra. Đó là Mastermind với phản hồi theo vị trí, và mỗi dây đã đúng thì
/// đứng yên vĩnh viễn ở các lượt sau.
///
/// Cách chọn: quy hoạch động CHÍNH XÁC, tối thiểu hoá SỐ LƯỢT KIỂM TRA KỲ VỌNG. n ≤ 5 nên nhiều
/// nhất 120 khả năng — bảng nhỏ tới mức không cần xấp xỉ. Đo trên bản Python: kỳ vọng 2.000 lượt
/// với 3 dây, 3.000 lượt với 5 dây.
///
/// Vì sao chỉ đoán trong TẬP CÒN KHẢ THI chứ không đoán tuỳ ý: mỗi lượt kiểm tra vì thế luôn có
/// xác suất kết thúc ngay. Một nước đoán ngoài tập có thể chia thông tin đẹp hơn chút nhưng không
/// bao giờ thắng luôn, mà mỗi lượt trong game này phải trả bằng một lần rung màn và ~0.5s khoá
/// tương tác.
///
/// Lớp này KHÔNG chạm tới pixel — đó là việc của <see cref="WireReader"/>. Tách ra để phần suy
/// luận kiểm chứng được bằng số, không cần game và không cần ảnh.
/// </summary>
internal sealed class WirePolicy
{
    private readonly int _n;
    private readonly int _fullMask;
    private readonly int[][] _perms;

    /// <summary>
    /// <c>_match[s][g]</c> = mặt nạ những vị trí mà hoán vị s và g trùng nhau. Dựng sẵn vì vòng
    /// lặp DP hỏi nó 120×120 lần cho mỗi nút.
    /// </summary>
    private readonly int[][] _match;

    /// <summary>
    /// Nhớ kết quả theo (tập ứng viên, mặt nạ đã chốt). Tập ứng viên gói thành 128 bit — n! ≤ 120
    /// nên vừa đúng hai ulong, không phải cấp phát chuỗi khoá trong vòng đệ quy.
    /// </summary>
    private readonly Dictionary<(ulong, ulong, int), (double Expected, int Guess)> _cache = new();

    public WirePolicy(int n)
    {
        if (n is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(n), n, "Chi ho tro 1..5 day (goi 128-bit).");

        _n = n;
        _fullMask = (1 << n) - 1;
        _perms = Permutations(n);

        _match = new int[_perms.Length][];
        for (int s = 0; s < _perms.Length; s++)
        {
            _match[s] = new int[_perms.Length];
            for (int g = 0; g < _perms.Length; g++)
            {
                int m = 0;
                for (int i = 0; i < n; i++)
                    if (_perms[s][i] == _perms[g][i]) m |= 1 << i;
                _match[s][g] = m;
            }
        }
    }

    public int WireCount => _n;

    public int PermutationCount => _perms.Length;

    /// <summary>Hoán vị thứ <paramref name="index"/>: <c>[i]</c> = ổ cắm mà đầu dây i cắm vào.</summary>
    public int[] Permutation(int index) => _perms[index];

    /// <summary>Toàn bộ chỉ số hoán vị — tập ứng viên ban đầu của một lượt mới.</summary>
    public List<int> AllCandidates()
    {
        var all = new List<int>(_perms.Length);
        for (int i = 0; i < _perms.Length; i++) all.Add(i);
        return all;
    }

    /// <summary>
    /// Thứ tự sinh theo từ điển, cùng thứ tự <c>itertools.permutations</c> của Python — để đối
    /// chiếu số với bản gốc khi cần.
    /// </summary>
    private static int[][] Permutations(int n)
    {
        var outp = new List<int[]>();
        var cur = new int[n];
        var used = new bool[n];

        void Walk(int depth)
        {
            if (depth == n) { outp.Add((int[])cur.Clone()); return; }
            for (int v = 0; v < n; v++)
            {
                if (used[v]) continue;
                used[v] = true;
                cur[depth] = v;
                Walk(depth + 1);
                used[v] = false;
            }
        }

        Walk(0);
        return outp.ToArray();
    }

    // ---------------------------------------------------------------- phan hoi

    /// <summary>
    /// Phản hồi mà game sẽ trả nếu bí mật là <paramref name="secret"/> và ta cắm
    /// <paramref name="guess"/>: mặt nạ các dây ĐANG XÉT mà đúng chỗ.
    /// </summary>
    public int Response(int secret, int guess, int activeMask) => _match[secret][guess] & activeMask;

    /// <summary>
    /// Bỏ khỏi tập ứng viên mọi bí mật không thể sinh ra phản hồi vừa quan sát.
    ///
    /// Đây là chỗ toàn bộ thông tin của lượt vừa rồi được dùng — kể cả thông tin ÂM: dây không
    /// dính nghĩa là bí mật KHÔNG phải ổ cắm đó, và điều đó loại đi rất nhiều khả năng.
    /// </summary>
    public List<int> Filter(IReadOnlyList<int> candidates, int fixedMask, int guess, int lockedMask)
    {
        int active = _fullMask ^ fixedMask;
        var outp = new List<int>(candidates.Count);
        foreach (int s in candidates)
            if (Response(s, guess, active) == lockedMask) outp.Add(s);
        return outp;
    }

    /// <summary>
    /// Lọc CHỈ theo những dây đã biết chắc là đúng, không kết luận gì về các dây còn lại.
    ///
    /// Khác <see cref="Filter"/> ở một điểm quan trọng. <see cref="Filter"/> dùng cho phản hồi
    /// SAU khi game kiểm tra: lúc đó câu trả lời là đầy đủ, nên "dây này không dính" là bằng chứng
    /// chắc chắn rằng bí mật KHÔNG phải ổ đó, và loại được rất nhiều khả năng.
    ///
    /// Hàm này dùng cho bước đồng bộ trạng thái vật lý ở đầu lượt sau, khi bot chỉ QUAN SÁT thấy
    /// vài sợi cáp còn dính. Ở đó "chưa thấy dính" KHÔNG có nghĩa là sai — có thể chỉ là khung đọc
    /// lửng lơ. Dùng <see cref="Filter"/> cho ca này là tự suy ra một điều mắt không nói, và nó có
    /// thể loại mất đúng bí mật thật rồi đẩy cả lượt vào mâu thuẫn.
    /// </summary>
    public List<int> FilterKnownCorrect(IReadOnlyList<int> candidates, int guess, int knownMask)
    {
        var g = _perms[guess];
        var outp = new List<int>(candidates.Count);

        foreach (int s in candidates)
        {
            var sec = _perms[s];
            bool ok = true;
            for (int i = 0; i < _n && ok; i++)
                if (((knownMask >> i) & 1) != 0 && sec[i] != g[i]) ok = false;
            if (ok) outp.Add(s);
        }
        return outp;
    }

    // ---------------------------------------------------------------- chon nuoc

    /// <summary>
    /// Nước cắm tiếp theo, cùng số lượt kiểm tra kỳ vọng còn lại.
    /// Ném nếu tập ứng viên rỗng — đó là mâu thuẫn dữ liệu, gọi bên trên phải xử.
    /// </summary>
    public (double Expected, int Guess) Choose(IReadOnlyList<int> candidates, int fixedMask)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException("Tap ung vien rong: phan hoi doc duoc khong khop bi mat nao.");

        return Value(Pack(candidates), fixedMask, candidates);
    }

    /// <summary>
    /// Số lượt kiểm tra kỳ vọng nếu chơi tối ưu từ trạng thái này. Truyền kèm
    /// <paramref name="list"/> để khỏi giải nén lại bộ bit ở nhánh đã có sẵn danh sách.
    /// </summary>
    private (double Expected, int Guess) Value((ulong Lo, ulong Hi) set, int fixedMask, IReadOnlyList<int> list = null)
    {
        var key = (set.Lo, set.Hi, fixedMask);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var cands = list ?? Unpack(set);

        // Da chot het: khong con luot nao phai danh.
        if (fixedMask == _fullMask)
        {
            var done = (0.0, cands.Count > 0 ? cands[0] : 0);
            _cache[key] = done;
            return done;
        }

        // Chi con mot kha nang: cam no la xong, dung mot luot.
        if (cands.Count == 1)
        {
            var one = (1.0, cands[0]);
            _cache[key] = one;
            return one;
        }

        int active = _fullMask ^ fixedMask;
        double bestVal = double.PositiveInfinity;
        int bestGuess = -1;

        var buckets = new Dictionary<int, List<int>>();

        foreach (int guess in cands)
        {
            buckets.Clear();
            foreach (int secret in cands)
            {
                int r = Response(secret, guess, active);
                if (!buckets.TryGetValue(r, out var bucket))
                {
                    bucket = new List<int>();
                    buckets[r] = bucket;
                }
                bucket.Add(secret);
            }

            // Nuoc khong tach duoc gi VA cung khong the thang: danh no la lap vo han.
            if (buckets.Count == 1 && !buckets.ContainsKey(active)) continue;

            double expNext = 0.0;
            foreach (var (r, bucket) in buckets)
            {
                double p = bucket.Count / (double)cands.Count;
                if (r == active) continue;      // moi day con lai deu dung -> luot nay ket thuc

                var (sub, _) = Value(Pack(bucket), fixedMask | r, bucket);
                expNext += p * sub;
            }

            double val = 1.0 + expNext;

            // So sanh voi khe hep: hai nuoc bang nhau ve ky vong thi lay chi so NHO hon, de cung
            // mot the co luon cho cung mot nuoc — bao dam nay lam cho log tai hien duoc.
            if (val < bestVal - 1e-12 || (Math.Abs(val - bestVal) <= 1e-12 && guess < bestGuess))
            {
                bestVal = val;
                bestGuess = guess;
            }
        }

        // Moi nuoc deu vo ich (khong xay ra voi n<=5, nhung khong de rot xuong -1).
        if (bestGuess < 0) { bestGuess = cands[0]; bestVal = 1.0; }

        var res = (bestVal, bestGuess);
        _cache[key] = res;
        return res;
    }

    // ---------------------------------------------------------------- suy phan hoi

    /// <summary>Ngưỡng để dịch điểm hình học thành phản hồi. Xem <see cref="WireSettings"/>.</summary>
    internal readonly struct FeedbackThresholds
    {
        public double Low { get; init; }
        public double High { get; init; }
        public double Center { get; init; }
        public double Scale { get; init; }
        public double Margin { get; init; }
    }

    /// <summary>
    /// Những phản hồi mà game CÓ THỂ trả, kèm số bí mật sinh ra mỗi phản hồi.
    /// Đây là tập hợp mà mắt không được phép bước ra ngoài.
    /// </summary>
    public Dictionary<int, int> ResponsePartitions(IReadOnlyList<int> candidates, int fixedMask, int guess)
    {
        int active = _fullMask ^ fixedMask;
        var parts = new Dictionary<int, int>();
        foreach (int s in candidates)
        {
            int r = Response(s, guess, active);
            parts[r] = parts.TryGetValue(r, out int c) ? c + 1 : 1;
        }
        return parts;
    }

    /// <summary>
    /// Dịch điểm hình học đọc được thành phản hồi của game — nhưng CHỈ trong những phản hồi khả
    /// thi về mặt logic.
    ///
    /// Đây là chốt an toàn quan trọng nhất của cả bộ giải: nếu để mắt tự do khai báo tổ hợp nào
    /// cũng được, một khung hoạt ảnh xấu có thể sinh ra tổ hợp mà KHÔNG bí mật nào tạo ra nổi, và
    /// tập ứng viên tụt xuống 0 — lúc đó bot mất hết thông tin đã trả giá để có.
    ///
    /// Trình tự: (1) điểm nằm hẳn ngoài dải lửng lơ thì coi là ràng buộc CỨNG, lọc tập khả thi;
    /// (2) còn đúng một phương án thì chốt luôn; (3) còn nhiều thì chấm log-likelihood với tiên
    /// nghiệm là tỉ lệ bí mật sinh ra phản hồi đó, cộng logistic của từng điểm; (4) nhất và nhì
    /// chưa cách nhau đủ <see cref="FeedbackThresholds.Margin"/> thì TRẢ VỀ NULL — thà dừng còn
    /// hơn cắm lại đúng phương án vừa sai.
    ///
    /// <paramref name="scoreBySource"/>: điểm hình học của ổ cắm mà đầu dây i vừa cắm vào; chỉ các
    /// i đang xét mới có nghĩa.
    /// </summary>
    internal (int? Mask, double Margin, string How) InferResponse(
        IReadOnlyList<int> candidates, int fixedMask, int guess,
        double[] scoreBySource, FeedbackThresholds th)
    {
        int active = _fullMask ^ fixedMask;
        var parts = ResponsePartitions(candidates, fixedMask, guess);

        int strongOne = 0, strongZero = 0;
        for (int i = 0; i < _n; i++)
        {
            if (((active >> i) & 1) == 0) continue;
            double s = scoreBySource[i];
            if (s >= th.High) strongOne |= 1 << i;
            else if (s <= th.Low) strongZero |= 1 << i;
        }

        bool Compatible(int mask) =>
            (mask & strongOne) == strongOne && ((~mask) & strongZero & active) == strongZero;

        var feasible = new List<int>();
        foreach (int r in parts.Keys)
            if (Compatible(r)) feasible.Add(r);

        // Diem co the vuot nguong mot chut dung luc hoat anh dang chay. Khong bao gio de tap
        // ung vien ve 0: quay lai toan bo phan hoi kha thi va de xac suat quyet dinh.
        bool fallback = feasible.Count == 0;
        if (fallback) feasible.AddRange(parts.Keys);

        if (feasible.Count == 1) return (feasible[0], double.PositiveInfinity,
            fallback ? "duy-nhat-kha-thi (nguong khong khop)" : "duy-nhat-kha-thi");

        double total = Math.Max(1, candidates.Count);
        double bestLl = double.NegativeInfinity, secondLl = double.NegativeInfinity;
        int bestMask = 0;

        foreach (int mask in feasible)
        {
            double ll = Math.Log(Math.Max(parts.GetValueOrDefault(mask, 0), 1) / total);
            for (int i = 0; i < _n; i++)
            {
                if (((active >> i) & 1) == 0) continue;

                double z = (scoreBySource[i] - th.Center) / th.Scale;
                double p1 = z >= 0
                    ? 1.0 / (1.0 + Math.Exp(-Math.Min(z, 60.0)))
                    : Math.Exp(Math.Max(z, -60.0)) / (1.0 + Math.Exp(Math.Max(z, -60.0)));
                p1 = Math.Clamp(p1, 1e-6, 1.0 - 1e-6);

                ll += Math.Log(((mask >> i) & 1) != 0 ? p1 : 1.0 - p1);
            }

            if (ll > bestLl) { secondLl = bestLl; bestLl = ll; bestMask = mask; }
            else if (ll > secondLl) secondLl = ll;
        }

        double margin = bestLl - secondLl;
        if (margin >= th.Margin) return (bestMask, margin, fallback ? "xac-suat (nguong khong khop)" : "xac-suat");
        return (null, margin, "khong du cach biet");
    }

    // ---------------------------------------------------------------- goi bit

    private (ulong Lo, ulong Hi) Pack(IReadOnlyList<int> candidates)
    {
        ulong lo = 0, hi = 0;
        foreach (int i in candidates)
        {
            if (i < 64) lo |= 1UL << i;
            else hi |= 1UL << (i - 64);
        }
        return (lo, hi);
    }

    private List<int> Unpack((ulong Lo, ulong Hi) set)
    {
        var outp = new List<int>();
        for (int i = 0; i < _perms.Length; i++)
        {
            ulong bit = i < 64 ? set.Lo >> i : set.Hi >> (i - 64);
            if ((bit & 1) != 0) outp.Add(i);
        }
        return outp;
    }
}
