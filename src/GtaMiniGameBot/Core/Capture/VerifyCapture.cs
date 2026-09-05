using System.Diagnostics;

namespace GtaMiniGameBot;

/// <summary>Smoke-test và benchmark hai backend capture mà không ghi ảnh ra đĩa.</summary>
internal static class VerifyCapture
{
    public static int Run(string[] args)
    {
        Console.WriteLine("== kiểm tra capture màn hình ==");
        Screen screen = Screen.PrimaryScreen ?? Screen.AllScreens.First();
        int width = Math.Min(480, Math.Max(64, screen.Bounds.Width / 4));
        int height = Math.Min(270, Math.Max(64, screen.Bounds.Height / 4));
        var region = new Rectangle(
            screen.Bounds.Left + (screen.Bounds.Width - width) / 2,
            screen.Bounds.Top + (screen.Bounds.Height - height) / 2,
            width, height);

        bool strict = args.Any(a => a.Equals("--strict", StringComparison.OrdinalIgnoreCase));
        int failures = 0;
        try
        {
            using var dxgi = new DxgiScreenCapture(screen);
            failures += Benchmark(dxgi, region, 40, 250);
        }
        catch (Exception ex) when (!strict && IsUnsupportedDxgi(ex))
        {
            Console.WriteLine("DXGI không khả dụng trên cấu hình này: " + ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine("HỎNG DXGI: " + ex);
            failures++;
        }

        using (var gdi = new GdiScreenCapture())
            failures += Benchmark(gdi, region, 20, 0);

        using (var automatic = ScreenCaptureFactory.Create(screen, Console.WriteLine))
            failures += CaptureOnce(automatic, region) ? 0 : 1;

        Console.WriteLine(failures == 0 ? "CAPTURE ĐẠT" : $"CAPTURE HỎNG {failures} ca");
        return failures == 0 ? 0 : 1;
    }

    private static int Benchmark(IScreenCaptureSession capture, Rectangle region,
                                 int count, int timeoutMs)
    {
        Console.WriteLine($"-- {capture.BackendName}, ROI {region.Width}×{region.Height} --");
        byte[] reuse = new byte[region.Width * region.Height * 4];
        var samples = new List<double>(count);
        long previousFrame = -1;
        int failures = 0;

        for (int i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            bool ok = capture.TryCapture(region, reuse, timeoutMs, out var frame);
            sw.Stop();
            if (!ok)
            {
                if (capture.Status == ScreenCaptureStatus.TimedOut) continue;
                Console.WriteLine("  HỎNG capture: " + capture.StatusDetail);
                failures++;
                break;
            }

            if (!ReferenceEquals(frame.Bgra, reuse) ||
                frame.Stride != region.Width * 4 ||
                frame.Bgra.Length != frame.Stride * region.Height ||
                frame.Region != region ||
                frame.FrameId <= previousFrame ||
                frame.CaptureTimestamp <= 0)
            {
                Console.WriteLine("  HỎNG contract BGRA/stride/frameId/buffer-reuse");
                failures++;
                break;
            }

            previousFrame = frame.FrameId;
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        if (samples.Count > 0)
        {
            samples.Sort();
            double mean = samples.Average();
            double p50 = samples[(int)((samples.Count - 1) * 0.50)];
            double p95 = samples[(int)((samples.Count - 1) * 0.95)];
            Console.WriteLine($"  {samples.Count} frame: tb {mean:F2}ms, p50 {p50:F2}ms, p95 {p95:F2}ms");
        }
        else
        {
            Console.WriteLine("  HỎNG — không nhận được frame nào");
            failures++;
        }
        return failures;
    }

    private static bool IsUnsupportedDxgi(Exception ex) =>
        ex is NotSupportedException or DllNotFoundException or EntryPointNotFoundException;

    private static bool CaptureOnce(IScreenCaptureSession capture, Rectangle region)
    {
        bool ok = capture.TryCapture(region, null, 250, out var frame);
        Console.WriteLine(ok
            ? $"factory chọn {capture.BackendName}, frame {frame.FrameId}"
            : $"factory thất bại: {capture.StatusDetail}");
        return ok;
    }
}
