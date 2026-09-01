namespace GtaMiniGameBot;

/// <summary>
/// Pha cua vong cau. Truoc day pha khong ton tai duoi dang mot gia tri nao: chi co
/// `bool fighting` + may co roi, va ba pha (cat ca, do cop, cuoi phien) chi ton tai
/// duoi dang stack frame. Ket qua la khong the suy ra dang o dau — `fighting` van
/// true suot ca quang cat ca lan do cop.
/// </summary>
internal enum FishingPhase
{
    Idle,
    Casting,
    WaitingForBite,
    Fighting,
    WaitingForKeep,
    ClickingKeep,
    ClickingRelease,
    CheckingWeight,
    Dumping,
    EndgameWeighing,
    Stopped
}

/// <summary>
/// Anh chup trang thai bot, bat bien. Dung cung kieu truyen nhu
/// <see cref="FishingSnapshot"/> — dung qua event, moi field init-only — nen khong
/// can lock nao ca.
///
/// Quan trong: cac so cua cop duoc COPY vao day luc phat, khong phoi TrunkDumper ra
/// ngoai. `_dumper` bi set null trong finally cua luong bot, phoi ra la dua.
/// </summary>
internal sealed class FishingState
{
    public FishingPhase Phase { get; init; }

    /// <summary>Da o trong pha nay bao lau.</summary>
    public long PhaseMs { get; init; }

    /// <summary>Tong thoi gian phien, tinh tu luc bot bat dau.</summary>
    public long SessionMs { get; init; }

    // ---------- dem theo ket qua moi cu tha ----------
    public int Casts { get; init; }
    public int Bites { get; init; }
    public int Rejects { get; init; }
    /// <summary>
    /// Cú thả bị chặn vì "không đứng gần mặt nước". Trước khi có mẫu nhận thông báo này thì
    /// chúng rơi vào <see cref="CastMissed"/>, nên hai số này thông nhau — đừng cộng cả hai
    /// rồi tưởng là hai chuyện riêng.
    /// </summary>
    public int NoWater { get; init; }
    /// <summary>
    /// Cú thả bị chặn vì "không có cá nào phù hợp với cần và độ sâu". Cũng từng rơi vào
    /// <see cref="CastMissed"/> như <see cref="NoWater"/>, cùng lưu ý về việc đừng cộng dồn.
    /// </summary>
    public int NoFish { get; init; }
    public int CastMissed { get; init; }
    /// <summary>
    /// Cú thả bị cắt sớm vì thanh câu đã hiện rồi tắt giữa chừng. Ăn thẳng vào
    /// <see cref="BiteTimeouts"/>: mỗi cái ở đây là một lượt lẽ ra phải chờ trọn WaitBiteMs.
    /// </summary>
    public int BarGoneRecasts { get; init; }
    public int BiteTimeouts { get; init; }
    public int FightTimeouts { get; init; }

    /// <summary>Ca da cat vao — dem o dung cho ca len, khong phai o MaybeDump.</summary>
    public int Catches { get; init; }
    public int CatchesSinceDump { get; init; }

    /// <summary>Ca da an THẢ RA — khong vao ba lo, khong tinh vao Catches.</summary>
    public int Released { get; init; }

    /// <summary>Lan tha lai vi thanh khong hien, trong dung cu tha nay.</summary>
    public int CastRetries { get; init; }
    public int CastConfirmRetries { get; init; }

    /// <summary>
    /// Fill thanh cau doc duoc gan nhat, 0..1. -1 = chua doc duoc.
    /// Co trong day de badge overlay ve duoc thanh tien do ma khong phai
    /// dang ky them mot event snapshot nua.
    /// </summary>
    public double Fill01 { get; init; } = -1;

    // ---------- cop / ba lo (-1 = chua biet) ----------
    public double BagKg { get; init; } = -1;
    public double BagCapKg { get; init; } = -1;
    public double PendingFishKg { get; init; } = -1;
    public double TrunkFreeKg { get; init; } = -1;
    public double TrunkCapKg { get; init; } = -1;
    public int TrunkFullStrikes { get; init; }
    public int TrunkFullTries { get; init; }
    public bool TrunkFull { get; init; }
    public bool OcrHealthy { get; init; } = true;
    public bool DumpOn { get; init; }

    public static readonly FishingState Idle = new();

    /// <summary>Ten pha, dung chung cho ca panel lan badge overlay.</summary>
    public string PhaseName => Phase switch
    {
        FishingPhase.Casting => "Thả câu",
        FishingPhase.WaitingForBite => "Chờ cắn",
        FishingPhase.Fighting => "Giữ S",
        FishingPhase.WaitingForKeep => "Chờ nút",
        FishingPhase.ClickingKeep => "Cất vào",
        FishingPhase.ClickingRelease => "Thả ra",
        FishingPhase.CheckingWeight => "Cân ba lô",
        FishingPhase.Dumping => "Đổ cốp",
        FishingPhase.EndgameWeighing => "Cân cuối phiên",
        FishingPhase.Stopped => "Đã dừng",
        _ => "Chưa chạy"
    };

    /// <summary>Tram nao tren so do vong cau dang sang. -1 = khong tram nao.</summary>
    public int Station => Phase switch
    {
        FishingPhase.Casting => 0,
        FishingPhase.WaitingForBite => 1,
        FishingPhase.Fighting => 2,
        FishingPhase.WaitingForKeep or FishingPhase.ClickingKeep or FishingPhase.ClickingRelease => 3,
        FishingPhase.CheckingWeight or FishingPhase.Dumping or FishingPhase.EndgameWeighing => 4,
        _ => -1
    };

    public bool Running => Phase is not (FishingPhase.Idle or FishingPhase.Stopped);

    /// <summary>Ca moi gio. -1 khi chua chay du lau de con so co nghia.</summary>
    public double CatchesPerHour =>
        SessionMs < 60_000 ? -1 : Catches * 3_600_000.0 / SessionMs;

    /// <summary>Ti le cu tha an — cua tha nao dan toi ca can. -1 khi chua tha lan nao.</summary>
    public double BiteRate01 => Casts <= 0 ? -1 : Math.Min(1.0, Bites / (double)Casts);

    /// <summary>Giay trung binh moi con. -1 khi chua bat duoc con nao.</summary>
    public double SecondsPerCatch => Catches <= 0 ? -1 : SessionMs / 1000.0 / Catches;
}
