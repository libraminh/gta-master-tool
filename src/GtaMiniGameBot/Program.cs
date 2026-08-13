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

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
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
