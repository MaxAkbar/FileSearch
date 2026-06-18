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
    long LastValidationFailedCount = 0);
