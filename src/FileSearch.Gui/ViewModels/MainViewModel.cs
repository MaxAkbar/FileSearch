using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// Thin composition shell: exposes the feature view models for binding
/// (Search, Index, History, Status) and keeps only app-level commands
/// (theme, Windows shell integration) and lifecycle forwarding.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IThemeService _themeService;
    private readonly IStyleService _styleService;
    private readonly IShellIntegrationService _shellIntegrationService;

    public MainViewModel(
        SearchViewModel search,
        IndexViewModel index,
        HistoryViewModel history,
        ApplicationSettingsViewModel settings,
        StatusBarViewModel status,
        WorkflowsViewModel workflows,
        IThemeService themeService,
        IStyleService styleService,
        IShellIntegrationService shellIntegrationService)
    {
        Search = search;
        Index = index;
        History = history;
        Settings = settings;
        Status = status;
        Workflows = workflows;
        _themeService = themeService;
        _styleService = styleService;
        _shellIntegrationService = shellIntegrationService;
    }

    public SearchViewModel Search { get; }

    public IndexViewModel Index { get; }

    public HistoryViewModel History { get; }

    public ApplicationSettingsViewModel Settings { get; }

    public StatusBarViewModel Status { get; }

    public WorkflowsViewModel Workflows { get; }

    [RelayCommand]
    private void ApplyTheme(string themeName)
    {
        if (Enum.TryParse<AppTheme>(themeName, out var theme))
            _themeService.SetTheme(theme);
    }

    [RelayCommand]
    private void ApplyStyle(string styleName)
    {
        if (Enum.TryParse<AppStyle>(styleName, out var style))
        {
            foreach (var option in Settings.StyleOptions)
            {
                if (option.Value != style)
                    continue;

                Settings.SelectedStyle = option;
                return;
            }

            _styleService.SetStyle(style);
        }
    }

    [RelayCommand]
    private void InstallWindowsIntegration()
    {
        try
        {
            _shellIntegrationService.Install();
            Status.Text = "Windows integration installed. Pin FileSearch from the Start menu if you want it on the taskbar.";
        }
        catch (Exception ex)
        {
            Status.Text = $"Failed to install Windows integration: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveWindowsIntegration()
    {
        try
        {
            _shellIntegrationService.Remove();
            Status.Text = "Windows integration removed.";
        }
        catch (Exception ex)
        {
            Status.Text = $"Failed to remove Windows integration: {ex.Message}";
        }
    }

    /// <summary>Snapshots all view-model state into the shared settings.
    /// Children also save eagerly when their state changes; this is the
    /// exit-time safety net.</summary>
    public void PersistSettings()
    {
        Settings.SaveSettings();
        Search.SaveOptions();
        History.SaveHistory();
        Index.SaveLocations();
    }

    public FileTypeOptions BuildFileTypeOptions() => Search.BuildFileTypeOptions();

    public Task StartBackgroundIndexingAsync() => Index.StartBackgroundIndexingAsync();

    public Task StopBackgroundIndexingAsync() => Index.StopBackgroundIndexingAsync();

    public void Dispose()
    {
        Search.Dispose();
        Index.Dispose();
        Workflows.Dispose();
    }
}
