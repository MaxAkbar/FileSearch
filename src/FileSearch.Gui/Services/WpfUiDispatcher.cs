using System;

namespace FileSearch.Gui.Services;

public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Post(Action action) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
}
