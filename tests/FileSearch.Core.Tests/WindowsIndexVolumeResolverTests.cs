using FileSearch.Core.Indexing;

namespace FileSearch.Core.Tests;

public sealed class WindowsIndexVolumeResolverTests
{
    [Fact]
    public void ToVolumeDevicePathRemovesDriveRootTrailingSlash()
    {
        Assert.Equal(@"\\.\C:", WindowsIndexVolumeResolver.ToVolumeDevicePath(@"C:\", string.Empty));
    }

    [Fact]
    public void ToRootDirectoryPathKeepsDriveRootTrailingSlash()
    {
        Assert.Equal(@"C:\", WindowsIndexVolumeResolver.ToRootDirectoryPath(@"C:\", string.Empty));
    }

    [Fact]
    public void ToVolumeDevicePathRemovesGuidTrailingSlash()
    {
        Assert.Equal(
            @"\\?\Volume{11111111-2222-3333-4444-555555555555}",
            WindowsIndexVolumeResolver.ToVolumeDevicePath(
                @"C:\",
                @"\\?\Volume{11111111-2222-3333-4444-555555555555}\"));
    }

    [Fact]
    public void ToRootDirectoryPathKeepsGuidTrailingSlash()
    {
        Assert.Equal(
            @"\\?\Volume{11111111-2222-3333-4444-555555555555}\",
            WindowsIndexVolumeResolver.ToRootDirectoryPath(
                @"C:\",
                @"\\?\Volume{11111111-2222-3333-4444-555555555555}\"));
    }
}
