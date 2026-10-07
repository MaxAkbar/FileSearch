using System.Text.Json;
using FileSearch.Core.Engine;

namespace FileSearch.Cli;

internal sealed class DocumentModelCommands(
    IEmbeddingModelPackCatalog catalog,
    IEmbeddingModelPackStore store,
    IEmbeddingModelPackInstaller installer,
    ISemanticIndexStatusService status)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        switch (args.Count > 0 ? args[0].ToLowerInvariant() : "list")
        {
            case "list":
            case "status":
                var installed = await store.GetInstalledPacksAsync(cancellationToken).ConfigureAwait(false);
                var selected = store.SelectedModelPackId;
                var rootStatus = args.Count > 1 ? await status.GetRootStatusAsync(Path.GetFullPath(args[1]), cancellationToken).ConfigureAwait(false) : null;
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Enabled = !string.IsNullOrWhiteSpace(selected),
                    SelectedModelId = selected,
                    Models = catalog.Entries.Select(entry => new
                    {
                        entry.Id,
                        entry.DisplayName,
                        entry.IsRecommended,
                        entry.InstallSizeLabel,
                        entry.Manifest.LicenseUrl,
                        entry.Manifest.RequiresLicenseAcceptance,
                        Installed = installed.Any(pack => pack.Manifest.Id == entry.Id),
                        Usable = installed.Any(pack => pack.Manifest.Id == entry.Id && pack.IsUsable),
                        Status = installed.FirstOrDefault(pack => pack.Manifest.Id == entry.Id)?.Status,
                    }),
                    RootStatus = rootStatus,
                }, s_jsonOptions));
                return 0;
            case "install" when args.Count >= 2:
                var pack = await installer.InstallAsync(args[1], progress: null,
                    acceptLicense: args.Contains("--accept-license", StringComparer.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"{pack.Manifest.DisplayName}: {pack.Status}");
                Console.WriteLine("Select the model in Settings, then explicitly Rebuild Smart Search for each indexed location.");
                return pack.IsUsable ? 0 : 1;
            default:
                Console.Error.WriteLine("Usage: filesearch models list|status [ROOT]|install ID [--accept-license]");
                return 2;
        }
    }
}
