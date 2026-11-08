namespace FileSearch.Core.Databases;

public enum DatabasePreparationStage { Snapshot, Engine, Schema, Validation }

/// <summary>Preparation of a private read-only database snapshot. Byte counts include its WAL.</summary>
public sealed record DatabasePreparationProgress(DatabasePreparationStage Stage, long BytesCopied = 0, long TotalBytes = 0);
