using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// Tiện ích toàn app: một phím toggle giữ W, một phím toggle giữ W + Left Shift (chạy nước rút),
/// một phím giữ lâu thì thêm Left Ctrl.
/// Các phím này lấy từ <see cref="HotkeyConfig"/>. Chỉ inject khi cửa sổ game đang focus.
///
/// Tự chạy và chạy nước rút LOẠI TRỪ NHAU: bật cái này thì cái kia tắt. Cả hai đều giữ W,
/// nên W đi theo <see cref="WantW_NoLock"/> chứ không theo riêng cờ nào.
/// </summary>
internal sealed class UtilityService : IDisposable
{
    public const ushort VK_W = 0x57;
    public const ushort VK_LCONTROL = 0xA2;

    private const int HoldFMs = 200;
    private const int KeepAliveMs = 400;
    private const int TickMs = 50;

    private readonly object _gate = new();
    private readonly string _windowMatch;
    private readonly Native.LowLevelKeyboardProc _hookProc;
    private readonly System.Threading.Timer _tick;
    private readonly System.Threading.Timer _holdF;

    private IntPtr _hook;
    private bool _enabled;
    private bool _autoRun;
    private bool _sprint;
    private bool _wInjected;
    private bool _shiftInjected;
    private bool _ctrlHeld;
    private bool _fDown;
    private bool _eatCapsUp;
    private bool _eatSprintUp;
    private bool _gameFocus;
    private long _lastWPing;
    private long _lastShiftPing;
    private uint _autoRunVk = HotkeyConfig.DefaultAutoRunVk;
    private uint _holdVk = HotkeyConfig.DefaultHoldCtrlVk;
    private uint _sprintVk = HotkeyConfig.DefaultSprintVk;

    public event Action Changed;

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    public bool AutoRun
    {
        get { lock (_gate) return _autoRun; }
    }

    public bool Sprint
    {
        get { lock (_gate) return _sprint; }
    }

    public bool CtrlHeld
    {
        get { lock (_gate) return _ctrlHeld; }
    }

    public bool GameFocused
    {
        get { lock (_gate) return _gameFocus; }
    }

    public UtilityService()
    {
        _windowMatch = BotConfig.Load()?.WindowMatch ?? "PlayXGTA";
        if (string.IsNullOrWhiteSpace(_windowMatch))
            _windowMatch = "PlayXGTA";

        var keys = HotkeyConfig.Load();
        _autoRunVk = keys.AutoRunVk;
        _holdVk = keys.HoldCtrlVk;
        _sprintVk = keys.SprintVk;

        _hookProc = HookCallback;
        _tick = new System.Threading.Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
        _holdF = new System.Threading.Timer(OnHoldFElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Toggle()
    {
        if (Enabled) Disable();
        else Enable();
    }

    public void Enable()
    {
        lock (_gate)
        {
            if (_enabled) return;
            _enabled = true;
            _gameFocus = GameIsForeground_NoLock();
            InstallHook_NoLock();
            _tick.Change(TickMs, TickMs);
        }
        RaiseChanged();
    }

    public void Disable()
    {
        lock (_gate)
        {
            if (!_enabled) return;
            _enabled = false;
            _autoRun = false;
            _sprint = false;
            _fDown = false;
            _eatCapsUp = false;
            _eatSprintUp = false;
            _holdF.Change(Timeout.Infinite, Timeout.Infinite);
            _tick.Change(Timeout.Infinite, Timeout.Infinite);
            ReleaseInjected_NoLock();
            UninstallHook_NoLock();
        }
        RaiseChanged();
    }

    /// <summary>
    /// Đổi phím lúc đang bật: nhả W/Shift/Ctrl đang giữ trước, nếu không phím cũ kẹt xuống.
    /// </summary>
    public void SetKeys(uint autoRunVk, uint holdCtrlVk, uint sprintVk)
    {
        lock (_gate)
        {
            if (_autoRunVk == autoRunVk && _holdVk == holdCtrlVk && _sprintVk == sprintVk) return;
            ReleaseInjected_NoLock();
            _autoRun = false;
            _sprint = false;
            _fDown = false;
            _eatCapsUp = false;
            _eatSprintUp = false;
            _holdF.Change(Timeout.Infinite, Timeout.Infinite);
            _autoRunVk = autoRunVk;
            _holdVk = holdCtrlVk;
            _sprintVk = sprintVk;
        }
        RaiseChanged();
    }

    public void Shutdown()
    {
        Disable();
        _tick.Dispose();
        _holdF.Dispose();
    }

    public void Dispose() => Shutdown();

    private void InstallHook_NoLock()
    {
        if (_hook != IntPtr.Zero) return;
        IntPtr mod = Native.GetModuleHandle(null);
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, mod, 0);
    }

    private void UninstallHook_NoLock()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        bool isUp = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
        if (!isDown && !isUp)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        if (info.dwExtraInfo == Native.MAGIC)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        if ((info.flags & Native.LLKHF_INJECTED) != 0)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        bool enabled;
        uint autoRunVk, holdVk, sprintVk;
        lock (_gate)
        {
            enabled = _enabled;
            autoRunVk = _autoRunVk;
            holdVk = _holdVk;
            sprintVk = _sprintVk;
        }
        if (!enabled)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        if (info.vkCode == autoRunVk)
        {
            if (isDown && GameIsForeground_NoLock())
            {
                lock (_gate) _eatCapsUp = true;
                ThreadPool.QueueUserWorkItem(_ => ToggleAutoRun());
                return (IntPtr)1;
            }
            if (isUp)
            {
                bool eat;
                lock (_gate)
                {
                    eat = _eatCapsUp;
                    _eatCapsUp = false;
                }
                if (eat) return (IntPtr)1;
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (info.vkCode == sprintVk)
        {
            if (isDown && GameIsForeground_NoLock())
            {
                lock (_gate) _eatSprintUp = true;
                ThreadPool.QueueUserWorkItem(_ => ToggleSprint());
                return (IntPtr)1;
            }
            if (isUp)
            {
                bool eat;
                lock (_gate)
                {
                    eat = _eatSprintUp;
                    _eatSprintUp = false;
                }
                if (eat) return (IntPtr)1;
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (info.vkCode == holdVk)
        {
            if (isDown) ThreadPool.QueueUserWorkItem(_ => OnFDown());
            else ThreadPool.QueueUserWorkItem(_ => OnFUp());
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void ToggleAutoRun()
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_enabled) return;
            if (!GameIsForeground_NoLock()) return;
            _autoRun = !_autoRun;
            if (_autoRun) _sprint = false;   // loại trừ nhau
            changed = true;
            ApplyHold_NoLock();
        }
        if (changed) RaiseChanged();
    }

    private void ToggleSprint()
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_enabled) return;
            if (!GameIsForeground_NoLock()) return;
            _sprint = !_sprint;
            if (_sprint) _autoRun = false;   // loại trừ nhau
            changed = true;
            ApplyHold_NoLock();
        }
        if (changed) RaiseChanged();
    }

    private void OnFDown()
    {
        lock (_gate)
        {
            if (!_enabled || _fDown) return;
            _fDown = true;
            _holdF.Change(HoldFMs, Timeout.Infinite);
        }
    }

    private void OnFUp()
    {
        bool changed = false;
        lock (_gate)
        {
            _fDown = false;
            _holdF.Change(Timeout.Infinite, Timeout.Infinite);
            if (_ctrlHeld)
            {
                ReleaseCtrl_NoLock();
                changed = true;
            }
        }
        if (changed) RaiseChanged();
    }

    private void OnHoldFElapsed(object _)
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_enabled || !_fDown || _ctrlHeld) return;
            if (!GameIsForeground_NoLock()) return;
            if (!Native.IsKeyDown((int)_holdVk)) return;
            PressCtrl_NoLock();
            changed = true;
        }
        if (changed) RaiseChanged();
    }

    private void Tick(object _)
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_enabled) return;

            bool focus = GameIsForeground_NoLock();
            if (focus != _gameFocus)
            {
                _gameFocus = focus;
                changed = true;
            }

            // Mất focus thì nhả phím nhưng GIỮ ý định — quay lại game là chạy tiếp.
            if (focus) ApplyHold_NoLock();
            else { ReleaseW_NoLock(); ReleaseShift_NoLock(); }

            if (_ctrlHeld && (!focus || !_fDown || !Native.IsKeyDown((int)_holdVk)))
            {
                ReleaseCtrl_NoLock();
                changed = true;
            }
        }
        if (changed) RaiseChanged();
    }

    private bool GameIsForeground_NoLock()
    {
        if (string.IsNullOrWhiteSpace(_windowMatch)) return true;
        return Native.ForegroundTitle().Contains(_windowMatch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cả tự chạy lẫn chạy nước rút đều giữ W, nên W theo cờ gộp.</summary>
    private bool WantW_NoLock() => _autoRun || _sprint;

    /// <summary>
    /// Đặt phím giữ khớp với ý định hiện tại. Gọi cả lúc toggle lẫn mỗi tick —
    /// tick còn lo keep-alive, vì game hay bỏ rơi phím giữ lâu và
    /// <see cref="HeldKeys.ReleaseAll"/> của job Thợ mỏ cũng nhả W/Shift.
    /// </summary>
    private void ApplyHold_NoLock()
    {
        if (WantW_NoLock()) EnsureW_NoLock();
        else ReleaseW_NoLock();

        if (_sprint) EnsureShift_NoLock();
        else ReleaseShift_NoLock();
    }

    private void EnsureW_NoLock()
    {
        long now = Environment.TickCount64;
        if (!_wInjected || now - _lastWPing >= KeepAliveMs)
        {
            try { InputSender.KeyDown(VK_W); } catch { }
            _wInjected = true;
            _lastWPing = now;
        }
    }

    private void ReleaseW_NoLock()
    {
        if (!_wInjected) return;
        try { InputSender.KeyUp(VK_W); } catch { }
        _wInjected = false;
    }

    /// <summary>
    /// Left Shift đi thẳng scancode qua <see cref="InputSender.ShiftDown"/> —
    /// xem chú thích ở đó, VK_LSHIFT hay ra scancode 0 nên phím không bao giờ xuống.
    /// </summary>
    private void EnsureShift_NoLock()
    {
        long now = Environment.TickCount64;
        if (!_shiftInjected || now - _lastShiftPing >= KeepAliveMs)
        {
            try { InputSender.ShiftDown(); } catch { }
            _shiftInjected = true;
            _lastShiftPing = now;
        }
    }

    private void ReleaseShift_NoLock()
    {
        if (!_shiftInjected) return;
        try { InputSender.ShiftUp(); } catch { }
        _shiftInjected = false;
    }

    private void PressCtrl_NoLock()
    {
        if (_ctrlHeld) return;
        try { InputSender.KeyDown(CtrlVk()); } catch { }
        _ctrlHeld = true;
    }

    private void ReleaseCtrl_NoLock()
    {
        if (!_ctrlHeld) return;
        try { InputSender.KeyUp(CtrlVk()); } catch { }
        _ctrlHeld = false;
    }

    private void ReleaseInjected_NoLock()
    {
        ReleaseW_NoLock();
        ReleaseShift_NoLock();
        ReleaseCtrl_NoLock();
    }

    /// <summary>
    /// VK_LCONTROL đôi khi MapVirtualKey ra 0; lúc đó dùng VK_CONTROL (Left Ctrl).
    /// </summary>
    private static ushort CtrlVk()
    {
        uint sc = Native.MapVirtualKey(VK_LCONTROL, Native.MAPVK_VK_TO_VSC_EX);
        return sc == 0 ? (ushort)0x11 : VK_LCONTROL;
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h == null) return;
        try { h(); } catch { }
    }
}
