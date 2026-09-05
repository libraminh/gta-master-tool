namespace GtaMiniGameBot;

/// <summary>Kết quả mới nhất của luồng quét khung nhìn thế giới — luồng chính chỉ đọc, không tính.</summary>
internal sealed class WorldSnapshot
{
    public WorldMarker Marker { get; init; } = WorldMarker.None;
    /// <summary>Scanner để false; NavBot ghi đè từ locator mỗi tick.</summary>
    public bool PromptVisible { get; init; }

    /// <summary>Cùng <see cref="PromptVisible"/> — NavBot ghi đè từ <see cref="ElectricLocator"/> mỗi tick.</summary>
    public bool WorkPromptVisible { get; init; }

    public JobBoardInfo Board { get; init; }
    public double T { get; init; }

    /// <summary>Nhịp thực của luồng quét (Hz, EMA) — in ra log để biết máy có kịp không.</summary>
    public double Hz { get; init; }

    public int Seq { get; init; }
}

/// <summary>
/// Chụp màn cho bộ điều hướng — thay <c>ScreenCapture</c> (dxcam 60 fps toàn màn) của bản Python.
///
/// C# thuần không có Desktop Duplication, và GDI chụp cả màn 2K tốn ~30 ms — quá chậm cho nhịp
/// 25 ms mà bộ lọc theo frame của bản Python cần. Nên tách hai đường:
///   - Luồng chính chụp riêng ROI MINIMAP (403×341 ở 2K, ≈ 3.3 ms — đo ở <see cref="RegionReader.MoveWindow"/>)
///     mỗi tick; ROI này chứa luôn gốc mũi tên và vùng mảnh.
///   - Một luồng <c>WorldScanner</c> chụp ROI WORLD (2560×1187) liên tục, chạy bộ dò đầu nối 3D,
///     bảng nghề (khi cần), và quan sát vật cản; công bố <see cref="WorldSnapshot"/> mới nhất.
///     Prompt <c>[E] TƯƠNG TÁC</c> do <see cref="ElectricLocator"/> quét ô đã khoanh trên luồng nav.
///
/// Hai <see cref="RegionReader"/> là hai instance riêng nên chụp song song an toàn; DWM có thể xếp
/// hàng hai cú CopyFromScreen, vì thế luồng chính đo và in thời gian tick của mình.
/// </summary>
internal sealed class NavCapture : IDisposable
{
    private readonly Screen _screen;
    private readonly NavScale _s;

    private readonly RegionReader _mini;
    private readonly Rectangle _miniRel;

    private readonly RegionReader _world;
    private readonly Rectangle _worldRel;
    private readonly object _frameLock = new();

    private readonly RegionReader _surv;
    private readonly Rectangle _survRel;

    private Thread _thread;
    private volatile bool _stop;
    private volatile bool _resetWorldRequested;
    private WorldSnapshot _snap = new();
    private int _seq;

    /// <summary>Luồng chính bật khi reset nghề đang chờ bảng NPC — dò bảng tốn thêm vài ms mỗi khung.</summary>
    public volatile bool WantBoard;

    public WorldMarkerDetector World { get; }
    public ObstacleClassifier Obstacle { get; }

    /// <summary>Lỗi của luồng quét (chụp màn hỏng…). Khác null là luồng đã tự dừng.</summary>
    public Exception Fault { get; private set; }

    public Rectangle MinimapRegion => _miniRel;
    public Rectangle WorldRegion => _worldRel;

    public NavCapture(Screen screen, NavScale s)
    {
        _screen = screen;
        _s = s;
        World = new WorldMarkerDetector(s);
        Obstacle = new ObstacleClassifier(s);

        var t = NavTuning.TargetRoiRef;
        _miniRel = s.RoiRef(t[0], t[1], t[2], t[3]);
        if (_miniRel.IsEmpty) throw new InvalidOperationException($"màn {s.ScreenW}×{s.ScreenH} quá nhỏ, không suy được vùng minimap");
        _mini = new RegionReader(Abs(_miniRel));

        var w = NavTuning.WorldRoiRef;
        _worldRel = s.RoiRef(w[0], w[1], w[2], w[3]);
        if (_worldRel.IsEmpty) throw new InvalidOperationException("không suy được vùng world");
        _world = new RegionReader(Abs(_worldRel));

        var v = NavTuning.SurvivalRoiRef;
        _survRel = s.RoiRef(v[0], v[1], v[2], v[3]);
        if (_survRel.IsEmpty) throw new InvalidOperationException("không suy được vùng đồng hồ đói/khát");
        _surv = new RegionReader(Abs(_survRel));
    }

    private Rectangle Abs(Rectangle rel) => new(_screen.Bounds.X + rel.X, _screen.Bounds.Y + rel.Y, rel.Width, rel.Height);

    /// <summary>Chụp minimap ngay bây giờ (luồng gọi). Khung trả về bọc thẳng đệm của reader — chỉ hợp lệ tới lần chụp sau.</summary>
    public NavFrame GrabMinimap(double now)
    {
        _mini.Refresh();
        return new NavFrame
        {
            Bgra = _mini.Raw, Stride = _mini.Stride, Width = _miniRel.Width, Height = _miniRel.Height,
            OriginX = _miniRel.X, OriginY = _miniRel.Y, T = now
        };
    }

    /// <summary>
    /// Chụp vùng hai đồng hồ đói/khát (≈187×100 px ở 2K, dưới 1 ms). Gọi trên luồng chính và CHỈ khi
    /// <see cref="SurvivalGauge.Due"/> đúng — 0.25 s một lần, không phải mỗi tick 25 ms.
    /// Khung trả về bọc thẳng đệm của reader, chỉ hợp lệ tới lần chụp sau, giống <see cref="GrabMinimap"/>.
    /// </summary>
    public NavFrame GrabSurvival(double now)
    {
        _surv.Refresh();
        return new NavFrame
        {
            Bgra = _surv.Raw, Stride = _surv.Stride, Width = _survRel.Width, Height = _survRel.Height,
            OriginX = _survRel.X, OriginY = _survRel.Y, T = now
        };
    }

    private NavFrame WorldFrame(double now) => new()
    {
        Bgra = _world.Raw, Stride = _world.Stride, Width = _worldRel.Width, Height = _worldRel.Height,
        OriginX = _worldRel.X, OriginY = _worldRel.Y, T = now
    };

    public WorldSnapshot Latest => Volatile.Read(ref _snap);

    /// <summary>Xoá trạng thái bộ dò đầu nối (streak/EMA) — thực hiện trên luồng quét ở khung kế.</summary>
    public void ResetWorld() => _resetWorldRequested = true;

    public void StartScanner()
    {
        if (_thread is { IsAlive: true }) return;
        _stop = false;
        _thread = new Thread(ScanLoop) { IsBackground = true, Name = "NavWorldScanner" };
        _thread.Start();
    }

    public void StopScanner(int joinMs = 500)
    {
        _stop = true;
        var t = _thread;
        if (t is { IsAlive: true }) { try { t.Join(joinMs); } catch { } }
    }

    private void ScanLoop()
    {
        double hz = 0;
        try
        {
            while (!_stop)
            {
                double t0 = NavClock.Now;
                lock (_frameLock) _world.Refresh();
                var frame = WorldFrame(t0);

                if (_resetWorldRequested) { World.Reset(); _resetWorldRequested = false; }
                var marker = World.Update(frame, t0);
                JobBoardInfo board = WantBoard ? JobBoardReader.Read(frame, _s) : null;
                Obstacle.Observe(frame, t0);

                double dt = Math.Max(0.001, NavClock.Now - t0);
                hz = hz <= 0 ? 1.0 / dt : 0.9 * hz + 0.1 / dt;
                _seq++;
                Volatile.Write(ref _snap, new WorldSnapshot
                {
                    Marker = marker, PromptVisible = false, WorkPromptVisible = false,
                    Board = board, T = t0, Hz = hz, Seq = _seq
                });

                if (dt < 0.004) Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            Fault = ex;
        }
    }

    /// <summary>Bên vật cản trên khung world mới nhất (khi vừa xác nhận kẹt). Chạy Canny ~20 ms dưới khoá khung.</summary>
    public int AnalyzeObstacleSide(double now, out string note)
    {
        lock (_frameLock) return Obstacle.AnalyzeSide(WorldFrame(now), now, out note);
    }

    public void Dispose()
    {
        StopScanner();
        _mini.Dispose();
        _world.Dispose();
        _surv.Dispose();
    }
}
