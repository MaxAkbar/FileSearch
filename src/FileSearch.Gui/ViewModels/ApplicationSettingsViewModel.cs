using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Engine;
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
    public const int DefaultOcrMaxPdfPages = 50;
    public const int MaximumOcrMaxPdfPages = 1_000;

    private readonly ISettingsService _settingsService;
    private readonly IStartupRegistrationService? _startupRegistration;
    private readonly IThemeService? _themeService;
    private readonly IStyleService? _styleService;
    private readonly IGlobalHotkeyService? _hotkeyService;
    private readonly IEmbeddingModelPackCatalog? _semanticModelCatalog;
    private readonly IEmbeddingModelPackStore? _semanticModelStore;
    private readonly IEmbeddingModelPackInstaller? _semanticModelInstaller;
    private readonly EmbeddingModelPackOptions? _semanticModelOptions;
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
    private string _ocrLanguageTag = string.Empty;
    private int _ocrMaxPdfPages;
    private SemanticModelPackOption _semanticModelPack;
    private string _semanticModelPacksDirectory = string.Empty;
    private bool _enableLocalReranker;
    private string _semanticModelInstallStatus = string.Empty;
    private CustomThemeInfo? _selectedCustomTheme;
    private AppStyleOption _selectedStyle;
    private bool _isUpdatingShortcuts;

    public ApplicationSettingsViewModel(
        ISettingsService settingsService,
        StatusBarViewModel status,
        IStartupRegistrationService? startupRegistration = null,
        IThemeService? themeService = null,
        IStyleService? styleService = null,
        IGlobalHotkeyService? hotkeyService = null,
        IEmbeddingModelPackCatalog? semanticModelCatalog = null,
        IEmbeddingModelPackStore? semanticModelStore = null,
        IEmbeddingModelPackInstaller? semanticModelInstaller = null,
        EmbeddingModelPackOptions? semanticModelOptions = null)
    {
        _settingsService = settingsService;
        _startupRegistration = startupRegistration;
        _themeService = themeService;
        _styleService = styleService;
        _hotkeyService = hotkeyService;
        _semanticModelCatalog = semanticModelCatalog;
        _semanticModelStore = semanticModelStore;
        _semanticModelInstaller = semanticModelInstaller;
        _semanticModelOptions = semanticModelOptions;
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
        _ocrLanguageTag = NormalizeOcrLanguageTag(_settingsService.Current.OcrLanguageTag);
        _ocrMaxPdfPages = NormalizeOcrMaxPdfPages(_settingsService.Current.OcrMaxPdfPages);
        SemanticModelPackOptions = BuildSemanticModelPackOptions(_semanticModelCatalog);
        _semanticModelPack = FindSemanticModelOption(_settingsService.Current.SemanticModelPackId);
        _semanticModelPacksDirectory = NormalizeSemanticModelDirectory(_settingsService.Current.SemanticModelPacksDirectory);
        _enableLocalReranker = _settingsService.Current.EnableLocalReranker;
        _selectedStyle = FindStyleOption(NormalizeStyle(_settingsService.Current.Style));
        ApplyShortcutSettings(NormalizeShortcutSettings(_settingsService.Current.Shortcuts));
        ApplyQuickSearchShortcutSettings(NormalizeQuickSearchShortcutSettings(_settingsService.Current.QuickSearchShortcuts));
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

    public IReadOnlyList<int> OcrMaxPdfPageOptions { get; } = [0, 10, 25, 50, 100, 250];

    public IReadOnlyList<SemanticModelPackOption> SemanticModelPackOptions { get; }

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

    public IReadOnlyList<AppStyleOption> StyleOptions { get; } =
    [
        new(AppStyle.Comfortable, "Comfortable", "Uses roomier controls and spacing."),
        new(AppStyle.Compact, "Compact", "Fits more controls and results on screen."),
        new(AppStyle.Vela, "Vela", "Uses the compact concept palette and denser shell."),
    ];

    public ObservableCollection<AppShortcutBindingViewModel> ShortcutBindings { get; } = new();

    public ObservableCollection<QuickSearchShortcutBindingViewModel> QuickSearchShortcutBindings { get; } = new();

    public IReadOnlyList<AppShortcutOption> ShortcutOptions { get; } =
    [
        new(AppShortcutGesture.Disabled, "Disabled"),
        new(AppShortcutGesture.CtrlF, "Ctrl+F"),
        new(AppShortcutGesture.CtrlL, "Ctrl+L"),
        new(AppShortcutGesture.CtrlEnter, "Ctrl+Enter"),
        new(AppShortcutGesture.Escape, "Esc"),
        new(AppShortcutGesture.CtrlR, "Ctrl+R"),
        new(AppShortcutGesture.F8, "F8"),
        new(AppShortcutGesture.Enter, "Enter"),
        new(AppShortcutGesture.CtrlO, "Ctrl+O"),
        new(AppShortcutGesture.CtrlE, "Ctrl+E"),
        new(AppShortcutGesture.CtrlShiftC, "Ctrl+Shift+C"),
        new(AppShortcutGesture.CtrlP, "Ctrl+P"),
        new(AppShortcutGesture.CtrlShiftS, "Ctrl+Shift+S"),
        new(AppShortcutGesture.F2, "F2"),
        new(AppShortcutGesture.Delete, "Delete"),
        new(AppShortcutGesture.CtrlShiftW, "Ctrl+Shift+W"),
        new(AppShortcutGesture.CtrlShiftBackspace, "Ctrl+Shift+Backspace"),
    ];

    public IReadOnlyList<AppShortcutOption> QuickSearchShortcutOptions { get; } =
    [
        new(AppShortcutGesture.Disabled, "Disabled"),
        new(AppShortcutGesture.Escape, "Esc"),
        new(AppShortcutGesture.Down, "Down"),
        new(AppShortcutGesture.Enter, "Enter"),
        new(AppShortcutGesture.CtrlR, "Ctrl+R"),
        new(AppShortcutGesture.CtrlC, "Ctrl+C"),
        new(AppShortcutGesture.CtrlP, "Ctrl+P"),
        new(AppShortcutGesture.F4, "F4"),
        new(AppShortcutGesture.CtrlI, "Ctrl+I"),
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

    public AppStyleOption SelectedStyle
    {
        get => _selectedStyle;
        set
        {
            if (value is null || !SetProperty(ref _selectedStyle, value))
                return;

            OnPropertyChanged(nameof(StyleSummary));

            if (!_isInitialized)
                return;

            _styleService?.SetStyle(value.Value);
            SaveSettings(updateStartupRegistration: false);

            _status.Text = $"Using {value.DisplayName} style.";
        }
    }

    public string StyleSummary => SelectedStyle.Value switch
    {
        AppStyle.Compact => "Compact uses tighter controls, lists, and panels.",
        AppStyle.Vela => "Vela uses the compact concept palette, tighter cards, and denser panels.",
        _ => "Comfortable uses the default roomier spacing.",
    };

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

    public string OcrLanguageTag
    {
        get => _ocrLanguageTag;
        set
        {
            var normalized = NormalizeOcrLanguageTag(value);
            if (!SetProperty(ref _ocrLanguageTag, normalized))
                return;

            OnPropertyChanged(nameof(OcrLanguageSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string OcrLanguageSummary =>
        string.IsNullOrWhiteSpace(OcrLanguageTag)
            ? "Uses the Windows user-profile OCR language"
            : $"Uses OCR language {OcrLanguageTag}";

    public int OcrMaxPdfPages
    {
        get => _ocrMaxPdfPages;
        set
        {
            var normalized = NormalizeOcrMaxPdfPages(value);
            if (!SetProperty(ref _ocrMaxPdfPages, normalized))
                return;

            OnPropertyChanged(nameof(OcrMaxPdfPagesSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string OcrMaxPdfPagesSummary =>
        OcrMaxPdfPages <= 0
            ? "Scanned PDF OCR has no page limit"
            : $"Scanned PDF OCR checks up to {OcrMaxPdfPages:n0} pages without native text";

    public SemanticModelPackOption SemanticModelPack
    {
        get => _semanticModelPack;
        set
        {
            if (value is null || !SetProperty(ref _semanticModelPack, value))
                return;

            OnPropertyChanged(nameof(SemanticModelSummary));
            OnPropertyChanged(nameof(CanInstallSemanticModel));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string SemanticModelPacksDirectory
    {
        get => _semanticModelPacksDirectory;
        set
        {
            var normalized = NormalizeSemanticModelDirectory(value);
            if (!SetProperty(ref _semanticModelPacksDirectory, normalized))
                return;

            OnPropertyChanged(nameof(SemanticModelSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string SemanticModelSummary
    {
        get
        {
            if (SemanticModelPack.IsDisabled)
                return "Smart Search is disabled; exact, indexed content, and OCR search still work.";

            if (!string.IsNullOrWhiteSpace(_semanticModelInstallStatus))
                return _semanticModelInstallStatus;

            return $"{SemanticModelPack.Description} Install size: {SemanticModelPack.InstallSizeLabel}.";
        }
    }

    public bool CanInstallSemanticModel =>
        !SemanticModelPack.IsDisabled && _semanticModelInstaller is not null;

    public bool EnableLocalReranker
    {
        get => _enableLocalReranker;
        set
        {
            if (!SetProperty(ref _enableLocalReranker, value))
                return;

            OnPropertyChanged(nameof(LocalRerankerSummary));

            if (_isInitialized)
                SaveSettings(updateStartupRegistration: false);
        }
    }

    public string LocalRerankerSummary =>
        EnableLocalReranker
            ? "Search results get a local ranking pass after provider fusion"
            : "Search results keep the raw fused provider order";

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
    private void ResetShortcutDefaults()
    {
        ApplyShortcutSettings(AppShortcutSettings.CreateDefaults());
        SaveSettings(updateStartupRegistration: false);
        _status.Text = "Keyboard shortcuts reset.";
    }

    [RelayCommand]
    private void ResetQuickSearchShortcutDefaults()
    {
        ApplyQuickSearchShortcutSettings(QuickSearchShortcutSettings.CreateDefaults());
        SaveSettings(updateStartupRegistration: false);
        _status.Text = "Quick Search shortcuts reset.";
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
    private async Task InstallSemanticModelAsync()
    {
        if (SemanticModelPack.IsDisabled || _semanticModelInstaller is null)
        {
            _status.Text = "Choose a Smart Search model before installing.";
            return;
        }

        SaveSettings(updateStartupRegistration: false);
        var progress = new Progress<EmbeddingModelInstallProgress>(item =>
        {
            _semanticModelInstallStatus =
                $"Downloading {SemanticModelPack.DisplayName}: {item.CurrentFile} ({item.CompletedFiles + 1:n0}/{item.TotalFiles:n0}).";
            OnPropertyChanged(nameof(SemanticModelSummary));
        });

        try
        {
            _semanticModelInstallStatus = $"Downloading {SemanticModelPack.DisplayName}.";
            OnPropertyChanged(nameof(SemanticModelSummary));
            var installed = await _semanticModelInstaller.InstallAsync(
                    SemanticModelPack.Id,
                    progress,
                    CancellationToken.None)
                .ConfigureAwait(true);
            _semanticModelInstallStatus = installed.IsUsable
                ? $"Installed {installed.Manifest.DisplayName}. {installed.Status}"
                : $"Installed files but model is not ready: {installed.Status}";
            _status.Text = _semanticModelInstallStatus;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _semanticModelInstallStatus = $"Smart Search model install failed: {ex.Message}";
            _status.Text = _semanticModelInstallStatus;
        }
        finally
        {
            OnPropertyChanged(nameof(SemanticModelSummary));
        }
    }

    [RelayCommand]
    private async Task RefreshSemanticModelsAsync()
    {
        if (_semanticModelStore is null)
        {
            _semanticModelInstallStatus = "Smart Search model discovery is unavailable.";
            OnPropertyChanged(nameof(SemanticModelSummary));
            return;
        }

        var selected = await _semanticModelStore.GetSelectedPackAsync(CancellationToken.None).ConfigureAwait(true);
        _semanticModelInstallStatus = selected is null
            ? "Selected Smart Search model is not installed."
            : selected.IsUsable
                ? $"Installed {selected.Manifest.DisplayName}. {selected.Status}"
                : selected.Status;
        _status.Text = _semanticModelInstallStatus;
        OnPropertyChanged(nameof(SemanticModelSummary));
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
            settings.OcrLanguageTag = OcrLanguageTag;
            settings.OcrMaxPdfPages = OcrMaxPdfPages;
            settings.SemanticModelPackId = SemanticModelPack.IsDisabled ? string.Empty : SemanticModelPack.Id;
            settings.SemanticModelPacksDirectory = SemanticModelPacksDirectory;
            settings.EnableLocalReranker = EnableLocalReranker;
            settings.Style = SelectedStyle.Value;
            settings.Shortcuts = BuildShortcutSettings();
            settings.QuickSearchShortcuts = BuildQuickSearchShortcutSettings();
            settings.RunInBackground = null;
        });
        if (_semanticModelOptions is not null)
        {
            _semanticModelOptions.SelectedModelPackId = SemanticModelPack.IsDisabled ? string.Empty : SemanticModelPack.Id;
            _semanticModelOptions.ModelPacksDirectory = SemanticModelPacksDirectory;
        }

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

    private static AppStyle NormalizeStyle(AppStyle style) =>
        Enum.IsDefined(style) ? style : AppStyle.Comfortable;

    private AppStyleOption FindStyleOption(AppStyle style) =>
        StyleOptions.FirstOrDefault(option => option.Value == style)
        ?? StyleOptions[0];

    private static IndexerResourceProfile NormalizeIndexerResourceProfile(IndexerResourceProfile profile) =>
        Enum.IsDefined(profile) ? profile : IndexerResourceProfile.Balanced;

    private static QuickSearchHotkey NormalizeQuickSearchHotkey(QuickSearchHotkey hotkey) =>
        Enum.IsDefined(hotkey) ? hotkey : FileSearch.Gui.Settings.QuickSearchHotkey.WinShiftF;

    private static QuickSearchScopeKind NormalizeQuickSearchScope(QuickSearchScopeKind scope) =>
        Enum.IsDefined(scope) ? scope : QuickSearchScopeKind.AllIndexedLocations;

    private static string NormalizeOcrLanguageTag(string? languageTag) =>
        (languageTag ?? string.Empty).Trim();

    private static int NormalizeOcrMaxPdfPages(int value) =>
        Math.Clamp(value, 0, MaximumOcrMaxPdfPages);

    private static int NormalizeCpuLimit(int value) =>
        value is <= 0 or >= 100 ? 0 : Math.Clamp(value, 1, 99);

    private static int NormalizeDiskPause(int value) =>
        Math.Clamp(value, 0, 1_000);

    private static string NormalizeSemanticModelDirectory(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? EmbeddingModelPackOptions.GetDefaultModelPacksDirectory()
            : value.Trim();

    private static List<SemanticModelPackOption> BuildSemanticModelPackOptions(
        IEmbeddingModelPackCatalog? catalog)
    {
        var options = new List<SemanticModelPackOption>
        {
            SemanticModelPackOption.Disabled,
        };

        foreach (var entry in catalog?.Entries ?? Array.Empty<EmbeddingModelPackCatalogEntry>())
        {
            options.Add(new SemanticModelPackOption(
                entry.Id,
                entry.DisplayName,
                entry.Summary,
                entry.InstallSizeLabel,
                IsDisabled: false,
                entry.IsRecommended));
        }

        return options;
    }

    private SemanticModelPackOption FindSemanticModelOption(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return SemanticModelPackOptions[0];

        return SemanticModelPackOptions.FirstOrDefault(option =>
            string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)) ?? SemanticModelPackOptions[0];
    }

    private void ApplyShortcutSettings(AppShortcutSettings settings)
    {
        _isUpdatingShortcuts = true;

        try
        {
            if (ShortcutBindings.Count == 0)
            {
                AddShortcutBinding(AppShortcutAction.FocusQuery, "Focus search text", "Move focus to the main search text box.", settings.FocusQuery);
                AddShortcutBinding(AppShortcutAction.FocusFolder, "Focus folder", "Move focus to the folder field.", settings.FocusFolder);
                AddShortcutBinding(AppShortcutAction.StartSearch, "Start search", "Run the current full-window search.", settings.StartSearch);
                AddShortcutBinding(AppShortcutAction.CancelSearch, "Cancel search", "Stop the running search.", settings.CancelSearch);
                AddShortcutBinding(AppShortcutAction.FocusResults, "Focus results", "Move focus into the visible results list.", settings.FocusResults);
                AddShortcutBinding(AppShortcutAction.TogglePreviewPane, "Toggle preview", "Show or hide the preview pane.", settings.TogglePreviewPane);
                AddShortcutBinding(AppShortcutAction.OpenSelectedResult, "Open selected result", "Open the selected result.", settings.OpenSelectedResult);
                AddShortcutBinding(AppShortcutAction.RevealSelectedResult, "Reveal selected result", "Show the selected result in Explorer.", settings.RevealSelectedResult);
                AddShortcutBinding(AppShortcutAction.CopySelectedResultPath, "Copy selected path", "Copy the selected result path.", settings.CopySelectedResultPath);
                AddShortcutBinding(AppShortcutAction.PinSelectedResult, "Pin selected result", "Pin or unpin the selected result.", settings.PinSelectedResult);
                AddShortcutBinding(AppShortcutAction.FavoriteSelectedResult, "Favorite selected result", "Add or remove the selected result as a favorite.", settings.FavoriteSelectedResult);
                AddShortcutBinding(AppShortcutAction.RenameSelectedResult, "Rename selected result", "Safely rename the selected result.", settings.RenameSelectedResult);
                AddShortcutBinding(AppShortcutAction.DeleteSelectedResult, "Move selected result to Recycle Bin", "Confirm and move the selected result to the Recycle Bin.", settings.DeleteSelectedResult);
                AddShortcutBinding(AppShortcutAction.SaveWorkspace, "Save workspace", "Save the current search, result view, favorites, and pins as a workspace.", settings.SaveWorkspace);
                AddShortcutBinding(AppShortcutAction.ClearResultFacets, "Clear result facets", "Reset result type, folder, date, source, and size facets.", settings.ClearResultFacets);
                return;
            }

            SetShortcut(AppShortcutAction.FocusQuery, settings.FocusQuery);
            SetShortcut(AppShortcutAction.FocusFolder, settings.FocusFolder);
            SetShortcut(AppShortcutAction.StartSearch, settings.StartSearch);
            SetShortcut(AppShortcutAction.CancelSearch, settings.CancelSearch);
            SetShortcut(AppShortcutAction.FocusResults, settings.FocusResults);
            SetShortcut(AppShortcutAction.TogglePreviewPane, settings.TogglePreviewPane);
            SetShortcut(AppShortcutAction.OpenSelectedResult, settings.OpenSelectedResult);
            SetShortcut(AppShortcutAction.RevealSelectedResult, settings.RevealSelectedResult);
            SetShortcut(AppShortcutAction.CopySelectedResultPath, settings.CopySelectedResultPath);
            SetShortcut(AppShortcutAction.PinSelectedResult, settings.PinSelectedResult);
            SetShortcut(AppShortcutAction.FavoriteSelectedResult, settings.FavoriteSelectedResult);
            SetShortcut(AppShortcutAction.RenameSelectedResult, settings.RenameSelectedResult);
            SetShortcut(AppShortcutAction.DeleteSelectedResult, settings.DeleteSelectedResult);
            SetShortcut(AppShortcutAction.SaveWorkspace, settings.SaveWorkspace);
            SetShortcut(AppShortcutAction.ClearResultFacets, settings.ClearResultFacets);
        }
        finally
        {
            _isUpdatingShortcuts = false;
        }
    }

    private void ApplyQuickSearchShortcutSettings(QuickSearchShortcutSettings settings)
    {
        _isUpdatingShortcuts = true;

        try
        {
            if (QuickSearchShortcutBindings.Count == 0)
            {
                AddQuickSearchShortcutBinding(QuickSearchShortcutAction.Close, "Close Quick Search", "Hide the quick window.", settings.Close);
                AddQuickSearchShortcutBinding(QuickSearchShortcutAction.FocusResults, "Focus results", "Move from the search box into the results list.", settings.FocusResults);
                AddQuickSearchShortcutBinding(QuickSearchShortcutAction.OpenSelectedResult, "Open selected result", "Open the selected result and close Quick Search.", settings.OpenSelectedResult);
                AddQuickSearchShortcutBinding(QuickSearchShortcutAction.RevealSelectedResult, "Reveal selected result", "Show the selected result in Explorer and close Quick Search.", settings.RevealSelectedResult);
                AddQuickSearchShortcutBinding(QuickSearchShortcutAction.CopySelectedResultPath, "Copy selected path", "Copy the selected result path and close Quick Search.", settings.CopySelectedResultPath);
                AddQuickSearchShortcutBinding(QuickSearchShortcutAction.PinSelectedResult, "Pin selected result", "Pin or unpin the selected result.", settings.PinSelectedResult);
                AddQuickSearchShortcutBinding(QuickSearchShortcutAction.PreviewSelectedResult, "Preview selected result", "Load or refresh the inline preview.", settings.PreviewSelectedResult);
                return;
            }

            SetQuickSearchShortcut(QuickSearchShortcutAction.Close, settings.Close);
            SetQuickSearchShortcut(QuickSearchShortcutAction.FocusResults, settings.FocusResults);
            SetQuickSearchShortcut(QuickSearchShortcutAction.OpenSelectedResult, settings.OpenSelectedResult);
            SetQuickSearchShortcut(QuickSearchShortcutAction.RevealSelectedResult, settings.RevealSelectedResult);
            SetQuickSearchShortcut(QuickSearchShortcutAction.CopySelectedResultPath, settings.CopySelectedResultPath);
            SetQuickSearchShortcut(QuickSearchShortcutAction.PinSelectedResult, settings.PinSelectedResult);
            SetQuickSearchShortcut(QuickSearchShortcutAction.PreviewSelectedResult, settings.PreviewSelectedResult);
        }
        finally
        {
            _isUpdatingShortcuts = false;
        }
    }

    private void AddShortcutBinding(AppShortcutAction action, string displayName, string description, AppShortcutGesture gesture)
    {
        ShortcutBindings.Add(new AppShortcutBindingViewModel(
            action,
            displayName,
            description,
            NormalizeShortcutGesture(gesture),
            ShortcutOptions,
            OnShortcutChanged));
    }

    private void AddQuickSearchShortcutBinding(
        QuickSearchShortcutAction action,
        string displayName,
        string description,
        AppShortcutGesture gesture)
    {
        QuickSearchShortcutBindings.Add(new QuickSearchShortcutBindingViewModel(
            action,
            displayName,
            description,
            NormalizeShortcutGesture(gesture),
            QuickSearchShortcutOptions,
            OnShortcutChanged));
    }

    private void SetShortcut(AppShortcutAction action, AppShortcutGesture gesture)
    {
        var binding = ShortcutBindings.FirstOrDefault(item => item.Action == action);
        if (binding is null)
            return;

        binding.SetGesture(NormalizeShortcutGesture(gesture));
    }

    private void SetQuickSearchShortcut(QuickSearchShortcutAction action, AppShortcutGesture gesture)
    {
        var binding = QuickSearchShortcutBindings.FirstOrDefault(item => item.Action == action);
        if (binding is null)
            return;

        binding.SetGesture(NormalizeShortcutGesture(gesture));
    }

    private void OnShortcutChanged()
    {
        if (_isUpdatingShortcuts)
            return;

        if (_isInitialized)
            SaveSettings(updateStartupRegistration: false);
    }

    public AppShortcutGesture GetShortcut(AppShortcutAction action) =>
        ShortcutBindings.FirstOrDefault(binding => binding.Action == action)?.Gesture ?? AppShortcutGesture.Disabled;

    public AppShortcutGesture GetQuickSearchShortcut(QuickSearchShortcutAction action) =>
        QuickSearchShortcutBindings.FirstOrDefault(binding => binding.Action == action)?.Gesture ?? AppShortcutGesture.Disabled;

    private AppShortcutSettings BuildShortcutSettings() => new()
    {
        FocusQuery = GetShortcut(AppShortcutAction.FocusQuery),
        FocusFolder = GetShortcut(AppShortcutAction.FocusFolder),
        StartSearch = GetShortcut(AppShortcutAction.StartSearch),
        CancelSearch = GetShortcut(AppShortcutAction.CancelSearch),
        FocusResults = GetShortcut(AppShortcutAction.FocusResults),
        TogglePreviewPane = GetShortcut(AppShortcutAction.TogglePreviewPane),
        OpenSelectedResult = GetShortcut(AppShortcutAction.OpenSelectedResult),
        RevealSelectedResult = GetShortcut(AppShortcutAction.RevealSelectedResult),
        CopySelectedResultPath = GetShortcut(AppShortcutAction.CopySelectedResultPath),
        PinSelectedResult = GetShortcut(AppShortcutAction.PinSelectedResult),
        FavoriteSelectedResult = GetShortcut(AppShortcutAction.FavoriteSelectedResult),
        RenameSelectedResult = GetShortcut(AppShortcutAction.RenameSelectedResult),
        DeleteSelectedResult = GetShortcut(AppShortcutAction.DeleteSelectedResult),
        SaveWorkspace = GetShortcut(AppShortcutAction.SaveWorkspace),
        ClearResultFacets = GetShortcut(AppShortcutAction.ClearResultFacets),
    };

    private QuickSearchShortcutSettings BuildQuickSearchShortcutSettings() => new()
    {
        Close = GetQuickSearchShortcut(QuickSearchShortcutAction.Close),
        FocusResults = GetQuickSearchShortcut(QuickSearchShortcutAction.FocusResults),
        OpenSelectedResult = GetQuickSearchShortcut(QuickSearchShortcutAction.OpenSelectedResult),
        RevealSelectedResult = GetQuickSearchShortcut(QuickSearchShortcutAction.RevealSelectedResult),
        CopySelectedResultPath = GetQuickSearchShortcut(QuickSearchShortcutAction.CopySelectedResultPath),
        PinSelectedResult = GetQuickSearchShortcut(QuickSearchShortcutAction.PinSelectedResult),
        PreviewSelectedResult = GetQuickSearchShortcut(QuickSearchShortcutAction.PreviewSelectedResult),
    };

    private static AppShortcutSettings NormalizeShortcutSettings(AppShortcutSettings? settings)
    {
        settings ??= AppShortcutSettings.CreateDefaults();

        return new AppShortcutSettings
        {
            FocusQuery = NormalizeShortcutGesture(settings.FocusQuery),
            FocusFolder = NormalizeShortcutGesture(settings.FocusFolder),
            StartSearch = NormalizeShortcutGesture(settings.StartSearch),
            CancelSearch = NormalizeShortcutGesture(settings.CancelSearch),
            FocusResults = NormalizeShortcutGesture(settings.FocusResults),
            TogglePreviewPane = NormalizeShortcutGesture(settings.TogglePreviewPane),
            OpenSelectedResult = NormalizeShortcutGesture(settings.OpenSelectedResult),
            RevealSelectedResult = NormalizeShortcutGesture(settings.RevealSelectedResult),
            CopySelectedResultPath = NormalizeShortcutGesture(settings.CopySelectedResultPath),
            PinSelectedResult = NormalizeShortcutGesture(settings.PinSelectedResult),
            FavoriteSelectedResult = NormalizeShortcutGesture(settings.FavoriteSelectedResult),
            RenameSelectedResult = NormalizeShortcutGesture(settings.RenameSelectedResult),
            DeleteSelectedResult = NormalizeShortcutGesture(settings.DeleteSelectedResult),
            SaveWorkspace = NormalizeShortcutGesture(settings.SaveWorkspace),
            ClearResultFacets = NormalizeShortcutGesture(settings.ClearResultFacets),
        };
    }

    private static QuickSearchShortcutSettings NormalizeQuickSearchShortcutSettings(QuickSearchShortcutSettings? settings)
    {
        settings ??= QuickSearchShortcutSettings.CreateDefaults();

        return new QuickSearchShortcutSettings
        {
            Close = NormalizeShortcutGesture(settings.Close),
            FocusResults = NormalizeShortcutGesture(settings.FocusResults),
            OpenSelectedResult = NormalizeShortcutGesture(settings.OpenSelectedResult),
            RevealSelectedResult = NormalizeShortcutGesture(settings.RevealSelectedResult),
            CopySelectedResultPath = NormalizeShortcutGesture(settings.CopySelectedResultPath),
            PinSelectedResult = NormalizeShortcutGesture(settings.PinSelectedResult),
            PreviewSelectedResult = NormalizeShortcutGesture(settings.PreviewSelectedResult),
        };
    }

    private static AppShortcutGesture NormalizeShortcutGesture(AppShortcutGesture gesture) =>
        Enum.IsDefined(gesture) ? gesture : AppShortcutGesture.Disabled;

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

public sealed record AppStyleOption(AppStyle Value, string DisplayName, string Description)
{
    public override string ToString() => DisplayName;
}

public sealed record SemanticModelPackOption(
    string Id,
    string DisplayName,
    string Description,
    string InstallSizeLabel,
    bool IsDisabled,
    bool IsRecommended)
{
    public static SemanticModelPackOption Disabled { get; } =
        new(string.Empty, "Disabled", "Semantic search is off.", string.Empty, true, false);

    public override string ToString() => DisplayName;
}

public sealed record AppShortcutOption(AppShortcutGesture Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class AppShortcutBindingViewModel : ObservableObject
{
    private readonly IReadOnlyList<AppShortcutOption> _shortcutOptions;
    private readonly Action _changed;
    private AppShortcutOption _selectedShortcut;

    public AppShortcutBindingViewModel(
        AppShortcutAction action,
        string displayName,
        string description,
        AppShortcutGesture selectedShortcut,
        IReadOnlyList<AppShortcutOption> shortcutOptions,
        Action changed)
    {
        Action = action;
        DisplayName = displayName;
        Description = description;
        _shortcutOptions = shortcutOptions;
        _changed = changed;
        _selectedShortcut = FindOption(selectedShortcut);
    }

    public AppShortcutAction Action { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public IReadOnlyList<AppShortcutOption> ShortcutOptions => _shortcutOptions;

    public AppShortcutOption SelectedShortcut
    {
        get => _selectedShortcut;
        set
        {
            if (value is null || !SetProperty(ref _selectedShortcut, value))
                return;

            _changed();
        }
    }

    public AppShortcutGesture Gesture => SelectedShortcut.Value;

    public void SetGesture(AppShortcutGesture gesture)
    {
        SelectedShortcut = FindOption(gesture);
    }

    private AppShortcutOption FindOption(AppShortcutGesture gesture) =>
        _shortcutOptions.FirstOrDefault(option => option.Value == gesture)
        ?? _shortcutOptions.First(option => option.Value == AppShortcutGesture.Disabled);
}

public sealed class QuickSearchShortcutBindingViewModel : ObservableObject
{
    private readonly IReadOnlyList<AppShortcutOption> _shortcutOptions;
    private readonly Action _changed;
    private AppShortcutOption _selectedShortcut;

    public QuickSearchShortcutBindingViewModel(
        QuickSearchShortcutAction action,
        string displayName,
        string description,
        AppShortcutGesture selectedShortcut,
        IReadOnlyList<AppShortcutOption> shortcutOptions,
        Action changed)
    {
        Action = action;
        DisplayName = displayName;
        Description = description;
        _shortcutOptions = shortcutOptions;
        _changed = changed;
        _selectedShortcut = FindOption(selectedShortcut);
    }

    public QuickSearchShortcutAction Action { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public IReadOnlyList<AppShortcutOption> ShortcutOptions => _shortcutOptions;

    public AppShortcutOption SelectedShortcut
    {
        get => _selectedShortcut;
        set
        {
            if (value is null || !SetProperty(ref _selectedShortcut, value))
                return;

            _changed();
        }
    }

    public AppShortcutGesture Gesture => SelectedShortcut.Value;

    public void SetGesture(AppShortcutGesture gesture)
    {
        SelectedShortcut = FindOption(gesture);
    }

    private AppShortcutOption FindOption(AppShortcutGesture gesture) =>
        _shortcutOptions.FirstOrDefault(option => option.Value == gesture)
        ?? _shortcutOptions.First(option => option.Value == AppShortcutGesture.Disabled);
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
