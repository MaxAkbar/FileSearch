using System;

namespace FileSearch.Gui.Services;

/// <summary>
/// Marshals work onto the UI thread without coupling view models to WPF's
/// Application/Dispatcher statics.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
