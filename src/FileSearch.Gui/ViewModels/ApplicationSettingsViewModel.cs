using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Indexing;
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
    private readonly StatusBarViewModel _status;
    private readonly bool _isInitialized;
    private int _sidebarPageSize;
    private IndexerResourceProfile _indexerResourceProfile;

    public ApplicationSettingsViewModel(ISettingsService settingsService, StatusBarViewModel status)
    {
        _settingsService = settingsService;
        _status = status;
        _sidebarPageSize = NormalizeSidebarPageSize(_settingsService.Current.SidebarPageSize);
        _indexerResourceProfile = NormalizeIndexerResourceProfile(_settingsService.Current.IndexerResourceProfile);
        _isInitialized = true;
    }

    public IReadOnlyList<int> SidebarPageSizeOptions { get; } =
        Enumerable.Range(MinimumSidebarPageSize, MaximumSidebarPageSize - MinimumSidebarPageSize + 1).ToList();

    public IReadOnlyList<IndexerResourceProfile> IndexerResourceProfileOptions { get; } =
        [IndexerResourceProfile.Low, IndexerResourceProfile.Balanced, IndexerResourceProfile.High];

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
                SaveSettings();
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
                SaveSettings();
        }
    }

    public string IndexerResourceProfileSummary =>
        IndexerResourceProfile switch
        {
            IndexerResourceProfile.Low => "Pauses often so indexing stays quiet in the background",
            IndexerResourceProfile.High => "Indexes as fast as possible while the app is idle",
            _ => "Balances indexing progress with foreground responsiveness",
        };

    [RelayCommand]
    private void ResetNavigationDefaults()
    {
        SidebarPageSize = DefaultSidebarPageSize;
        IndexerResourceProfile = IndexerResourceProfile.Balanced;
    }

    public void SaveSettings()
    {
        _settingsService.Update(settings =>
        {
            settings.SidebarPageSize = SidebarPageSize;
            settings.IndexerResourceProfile = IndexerResourceProfile;
        });

        _status.Text = "Settings saved.";
    }

    private static int NormalizeSidebarPageSize(int value) =>
        value < MinimumSidebarPageSize || value > MaximumSidebarPageSize
            ? DefaultSidebarPageSize
            : value;

    private static IndexerResourceProfile NormalizeIndexerResourceProfile(IndexerResourceProfile profile) =>
        Enum.IsDefined(profile) ? profile : IndexerResourceProfile.Balanced;
}
