using System;
using System.Collections.Generic;
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
    private readonly StatusBarViewModel _status;
    private readonly bool _isInitialized;
    private int _sidebarPageSize;
    private IndexerResourceProfile _indexerResourceProfile;
    private bool _keepIndexUpdatedAfterClose;
    private bool _startBackgroundIndexerAtSignIn;
    private bool _pauseIndexingOnBattery;
    private bool _indexOnlyWhenIdle;
    private int _indexerCpuLimitPercent;
    private int _indexerDiskPauseMilliseconds;

    public ApplicationSettingsViewModel(
        ISettingsService settingsService,
        StatusBarViewModel status,
        IStartupRegistrationService? startupRegistration = null)
    {
        _settingsService = settingsService;
        _startupRegistration = startupRegistration;
        _status = status;
        _sidebarPageSize = NormalizeSidebarPageSize(_settingsService.Current.SidebarPageSize);
        _indexerResourceProfile = NormalizeIndexerResourceProfile(_settingsService.Current.IndexerResourceProfile);
        _keepIndexUpdatedAfterClose = _settingsService.Current.KeepIndexUpdatedAfterClose;
        _startBackgroundIndexerAtSignIn = _settingsService.Current.StartBackgroundIndexerAtSignIn;
        _pauseIndexingOnBattery = _settingsService.Current.PauseIndexingOnBattery;
        _indexOnlyWhenIdle = _settingsService.Current.IndexOnlyWhenIdle;
        _indexerCpuLimitPercent = NormalizeCpuLimit(_settingsService.Current.IndexerCpuLimitPercent);
        _indexerDiskPauseMilliseconds = NormalizeDiskPause(_settingsService.Current.IndexerDiskPauseMilliseconds);
        _isInitialized = true;
    }

    public IReadOnlyList<int> SidebarPageSizeOptions { get; } =
        Enumerable.Range(MinimumSidebarPageSize, MaximumSidebarPageSize - MinimumSidebarPageSize + 1).ToList();

    public IReadOnlyList<IndexerResourceProfile> IndexerResourceProfileOptions { get; } =
        [IndexerResourceProfile.Low, IndexerResourceProfile.Balanced, IndexerResourceProfile.High];

    public IReadOnlyList<int> IndexerCpuLimitOptions { get; } = [0, 25, 50, 75];

    public IReadOnlyList<int> IndexerDiskPauseOptions { get; } = [0, 5, 25, 100, 250];

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
            ? "Closing the search window hands indexing to the background worker"
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

    public void SaveSettings() => SaveSettings(updateStartupRegistration: true);

    private void SaveSettings(bool updateStartupRegistration)
    {
        _settingsService.Update(settings =>
        {
            settings.SidebarPageSize = SidebarPageSize;
            settings.IndexerResourceProfile = IndexerResourceProfile;
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

    private static int NormalizeCpuLimit(int value) =>
        value is <= 0 or >= 100 ? 0 : Math.Clamp(value, 1, 99);

    private static int NormalizeDiskPause(int value) =>
        Math.Clamp(value, 0, 1_000);
}
