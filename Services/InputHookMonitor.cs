using System.Runtime.InteropServices;

namespace CraftStats;

/// <summary>Installs and removes the low-level keyboard and mouse hooks.</summary>
public sealed class InputHookMonitor : IDisposable
{
    public enum MouseButton { Left, Right, Middle }

    private readonly NativeMethods.HookProc _keyboardProc;
    private readonly NativeMethods.HookProc _mouseProc;
    private nint _keyboardHook;
    private nint _mouseHook;

    public event Action<int>? KeyPressed;
    public event Action<MouseButton>? MouseButtonPressed;
    public bool LastInstallFailed { get; private set; }

    public InputHookMonitor()
    {
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void UpdateHooks(bool keyboard, bool mouse)
    {
        var module = NativeMethods.GetModuleHandle(null);

        if (keyboard && _keyboardHook == 0)
            _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        else if (!keyboard && _keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }

        if (mouse && _mouseHook == 0)
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        else if (!mouse && _mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }

        LastInstallFailed = (keyboard && _keyboardHook == 0) || (mouse && _mouseHook == 0);
    }

    private nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN))
            KeyPressed?.Invoke(Marshal.ReadInt32(lParam));
        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            MouseButton? button = (int)wParam switch
            {
                NativeMethods.WM_LBUTTONDOWN => MouseButton.Left,
                NativeMethods.WM_RBUTTONDOWN => MouseButton.Right,
                NativeMethods.WM_MBUTTONDOWN => MouseButton.Middle,
                _ => null
            };
            if (button.HasValue)
                MouseButtonPressed?.Invoke(button.Value);
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
    }
}
