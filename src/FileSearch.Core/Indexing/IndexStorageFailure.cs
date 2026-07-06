using System;

namespace FileSearch.Core.Indexing;

/// <summary>
/// Classifies exceptions that originate inside the CSharpDB storage engine.
/// Reads that race a concurrent write session can fail there with types as
/// generic as <see cref="ArgumentOutOfRangeException"/>, so classification
/// walks the exception chain for the engine's namespace in the type or the
/// stack trace rather than matching specific exception types.
/// </summary>
internal static class IndexStorageFailure
{
    public static bool IsStorageEngineFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
            return false;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var ns = current.GetType().Namespace;
            if (ns is not null && ns.StartsWith("CSharpDB", StringComparison.Ordinal))
                return true;

            if (current.StackTrace?.Contains("CSharpDB.", StringComparison.Ordinal) == true)
                return true;
        }

        return false;
    }
}
