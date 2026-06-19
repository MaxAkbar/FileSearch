using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Indexing;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// Application-wide preferences that affect how the GUI behaves. Keep new
/// cross-cutting UI settings here rather than in individual feature view models.
/// </summary>
public sealed partial class ApplicationSettingsViewModel : ObservableObject
{
    public const int DefaultSidebarPageSize = 7;
    public const int MinimumSidebarPageSize = 3;
    public const int MaximumSidebarPageSize = 50;

    private readonly ISettingsService _settingsService;
    private readonly IStartupRegistrationService? _startupRegistration;
    private readonly IThemeService? _themeService;
    private readonly IGlobalHotkeyService? _hotkeyService;
    private readonly StatusBarViewModel _status;
    private readonly bool _isInitialized;
    private int _sidebarPageSize;
    private IndexerResourceProfile _indexerResourceProfile;
    private QuickSearchHotkeyOption _quickSearchHotkey;
    private QuickSearchScopeOption _quickSearchDefaultScope;
    private bool _quickSearchRememberLastScope;
    private bool _quickSearchIncludeContent;
    private bool _keepIndexUpdatedAfterClose;
    private bool _startBackgroundIndexerAtSignIn;
    private bool _pauseIndexingOnBattery;
    private bool _indexOnlyWhenIdle;
    private int _indexerCpuLimitPercent;
    private int _indexerDiskPauseMilliseconds;
    private CustomThemeInfo? _selectedCustomTheme;

    public ApplicationSettingsViewModel(
        ISettingsService settingsService,
        StatusBarViewModel status,
        IStartupRegistrationService? startupRegistration = null,
        IThemeService? themeService = null,
        IGlobalHotkeyService? hotkeyService = null)
    {
        _settingsService = settingsService;
        _startupRegistration = startupRegistration;
        _themeService = themeService;
        _hotkeyService = hotkeyService;
        _status = status;
        _sidebarPageSize = NormalizeSidebarPageSize(_settingsService.Current.SidebarPageSize);
        _indexerResourceProfile = NormalizeIndexerResourceProfile(_settingsService.Current.IndexerResourceProfile);
        _quickSearchHotkey = QuickSearchHotkeyOptions.First(option =>
            option.Value == NormalizeQuickSearchHotkey(_settingsService.Current.QuickSearchHotkey));
        _quickSearchDefaultScope = QuickSearchScopeOptions.First(option =>
            option.Value == NormalizeQuickSearchScope(_settingsService.Current.QuickSearchDefaultScope));
        _quickSearchRememberLastScope = _settingsService.Current.QuickSearchRememberLastScope;
        _quickSearchIncludeContent = _settingsService.Current.QuickSearchIncludeContent;
        _keepIndexUpdatedAfterClose = _settingsService.Current.KeepIndexUpdatedAfterClose;
        _startBackgroundIndexerAtSignIn = _settingsService.Current.StartBackgroundIndexerAtSignIn;
        _pauseIndexingOnBattery = _settingsService.Current.PauseIndexingOnBattery;
        _indexOnlyWhenIdle = _settingsService.Current.IndexOnlyWhenIdle;
        _indexerCpuLimitPercent = NormalizeCpuLimit(_settingsService.Current.IndexerCpuLimitPercent);
        _indexerDiskPauseMilliseconds = NormalizeDiskPause(_settingsService.Current.IndexerDiskPauseMilliseconds);
        RefreshQuickIndexedLocationSelectionsCore();
        RefreshCustomThemesCore();
        _isInitialized = true;
    }

    public IReadOnlyList<int> SidebarPageSizeOptions { get; } =
        Enumerable.Range(MinimumSidebarPageSize, MaximumSidebarPageSize - MinimumSidebarPageSize + 1).ToList();

    public IReadOnlyList<IndexerResourceProfile> IndexerResourceProfileOptions { get; } =
        [IndexerResourceProfile.Low, IndexerResourceProfile.Balanced, IndexerResourceProfile.High];

    public IReadOnlyList<int> IndexerCpuLimitOptions { get; } = [0, 25, 50, 75];

    public IReadOnlyList<int> IndexerDiskPauseOptions { get; } = [0, 5, 25, 100, 250];

    public IReadOnlyList<QuickSearchHotkeyOption> QuickSearchHotkeyOptions { get; } =
    [
        new(FileSearch.Gui.Settings.QuickSearchHotkey.WinShiftF, "Win+Shift+F"),
        new(FileSearch.Gui.Settings.QuickSearchHotkey.AltSpace, "Alt+Space"),
        new(FileSearch.Gui.Settings.QuickSearchHotkey.CtrlSpace, "Ctrl+Space"),
    ];

    public IReadOnlyList<QuickSearchScopeOption> QuickSearchScopeOptions { get; } =
    [
        new(QuickSearchScopeKind.CurrentFolder, "Selected folder"),
        new(QuickSearchScopeKind.SelectedIndexedLocations, "Selected indexed locations"),
        new(QuickSearchScopeKind.AllIndexedLocations, "All indexed locations"),
        new(QuickSearchScopeKind.EntireMachineMetadata, "Entire machine metadata"),
    ];

    public ObservableCollection<CustomThemeInfo> CustomThemes { get; } = new();

    public ObservableCollection<QuickIndexedLocationSelection> QuickIndexedLocationSelections { get; } = new();

    public string CustomThemeFolderPath => _themeService?.CustomThemeFolderPath ?? string.Empty;

    public bool HasCustomThemes => CustomThemes.Count > 0;

    public CustomThemeInfo? SelectedCustomTheme
    {
        get => _selectedCustomTheme;
        set
        {
            if (!SetProperty(ref _selectedCustomTheme, value))
                return;

            OnPropertyChanged(nameof(CustomThemeSummary));

            if (!_isInitialized || _themeService is null)
                return;

            if (value is null)
                return;

            if (_themeService.TrySetCustomTheme(value.FileName, out var error))
                _status.Text = $"Applied custom theme: {value.Name}.";
            else
                _status.Text = $"Could not apply custom theme: {error}";
        }
    }

    public string CustomThemeSummary
    {
        get
        {
            if (SelectedCustomTheme is not null)
                return $"Using {SelectedCustomTheme.Name}.";

            if (!string.IsNullOrWhiteSpace(_settingsService.Current.CustomThemeFileName))
                return "Saved custom theme not found. Add it to the theme folder and refresh.";

            return "Using the built-in theme menu selection.";
        }
    }

    public int SidebarPageSize
    {
        get => _sidebarPageSize;
        set
        {
            var normalized = NormalizeSidebarPageSize(value);
            if (!SetProperty(ref _sidebarPageSize, normalized))
                return;

            OnPropertyChanged(nameof(SidebarPageSizeSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string SidebarPageSizeSummary => $"{SidebarPageSize:n0} rows per sidebar section";

    public QuickSearchHotkeyOption QuickSearchHotkey
    {
        get => _quickSearchHotkey;
        set
        {
            if (value is null || !SetProperty(ref _quickSearchHotkey, value))
                return;

            OnPropertyChanged(nameof(QuickSearchHotkeySummary));

            if (_isInitialized)
            {
                SaveSettings(updateStartupRegistration: false);
                RegisterQuickSearchHotkey();
            }
        }
    }

    public string QuickSearchHotkeySummary => $"Opens Quick Search with {QuickSearchHotkey.DisplayName}";

    public QuickSearchScopeOption QuickSearchDefaultScope
    {
        get => _quickSearchDefaultScope;
        set
        {
            if (value is null || !SetProperty(ref _quickSearchDefaultScope, value))
                return;

            OnPropertyChanged(nameof(QuickSearchScopeSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public bool QuickSearchRememberLastScope
    {
        get => _quickSearchRememberLastScope;
        set
        {
            if (!SetProperty(ref _quickSearchRememberLastScope, value))
                return;

            OnPropertyChanged(nameof(QuickSearchScopeSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string QuickSearchScopeSummary =>
        QuickSearchRememberLastScope
            ? "Quick Search reopens with the last scope you used"
            : $"Quick Search starts in {QuickSearchDefaultScope.DisplayName}";

    public bool QuickSearchIncludeContent
    {
        get => _quickSearchIncludeContent;
        set
        {
            if (!SetProperty(ref _quickSearchIncludeContent, value))
                return;

            OnPropertyChanged(nameof(QuickSearchContentSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string QuickSearchContentSummary =>
        QuickSearchIncludeContent
            ? "Quick Search includes indexed content matches after filename and path matches"
            : "Quick Search only searches file names and paths";

    public bool HasQuickIndexedLocations => QuickIndexedLocationSelections.Count > 0;

    public IndexerResourceProfile IndexerResourceProfile
    {
        get => _indexerResourceProfile;
        set
        {
            var normalized = NormalizeIndexerResourceProfile(value);
            if (!SetProperty(ref _indexerResourceProfile, normalized))
                return;

            OnPropertyChanged(nameof(IndexerResourceProfileSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string IndexerResourceProfileSummary =>
        IndexerResourceProfile switch
        {
            IndexerResourceProfile.Low => "Pauses often so indexing stays quiet in the background",
            IndexerResourceProfile.High => "Indexes as fast as possible while the app is idle",
            _ => "Balances indexing progress with foreground responsiveness",
        };

    public bool KeepIndexUpdatedAfterClose
    {
        get => _keepIndexUpdatedAfterClose;
        set
        {
            if (!SetProperty(ref _keepIndexUpdatedAfterClose, value))
                return;

            OnPropertyChanged(nameof(KeepIndexUpdatedAfterCloseSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string KeepIndexUpdatedAfterCloseSummary =>
        KeepIndexUpdatedAfterClose
            ? "Closing the search window keeps FileSearch running in the tray"
            : "Closing the search window stops background indexing";

    public bool StartBackgroundIndexerAtSignIn
    {
        get => _startBackgroundIndexerAtSignIn;
        set
        {
            if (!SetProperty(ref _startBackgroundIndexerAtSignIn, value))
                return;

            OnPropertyChanged(nameof(StartBackgroundIndexerAtSignInSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: true);
        }
    }

    public string StartBackgroundIndexerAtSignInSummary =>
        StartBackgroundIndexerAtSignIn
            ? "The background indexer starts when you sign in to Windows"
            : "The background indexer starts only when FileSearch asks for it";

    public bool PauseIndexingOnBattery
    {
        get => _pauseIndexingOnBattery;
        set
        {
            if (!SetProperty(ref _pauseIndexingOnBattery, value))
                return;

            OnPropertyChanged(nameof(PauseIndexingOnBatterySummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string PauseIndexingOnBatterySummary =>
        PauseIndexingOnBattery
            ? "Indexing pauses while Windows reports battery power"
            : "Indexing can continue on battery power";

    public bool IndexOnlyWhenIdle
    {
        get => _indexOnlyWhenIdle;
        set
        {
            if (!SetProperty(ref _indexOnlyWhenIdle, value))
                return;

            OnPropertyChanged(nameof(IndexOnlyWhenIdleSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string IndexOnlyWhenIdleSummary =>
        IndexOnlyWhenIdle
            ? "Indexing waits until there has been no keyboard or mouse input for 5 minutes"
            : "Indexing can run while you are using the computer";

    public int IndexerCpuLimitPercent
    {
        get => _indexerCpuLimitPercent;
        set
        {
            var normalized = NormalizeCpuLimit(value);
            if (!SetProperty(ref _indexerCpuLimitPercent, normalized))
                return;

            OnPropertyChanged(nameof(IndexerCpuLimitSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string IndexerCpuLimitSummary =>
        IndexerCpuLimitPercent <= 0
            ? "No additional CPU throttle beyond the resource profile"
            : $"Adds CPU throttling around {IndexerCpuLimitPercent}% target activity";

    public int IndexerDiskPauseMilliseconds
    {
        get => _indexerDiskPauseMilliseconds;
        set
        {
            var normalized = NormalizeDiskPause(value);
            if (!SetProperty(ref _indexerDiskPauseMilliseconds, normalized))
                return;

            OnPropertyChanged(nameof(IndexerDiskPauseSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string IndexerDiskPauseSummary =>
        IndexerDiskPauseMilliseconds <= 0
            ? "No additional disk I/O delay"
            : $"Pauses {IndexerDiskPauseMilliseconds:n0} ms between indexed files";

    [RelayCommand]
    private void ResetNavigationDefaults()
    {
        SidebarPageSize = DefaultSidebarPageSize;
        IndexerResourceProfile = IndexerResourceProfile.Balanced;
    }

    [RelayCommand]
    private void RefreshQuickIndexedLocations()
    {
        RefreshQuickIndexedLocationSelectionsCore();
        _status.Text = HasQuickIndexedLocations
            ? $"Loaded {QuickIndexedLocationSelections.Count:n0} indexed locations for Quick Search."
            : "No indexed locations configured.";
    }

    [RelayCommand]
    private void RefreshCustomThemes()
    {
        RefreshCustomThemesCore();
        _status.Text = HasCustomThemes
            ? $"Loaded {CustomThemes.Count:n0} custom theme files."
            : "No custom theme files found.";
    }

    [RelayCommand]
    private void UseBuiltInTheme()
    {
        SelectedCustomTheme = null;
        _themeService?.SetTheme(_settingsService.Current.Theme);
        OnPropertyChanged(nameof(CustomThemeSummary));
        _status.Text = "Using built-in theme selection.";
    }

    public void SaveSettings() => SaveSettings(updateStartupRegistration: true);

    private void SaveSettings(bool updateStartupRegistration)
    {
        _settingsService.Update(settings =>
        {
            settings.SidebarPageSize = SidebarPageSize;
            settings.IndexerResourceProfile = IndexerResourceProfile;
            settings.QuickSearchHotkey = QuickSearchHotkey.Value;
            settings.QuickSearchDefaultScope = QuickSearchDefaultScope.Value;
            settings.QuickSearchRememberLastScope = QuickSearchRememberLastScope;
            settings.QuickSearchIncludeContent = QuickSearchIncludeContent;
            settings.QuickSearchSelectedIndexedRoots = QuickIndexedLocationSelections
                .Where(location => location.IsSelected)
                .Select(location => location.Root)
                .ToList();
            settings.KeepIndexUpdatedAfterClose = KeepIndexUpdatedAfterClose;
            settings.StartBackgroundIndexerAtSignIn = StartBackgroundIndexerAtSignIn;
            settings.PauseIndexingOnBattery = PauseIndexingOnBattery;
            settings.IndexOnlyWhenIdle = IndexOnlyWhenIdle;
            settings.IndexerCpuLimitPercent = IndexerCpuLimitPercent;
            settings.IndexerDiskPauseMilliseconds = IndexerDiskPauseMilliseconds;
            settings.RunInBackground = null;
        });

        if (!updateStartupRegistration)
        {
            _status.Text = "Settings saved.";
            return;
        }

        try
        {
            if (StartBackgroundIndexerAtSignIn)
                _startupRegistration?.EnableBackgroundStartup();
            else
                _startupRegistration?.DisableBackgroundStartup();
        }
        catch (Exception ex)
        {
            _status.Text = $"Settings saved, but startup registration failed: {ex.Message}";
            return;
        }

        _status.Text = "Settings saved.";
    }

    private static int NormalizeSidebarPageSize(int value) =>
        value < MinimumSidebarPageSize || value > MaximumSidebarPageSize
            ? DefaultSidebarPageSize
            : value;

    private static IndexerResourceProfile NormalizeIndexerResourceProfile(IndexerResourceProfile profile) =>
        Enum.IsDefined(profile) ? profile : IndexerResourceProfile.Balanced;

    private static QuickSearchHotkey NormalizeQuickSearchHotkey(QuickSearchHotkey hotkey) =>
        Enum.IsDefined(hotkey) ? hotkey : FileSearch.Gui.Settings.QuickSearchHotkey.WinShiftF;

    private static QuickSearchScopeKind NormalizeQuickSearchScope(QuickSearchScopeKind scope) =>
        Enum.IsDefined(scope) ? scope : QuickSearchScopeKind.AllIndexedLocations;

    private static int NormalizeCpuLimit(int value) =>
        value is <= 0 or >= 100 ? 0 : Math.Clamp(value, 1, 99);

    private static int NormalizeDiskPause(int value) =>
        Math.Clamp(value, 0, 1_000);

    private void RefreshCustomThemesCore()
    {
        var selectedFileName = SelectedCustomTheme?.FileName;
        if (string.IsNullOrWhiteSpace(selectedFileName))
            selectedFileName = _settingsService.Current.CustomThemeFileName;

        CustomThemes.Clear();
        foreach (var theme in _themeService?.GetCustomThemes() ?? Array.Empty<CustomThemeInfo>())
            CustomThemes.Add(theme);

        _selectedCustomTheme = CustomThemes.FirstOrDefault(theme =>
            string.Equals(theme.FileName, selectedFileName, StringComparison.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(SelectedCustomTheme));
        OnPropertyChanged(nameof(CustomThemeSummary));
        OnPropertyChanged(nameof(HasCustomThemes));
        OnPropertyChanged(nameof(CustomThemeFolderPath));
    }

    private void RefreshQuickIndexedLocationSelectionsCore()
    {
        var selected = _settingsService.Current.QuickSearchSelectedIndexedRoots
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        QuickIndexedLocationSelections.Clear();
        foreach (var location in _settingsService.Current.IndexedLocations)
        {
            if (string.IsNullOrWhiteSpace(location.Root))
                continue;

            var row = new QuickIndexedLocationSelection(
                location.DisplayName,
                location.Root,
                selected.Count == 0 || selected.Contains(location.Root),
                SaveQuickIndexedLocationSelections);
            QuickIndexedLocationSelections.Add(row);
        }

        OnPropertyChanged(nameof(HasQuickIndexedLocations));
    }

    private void SaveQuickIndexedLocationSelections()
    {
        if (_isInitialized)
            SaveSettings(updateStartupRegistration: false);
    }

    private void RegisterQuickSearchHotkey()
    {
        if (_hotkeyService is null)
            return;

        if (_hotkeyService.Register(QuickSearchHotkey.Value))
            _status.Text = $"Quick Search hotkey set to {QuickSearchHotkey.DisplayName}.";
        else
            _status.Text = $"Quick Search hotkey could not be registered: {_hotkeyService.LastError}";
    }
}

public sealed record QuickSearchHotkeyOption(QuickSearchHotkey Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class QuickIndexedLocationSelection : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public QuickIndexedLocationSelection(string displayName, string root, bool isSelected, Action changed)
    {
        DisplayName = displayName;
        Root = root;
        _isSelected = isSelected;
        _changed = changed;
    }

    public string DisplayName { get; }

    public string Root { get; }

    public string Summary => Root;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                _changed();
        }
    }
}
