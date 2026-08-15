namespace GtaMiniGameBot;

internal sealed class MinerSnapshot
{
    public bool MiningConfigured { get; init; }
    public bool LiftConfigured { get; init; }
    public bool CashConfigured { get; init; }

    /// <summary>Ô "ĐANG KHAI THÁC…" đang hiện — tức cú E vừa rồi đã ăn và đang chạy tiến trình.</summary>
    public bool Mining { get; init; }
    public double MiningScore { get; init; } = -1;

    /// <summary>Đang đứng đúng chỗ giếng thang, gợi ý "[E] DÙNG THANG MÁY" đang hiện.</summary>
    public bool LiftPrompt { get; init; }
    public double LiftScore { get; init; } = -1;

    /// <summary>Toast "Tiền mặt: + $…" đang hiện — vừa giao hàng xong.</summary>
    public bool CashToast { get; init; }
    public double CashScore { get; init; } = -1;

    /// <summary>Chưa khoanh ô nào thì bot phải chạy kiểu mù, không được suy diễn từ -1.</summary>
    public bool AnyConfigured => MiningConfigured || LiftConfigured || CashConfigured;
}

/// <summary>
/// Đọc ba ô HUD của job thợ mỏ tại đúng toạ độ đã khoanh, so khớp bằng NCC với mẫu đã chụp.
/// Thiếu ô hoặc thiếu mẫu → field tương ứng false/-1 và một câu giải thích trong *Problem,
/// không đoán bừa: đoán sai ở đây nghĩa là bot tưởng đang đào trong khi đứng không.
///
/// Cả ba đều so khớp tại Ô CỐ ĐỊNH chứ không dò vị trí, vì HUD của server này neo cứng —
/// khác nút CẤT VÀO của job câu cá, chỗ đó bị tên cá dài đẩy trôi nên mới phải dò.
/// </summary>
internal sealed class MinerReader : IDisposable
{
    private readonly MinerConfig _cfg;

    private readonly RegionReader _mining;
    private readonly RegionReader _lift;
    private readonly RegionReader _cash;
    private readonly GrayTemplate _miningTpl;
    private readonly GrayTemplate _liftTpl;
    private readonly GrayTemplate _cashTpl;

    public string MiningProblem { get; }
    public string LiftProblem { get; }
    public string CashProblem { get; }

    public MinerReader(MinerConfig cfg, Screen screen, MinerProfile profile)
    {
        _cfg = cfg;

        (_mining, _miningTpl, string mp) = Open(
            screen, profile?.MiningBox, MinerConfig.MiningTemplatePath(profile?.Key ?? ""), "ô đào");
        MiningProblem = mp;

        (_lift, _liftTpl, string lp) = Open(
            screen, profile?.LiftPrompt, MinerConfig.LiftTemplatePath(profile?.Key ?? ""), "gợi ý thang máy");
        LiftProblem = lp;

        (_cash, _cashTpl, string cp) = Open(
            screen, profile?.CashToast, MinerConfig.CashTemplatePath(profile?.Key ?? ""), "toast tiền");
        CashProblem = cp;
    }

    private static (RegionReader reader, GrayTemplate tpl, string problem) Open(
        Screen screen, FishingRect rect, string templatePath, string name)
    {
        if (rect?.IsSet != true)
            return (null, null, $"chưa khoanh {name}");

        var abs = FishingConfig.ToAbsolute(screen, rect);
        var (tpl, problem) = LoadTemplate(templatePath, abs.Size, name);
        if (tpl is null)
            return (null, null, problem);

        return (new RegionReader(abs), tpl, null);
    }

    private static (GrayTemplate tpl, string problem) LoadTemplate(string path, Size roi, string name)
    {
        if (!File.Exists(path))
            return (null, $"chưa có mẫu {name} ({Path.GetFileName(path)})");
        try
        {
            var t = GrayTemplate.FromFile(path);
            if (t.IsFlat)
                return (null, $"mẫu {name} phẳng — khoanh lại lúc HUD đang hiện");
            if (t.Width != roi.Width || t.Height != roi.Height)
                return (null, $"mẫu {name} {t.Width}×{t.Height} lệch ô {roi.Width}×{roi.Height} — khoanh lại");
            return (t, null);
        }
        catch (Exception ex)
        {
            return (null, $"mẫu {name}: {ex.Message}");
        }
    }

    public MinerSnapshot Read()
    {
        double mining = Score(_mining, _miningTpl);
        double lift = Score(_lift, _liftTpl);
        double cash = Score(_cash, _cashTpl);

        return new MinerSnapshot
        {
            MiningConfigured = _miningTpl is not null,
            LiftConfigured = _liftTpl is not null,
            CashConfigured = _cashTpl is not null,
            Mining = mining >= _cfg.MiningNccMin,
            MiningScore = mining,
            LiftPrompt = lift >= _cfg.LiftNccMin,
            LiftScore = lift,
            CashToast = cash >= _cfg.CashNccMin,
            CashScore = cash
        };
    }

    private static double Score(RegionReader r, GrayTemplate tpl)
    {
        if (r is null || tpl is null) return -1;
        r.Refresh();
        return tpl.Score(r.GrayBuffer(r.Region));
    }

    public void Dispose()
    {
        _mining?.Dispose();
        _lift?.Dispose();
        _cash?.Dispose();
    }
}
