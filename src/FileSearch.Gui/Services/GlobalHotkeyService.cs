using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.Services;

public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0x5153;
    private const int WmHotkey = 0x0312;
    private const int WsPopup = unchecked((int)0x80000000);

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkSpace = 0x20;
    private const uint VkF = 0x46;

    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? HotkeyPressed;

    public string? LastError { get; private set; }

    public bool Register(QuickSearchHotkey hotkey)
    {
        Unregister();
        EnsureMessageWindow();

        var (modifiers, key) = ToNativeHotkey(hotkey);
        modifiers |= ModNoRepeat;

        if (!RegisterHotKey(_source!.Handle, HotkeyId, modifiers, key))
        {
            LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        LastError = null;
        _registered = true;
        return true;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
            _ = UnregisterHotKey(_source.Handle, HotkeyId);

        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        if (_source is null)
            return;

        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }

    private void EnsureMessageWindow()
    {
        if (_source is not null)
            return;

        var parameters = new HwndSourceParameters("FileSearchQuickSearchHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WsPopup,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private static (uint Modifiers, uint Key) ToNativeHotkey(QuickSearchHotkey hotkey) =>
        hotkey switch
        {
            QuickSearchHotkey.AltSpace => (ModAlt, VkSpace),
            QuickSearchHotkey.CtrlSpace => (ModControl, VkSpace),
            QuickSearchHotkey.WinShiftF => (ModWin | ModShift, VkF),
            _ => (ModWin | ModShift, VkF),
        };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

