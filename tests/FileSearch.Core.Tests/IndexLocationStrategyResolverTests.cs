using FileSearch.Core.Indexing;

namespace FileSearch.Core.Tests;

public sealed class IndexLocationStrategyResolverTests
{
    [Fact]
    public void ClassifyUsesUsnForLocalSupportedVolumes()
    {
        var strategy = IndexLocationStrategyResolver.Classify(@"C:\Code", Volume(usnSupported: true));

        Assert.Equal(IndexLocationKind.LocalUsn, strategy.LocationKind);
        Assert.Equal(IndexUpdateStrategy.UsnJournalAndWatcher, strategy.UpdateStrategy);
        Assert.True(strategy.UsnCatchUpEnabled);
    }

    [Fact]
    public void ClassifyUsesSnapshotForUnsupportedLocalFilesystems()
    {
        var strategy = IndexLocationStrategyResolver.Classify(@"E:\Docs", Volume(filesystem: "exFAT"));

        Assert.Equal(IndexLocationKind.LocalSnapshot, strategy.LocationKind);
        Assert.Equal(IndexUpdateStrategy.SnapshotScanAndWatcher, strategy.UpdateStrategy);
        Assert.False(strategy.UsnCatchUpEnabled);
    }

    [Fact]
    public void ClassifyUsesNetworkSnapshotStrategyForRemoteRoots()
    {
        var strategy = IndexLocationStrategyResolver.Classify(
            @"\\server\share\docs",
            Volume(
                volumeKey: @"\\server\share",
                rootDirectoryPath: @"\\server\share\",
                filesystem: "remote",
                isRemote: true,
                driveKind: IndexVolumeDriveKind.Network));

        Assert.Equal(IndexLocationKind.NetworkShare, strategy.LocationKind);
        Assert.Equal(IndexUpdateStrategy.ScheduledSnapshotScan, strategy.UpdateStrategy);
        Assert.False(strategy.UsnCatchUpEnabled);
        Assert.False(strategy.WatcherRecommended);
    }

    [Fact]
    public void ClassifyUsesReconnectStrategyForRemovableVolumes()
    {
        var strategy = IndexLocationStrategyResolver.Classify(
            @"E:\Photos",
            Volume(filesystem: "exFAT", driveKind: IndexVolumeDriveKind.Removable));

        Assert.Equal(IndexLocationKind.Removable, strategy.LocationKind);
        Assert.Equal(IndexUpdateStrategy.OfflineCacheAndReconnectScan, strategy.UpdateStrategy);
        Assert.False(strategy.UsnCatchUpEnabled);
    }

    [Fact]
    public void ClassifyUsesCloudStrategyForKnownCloudPathOnUsnVolume()
    {
        var strategy = IndexLocationStrategyResolver.Classify(
            @"C:\Users\max\OneDrive - Contoso\Project",
            Volume(usnSupported: true));

        Assert.Equal(IndexLocationKind.CloudBacked, strategy.LocationKind);
        Assert.Equal(IndexUpdateStrategy.SnapshotScanAndWatcher, strategy.UpdateStrategy);
        Assert.False(strategy.UsnCatchUpEnabled);
    }

    private static IndexVolumeInfo Volume(
        string volumeKey = @"C:",
        string volumeDevicePath = @"\\.\C:",
        string rootDirectoryPath = @"C:\",
        string filesystem = "NTFS",
        bool isRemote = false,
        bool usnSupported = false,
        IndexVolumeDriveKind driveKind = IndexVolumeDriveKind.Fixed) =>
        new(
            volumeKey,
            volumeDevicePath,
            rootDirectoryPath,
            "123",
            filesystem,
            isRemote,
            usnSupported,
            driveKind);
}
