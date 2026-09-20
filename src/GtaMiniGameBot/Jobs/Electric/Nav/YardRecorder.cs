using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GtaMiniGameBot;

/// <summary>Một dòng tick đã đọc lại từ CSV. Trường nào thiếu trong bản ghi thì null, không phải 0.</summary>
internal sealed class YardTickRow
{
    public double T { get; set; }
    public int Seq { get; set; }
    public string Q { get; set; }
    public double Conf { get; set; }
    public double S { get; set; }
    public double Phi { get; set; }
    public double Theta { get; set; }
    public double? Px { get; set; }
    public double? Py { get; set; }
    public double? Tx { get; set; }
    public double? Ty { get; set; }

    /// <summary>⚡ tương đối gốc mũi tên (px màn).</summary>
    public double? Ax { get; set; }

    public double? Ay { get; set; }
    public double? Ancc { get; set; }
    public double? Ainset { get; set; }

    /// <summary>✕ tương đối gốc mũi tên.</summary>
    public double? Bx { get; set; }

    public double? By { get; set; }
    public double? Bncc { get; set; }
    public double? Binset { get; set; }

    /// <summary>🍕 tương đối gốc mũi tên.</summary>
    public double? Zx { get; set; }

    public double? Zy { get; set; }
    public double? Zncc { get; set; }
    public double? Zinset { get; set; }

    public int Ny { get; set; }
    public int Nr { get; set; }

    public double? Dotx { get; set; }
    public double? Doty { get; set; }
    public string Dotq { get; set; }
    public double Dotconf { get; set; }
    public string Dotstate { get; set; }
    public double Dotdist { get; set; }

    public int Wpresent { get; set; }
    public double? Wx { get; set; }
    public double Warea { get; set; }
    public double Wconf { get; set; }

    public int Prompt { get; set; }
    public int Panel { get; set; }
    public double Tickms { get; set; }
    public long? Xsent { get; set; }
    public string Keys { get; set; }
    public string Ctlstate { get; set; }
}

internal sealed class YardEventRow
{
    public double T { get; set; }
    public int Seq { get; set; }
    public string Name { get; set; }
    public string Detail { get; set; }
}

/// <summary>Một file <c>rec-*.csv</c> đã đọc lại.</summary>
internal sealed class YardRecording
{
    public string Path { get; set; }
    public List<YardTickRow> Ticks { get; } = new();
    public List<YardEventRow> Events { get; } = new();
}

/// <summary>
/// Ghi lại một buổi DẠY bản đồ: mỗi tick một dòng, mỗi sự kiện một dòng, cộng một vòng đệm khung
/// minimap để chụp lại đúng lúc đáng ngờ.
///
/// Vì sao ghi thô (a, b, z tương đối gốc mũi tên) chứ không chỉ ghi pose đã giải: buổi dạy chạy với
/// ngưỡng dò của hôm đó, mà cả bảng ngưỡng lẫn bộ lọc đều còn phải chỉnh. Có số thô thì chỉnh xong
/// GIẢI LẠI trên bản ghi cũ được — không phải bắt người dùng đi tay 20–30 chuyến lần nữa.
///
/// Mọi số ghi bằng <see cref="CultureInfo.InvariantCulture"/>: máy này đang ở locale Việt, dấu phẩy
/// thập phân sẽ phá luôn cấu trúc CSV.
/// </summary>
internal sealed class YardRecorder : IDisposable
{
    /// <summary>Đổi số này khi đổi cột — bộ đọc từ chối file khác phiên bản thay vì đọc lệch cột.</summary>
    public const int SchemaVersion = 1;

    private const string TickHeader =
        "kind,t,seq,q,conf,s,phi,theta,px,py,tx,ty," +
        "ax,ay,ancc,ainset,bx,by,bncc,binset,zx,zy,zncc,zinset,ny,nr," +
        "dotx,doty,dotq,dotconf,dotstate,dotdist," +
        "wpresent,wx,warea,wconf,prompt,panel,tickms,xsent,keys,ctlstate";

    private readonly string _stamp;
    private readonly string _framesDir;
    private StreamWriter _w;
    private double _lastFlush;
    private int _seq;

    private readonly RingItem[] _ring = new RingItem[NavTuning.YardFrameRingN];
    private int _ringCount;

    public string Path { get; }
    public string Stamp => _stamp;
    public int TickCount { get; private set; }
    public int EventCount { get; private set; }

    /// <summary>Lỗi ghi đầu tiên. Khác null = đã ngừng ghi; buổi dạy vẫn chạy tiếp.</summary>
    public Exception Fault { get; private set; }

    private sealed class RingItem
    {
        public byte[] Bgra;
        public int W, H, Stride;
        public double T;
        public int Seq;
    }

    public YardRecorder(string key) : this(key, null) { }

    /// <summary>
    /// <paramref name="pathOverride"/> khác null = ghi ra đúng file đó (ca kiểm dùng thư mục tạm).
    /// Dữ liệu thật của người dùng không được dính gì tới <c>--verify-map</c>.
    /// </summary>
    public YardRecorder(string key, string pathOverride)
    {
        _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Path = pathOverride ?? ElectricConfig.RecPath(key, _stamp);
        _framesDir = pathOverride is null
            ? ElectricConfig.MapFramesDir(key)
            : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(pathOverride))!, "frames");
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            _w = new StreamWriter(Path, append: false, new UTF8Encoding(false)) { AutoFlush = false };
            _w.WriteLine("#yard-rec v" + SchemaVersion.ToString(CultureInfo.InvariantCulture));
            _w.WriteLine(TickHeader);
        }
        catch (Exception ex) { Fault = ex; _w = null; }
    }

    // ---------------------------------------------------------------- ghi

    private static string N(double v, int d) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    private static string N(double? v, int d) => v is null ? "" : N(v.Value, d);

    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Khong dung dau nhay: chi thay ky tu pha cau truc. Bao cao doc bang mat, khong can CSV chuan.
        return s.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ');
    }

    public void Event(double t, string name, string detail = "")
    {
        if (_w is null) return;
        try
        {
            _w.WriteLine(string.Join(',', "E", N(t, 3), _seq.ToString(CultureInfo.InvariantCulture), Esc(name), Esc(detail)));
            EventCount++;
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Một dòng tick. <paramref name="xSent"/> null (chế độ dạy) → ba cột cuối để trống.</summary>
    public void Tick(double t, YardPose pose, IReadOnlyList<BlipHit> hits, TargetOutput dot,
                     int yellowBlobs, int redBlobs, WorldMarker world, double originX, double originY,
                     bool prompt, bool panel, double tickMs, long? xSent = null,
                     string keys = null, string ctlState = null)
    {
        if (_w is null) return;
        _seq++;
        try
        {
            BlipHit Hit(BlipId id)
            {
                if (hits is null) return null;
                foreach (var h in hits) if (h.Id == id) return h;
                return null;
            }

            string Blip(BlipHit h) => h is null
                ? ",,,"
                : string.Join(',', N(h.X - originX, 1), N(h.Y - originY, 1), N(h.Ncc, 2), N(h.InsetPx, 1));

            double dotDist = 0;
            double? dotx = null, doty = null;
            if (dot is not null && dot.HasPos)
            {
                dotx = dot.X.Value - originX;
                doty = dot.Y.Value - originY;
                dotDist = Math.Sqrt(dotx.Value * dotx.Value + doty.Value * doty.Value);
            }

            var sb = new StringBuilder(280);
            sb.Append("T,").Append(N(t, 3)).Append(',').Append(_seq.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(pose?.Quality ?? YardQuality.None).Append(',');
            sb.Append(N(pose?.Conf ?? 0, 3)).Append(',');
            sb.Append(N(pose?.S ?? 0, 4)).Append(',');
            sb.Append(N(pose?.Phi ?? 0, 5)).Append(',');
            sb.Append(N(pose?.ThetaDeg ?? 0, 2)).Append(',');
            sb.Append(pose is null ? "," : N(pose.P.X, 2) + "," + N(pose.P.Y, 2)).Append(',');
            sb.Append(pose?.T is null ? "," : N(pose.T.Value.X, 2) + "," + N(pose.T.Value.Y, 2)).Append(',');
            sb.Append(Blip(Hit(BlipId.Lightning))).Append(',');
            sb.Append(Blip(Hit(BlipId.Cross))).Append(',');
            sb.Append(Blip(Hit(BlipId.Pizza))).Append(',');
            sb.Append(yellowBlobs.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(redBlobs.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(N(dotx, 1)).Append(',').Append(N(doty, 1)).Append(',');
            sb.Append(dot?.Quality ?? "NONE").Append(',');
            sb.Append(N(dot?.Confidence ?? 0, 3)).Append(',');
            sb.Append(dot?.State ?? "LOST").Append(',');
            sb.Append(N(dotDist, 1)).Append(',');
            sb.Append(world is not null && world.Present ? "1" : "0").Append(',');
            sb.Append(world?.X is null ? "" : N(world.X.Value, 1)).Append(',');
            sb.Append(N(world?.Area ?? 0, 0)).Append(',');
            sb.Append(N(world?.Confidence ?? 0, 3)).Append(',');
            sb.Append(prompt ? "1" : "0").Append(',');
            sb.Append(panel ? "1" : "0").Append(',');
            sb.Append(N(tickMs, 2)).Append(',');
            sb.Append(xSent?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',');
            sb.Append(Esc(keys)).Append(',');
            sb.Append(Esc(ctlState));

            _w.WriteLine(sb.ToString());
            TickCount++;

            if (t - _lastFlush >= NavTuning.YardRecFlushS) { _w.Flush(); _lastFlush = t; }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        Fault ??= ex;
        try { _w?.Dispose(); } catch { }
        _w = null;
    }

    // ---------------------------------------------------------------- vong dem khung

    /// <summary>
    /// Đẩy khung minimap vào vòng đệm. PHẢI copy: khung của <see cref="NavCapture.GrabMinimap"/> bọc
    /// thẳng đệm của reader và chỉ hợp lệ tới lần chụp sau.
    /// </summary>
    public void PushFrame(NavFrame f, double t)
    {
        if (f?.Bgra is null) return;
        int i = _ringCount % _ring.Length;
        var item = _ring[i];
        int need = f.Stride * f.Height;
        if (item is null || item.Bgra.Length != need) item = _ring[i] = new RingItem { Bgra = new byte[need] };
        Buffer.BlockCopy(f.Bgra, 0, item.Bgra, 0, need);
        item.W = f.Width; item.H = f.Height; item.Stride = f.Stride; item.T = t; item.Seq = _seq;
        _ringCount++;
    }

    /// <summary>
    /// Ghi ra PNG những khung gần nhất cách <paramref name="agoS"/> giây. Dùng khi mở bảng (để xem
    /// pose lúc tiếp cận) và khi mất ⚡ lâu (để xem blip bị gì).
    /// </summary>
    public void DumpRing(double now, string tag, params double[] agoS)
    {
        if (_ringCount == 0) return;
        try
        {
            string dir = _framesDir;
            Directory.CreateDirectory(dir);

            foreach (double ago in agoS)
            {
                RingItem best = null;
                double bestD = double.MaxValue;
                for (int k = 0; k < Math.Min(_ringCount, _ring.Length); k++)
                {
                    var it = _ring[k];
                    if (it is null) continue;
                    double d = Math.Abs(now - ago - it.T);
                    if (d < bestD) { bestD = d; best = it; }
                }
                if (best is null) continue;

                string name = string.Format(CultureInfo.InvariantCulture, "{0}-{1:D5}-{2}-{3:F1}s.png",
                                            _stamp, best.Seq, tag, ago);
                SavePng(best, System.IO.Path.Combine(dir, name));
            }
        }
        catch { /* anh phu tro, khong duoc lam hong buoi ghi */ }
    }

    private static void SavePng(RingItem it, string path)
    {
        using var bmp = new Bitmap(it.W, it.H, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, it.W, it.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < it.H; y++)
                Marshal.Copy(it.Bgra, y * it.Stride, bd.Scan0 + y * bd.Stride, it.W * 4);
        }
        finally { bmp.UnlockBits(bd); }
        bmp.Save(path, ImageFormat.Png);
    }

    public void Dispose()
    {
        try { _w?.Flush(); _w?.Dispose(); } catch { }
        _w = null;
    }

    // ---------------------------------------------------------------- doc lai

    /// <summary>Đọc lại một file bản ghi. File hỏng/khác phiên bản → null (bộ dựng bỏ qua, báo trong report).</summary>
    public static YardRecording Parse(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2 || !lines[0].StartsWith("#yard-rec v", StringComparison.Ordinal)) return null;
            if (lines[0].Substring("#yard-rec v".Length).Trim() != SchemaVersion.ToString(CultureInfo.InvariantCulture)) return null;

            var rec = new YardRecording { Path = path };
            int cols = TickHeader.Split(',').Length;

            foreach (var line in lines.Skip(2))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var f = line.Split(',');
                if (f.Length == 0) continue;

                if (f[0] == "E" && f.Length >= 4)
                {
                    rec.Events.Add(new YardEventRow
                    {
                        T = D(f[1]) ?? 0,
                        Seq = (int)(D(f[2]) ?? 0),
                        Name = f[3],
                        Detail = f.Length > 4 ? string.Join(",", f.Skip(4)) : ""
                    });
                    continue;
                }
                if (f[0] != "T" || f.Length < cols) continue;

                int i = 1;
                var r = new YardTickRow
                {
                    T = D(f[i++]) ?? 0,
                    Seq = (int)(D(f[i++]) ?? 0),
                    Q = f[i++],
                    Conf = D(f[i++]) ?? 0,
                    S = D(f[i++]) ?? 0,
                    Phi = D(f[i++]) ?? 0,
                    Theta = D(f[i++]) ?? 0,
                    Px = D(f[i++]),
                    Py = D(f[i++]),
                    Tx = D(f[i++]),
                    Ty = D(f[i++]),
                    Ax = D(f[i++]),
                    Ay = D(f[i++]),
                    Ancc = D(f[i++]),
                    Ainset = D(f[i++]),
                    Bx = D(f[i++]),
                    By = D(f[i++]),
                    Bncc = D(f[i++]),
                    Binset = D(f[i++]),
                    Zx = D(f[i++]),
                    Zy = D(f[i++]),
                    Zncc = D(f[i++]),
                    Zinset = D(f[i++]),
                    Ny = (int)(D(f[i++]) ?? 0),
                    Nr = (int)(D(f[i++]) ?? 0),
                    Dotx = D(f[i++]),
                    Doty = D(f[i++]),
                    Dotq = f[i++],
                    Dotconf = D(f[i++]) ?? 0,
                    Dotstate = f[i++],
                    Dotdist = D(f[i++]) ?? 0,
                    Wpresent = (int)(D(f[i++]) ?? 0),
                    Wx = D(f[i++]),
                    Warea = D(f[i++]) ?? 0,
                    Wconf = D(f[i++]) ?? 0,
                    Prompt = (int)(D(f[i++]) ?? 0),
                    Panel = (int)(D(f[i++]) ?? 0),
                    Tickms = D(f[i++]) ?? 0
                };
                string xs = f[i++];
                r.Xsent = string.IsNullOrEmpty(xs) ? null : long.Parse(xs, CultureInfo.InvariantCulture);
                r.Keys = i < f.Length ? f[i++] : "";
                r.Ctlstate = i < f.Length ? f[i] : "";
                rec.Ticks.Add(r);
            }
            return rec;
        }
        catch { return null; }
    }

    private static double? D(string s) =>
        string.IsNullOrEmpty(s) ? null : double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Mọi bản ghi của một profile, sắp theo tên (tức theo thời gian).</summary>
    public static List<YardRecording> LoadAll(string key, out int broken)
    {
        broken = 0;
        var outp = new List<YardRecording>();
        string dir = ElectricConfig.MapDir(key);
        if (!Directory.Exists(dir)) return outp;

        foreach (var path in Directory.GetFiles(dir, "rec-*.csv").OrderBy(x => x, StringComparer.Ordinal))
        {
            var rec = Parse(path);
            if (rec is null || rec.Ticks.Count == 0) { broken++; continue; }
            outp.Add(rec);
        }
        return outp;
    }
}
