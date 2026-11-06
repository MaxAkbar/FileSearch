using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;

namespace FileSearch.PowerToys;

[Guid("969211A8-FD4E-4DA0-A1B7-A855127DBC7A")]
public sealed partial class FileSearchExtension(ManualResetEvent stopped) : IExtension, IDisposable
{
    private readonly FileSearchCommandsProvider _provider = new();
    private int _disposed;

    public object? GetProvider(ProviderType providerType) =>
        providerType == ProviderType.Commands ? _provider : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _provider.Dispose();
        }
        finally
        {
            stopped.Set();
        }
    }
}
