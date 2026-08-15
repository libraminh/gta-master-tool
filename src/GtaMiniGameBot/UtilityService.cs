using System.Runtime.InteropServices;

namespace GtaMiniGameBot;

/// <summary>
/// Tiện ích toàn app: một phím toggle giữ W, một phím giữ lâu thì thêm Left Ctrl.
/// Hai phím này lấy từ <see cref="HotkeyConfig"/>. Chỉ inject khi cửa sổ game đang focus.
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
    private bool _wInjected;
    private bool _ctrlHeld;
    private bool _fDown;
    private bool _eatCapsUp;
    private bool _gameFocus;
    private long _lastWPing;
    private uint _autoRunVk = HotkeyConfig.DefaultAutoRunVk;
    private uint _holdVk = HotkeyConfig.DefaultHoldCtrlVk;

    public event Action Changed;

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    public bool AutoRun
    {
        get { lock (_gate) return _autoRun; }
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
            _fDown = false;
            _eatCapsUp = false;
            _holdF.Change(Timeout.Infinite, Timeout.Infinite);
            _tick.Change(Timeout.Infinite, Timeout.Infinite);
            ReleaseInjected_NoLock();
            UninstallHook_NoLock();
        }
        RaiseChanged();
    }

    /// <summary>
    /// Đổi phím lúc đang bật: nhả W/Ctrl đang giữ trước, nếu không phím cũ kẹt xuống.
    /// </summary>
    public void SetKeys(uint autoRunVk, uint holdCtrlVk)
    {
        lock (_gate)
        {
            if (_autoRunVk == autoRunVk && _holdVk == holdCtrlVk) return;
            ReleaseInjected_NoLock();
            _autoRun = false;
            _fDown = false;
            _eatCapsUp = false;
            _holdF.Change(Timeout.Infinite, Timeout.Infinite);
            _autoRunVk = autoRunVk;
            _holdVk = holdCtrlVk;
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
        uint autoRunVk, holdVk;
        lock (_gate)
        {
            enabled = _enabled;
            autoRunVk = _autoRunVk;
            holdVk = _holdVk;
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
            changed = true;
            if (_autoRun) EnsureW_NoLock();
            else ReleaseW_NoLock();
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

            if (_autoRun)
            {
                if (focus) EnsureW_NoLock();
                else ReleaseW_NoLock();
            }

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
