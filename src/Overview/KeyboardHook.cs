using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Overview;

/// <summary>
/// Detects a lone tap of the Windows (Super) key with a low-level keyboard hook.
/// When enabled, it masks the tap (like AutoHotkey's vkE8 trick) so the Start menu
/// doesn't open, and calls <c>onTap</c> instead. Win+anything shortcuts keep working.
/// The hook runs on its own thread so a busy UI can never lag system-wide typing.
/// </summary>
internal sealed class KeyboardHook
{
    readonly Dispatcher _ui;
    readonly Action _onTap;
    Thread? _thread;
    uint _threadId;
    IntPtr _hook;
    Native.HookProc? _proc; // keep the delegate alive

    public volatile bool Enabled = true;

    bool _winDown, _otherKey;
    uint _winVk, _lastWinTime;

    public KeyboardHook(Dispatcher ui, Action onTap)
    {
        _ui = ui;
        _onTap = onTap;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Overview keyboard hook" };
        _thread.Start();
    }

    public void Stop()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Write("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
            return;
        }
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        Native.UnhookWindowsHookEx(_hook);
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                if (Handle((int)wParam, Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam)))
                    return (IntPtr)1; // swallow this event
            }
            catch { /* never let an exception escape a hook */ }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <returns>true to swallow the event.</returns>
    bool Handle(int msg, Native.KBDLLHOOKSTRUCT k)
    {
        if ((k.flags & Native.LLKHF_INJECTED) != 0) return false; // ignore our own (and other tools') synthetic keys

        bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
        bool isWin = k.vkCode is Native.VK_LWIN or Native.VK_RWIN;

        if (isWin)
        {
            if (down)
            {
                // Auto-repeat sends repeated key-downs while held; treat those as the same press.
                // The time check recovers if a key-up was missed (e.g. Win+L locked the session).
                bool repeat = _winDown && k.vkCode == _winVk && unchecked(k.time - _lastWinTime) < 1000;
                _lastWinTime = k.time;
                if (!repeat)
                {
                    _winDown = true;
                    _winVk = k.vkCode;
                    _otherKey = false;
                }
            }
            else if (up)
            {
                bool tap = _winDown && !_otherKey && k.vkCode == _winVk;
                _winDown = false;
                if (tap && Enabled)
                {
                    _ui.InvokeAsync(_onTap);
                    // Replace the real release with "dummy key, release". Windows only opens Start
                    // when nothing happened between Win down and Win up, so Start stays closed.
                    // vkE8 is unassigned, so the dummy key has no other effect. If injecting
                    // fails, let the real release through so the Win key can never get stuck.
                    return Native.SendMaskedWinRelease(k.vkCode);
                }
            }
        }
        else if (down && _winDown)
        {
            _otherKey = true; // Win+E, Win+Shift+S, ... -> not a tap
        }
        return false;
    }
}
