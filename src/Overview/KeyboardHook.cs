using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Overview;

/// <summary>
/// Detects a lone tap of the Windows (Super) key with a low-level keyboard hook and calls
/// <c>onTap</c>. To keep the Start menu closed it uses AutoHotkey's trick: right after the Windows
/// key goes down, an unassigned key (vkE8) is pressed, and Windows doesn't open Start when another
/// key happened between Windows-down and Windows-up. Win+anything shortcuts keep working.
///
/// Real key events are never blocked, so the Windows key can't get stuck "held down" (which made
/// every key act as a Win+ shortcut when Windows silently dropped injected keys, e.g. while an
/// admin window had focus). The hook runs on its own thread and returns at once, so a busy UI can
/// never lag system-wide typing.
/// </summary>
internal sealed class KeyboardHook
{
    const uint WM_APP_MASK = 0x8001;   // posted to the hook thread: send the vkE8 mask now
    const uint WM_APP_REARM = 0x8002;  // posted by the timer: reinstall the hook
    const uint WM_TIMER = 0x0113;

    readonly Dispatcher _ui;
    readonly Action<long> _onTap;
    Thread? _thread;
    uint _threadId;
    IntPtr _hook;
    Native.HookProc? _proc; // keep the delegate alive

    public volatile bool Enabled = true;

    bool _winDown, _otherKey, _maskSent;
    uint _winVk, _lastWinTime;

    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);

    /// <param name="onTap">Called on the UI thread with the tick count when the tap happened.</param>
    public KeyboardHook(Dispatcher ui, Action<long> onTap)
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
        if (!Install()) return;
        // Windows silently removes a low-level hook if it ever responds too slowly (e.g. the PC was
        // overloaded), and the Windows key would stop working. Reinstall it every 10 minutes.
        SetTimer(IntPtr.Zero, UIntPtr.Zero, 10 * 60 * 1000, IntPtr.Zero);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            switch (msg.message)
            {
                case WM_APP_MASK:
                    if (_winDown && !_maskSent)
                    {
                        Native.SendKeyTap(Native.VK_MASK);
                        _maskSent = true;
                    }
                    break;
                case WM_TIMER:
                case WM_APP_REARM:
                    if (!_winDown) // never in the middle of a key press
                    {
                        Native.UnhookWindowsHookEx(_hook);
                        Install();
                    }
                    break;
                default:
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                    break;
            }
        }
        Native.UnhookWindowsHookEx(_hook);
    }

    bool Install()
    {
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc!, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        return _hook != IntPtr.Zero;
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try { Handle((int)wParam, Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam)); }
            catch { /* never let an exception escape a hook */ }
        }
        // Always pass the key on: blocking real key events is what could leave the Windows key stuck.
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void Handle(int msg, Native.KBDLLHOOKSTRUCT k)
    {
        if ((k.flags & Native.LLKHF_INJECTED) != 0) return; // ignore our own (and other tools') synthetic keys

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
                    _maskSent = false;
                    // Send the mask key *after* this callback returns, so it lands after Windows-down.
                    // (Sent from inside the callback it would arrive before it, and Start would open.)
                    if (Enabled) Native.PostThreadMessage(_threadId, WM_APP_MASK, IntPtr.Zero, IntPtr.Zero);
                }
            }
            else if (up)
            {
                bool tap = _winDown && !_otherKey && k.vkCode == _winVk;
                _winDown = false;
                if (tap && Enabled)
                {
                    long now = Environment.TickCount64;
                    _ui.InvokeAsync(() => _onTap(now));
                }
            }
        }
        else if (down && _winDown)
        {
            _otherKey = true; // Win+E, Win+Shift+S, ... -> not a tap
        }
    }
}
