namespace GtaMiniGameBot;

internal sealed class FishingSnapshot
{
    public bool BarConfigured { get; init; }
    public bool FishConfigured { get; init; }
    public bool RejectConfigured { get; init; }
    public bool KeepConfigured { get; init; }

    /// <summary>HUD thanh câu đang hiện (đủ pixel cyan). False nếu chưa khoanh hoặc không thấy.</summary>
    public bool UiOpen { get; init; }

    /// <summary>0..1 fill từ dưới lên; -1 = chưa khoanh / không đọc được.</summary>
    public double BlueFill01 { get; init; } = -1;

    public bool FishBite { get; init; }
    public double FishScore { get; init; } = -1;

    public bool FailNotice { get; init; }
    public double RejectScore { get; init; } = -1;

    public bool KeepVisible { get; init; }
    public double KeepScore { get; init; } = -1;
}

/// <summary>
/// Đọc 3 ROI HUD câu cá. Thiếu ô hoặc mẫu → field tương ứng Unknown (-1 / false), không đoán.
/// </summary>
internal sealed class FishingReader : IDisposable
{
    private readonly FishingConfig _cfg;

    private readonly RegionReader _bar;
    private readonly RegionReader _fish;
    private readonly RegionReader _reject;
    private readonly RegionReader _keep;
    private readonly GrayTemplate _fishTpl;
    private readonly GrayTemplate _rejectTpl;
    private readonly GrayTemplate _keepTpl;
    private readonly string _fishProblem;
    private readonly string _rejectProblem;
    private readonly string _keepProblem;

    public string FishTemplateProblem => _fishProblem;
    public string RejectTemplateProblem => _rejectProblem;
    public string KeepTemplateProblem => _keepProblem;

    public FishingReader(FishingConfig cfg, Screen screen, FishingProfile profile)
    {
        _cfg = cfg;

        if (profile?.Bar.IsSet == true)
            _bar = new RegionReader(FishingConfig.ToAbsolute(screen, profile.Bar));

        if (profile?.Fish.IsSet == true)
        {
            var abs = FishingConfig.ToAbsolute(screen, profile.Fish);
            _fish = new RegionReader(abs);
            (_fishTpl, _fishProblem) = LoadTemplate(FishingConfig.FishTemplatePath(profile.Key), abs.Size, "cá");
        }
        else
            _fishProblem = "chưa khoanh ô cá";

        if (profile?.Reject.IsSet == true)
        {
            var abs = FishingConfig.ToAbsolute(screen, profile.Reject);
            _reject = new RegionReader(abs);
            (_rejectTpl, _rejectProblem) = LoadTemplate(FishingConfig.RejectTemplatePath(profile.Key), abs.Size, "thông báo");
        }
        else
            _rejectProblem = "chưa khoanh ô thông báo";

        if (profile?.Keep.IsSet == true)
        {
            var abs = FishingConfig.ToAbsolute(screen, profile.Keep);
            _keep = new RegionReader(abs);
            (_keepTpl, _keepProblem) = LoadTemplate(FishingConfig.KeepTemplatePath(profile.Key), abs.Size, "CẤT VÀO");
        }
        else
            _keepProblem = "chưa khoanh ô CẤT VÀO";
    }

    private static (GrayTemplate tpl, string problem) LoadTemplate(string path, Size roi, string name)
    {
        if (!File.Exists(path))
            return (null, $"chưa có mẫu {name} ({Path.GetFileName(path)})");
        try
        {
            var t = GrayTemplate.FromFile(path);
            if (t.IsFlat)
                return (null, $"mẫu {name} phẳng — khoanh lại lúc UI đang hiện");
            if (t.Width != roi.Width || t.Height != roi.Height)
                return (null, $"mẫu {name} {t.Width}×{t.Height} lệch ô {roi.Width}×{roi.Height} — khoanh lại");
            return (t, null);
        }
        catch (Exception ex)
        {
            return (null, $"mẫu {name}: {ex.Message}");
        }
    }

    public FishingSnapshot Read()
    {
        bool uiOpen = false;
        double fill = -1;
        if (_bar is not null)
        {
            _bar.Refresh();
            fill = _bar.BottomUpFill01(IsCyan);
            uiOpen = _bar.CountMatch(IsCyan) >= 30 || fill >= 0.03;
            if (!uiOpen) fill = 0;
        }

        double fishScore = -1;
        bool bite = false;
        if (_fish is not null && _fishTpl is not null)
        {
            _fish.Refresh();
            fishScore = _fishTpl.Score(_fish.GrayBuffer(_fish.Region));
            bite = fishScore >= _cfg.FishNccMin;
        }

        double rejectScore = -1;
        bool fail = false;
        if (_reject is not null && _rejectTpl is not null)
        {
            _reject.Refresh();
            rejectScore = _rejectTpl.Score(_reject.GrayBuffer(_reject.Region));
            fail = rejectScore >= _cfg.RejectNccMin;
        }

        double keepScore = -1;
        bool keep = false;
        if (_keep is not null && _keepTpl is not null)
        {
            _keep.Refresh();
            keepScore = _keepTpl.Score(_keep.GrayBuffer(_keep.Region));
            keep = keepScore >= _cfg.KeepNccMin;
        }

        return new FishingSnapshot
        {
            BarConfigured = _bar is not null,
            FishConfigured = _fishTpl is not null,
            RejectConfigured = _rejectTpl is not null,
            KeepConfigured = _keepTpl is not null,
            UiOpen = uiOpen,
            BlueFill01 = fill,
            FishBite = bite,
            FishScore = fishScore,
            FailNotice = fail,
            RejectScore = rejectScore,
            KeepVisible = keep,
            KeepScore = keepScore
        };
    }

    /// <summary>
    /// Thanh câu: fill cyan/xanh lơ sáng, tách khỏi nền tối và chữ trắng.
    /// </summary>
    private static bool IsCyan(int b, int g, int r)
    {
        return b >= 150 && g >= 130 && r <= 190 && (b + g) > r * 2 + 30 && Math.Abs(b - g) < 80;
    }

    public void Dispose()
    {
        _bar?.Dispose();
        _fish?.Dispose();
        _reject?.Dispose();
        _keep?.Dispose();
    }
}
