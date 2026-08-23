namespace GtaMiniGameBot;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // PHAI dat truoc moi thao tac ve UI hay chup man hinh:
        // khong co no, Windows se scale lai toa do va moi phep do pixel deu lech.
        try { Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* Windows cu hon 1703 - bo qua, may nay dang scale 100% nen khong anh huong */ }

        if (args.Length > 0 && args[0].Equals("--verify", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();

            // WinExe khong chac gui duoc stdout ve terminal, nen ghi song song ra file.
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = Verify.Run(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            string path0 = Path.Combine(AppContext.BaseDirectory, "verify-report.txt");
            TryWriteUtf8(path0, report.ToString());
            return rc;
        }

        if (args.Length > 0 && args[0].Equals("--verify-ocr", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = VerifyOcr.Run(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            TryWriteUtf8(Path.Combine(AppContext.BaseDirectory, "verify-ocr.txt"), report.ToString());
            return rc;
        }

        if (args.Length > 0 && args[0].Equals("--verify-wood", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = VerifyWood.Run(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            TryWriteUtf8(Path.Combine(AppContext.BaseDirectory, "verify-wood.txt"), report.ToString());
            return rc;
        }

        if (args.Length > 0 && args[0].Equals("--verify-wire", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = VerifyWire.Run(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            TryWriteUtf8(Path.Combine(AppContext.BaseDirectory, "verify-wire.txt"), report.ToString());
            return rc;
        }

        if (args.Length > 0 && args[0].Equals("--verify-ui", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = VerifyUi.Run(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            TryWriteUtf8(Path.Combine(AppContext.BaseDirectory, "verify-ui.txt"), report.ToString());
            return rc;
        }

        if (args.Length > 0 && args[0].Equals("--verify-nav", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = VerifyNav.Run(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            TryWriteUtf8(Path.Combine(AppContext.BaseDirectory, "verify-nav.txt"), report.ToString());
            return rc;
        }

        if (args.Length > 0 && args[0].Equals("--verify-board", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = VerifyBoard.Run(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            TryWriteUtf8(Path.Combine(AppContext.BaseDirectory, "verify-board.txt"), report.ToString());
            return rc;
        }

        // Trich icon khong can UI: chay duoc tu terminal de kiem chung phep doc cache truoc khi
        // dung no trong game.
        if (args.Length > 0 && args[0].Equals("--harvest-icons", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = HarvestIcons(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            TryWriteUtf8(Path.Combine(AppContext.BaseDirectory, "harvest-icons.txt"), report.ToString());
            return rc;
        }

        if (args.Length > 0 && args[0].Equals("--test-items", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = VerifyItems.Run(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            TryWriteUtf8(Path.Combine(AppContext.BaseDirectory, "test-items.txt"), report.ToString());
            return rc;
        }

        if (args.Length > 0 && args[0].Equals("--pick-car-template", StringComparison.OrdinalIgnoreCase))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            TrySetUtf8Console();
            var report = new StringWriter();
            Console.SetOut(new TeeWriter(Console.Out, report));

            int rc;
            try { rc = CarTemplatePicker.Run(args); }
            catch (Exception ex) { Console.WriteLine("LOI: " + ex); rc = 3; }

            TryWriteUtf8(Path.Combine(AppContext.BaseDirectory, "pick-car-template.txt"), report.ToString());
            return rc;
        }

        AppPaths.MigrateFromExeFolder();
        BotLog.Load();
        LogHousekeeping.RunAtStart();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        InstallSafetyNets();
        Application.Run(new HomeForm());
        return 0;
    }

    /// <summary>
    /// Ngoai le khong bat duoc lam app chet ma OnFormClosing KHONG chay - phim S, chuot trai,
    /// Alt se ket xuong trong game va nguoi choi phai tu bam lai de go. Ba moc nay la lop cuoi
    /// cung nha chung ra. Van hien loi len de khong nuot mat, dung nhu hop thoai mac dinh.
    /// </summary>
    private static void InstallSafetyNets()
    {
        Application.ThreadException += (_, e) =>
        {
            HeldKeys.ReleaseAll();
            try
            {
                MessageBox.Show(e.Exception.ToString(), "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => HeldKeys.ReleaseAll();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => HeldKeys.ReleaseAll();
    }

    /// <summary>
    /// --harvest-icons [thư mục cache] — moi icon vật phẩm ra rồi in kết quả.
    /// Không cần game đang chạy, cũng không cần mở app.
    /// </summary>
    private static int HarvestIcons(string[] args)
    {
        var cfg = FishingConfig.Load();
        string dir = args.Length > 1 ? args[1] : cfg.ItemCachePath;

        Console.WriteLine("cache: " + dir);
        var res = ItemIconExtractor.Harvest(dir, cfg.AllowIconDownload);

        foreach (string n in res.Notes) Console.WriteLine("  ghi chú: " + n);
        Console.WriteLine($"lấy được {res.Saved.Count} icon → {ItemIconExtractor.ItemDir}");

        if (res.Missing.Count > 0)
            Console.WriteLine($"thiếu ảnh ({res.Missing.Count}): {string.Join(", ", res.Missing)}");

        foreach (string name in res.Saved.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine("  " + name);

        return res.Saved.Count > 0 ? 0 : 1;
    }

    /// <summary>De console hien duoc tieng Viet co dau thay vi ky tu la.</summary>
    private static void TrySetUtf8Console()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
    }

    /// <summary>Ghi UTF-8 CO BOM de Notepad nhan ra, khong thi chu co dau thanh ky tu la.</summary>
    private static void TryWriteUtf8(string path, string text)
    {
        try { File.WriteAllText(path, text, new System.Text.UTF8Encoding(true)); } catch { }
    }

    /// <summary>Ghi ra ca console lan bo dem, de bao gio cung co file de doi chieu.</summary>
    private sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char c) { a.Write(c); b.Write(c); }
        public override void Write(string s) { a.Write(s); b.Write(s); }
        public override void WriteLine(string s) { a.WriteLine(s); b.WriteLine(s); }
        public override void Flush() { a.Flush(); b.Flush(); }
    }
}
