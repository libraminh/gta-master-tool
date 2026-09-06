namespace GtaMiniGameBot;

internal sealed class ScreenState
{
    public bool BagOpen { get; init; }
    public bool TrunkOpen { get; init; }
    public bool PauseOpen { get; init; }
    public bool PauseKnown { get; init; }

    /// <summary>
    /// Cột TRÊN ĐẤT đang hiện. Không gộp vào <see cref="AnyOpen"/> — đây là panel con
    /// của kho đồ, hết đồ thì cùng chỗ đó thành TRANG BỊ.
    /// </summary>
    public bool GroundOpen { get; init; }
    public bool GroundKnown { get; init; }

    /// <summary>
    /// Chữ TRANG BỊ trên cột phải (hết đồ đất). Không gộp AnyOpen — cùng chỗ với đất.
    /// </summary>
    public bool EquipOpen { get; init; }
    public bool EquipKnown { get; init; }

    public double BagScore { get; init; } = -1;
    public double TrunkScore { get; init; } = -1;
    public double PauseScore { get; init; } = -1;
    public double GroundScore { get; init; } = -1;
    public double EquipScore { get; init; } = -1;

    public bool AnyOpen => BagOpen || TrunkOpen || PauseOpen;

    public override string ToString() =>
        $"ba lô={(BagOpen ? "MỞ" : "đóng")}({BagScore:F2})  " +
        $"cốp={(TrunkOpen ? "MỞ" : "đóng")}({TrunkScore:F2})  " +
        $"tạm dừng={(PauseKnown ? PauseOpen ? "MỞ" : "đóng" : "chưa khoanh")}({PauseScore:F2})  " +
        $"đất={(GroundKnown ? GroundOpen ? "MỞ" : "đóng" : "chưa khoanh")}({GroundScore:F2})  " +
        $"trang bị={(EquipKnown ? EquipOpen ? "MỞ" : "đóng" : "chưa khoanh")}({EquipScore:F2})";
}

/// <summary>
/// Biết màn hình nào đang mở, bằng NCC trên mấy ô chữ tiêu đề cố định.
///
/// Đây là thứ khiến bot không phải đoán theo đồng hồ. Đo được trên máy thật: menu radial đổi
/// từ 2 nút sang 4 nút mất HƠN MỘT GIÂY — ngủ cứng 400 ms rồi click là click vào chỗ trống.
/// Mọi bước đều phải chờ tới khi NHÌN THẤY trạng thái mong đợi.
/// </summary>
internal sealed class InventoryReader : IDisposable
{
    private sealed record Probe(RegionReader Reader, GrayTemplate Tpl);

    private readonly FishingConfig _cfg;
    private readonly Probe _bag, _trunk, _pause, _ground, _equip;

    private InventoryReader(FishingConfig cfg, Probe bag, Probe trunk, Probe pause, Probe ground, Probe equip)
    {
        _cfg = cfg;
        _bag = bag;
        _trunk = trunk;
        _pause = pause;
        _ground = ground;
        _equip = equip;
    }

    /// <summary>Null + lý do nếu thiếu ô hoặc mẫu bắt buộc. Menu tạm dừng, TRÊN ĐẤT, TRANG BỊ là tuỳ chọn.</summary>
    public static InventoryReader Create(FishingConfig cfg, Screen screen, FishingProfile p, out string problem)
    {
        var missing = new List<string>();
        var bag = Load(screen, p.Key, p.BagHeader, "hdr-bag", "chữ BA LÔ", missing);
        var trunk = Load(screen, p.Key, p.TrunkHeader, "hdr-trunk", "chữ CỐP", missing);
        var pause = Load(screen, p.Key, p.PauseMarker, "hdr-pause", "menu tạm dừng", null);
        var ground = Load(screen, p.Key, p.GroundHeader, "hdr-ground", "chữ TRÊN ĐẤT", null);
        var equip = Load(screen, p.Key, p.EquipHeader, "hdr-equip", "chữ TRANG BỊ", null);

        if (missing.Count > 0)
        {
            bag?.Reader.Dispose();
            trunk?.Reader.Dispose();
            pause?.Reader.Dispose();
            ground?.Reader.Dispose();
            equip?.Reader.Dispose();
            problem = "thiếu " + string.Join("; ", missing);
            return null;
        }

        problem = null;
        return new InventoryReader(cfg, bag, trunk, pause, ground, equip);
    }

    private static Probe Load(Screen screen, string key, FishingRect roi, string file,
                              string label, List<string> missing)
    {
        if (!roi.IsSet) { missing?.Add(label + " chưa khoanh"); return null; }

        string path = FishingConfig.TrunkTemplatePath(key, file);
        if (!File.Exists(path)) { missing?.Add(label + " thiếu mẫu"); return null; }

        try
        {
            var t = GrayTemplate.FromFile(path);
            var abs = FishingConfig.ToAbsolute(screen, roi);
            if (t.IsFlat) { missing?.Add(label + " mẫu phẳng"); return null; }
            if (t.Width != abs.Width || t.Height != abs.Height)
            {
                missing?.Add($"{label} mẫu {t.Width}×{t.Height} lệch ô {abs.Width}×{abs.Height}");
                return null;
            }
            return new Probe(new RegionReader(abs), t);
        }
        catch (Exception ex)
        {
            missing?.Add($"{label}: {ex.Message}");
            return null;
        }
    }

    public ScreenState Read()
    {
        double bag = Score(_bag), trunk = Score(_trunk), pause = Score(_pause);
        double ground = Score(_ground), equip = Score(_equip);
        return new ScreenState
        {
            BagOpen = bag >= _cfg.HeaderNccMin,
            TrunkOpen = trunk >= _cfg.HeaderNccMin,
            PauseOpen = pause >= _cfg.HeaderNccMin,
            PauseKnown = _pause is not null,
            GroundOpen = ground >= _cfg.HeaderNccMin,
            GroundKnown = _ground is not null,
            EquipOpen = equip >= _cfg.HeaderNccMin,
            EquipKnown = _equip is not null,
            BagScore = bag,
            TrunkScore = trunk,
            PauseScore = pause,
            GroundScore = ground,
            EquipScore = equip
        };
    }

    private static double Score(Probe p)
    {
        if (p is null) return -1;
        p.Reader.Refresh();
        return p.Tpl.Score(p.Reader.GrayBuffer(p.Reader.Region));
    }

    public void Dispose()
    {
        _bag?.Reader.Dispose();
        _trunk?.Reader.Dispose();
        _pause?.Reader.Dispose();
        _ground?.Reader.Dispose();
        _equip?.Reader.Dispose();
    }
}
