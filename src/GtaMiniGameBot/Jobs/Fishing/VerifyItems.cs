namespace GtaMiniGameBot;

/// <summary>
/// --test-items — chấm nhận diện vật phẩm trên ẢNH TĨNH đã chụp, không cần vào game.
///
/// Có nó thì việc chỉnh dải cỡ icon và ngưỡng là phép đo lặp lại được: cùng một ảnh, đổi tham
/// số, so điểm. Chỉnh bằng cách vào game bấm thử từng lần thì mỗi lần một khung hình khác nhau,
/// không đối chiếu được với lần trước.
///
/// Ảnh nào cũng chỉ nói được về những gì có trong nó — ô nào trống thì không chứng minh gì.
/// </summary>
internal static class VerifyItems
{
    public static int Run(string[] args)
    {
        var cfg = FishingConfig.Load();
        var screen = Screen.PrimaryScreen;
        var profile = cfg.GetOrCreate(screen);

        if (profile is null) { Console.WriteLine("chưa có hồ sơ cho màn hình này"); return 2; }

        var cat = ItemCatalog.Load(cfg);
        Console.WriteLine($"bộ icon: {cat.Count} vật phẩm");
        if (cat.Count == 0) { Console.WriteLine("chưa trích icon — chạy --harvest-icons trước"); return 2; }

        // --test-items --cell <anh o> : cham diem MOT o da luu, roi ve luon may mau dan dau ra
        // canh no. Doi chieu bang mat la cach duy nhat thay duoc mau dang phu lech cho nao.
        if (args.Length > 2 && args[1].Equals("--cell", StringComparison.OrdinalIgnoreCase))
            return OneCell(cfg, cat, args[2]);

        if (args.Length > 2 && args[1].Equals("--align", StringComparison.OrdinalIgnoreCase))
            return Align(cat, args[2], args.Length > 3 ? args[3] : null);

        string shot = args.Length > 1 ? args[1] : FishingConfig.ShotPath(profile.Key, "bag");
        if (!File.Exists(shot)) { Console.WriteLine("không thấy ảnh: " + shot); return 2; }

        using var still = new Bitmap(shot);
        Console.WriteLine($"ảnh: {shot}  {still.Width}x{still.Height}");
        Console.WriteLine($"sàn {cfg.ItemNccMin:F2}, cách biệt {cfg.ItemMarginMin:F2}, " +
                          $"co ô {cfg.CellInsetFrac:F2}, cắt badge {cfg.BadgeFrac:F2}");

        string dumpDir = Path.Combine(FishingConfig.ProfileDir(profile.Key), "debug-items");
        Directory.CreateDirectory(dumpDir);

        Console.WriteLine($"ô trống khi lệch < {cfg.CellEmptyStdMax:F2}, " +
                          $"coi là đang tải icon khi lệch ≥ {cfg.CellFaintStdMin:F2}");

        int hit = 0, seen = 0, faint = 0;
        foreach (var (label, grid) in new[] { ("hotbar", profile.Hotbar),
                                              ("pockets", profile.Pockets),
                                              ("bag", profile.Bag) })
        {
            if (!grid.IsSet) { Console.WriteLine($"{label}: chưa khoanh lưới"); continue; }

            using var scanner = new GridScanner(cfg, screen, grid);
            foreach (var (cell, gray) in scanner.ScanStillPixels(still))
            {
                if (cell is null) continue;
                if (cell.IsEmpty)
                {
                    // Anh tinh thi panel luon da ve xong, nen o trong o day la trong THAT. Co o
                    // nao bi gan co "dang tai" tuc CellFaintStdMin dat qua thap: moi lan do cop
                    // se cho vo ich du ba lo chang co gi.
                    if (cell.Faint)
                    {
                        faint++;
                        Console.WriteLine($"{label} #{cell.Index,-2} trống, lệch={cell.Std:F1} " +
                                          "— BỊ COI LÀ ĐANG TẢI, hạ CellFaintStdMin là sai");
                    }
                    continue;
                }
                seen++;

                int w = cell.Rect.Width, h = cell.Rect.Height;
                var top = cat.Top(gray, w, h, 3);
                var guess = cat.Classify(gray, w, h);
                if (guess.Name is not null) hit++;

                Console.WriteLine($"{label} #{cell.Index,-2} {w}x{h} " +
                                  (guess.Name is null ? "KHÔNG RÕ  " : "→ " + guess.Name + "  ") +
                                  string.Join("  ", top.Select(t => $"{t.Name} {t.Score:F2}@{t.Scale:F2}")));

                Dump(Path.Combine(dumpDir, $"{label}-{cell.Index:00}.png"), gray, w, h);
            }
        }

        Console.WriteLine($"nhận ra {hit}/{seen} ô có đồ. Ảnh ô đã lưu ở {dumpDir}");
        Console.WriteLine(faint == 0
            ? $"không ô trống nào bị coi là đang tải — CellFaintStdMin {cfg.CellFaintStdMin:F2} an toàn"
            : $"CẢNH BÁO: {faint} ô trống bị coi là đang tải — nâng CellFaintStdMin lên, " +
              "không thì lượt đổ nào cũng chờ vô ích");
        return hit > 0 ? 0 : 1;
    }

    /// <summary>
    /// Dò vét cả cỡ LẪN độ lệch vị trí cho vài vật phẩm, trên một ô đã lưu.
    ///
    /// Câu hỏi nó trả lời: điểm thấp là vì mẫu đặt lệch chỗ, hay vì so bằng độ xám thì bản
    /// thân nó không đủ phân biệt? Nếu chỉnh đúng vị trí mà điểm vọt lên thì thêm phép dò lệch
    /// là xong; còn chỉnh hết cỡ vẫn thấp thì phải đổi cách so, thêm bao nhiêu cỡ cũng vô ích.
    /// </summary>
    private static int Align(ItemCatalog cat, string cellPath, string namesCsv)
    {
        if (!File.Exists(cellPath)) { Console.WriteLine("không thấy ảnh ô: " + cellPath); return 2; }

        using var bmp = new Bitmap(cellPath);
        int w = bmp.Width, h = bmp.Height;
        var gray = GlyphSeg.GrayOf(bmp, new Rectangle(0, 0, w, h), out int gw, out int gh);

        var names = string.IsNullOrWhiteSpace(namesCsv)
            ? new[] { "catfish", "pike", "trout", "perch", "carp", "bluegill", "striped_bass",
                      "bass_largemouth", "river_sturgeon", "brightscale_zander", "crayfish",
                      "danglemouth_catfish", "reel_callisto_mg", "lockpick", "hook_no6" }
            : namesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Console.WriteLine($"ô: {cellPath}  {w}x{h} — dò cỡ 0.6…2.0, lệch ±10px");

        var rows = new List<(string Name, double Best, double K, int Dx, int Dy)>();
        foreach (string name in names)
        {
            double best = -2, bk = 0; int bdx = 0, bdy = 0;
            for (double k = 0.6; k <= 2.001; k += 0.1)
            for (int dx = -10; dx <= 10; dx += 2)
            for (int dy = -10; dy <= 10; dy += 2)
            {
                var t = cat.MakeAt(name, gw, gh, k, dx, dy);
                if (t is null || t.IsFlat) continue;
                double s = t.Score(gray);
                if (s > best) { best = s; bk = k; bdx = dx; bdy = dy; }
            }
            rows.Add((name, best, bk, bdx, bdy));
        }

        foreach (var r in rows.OrderByDescending(r => r.Best))
            Console.WriteLine($"   {r.Name,-24} {r.Best:F3}  @{r.K:F1}  lệch {r.Dx,3},{r.Dy,3}");

        return 0;
    }

    /// <summary>Chấm một ô đã lưu, in top-8 và vẽ mẫu của 3 cái dẫn đầu ra cạnh nó.</summary>
    private static int OneCell(FishingConfig cfg, ItemCatalog cat, string cellPath)
    {
        if (!File.Exists(cellPath)) { Console.WriteLine("không thấy ảnh ô: " + cellPath); return 2; }

        using var bmp = new Bitmap(cellPath);
        int w = bmp.Width, h = bmp.Height;
        var gray = GlyphSeg.GrayOf(bmp, new Rectangle(0, 0, w, h), out int gw, out int gh);

        Console.WriteLine($"ô: {cellPath}  {w}x{h}");
        var top = cat.Top(gray, gw, gh, 8);
        foreach (var t in top) Console.WriteLine($"   {t.Name,-28} {t.Score:F3} @{t.Scale:F2}");

        string dir = Path.GetDirectoryName(Path.GetFullPath(cellPath));
        string stem = Path.GetFileNameWithoutExtension(cellPath);
        foreach (var t in top.Take(3))
        {
            using var img = cat.Render(t.Name, t.Scale, gw, gh);
            if (img is null) continue;
            string p = Path.Combine(dir, $"{stem}~{t.Name}.png");
            img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("   vẽ mẫu: " + p);
        }
        return 0;
    }

    /// <summary>Ghi đúng mảng xám mà bộ so khớp nhìn thấy — không phải ảnh chụp lại từ màn hình.</summary>
    private static void Dump(string path, byte[] gray, int w, int h)
    {
        try
        {
            using var bmp = new Bitmap(w, h);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = gray[y * w + x];
                bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { }
    }
}
