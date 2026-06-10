using System;

namespace FileSearch.Gui.Settings;

/// <summary>
/// Owns the single in-memory <see cref="AppSettings"/> instance for the app.
/// All mutations go through <see cref="Update"/> so concurrent writers
/// (view model, theme service, shutdown snapshot) can't clobber each other
/// with load-modify-save races against the file.
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    void Update(Action<AppSettings> mutate);
}
