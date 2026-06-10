using System;

namespace FileSearch.Gui.Settings;

public sealed class SettingsService : ISettingsService
{
    private readonly ISettingsStore _store;
    private readonly object _sync = new();

    public SettingsService(ISettingsStore store)
    {
        _store = store;
        Current = store.Load();
    }

    public AppSettings Current { get; }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_sync)
        {
            mutate(Current);
            _store.Save(Current);
        }
    }
}
