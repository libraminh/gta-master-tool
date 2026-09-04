using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GtaMiniGameBot;

/// <summary>
/// Kiểm tra bộ giải minigame đi dây, ba phần:
///   1. Tự vẽ panel giả ở CẢ 1920×1080 và 2560×1440 rồi dò lại — đây là phép thử chứng minh
///      đường 2K, và nó chạy được ngay khi chưa có ảnh chụp thật nào.
///   2. Mô phỏng toàn bộ bí mật (6 với 3 dây, 120 với 5 dây) qua bộ suy luận, đếm số lượt kiểm
///      tra thật rồi so với kỳ vọng mà quy hoạch động tự khai báo.
///   3. Dò trên ảnh tĩnh người dùng đã chụp, nếu có.
///
/// Phần 1 bắt đúng lớp lỗi mà bản Python mắc ở 2K: nhánh dò-theo-khối của nó chặn kích thước bằng
/// pixel tuyệt đối đo ở 1080p, nên ở 1440p không trả về đầu dây nào. Ở đây mọi neo là TỈ LỆ, và
/// phép thử này canh để nó ở mãi như vậy.
///
/// Chạy: GtaMiniGameBot.exe --verify-wire
/// </summary>
internal static class VerifyWire
{
    public static int Run(string[] args)
    {
        Console.WriteLine("== kiểm tra bộ giải đi dây ==");

        int fail = 0;
        fail += SyntheticTests();
        fail += PolicyTests();
        fail += StillTests();

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "TẤT CẢ ĐẠT" : $"HỎNG {fail} ca");
        return fail == 0 ? 0 : 1;
    }

    // ================================================================ panel gia

    /// <summary>
    /// Kích thước panel ở mốc 1080p, đo sao cho tỉ lệ khung nằm đúng hai bên ngưỡng 1.18:
    /// 3 dây thì panel vuông hơn (1.077), 5 dây thì bè ra (1.357).
    /// </summary>
    private static readonly (int W, int H) Panel3 = (560, 520);

    private static readonly (int W, int H) Panel5 = (760, 560);

    private static int SyntheticTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- tự kiểm tra (panel tự vẽ) --");

        int fail = 0;
        foreach (var (sw, sh) in new[] { (1920, 1080), (2560, 1440) })
        {
            double scale = ((sw / ElectricConfig.RefW) + (sh / ElectricConfig.RefH)) / 2.0;
            Console.WriteLine($"  màn {sw}×{sh} (tỉ lệ {scale:F3})");

            foreach (int n in new[] { 3, 5 })
            {
                var basePanel = n == 3 ? Panel3 : Panel5;
                var size = new Size((int)Math.Round(basePanel.W * scale),
                                    (int)Math.Round(basePanel.H * scale));
                fail += CheckSynthetic(sw, sh, n, size) ? 0 : 1;
            }
        }
        return fail;
    }

    private static bool CheckSynthetic(int screenW, int screenH, int n, Size panelSize)
    {
        var profile = new ElectricProfile { Width = screenW, Height = screenH };
        profile.Normalize();

        var cfg = new ElectricConfig();
        cfg.Normalize();

        var expected = new Rectangle(
            (screenW - panelSize.Width) / 2, (screenH - panelSize.Height) / 2,
            panelSize.Width, panelSize.Height);

        using var still = DrawPanel(screenW, screenH, n, expected);
        using var reader = WireReader.OpenForBitmap(cfg, profile, still);

        if (!reader.Configured)
        {
            Console.WriteLine($"    {n} dây: HỎNG — không mở được vùng quét: {reader.Problem}");
            return false;
        }

        var found = reader.FindPanel();
        if (found.IsEmpty)
        {
            Console.WriteLine($"    {n} dây: HỎNG — không thấy panel (mong {expected})");
            return false;
        }

        // Vien day 6px o moc 1080p nen hop bao trung khop tuyet doi; cho lech 2px cho lam tron.
        if (Math.Abs(found.X - expected.X) > 2 || Math.Abs(found.Y - expected.Y) > 2 ||
            Math.Abs(found.Width - expected.Width) > 2 || Math.Abs(found.Height - expected.Height) > 2)
        {
            Console.WriteLine($"    {n} dây: HỎNG — hộp lệch: thấy {found}, mong {expected}");
            return false;
        }

        var round = reader.ReadRound(found);
        if (round is null)
        {
            Console.WriteLine($"    {n} dây: HỎNG — không phân loại được slot");
            return false;
        }

        if (round.Count != n)
        {
            Console.WriteLine($"    {n} dây: HỎNG — nhận thành WIRE_{round.Count} " +
                              $"(tỉ lệ khung {found.Width / (double)found.Height:F3}, " +
                              $"ngưỡng {cfg.Wire.ProfileAspectSplit})");
            return false;
        }

        // Ve o dung slot nao thi phai doc ra dung slot do. Day la phan chung minh 2K:
        // neo la ti le nen panel to ra khong lam lech.
        var srcSlots = WirePalette.SourceSlots(n);
        var tgtSlots = WirePalette.TargetSlots(n);
        for (int i = 0; i < n; i++)
        {
            if (!SameSlot(round.SourceFrac[i], srcSlots[i]))
            {
                Console.WriteLine($"    {n} dây: HỎNG — {round.Sources[i]} đọc ra slot " +
                                  $"({round.SourceFrac[i].X:F3},{round.SourceFrac[i].Y:F3}), " +
                                  $"mong ({srcSlots[i].X:F3},{srcSlots[i].Y:F3})");
                return false;
            }
            if (!SameSlot(round.TargetFrac[i], tgtSlots[i]))
            {
                Console.WriteLine($"    {n} dây: HỎNG — {round.Targets[i]} đọc ra slot " +
                                  $"({round.TargetFrac[i].X:F3},{round.TargetFrac[i].Y:F3}), " +
                                  $"mong ({tgtSlots[i].X:F3},{tgtSlots[i].Y:F3})");
                return false;
            }
        }

        Console.WriteLine($"    {n} dây: đạt — panel {found.Width}×{found.Height}, " +
                          $"{n} đầu dây + {n} ổ cắm đúng slot");
        return true;
    }

    private static bool SameSlot(PointF got, (double X, double Y) want) =>
        Math.Abs(got.X - want.X) < 1e-4 && Math.Abs(got.Y - want.Y) < 1e-4;

    /// <summary>
    /// Vẽ một panel đi dây đủ để bộ dò nhận: khung viền, nền, nắp màu ở từng slot đầu dây, mấu
    /// màu ở từng slot ổ cắm.
    ///
    /// Tô bằng <see cref="Graphics.FillRectangle(Brush,Rectangle)"/> chứ không vẽ nét: nét có
    /// chống răng cưa sẽ sinh màu trung gian, mà bộ dò so màu với sai số 10 — làm mờ mép là tự
    /// tạo ra một phép thử khác cái mình muốn thử.
    /// </summary>
    private static Bitmap DrawPanel(int screenW, int screenH, int n, Rectangle panel)
    {
        double scale = ((screenW / ElectricConfig.RefW) + (screenH / ElectricConfig.RefH)) / 2.0;

        var bmp = new Bitmap(screenW, screenH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;

        // Canh game gia: mot mau khong dinh mau nao cua panel.
        g.Clear(Color.FromArgb(20, 22, 26));

        var border = Rgb(WirePalette.Border);
        var bg = Rgb(WirePalette.PanelBg);
        int t = Math.Max(3, (int)Math.Round(6 * scale));

        using (var bBorder = new SolidBrush(border))
        {
            g.FillRectangle(bBorder, panel.Left, panel.Top, panel.Width, t);
            g.FillRectangle(bBorder, panel.Left, panel.Bottom - t, panel.Width, t);
            g.FillRectangle(bBorder, panel.Left, panel.Top, t, panel.Height);
            g.FillRectangle(bBorder, panel.Right - t, panel.Top, t, panel.Height);
        }

        using (var bBg = new SolidBrush(bg))
            g.FillRectangle(bBg, panel.Left + t, panel.Top + t,
                            panel.Width - t * 2, panel.Height - t * 2);

        // Nap dau day: o vuong. Mau dau day nam o hang giua panel.
        int cap = Math.Max(8, (int)Math.Round(14 * scale));
        var sources = WirePalette.Sources(n);
        var srcSlots = WirePalette.SourceSlots(n);
        for (int i = 0; i < n; i++)
        {
            var p = WireRound.PointIn(panel, new PointF((float)srcSlots[i].X, (float)srcSlots[i].Y));
            using var b = new SolidBrush(Rgb(WirePalette.Of(sources[i])));
            g.FillRectangle(b, p.X - cap / 2, p.Y - cap / 2, cap, cap);
        }

        // Mau o cam: hoi cao hon rong, giong hinh that (~12×17 o 1080p).
        int tw = Math.Max(6, (int)Math.Round(12 * scale));
        int tzh = Math.Max(8, (int)Math.Round(17 * scale));
        var targets = WirePalette.Targets(n);
        var tgtSlots = WirePalette.TargetSlots(n);
        for (int j = 0; j < n; j++)
        {
            var p = WireRound.PointIn(panel, new PointF((float)tgtSlots[j].X, (float)tgtSlots[j].Y));
            using var b = new SolidBrush(Rgb(WirePalette.Of(targets[j])));
            g.FillRectangle(b, p.X - tw / 2, p.Y - tzh / 2, tw, tzh);
        }

        return bmp;
    }

    private static Color Rgb((int B, int G, int R) c) => Color.FromArgb(c.R, c.G, c.B);

    // ================================================================ suy luan

    /// <summary>
    /// Chơi hết mọi bí mật một cách hoàn hảo và đếm số lượt kiểm tra THẬT.
    ///
    /// Hai điều được chứng minh ở đây, không cần game:
    ///   - Bộ giải luôn kết thúc, và không bao giờ vượt n+1 lượt (chặn trên mà
    ///     <see cref="WireBot"/> đặt).
    ///   - Số lượt trung bình khớp ĐÚNG kỳ vọng mà quy hoạch động tự khai ở gốc. Lệch nghĩa là
    ///     hàm giá trị và cách chơi thật đã rời nhau — loại lỗi im lặng không cách nào thấy được
    ///     trong game.
    /// </summary>
    private static int PolicyTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- suy luận hoán vị ẩn --");

        int fail = 0;
        foreach (int n in new[] { 3, 5 })
        {
            var policy = new WirePolicy(n);
            int full = (1 << n) - 1;
            var (rootExpected, _) = policy.Choose(policy.AllCandidates(), 0);

            int worst = 0;
            long totalChecks = 0;
            bool broke = false;

            for (int secret = 0; secret < policy.PermutationCount; secret++)
            {
                var candidates = policy.AllCandidates();
                int fixedMask = 0;
                int checks = 0;

                while (fixedMask != full)
                {
                    if (++checks > n + 1)
                    {
                        Console.WriteLine($"    {n} dây: HỎNG — bí mật #{secret} không xong trong {n + 1} lượt");
                        broke = true;
                        break;
                    }

                    var (_, guess) = policy.Choose(candidates, fixedMask);
                    int response = policy.Response(secret, guess, full ^ fixedMask);

                    candidates = policy.Filter(candidates, fixedMask, guess, response);
                    if (candidates.Count == 0)
                    {
                        Console.WriteLine($"    {n} dây: HỎNG — bí mật #{secret} bị lọc mất khỏi tập ứng viên");
                        broke = true;
                        break;
                    }
                    if (!candidates.Contains(secret))
                    {
                        Console.WriteLine($"    {n} dây: HỎNG — bí mật #{secret} không còn trong tập sau lượt {checks}");
                        broke = true;
                        break;
                    }

                    fixedMask |= response;
                }

                if (broke) { fail++; break; }
                totalChecks += checks;
                worst = Math.Max(worst, checks);
            }

            if (broke) continue;

            double mean = totalChecks / (double)policy.PermutationCount;
            bool match = Math.Abs(mean - rootExpected) < 1e-9;
            Console.WriteLine($"    {n} dây: {policy.PermutationCount} bí mật, " +
                              $"trung bình {mean:F3} lượt (kỳ vọng {rootExpected:F3}), tệ nhất {worst}" +
                              (match ? " — đạt" : " — HỎNG: lệch kỳ vọng"));
            if (!match) fail++;
        }

        return fail;
    }

    // ================================================================ anh that

    private static int StillTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- ảnh tĩnh đã chụp --");

        var cfg = ElectricConfig.Load();
        if (cfg.Profiles.Count == 0)
        {
            Console.WriteLine("  chưa có profile nào trong electric.json — bỏ qua.");
            return 0;
        }

        int fail = 0, found = 0;
        foreach (var (key, profile) in cfg.Profiles.OrderBy(kv => kv.Key))
        {
            foreach (string name in new[] { "wire3", "wire5" })
            {
                string path = ElectricConfig.ShotPath(key, name);
                if (!File.Exists(path)) continue;

                found++;
                Console.WriteLine($"  {key}/{name}.png");
                try
                {
                    using var still = new Bitmap(path);
                    using var reader = WireReader.OpenForBitmap(cfg, profile, still);

                    var panel = reader.FindPanel();
                    if (panel.IsEmpty)
                    {
                        Console.WriteLine("    HỎNG — không thấy panel");
                        fail++;
                        continue;
                    }

                    var round = reader.ReadRound(panel);
                    if (round is null)
                    {
                        Console.WriteLine($"    HỎNG — thấy panel {panel} nhưng không phân loại được slot");
                        fail++;
                        continue;
                    }

                    Console.WriteLine("    " + round.Describe());

                    var (present, blobs) = reader.ReadTargetBlobs(round, panel);
                    if (!present)
                    {
                        Console.WriteLine("    HỎNG — đọc lại panel thất bại");
                        fail++;
                        continue;
                    }

                    for (int j = 0; j < round.Count; j++)
                        Console.WriteLine($"    ổ {round.Targets[j],-7} " +
                                          (blobs[j] is { } b ? b.ToString() : "KHÔNG THẤY KHỐI MÀU"));

                    int want = name == "wire3" ? 3 : 5;
                    if (round.Count != want)
                    {
                        Console.WriteLine($"    HỎNG — mong WIRE_{want}, đọc ra WIRE_{round.Count}");
                        fail++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("    HỎNG — " + ex.Message);
                    fail++;
                }
            }
        }

        if (found == 0)
            Console.WriteLine("  chưa có ảnh nào. Chụp bằng tab Điện → “Chụp ảnh tĩnh…”, " +
                              "lưu tên wire3.png / wire5.png.");

        return fail + NoFalsePanelOnBoard(cfg);
    }

    /// <summary>
    /// Bộ dò panel dây KHÔNG được nhận nhầm trên màn bảng nước/điện.
    ///
    /// Đây không phải lo xa. Hai minigame thuộc CÙNG một nghề, và <see cref="ElectricBot"/> thăm
    /// dò panel dây TRƯỚC; nhận nhầm một cái là nó giao quyền cho bộ giải dây, bộ đó không đọc
    /// được lượt nào, và bảng nước/điện bị bỏ mặc cho tới khi hết thời gian chờ — người dùng thấy
    /// đúng hiện tượng "app không điều khiển gì".
    ///
    /// Màn bảng có sẵn đủ thứ xám-xanh nhạt để dễ lẫn với màu viền panel dây: con dấu tròn của
    /// Los Santos Department of Water &amp; Power, các đầu nối xám, và chữ tiêu đề.
    /// </summary>
    private static int NoFalsePanelOnBoard(ElectricConfig cfg)
    {
        Console.WriteLine();
        Console.WriteLine("-- không nhận nhầm panel dây trên màn bảng --");

        int found = 0, fail = 0;
        foreach (var (key, profile) in cfg.Profiles.OrderBy(kv => kv.Key))
        {
            string path = ElectricConfig.ShotPath(key, "board");
            if (!File.Exists(path)) continue;

            found++;
            try
            {
                using var still = new Bitmap(path);
                using var reader = WireReader.OpenForBitmap(cfg, profile, still);

                var panel = reader.FindPanel();
                if (panel.IsEmpty)
                {
                    Console.WriteLine($"  {key}/board.png: đạt — không thấy panel dây");
                    continue;
                }

                Console.WriteLine($"  {key}/board.png: HỎNG — nhận nhầm panel dây tại {panel}");
                var round = reader.ReadRound(panel);
                Console.WriteLine(round is null
                    ? "    (không phân loại được slot — nhưng bộ điều phối đã giao quyền sai rồi)"
                    : "    " + round.Describe());
                fail++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  HỎNG — " + ex.Message);
                fail++;
            }
        }

        if (found == 0) Console.WriteLine("  chưa có ảnh bảng nào để thử.");
        return fail;
    }
}
