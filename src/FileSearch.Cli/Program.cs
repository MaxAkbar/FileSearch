using FileSearch.Cli;
using FileSearch.Core;
using FileSearch.Core.Engine;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddSingleton(new SearchOptions
{
    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
});
services.AddFileSearchCore();
services.AddSingleton<FileSearchRepl>();

using var provider = services.BuildServiceProvider();
return await provider.GetRequiredService<FileSearchRepl>().RunAsync(args).ConfigureAwait(false);
