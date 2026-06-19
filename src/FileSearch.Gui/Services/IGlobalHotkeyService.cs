using System;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.Services;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;

    string? LastError { get; }

    bool Register(QuickSearchHotkey hotkey);

    void Unregister();
}

