namespace GtaMiniGameBot;

/// <summary>
/// Nhả mọi phím/nút mà app có thể đang giữ trong game.
/// Trước đây mỗi panel tự nhả một danh sách riêng (FishingBot nhả S + chuột, OilWellPanel chỉ
/// nhả chuột) nên thêm một phím giữ mới là chắc chắn quên một chỗ. Giờ chỉ còn một nơi để sửa.
/// Alt là thứ nguy hiểm nhất trong danh sách: kẹt Alt làm hỏng cả game chứ không chỉ hỏng bot,
/// và trong lúc Alt còn xuống thì phím tắt dừng bot (đăng ký không modifier) cũng không nổ.
/// </summary>
internal static class HeldKeys
{
    public const ushort VK_S = 0x53;
    public const ushort VK_W = 0x57;
    public const ushort VK_A = 0x41;
    public const ushort VK_D = 0x44;
    public const ushort VK_ALT = 0x12;
    public const ushort VK_E = 0x45;
    public const ushort VK_ESC = 0x1B;

    /// <summary>Số hàng trên: mã phím ảo trùng mã ASCII của ký tự.</summary>
    public const ushort VK_1 = 0x31;

    public const ushort VK_9 = 0x39;

    /// <summary>
    /// An toàn khi gọi nhiều lần và khi chưa giữ gì. Mỗi thứ một try riêng: một cái ném lỗi
    /// không được phép chặn cái sau — đó đúng là ca mình cần nhả nhất.
    ///
    /// W và Left Shift là của job Thợ mỏ (MinerBot) và của bộ điều hướng thợ điện (<see cref="NavBot"/>
    /// giữ W+Shift liên tục). Nhả ở đây cũng đồng thời nhả W mà tiện ích CapsLock đang giữ, nhưng
    /// UtilityService tick 50 ms với keep-alive 400 ms sẽ tự giữ lại — đổi một khoảng hụt dưới nửa
    /// giây lấy bảo đảm "dừng job là chắc chắn không kẹt phím".
    ///
    /// A và D: bộ điều hướng hiện tại KHÔNG dùng (bản Python chỉ đi W+Shift), nhưng vẫn nhả cho
    /// chắc — kẹt A trong game thì nhân vật đi ngang mãi, cùng hạng với kẹt Alt.
    ///
    /// E và Esc: bộ điều hướng giữ E đúng 90 ms (một cú down, một cú up ở tick sau) và gõ Esc khi
    /// đóng bảng nghề; dừng bot đúng giữa hai cú đó thì phím còn xuống trong game.
    ///
    /// Số 1–9: bộ ăn/uống giữ một phím hotbar đúng 150 ms theo cùng kiểu hai tick đó, và ô nào chứa
    /// bánh/nước là người dùng đặt trong config nên không đoán trước được phím nào. Nhả cả dãy: kẹt
    /// một phím số trong game là nhân vật cầm mãi món đồ đó.
    /// </summary>
    public static void ReleaseAll()
    {
        try { InputSender.AltUp(); } catch { }
        try { InputSender.ShiftUp(); } catch { }
        try { InputSender.KeyUp(VK_S); } catch { }
        try { InputSender.KeyUp(VK_W); } catch { }
        try { InputSender.KeyUp(VK_A); } catch { }
        try { InputSender.KeyUp(VK_D); } catch { }
        try { InputSender.KeyUp(VK_E); } catch { }
        try { InputSender.KeyUp(VK_ESC); } catch { }
        for (ushort vk = VK_1; vk <= VK_9; vk++) { try { InputSender.KeyUp(vk); } catch { } }
        try { InputSender.LeftUp(); } catch { }
    }
}
