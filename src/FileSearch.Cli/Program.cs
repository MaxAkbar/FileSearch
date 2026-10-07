using FileSearch.Cli;
using FileSearch.Core;
using FileSearch.Core.Engine;
using FileSearch.Core.Logging;
using FileSearch.WindowsOcr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddSingleton(new SearchOptions
{
    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
});
// File-only logging: the console belongs to the REPL.
services.AddLogging(logging => logging.AddProvider(new FileLoggerProvider(
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileSearch", "logs"),
    "filesearch-cli")));
services.AddSingleton(EmbeddingModelSettings.Load());
services.AddFileSearchCore();
services.AddWindowsImageOcr();
services.AddSingleton<FileSearchRepl>();
services.AddSingleton<DocumentModelCommands>();

using var provider = services.BuildServiceProvider();
return await provider.GetRequiredService<FileSearchRepl>().RunAsync(args).ConfigureAwait(false);
