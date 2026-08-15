using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// Gui input vao game. GTA V doc raw input nen bat buoc dung SendInput voi
/// SCANCODE that, khong dung duoc keybd_event/VK-only.
/// </summary>
internal static class InputSender
{
    private static void Send(params Native.INPUT[] inputs)
    {
        uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException(
                $"SendInput gui duoc {sent}/{inputs.Length} (Win32Error={Marshal.GetLastWin32Error()}). " +
                "Thuong la do game chay quyen Admin ma app thi khong - hay chay app bang Run as administrator.");
    }

    /// <summary>Dua con tro tro toi (x,y) - toa do man hinh vat ly.</summary>
    public static void MoveTo(int x, int y)
    {
        // SetCursorPos cho UI thong thuong, SendInput absolute cho chac an voi game.
        Native.SetCursorPos(x, y);

        var (nx, ny) = Native.ToAbsolute(x, y);
        Send(new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion
            {
                mi = new Native.MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = Native.MAGIC
                }
            }
        });
    }

    /// <summary>
    /// CHI dat lai vi tri con tro, khong ban su kien chuot nao.
    /// <see cref="MoveTo"/> ban them MOUSEEVENTF_MOVE, ma GTA doc raw input de xoay camera —
    /// trong che do con dang cam camera (vi du menu radial giu Alt), cu do bi nuot thanh lenh
    /// xoay va menu tat theo. SetCursorPos khong sinh ra delta do.
    /// </summary>
    public static void MoveCursorOnly(int x, int y) => Native.SetCursorPos(x, y);

    /// <summary>Rê con trỏ nhiều bước nhỏ nhưng chỉ bằng SetCursorPos.</summary>
    public static void MoveCursorOnlySmooth(int x, int y, int steps, int stepDelayMs = 12)
    {
        Native.GetCursorPos(out var from);
        steps = Math.Max(1, steps);
        for (int i = 1; i <= steps; i++)
        {
            MoveCursorOnly(from.x + (x - from.x) * i / steps, from.y + (y - from.y) * i / steps);
            if (i < steps) Thread.Sleep(stepDelayMs);
        }
        MoveCursorOnly(x, y);
    }

    /// <summary>
    /// Di chuyen theo NHIEU BUOC NHO thay vi teleport mot nhat.
    /// Game cap nhat "dang hover cai nao" theo frame va co the theo doi chuyen dong;
    /// nhay mot phat toi dich de bi bo qua, khien cu LeftDown ngay sau do
    /// roi vao khoang trong.
    /// </summary>
    public static void MoveSmooth(int x, int y, int steps, int stepDelayMs = 12)
    {
        Native.GetCursorPos(out var from);
        steps = Math.Max(1, steps);
        for (int i = 1; i <= steps; i++)
        {
            int ix = from.x + (x - from.x) * i / steps;
            int iy = from.y + (y - from.y) * i / steps;
            MoveTo(ix, iy);
            if (i < steps) Thread.Sleep(stepDelayMs);
        }
        MoveTo(x, y);   // chot dung dich, tranh sai so chia nguyen
    }

    public static void LeftDown() => MouseButton(Native.MOUSEEVENTF_LEFTDOWN);
    public static void LeftUp() => MouseButton(Native.MOUSEEVENTF_LEFTUP);

    private static void MouseButton(uint flag)
    {
        Send(new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion
            {
                mi = new Native.MOUSEINPUT { dwFlags = flag, dwExtraInfo = Native.MAGIC }
            }
        });
    }

    /// <summary>Nhan roi nha 1 phim theo scancode that (vd VK 0x45 = E).</summary>
    public static void TapKey(ushort vk, int holdMs = 60)
    {
        KeyDown(vk);
        Thread.Sleep(holdMs);
        KeyUp(vk);
    }

    public static void KeyDown(ushort vk) => Key(vk, false);
    public static void KeyUp(ushort vk) => Key(vk, true);

    /// <summary>Scancode Alt TRAI. Xem <see cref="AltDown"/> de biet vi sao khong dung VK.</summary>
    private const ushort SCAN_LALT = 0x38;

    /// <summary>
    /// Giu/nha Alt TRAI bang scancode thang, KHONG extended.
    /// Di qua MapVirtualKey nhu cac phim khac co the ra E0 38 - tren nhieu layout do la AltGr
    /// va keo theo Ctrl ngam, game se thay mot modifier minh khong he dinh gui. Loi cung loai
    /// da gap voi Ctrl, xem UtilityService.CtrlVk().
    /// </summary>
    public static void AltDown() => ScanKey(SCAN_LALT, extended: false, up: false);
    public static void AltUp() => ScanKey(SCAN_LALT, extended: false, up: true);

    private static void Key(ushort vk, bool up)
    {
        uint sc = Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC_EX);
        bool extended = ((sc >> 8) & 0xFF) is 0xE0 or 0xE1;
        ScanKey((ushort)(sc & 0xFF), extended, up);
    }

    private static void ScanKey(ushort scan, bool extended, bool up)
    {
        uint flags = Native.KEYEVENTF_SCANCODE;
        if (extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        if (up) flags |= Native.KEYEVENTF_KEYUP;

        Send(new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = 0,          // scancode-only: game doc raw input se nhan duoc
                    wScan = scan,
                    dwFlags = flags,
                    dwExtraInfo = Native.MAGIC
                }
            }
        });
    }
}
