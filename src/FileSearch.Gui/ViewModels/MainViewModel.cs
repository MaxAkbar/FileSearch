using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;
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
        IShellIntegrationService shellIntegrationService,
        ReplacementViewModel? replacement = null)
    {
        Search = search;
        Index = index;
        History = history;
        Settings = settings;
        Status = status;
        Workflows = workflows;
        Replacement = replacement;
        _themeService = themeService;
        _styleService = styleService;
        _shellIntegrationService = shellIntegrationService;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        if (search is not null) search.PropertyChanged += OnActiveQueryChanged;
        if (Replacement is not null) Replacement.PropertyChanged += OnActiveQueryChanged;
    }

    public SearchViewModel Search { get; }

    public IndexViewModel Index { get; }

    public HistoryViewModel History { get; }

    public ApplicationSettingsViewModel Settings { get; }

    public StatusBarViewModel Status { get; }

    public WorkflowsViewModel Workflows { get; }

    public ReplacementViewModel? Replacement { get; }
    public bool IsNormalSearch => Replacement?.IsOpen != true;
    public bool CanEditQuery => Search?.IsReplacementBusy != true;
    public string ActiveQueryText { get => IsNormalSearch ? Search.QueryText : Replacement!.FindText; set { if (IsNormalSearch) Search.QueryText = value; else Replacement!.FindText = value; } }
    public bool ActiveMatchCase { get => IsNormalSearch ? Search.MatchCase : Replacement!.MatchCase; set { if (IsNormalSearch) Search.MatchCase = value; else Replacement!.MatchCase = value; } }
    public bool ActiveRegex { get => IsNormalSearch ? Search.IsRegexMode : Replacement!.UseRegex; set { if (IsNormalSearch) Search.IsRegexMode = value; else Replacement!.UseRegex = value; } }
    public string ActiveQueryPlaceholder => IsNormalSearch ? Search.QueryPlaceholderText : "Find literal text or a regular expression";
    public string ActiveSearchLabel => IsNormalSearch ? "Search" : "Preview changes";
    public ICommand ActiveStartCommand => IsNormalSearch ? Search.SearchCommand : Replacement!.PreviewCommand;
    public ICommand ActiveClearCommand => IsNormalSearch ? Search.ClearQueryCommand : Replacement!.ClearFindCommand;
    public ICommand ActiveCancelCommand => IsNormalSearch ? Search.CancelCommand : Replacement!.IsBusy ? Replacement.CancelCommand : Replacement.ToggleCommand;

    private void OnActiveQueryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ReplacementViewModel.IsOpen) or nameof(ReplacementViewModel.IsBusy) or nameof(ReplacementViewModel.FindText) or
            nameof(ReplacementViewModel.MatchCase) or nameof(ReplacementViewModel.UseRegex) or nameof(SearchViewModel.QueryText) or nameof(SearchViewModel.IsRegexMode) or nameof(SearchViewModel.QueryPlaceholderText) or nameof(SearchViewModel.IsReplacementBusy))
        {
            OnPropertyChanged(nameof(IsNormalSearch)); OnPropertyChanged(nameof(CanEditQuery)); OnPropertyChanged(nameof(ActiveQueryText));
            OnPropertyChanged(nameof(ActiveMatchCase)); OnPropertyChanged(nameof(ActiveRegex)); OnPropertyChanged(nameof(ActiveQueryPlaceholder));
            OnPropertyChanged(nameof(ActiveSearchLabel)); OnPropertyChanged(nameof(ActiveStartCommand)); OnPropertyChanged(nameof(ActiveClearCommand)); OnPropertyChanged(nameof(ActiveCancelCommand));
        }
    }

    public bool IsLightThemeSelected => _themeService.CurrentTheme == AppTheme.Light;

    public bool IsDarkThemeSelected => _themeService.CurrentTheme == AppTheme.Dark;

    public bool IsVisualStudioThemeSelected => _themeService.CurrentTheme == AppTheme.VisualStudio;

    public bool IsSystemThemeSelected => _themeService.CurrentTheme == AppTheme.System;

    public bool IsComfortableStyleSelected => Settings.SelectedStyle.Value == AppStyle.Comfortable;

    public bool IsCompactStyleSelected => Settings.SelectedStyle.Value == AppStyle.Compact;

    public bool IsVelaStyleSelected => Settings.SelectedStyle.Value == AppStyle.Vela;

    [RelayCommand]
    private void ApplyTheme(string themeName)
    {
        if (Enum.TryParse<AppTheme>(themeName, out var theme))
        {
            _themeService.SetTheme(theme);
            NotifyThemeSelectionChanged();
        }
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
            NotifyStyleSelectionChanged();
        }
    }

    /// <summary>Title-bar button: sidebar ⇄ icon rail.</summary>
    [RelayCommand]
    private void ToggleSidebar() => Settings.IsSidebarCollapsed = !Settings.IsSidebarCollapsed;

    [RelayCommand]
    private void ExpandSidebar() => Settings.IsSidebarCollapsed = false;

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

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationSettingsViewModel.SelectedStyle))
            NotifyStyleSelectionChanged();
    }

    private void NotifyThemeSelectionChanged()
    {
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
        OnPropertyChanged(nameof(IsVisualStudioThemeSelected));
        OnPropertyChanged(nameof(IsSystemThemeSelected));
    }

    private void NotifyStyleSelectionChanged()
    {
        OnPropertyChanged(nameof(IsComfortableStyleSelected));
        OnPropertyChanged(nameof(IsCompactStyleSelected));
        OnPropertyChanged(nameof(IsVelaStyleSelected));
    }

    public void Dispose()
    {
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        if (Search is not null) Search.PropertyChanged -= OnActiveQueryChanged;
        if (Replacement is not null) { Replacement.PropertyChanged -= OnActiveQueryChanged; Replacement.Dispose(); }
        Search?.Dispose();
        Index.Dispose();
        Workflows.Dispose();
    }
}
