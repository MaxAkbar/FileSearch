using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Volumes;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// Settings and live status for whole-drive file name indexes. The GUI
/// process owns the indexes it tracks: it keeps them current from the NTFS
/// change journal and saves their snapshots.
/// </summary>
public sealed partial class DriveNameIndexViewModel : ObservableObject, IDisposable
{
    private readonly IVolumeNameIndex _index;
    private readonly ISettingsService _settingsService;
    private readonly IUiDispatcher _dispatcher;
    private readonly StatusBarViewModel _status;
    private bool _useAdministratorScan;

    public DriveNameIndexViewModel(
        IVolumeNameIndex index,
        ISettingsService settingsService,
        IUiDispatcher dispatcher,
        StatusBarViewModel status)
    {
        _index = index;
        _settingsService = settingsService;
        _dispatcher = dispatcher;
        _status = status;
        _useAdministratorScan = settingsService.Current.DriveNameIndexUseAdministratorScan;
        _index.StatusChanged += OnStatusChanged;
        RefreshDrives();
    }

    public ObservableCollection<DriveNameIndexItemViewModel> Drives { get; } = new();

    public bool HasDrives => Drives.Count > 0;

    public bool UseAdministratorScan
    {
        get => _useAdministratorScan;
        set
        {
            if (!SetProperty(ref _useAdministratorScan, value))
                return;

            _settingsService.Update(settings => settings.DriveNameIndexUseAdministratorScan = value);
            OnPropertyChanged(nameof(ScanMethodSummary));
        }
    }

    public string ScanMethodSummary => UseAdministratorScan
        ? "Reads the NTFS master file table in seconds. Windows asks for administrator permission once per build; declining falls back to a folder scan."
        : "Scans every folder you can open, without administrator permission. Slower, and skips folders you cannot read.";

    /// <summary>Starts following the enabled drives' change journals.</summary>
    public Task InitializeAsync() =>
        _index.StartTrackingAsync(EnabledRoots(), CancellationToken.None);

    [RelayCommand]
    private void Refresh() => RefreshDrives();

    public void Dispose() => _index.StatusChanged -= OnStatusChanged;

    internal async Task SetIndexedAsync(DriveNameIndexItemViewModel drive, bool indexed)
    {
        drive.IsBusy = true;
        try
        {
            if (indexed)
            {
                _settingsService.Update(settings =>
                {
                    if (!settings.DriveNameIndexVolumes.Contains(drive.Root, StringComparer.OrdinalIgnoreCase))
                        settings.DriveNameIndexVolumes.Add(drive.Root);
                });

                await BuildAsync(drive).ConfigureAwait(true);
            }
            else
            {
                _settingsService.Update(settings =>
                    settings.DriveNameIndexVolumes.RemoveAll(root => string.Equals(root, drive.Root, StringComparison.OrdinalIgnoreCase)));
                await _index.RemoveAsync(drive.Root, CancellationToken.None).ConfigureAwait(true);
                drive.StatusText = "Not indexed";
                _status.Text = $"Removed the file name index for {drive.Root}.";
            }

            await _index.StartTrackingAsync(EnabledRoots(), CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            drive.IsBusy = false;
        }
    }

    internal async Task RebuildAsync(DriveNameIndexItemViewModel drive)
    {
        drive.IsBusy = true;
        try
        {
            await BuildAsync(drive).ConfigureAwait(true);
        }
        finally
        {
            drive.IsBusy = false;
        }
    }

    private async Task BuildAsync(DriveNameIndexItemViewModel drive)
    {
        var method = UseAdministratorScan ? VolumeBuildMethod.MasterFileTable : VolumeBuildMethod.DirectoryWalk;
        _status.Text = $"Building the file name index for {drive.Root}...";
        try
        {
            var status = await _index.BuildAsync(drive.Root, method, CancellationToken.None).ConfigureAwait(true);
            _status.Text = $"Indexed {status.EntryCount:n0} names on {drive.Root}.";
        }
        catch (OperationCanceledException) when (method == VolumeBuildMethod.MasterFileTable)
        {
            _status.Text = $"Administrator permission was declined; scanning folders on {drive.Root} instead.";
            await BuildWithFolderScanAsync(drive).ConfigureAwait(true);
        }
        catch (Exception ex) when (method == VolumeBuildMethod.MasterFileTable &&
                                   ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _status.Text = $"The administrator scan failed ({ex.Message}); scanning folders on {drive.Root} instead.";
            await BuildWithFolderScanAsync(drive).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            drive.StatusText = ex.Message;
            _status.Text = $"Could not index {drive.Root}: {ex.Message}";
        }
    }

    private async Task BuildWithFolderScanAsync(DriveNameIndexItemViewModel drive)
    {
        try
        {
            var status = await _index.BuildAsync(drive.Root, VolumeBuildMethod.DirectoryWalk, CancellationToken.None).ConfigureAwait(true);
            _status.Text = $"Indexed {status.EntryCount:n0} names on {drive.Root}.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            drive.StatusText = ex.Message;
            _status.Text = $"Could not index {drive.Root}: {ex.Message}";
        }
    }

    private void RefreshDrives()
    {
        var enabled = new HashSet<string>(_settingsService.Current.DriveNameIndexVolumes, StringComparer.OrdinalIgnoreCase);
        var statuses = _index.GetStatuses().ToDictionary(status => status.VolumeRoot, StringComparer.OrdinalIgnoreCase);
        Drives.Clear();
        foreach (var candidate in _index.GetCandidateVolumes())
        {
            var drive = new DriveNameIndexItemViewModel(
                this,
                candidate.VolumeRoot,
                $"{candidate.Label} ({candidate.VolumeRoot.TrimEnd('\\')})",
                candidate.IsSupported,
                enabled.Contains(candidate.VolumeRoot));
            drive.StatusText = candidate.IsSupported
                ? statuses.TryGetValue(candidate.VolumeRoot, out var status) ? Describe(status) : "Not indexed"
                : candidate.UnsupportedReason ?? "Not supported";
            Drives.Add(drive);
        }

        OnPropertyChanged(nameof(HasDrives));
    }

    private void OnStatusChanged(object? sender, VolumeIndexStatus status) =>
        _dispatcher.Post(() =>
        {
            var drive = Drives.FirstOrDefault(item => string.Equals(item.Root, status.VolumeRoot, StringComparison.OrdinalIgnoreCase));
            if (drive is not null)
                drive.StatusText = Describe(status);
        });

    private string[] EnabledRoots() =>
        _settingsService.Current.DriveNameIndexVolumes.ToArray();

    internal static string Describe(VolumeIndexStatus status) => status.Phase switch
    {
        VolumeIndexPhase.Building => status.ProgressEntries is > 0
            ? $"Building... {status.ProgressEntries.Value:n0} names"
            : status.Message,
        VolumeIndexPhase.Ready => string.Join(
            " · ",
            new[]
            {
                $"{status.EntryCount:n0} names",
                status.BuildMethod == VolumeBuildMethod.MasterFileTable ? "master file table" : "folder scan",
                status.IsTracking ? "live" : null,
                status.UpdatedUtc is { } updated ? $"updated {FormatAge(DateTime.UtcNow - updated)}" : null,
            }.Where(part => part is not null)),
        VolumeIndexPhase.NotIndexed => "Not indexed",
        _ => status.Message,
    };

    private static string FormatAge(TimeSpan age) =>
        age.TotalSeconds < 10 ? "just now"
        : age.TotalMinutes < 1 ? $"{age.TotalSeconds.ToString("0", CultureInfo.CurrentCulture)} s ago"
        : age.TotalHours < 1 ? $"{age.TotalMinutes.ToString("0", CultureInfo.CurrentCulture)} min ago"
        : age.TotalDays < 1 ? $"{age.TotalHours.ToString("0", CultureInfo.CurrentCulture)} h ago"
        : $"{age.TotalDays.ToString("0", CultureInfo.CurrentCulture)} d ago";
}

public sealed partial class DriveNameIndexItemViewModel : ObservableObject
{
    private readonly DriveNameIndexViewModel _owner;
    private bool _isIndexed;
    private bool _isBusy;
    private string _statusText = string.Empty;

    internal DriveNameIndexItemViewModel(
        DriveNameIndexViewModel owner,
        string root,
        string label,
        bool isSupported,
        bool isIndexed)
    {
        _owner = owner;
        Root = root;
        Label = label;
        IsSupported = isSupported;
        _isIndexed = isIndexed;
    }

    public string Root { get; }

    public string Label { get; }

    public bool IsSupported { get; }

    public bool CanChange => IsSupported && !IsBusy;

    public bool CanRebuild => IsSupported && IsIndexed && !IsBusy;

    public bool IsIndexed
    {
        get => _isIndexed;
        set
        {
            if (!SetProperty(ref _isIndexed, value))
                return;

            OnPropertyChanged(nameof(CanRebuild));
            _ = _owner.SetIndexedAsync(this, value);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value))
                return;

            OnPropertyChanged(nameof(CanChange));
            OnPropertyChanged(nameof(CanRebuild));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    [RelayCommand]
    private Task RebuildAsync() => _owner.RebuildAsync(this);
}
