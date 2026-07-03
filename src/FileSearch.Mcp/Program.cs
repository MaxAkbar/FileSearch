using FileSearch.Core;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Logging;
using FileSearch.Mcp;
using FileSearch.Mcp.Tools;
using FileSearch.WindowsOcr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

McpServerRails rails;
try
{
    rails = McpServerRails.Parse(
        args,
        Environment.GetEnvironmentVariable(McpServerRails.RootsEnvironmentVariable));
}
catch (ArgumentException ex)
{
    // stderr only: stdout is the MCP protocol channel.
    Console.Error.WriteLine($"FileSearch.Mcp: {ex.Message}");
    return 2;
}

// Parameterless on purpose: our --root/--allow-any-root flags are not host
// configuration and must not reach the command-line configuration provider.
var builder = Host.CreateApplicationBuilder();

// stdout carries JSON-RPC; the default console logger would corrupt it.
// Logs go to the same daily files the GUI and CLI use.
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileSearch", "logs"),
    "filesearch-mcp"));

builder.Services.AddSingleton(new SearchOptions
{
    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
});

// Read-only rail: must be registered before AddFileSearchCore so its
// TryAdd of the real coordinator (which persists pending index changes
// on cache-miss searches) is skipped.
builder.Services.AddSingleton<IIndexingSearchCoordinator>(new NoOpIndexingSearchCoordinator());
builder.Services.AddFileSearchCore();
// Extractor parity with the GUI/CLI/tray hosts; without it, index coverage
// checks reject every root ("extractor versions are out of date").
builder.Services.AddWindowsImageOcr();

builder.Services.AddSingleton(rails);
builder.Services.AddSingleton<RootPolicy>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "filesearch",
            Title = "FileSearch",
            Version = typeof(RootPolicy).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        };
        options.ServerInstructions =
            "Read-only access to this machine's files through FileSearch: live content search " +
            "(search_content), fast search over indexed folders (search_index), document text " +
            "extraction (extract_text), and index introspection (index_status, index_failures). " +
            "Call index_status first to learn the allowed roots, indexed locations, and supported " +
            "file types. Nothing on disk or in the index is ever modified, and only paths under " +
            "the allowed roots are readable.";
    })
    .WithStdioServerTransport()
    .WithTools<SearchTools>()
    .WithTools<ExtractTools>()
    .WithTools<IndexTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;
