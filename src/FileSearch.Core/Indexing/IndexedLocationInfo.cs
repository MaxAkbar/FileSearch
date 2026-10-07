using System;

namespace FileSearch.Core.Indexing;

public sealed record IndexedLocationInfo(
    string Root,
    long FileCount,
    long LineCount,
    DateTime? IndexedUtc,
    string Profile,
    bool Exists,
    DateTime? LastFullScanUtc = null,
    string? VolumeKey = null,
    DateTime? LastFullValidationUtc = null,
    string LastValidationStatus = "",
    string LastValidationMessage = "",
    long LastValidationFilesChecked = 0,
    long LastValidationMissingFromIndexCount = 0,
    long LastValidationChangedCount = 0,
    long LastValidationMissingFromDiskCount = 0,
    long LastValidationFailedCount = 0)
{
    /// <summary>Reconstructs the saved index profile for a refresh without adopting current search filters.</summary>
    public FileSearch.Core.Walker.WalkerOptions? GetWalkerOptions()
    {
        if (!IndexProfile.TryParse(Profile, out var profile)) return null;
        return new()
        {
            Recursive = profile.Recursive, IncludeHidden = profile.IncludeHidden, EnableOcr = profile.EnableOcr,
            IncludeExtensions = profile.IncludeExtensions, ExcludeExtensions = profile.ExcludeExtensions,
            IncludeDirectories = profile.IncludeDirectories, ExcludeDirectories = profile.ExcludeDirectories,
            MinFileSizeBytes = 0, MaxFileSizeBytes = 0,
        };
    }
}
