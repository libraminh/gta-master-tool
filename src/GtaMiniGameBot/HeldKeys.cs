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
    public const ushort VK_ALT = 0x12;

    /// <summary>
    /// An toàn khi gọi nhiều lần và khi chưa giữ gì. Mỗi thứ một try riêng: một cái ném lỗi
    /// không được phép chặn cái sau — đó đúng là ca mình cần nhả nhất.
    /// </summary>
    public static void ReleaseAll()
    {
        try { InputSender.AltUp(); } catch { }
        try { InputSender.KeyUp(VK_S); } catch { }
        try { InputSender.LeftUp(); } catch { }
    }
}
