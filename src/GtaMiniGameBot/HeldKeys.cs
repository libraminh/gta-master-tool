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

    /// <summary>
    /// An toàn khi gọi nhiều lần và khi chưa giữ gì. Mỗi thứ một try riêng: một cái ném lỗi
    /// không được phép chặn cái sau — đó đúng là ca mình cần nhả nhất.
    ///
    /// W và Left Shift là của job Thợ mỏ (MinerBot). Nhả ở đây cũng đồng thời nhả W mà tiện ích
    /// CapsLock đang giữ, nhưng UtilityService tick 50 ms với keep-alive 400 ms sẽ tự giữ lại —
    /// đổi một khoảng hụt dưới nửa giây lấy bảo đảm "dừng job là chắc chắn không kẹt phím".
    ///
    /// A và D là của <see cref="NavBot"/>: né vật cản bằng cách trượt ngang, nên có lúc A hoặc D
    /// đang xuống. Kẹt A trong game thì nhân vật đi ngang mãi — hỏng cả buổi chơi chứ không chỉ
    /// hỏng bot, cùng hạng với kẹt Alt.
    /// </summary>
    public static void ReleaseAll()
    {
        try { InputSender.AltUp(); } catch { }
        try { InputSender.ShiftUp(); } catch { }
        try { InputSender.KeyUp(VK_S); } catch { }
        try { InputSender.KeyUp(VK_W); } catch { }
        try { InputSender.KeyUp(VK_A); } catch { }
        try { InputSender.KeyUp(VK_D); } catch { }
        try { InputSender.LeftUp(); } catch { }
    }
}
