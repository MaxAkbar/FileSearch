using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Windows.Data.Pdf;

namespace FileSearch.WindowsOcr;

public sealed record WindowsPdfRenderResult(int Page, int PageCount, double Width, double Height, byte[] PngBytes);

/// <summary>Renders native PDF pages in an owned, disposable copy of a supported FileSearch host.</summary>
public static class WindowsPdfRenderer
{
    private const string WorkerArgument = "--pdf-render-worker";
    private const string PipePrefix = "FileSearchPdf-";
    private const int ProtocolVersion = 1;
    private const int MaxRequestBytes = 256 * 1024;
    private const int MaxResponseBytes = 16 * 1024;
    private const int MaxPngBytes = 256 * 1024 * 1024;
    private const long MaxPixels = 50_000_000;
    private const long DefaultMaxPdfBytes = 100 * 1024 * 1024;
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] HostNames = ["FileSearch.Gui", "FileSearch.Cli", "FileSearch.Indexer", "FileSearch.PowerToys"];

    public static async Task<WindowsPdfRenderResult> RenderAsync(string path, int pageNumber, CancellationToken token,
        int width = 1600, int height = 2000, bool fitBounds = true, long maxPdfBytes = DefaultMaxPdfBytes)
    {
        token.ThrowIfCancellationRequested();
        var request = new RenderRequest(ProtocolVersion, Path.GetFullPath(path), pageNumber, width, height, fitBounds, maxPdfBytes);
        ValidateRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RenderTimeout);
        var name = PipePrefix + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = new Process { StartInfo = CreateStartInfo(name) };
        Task<WindowsPdfRenderResult>? exchange = null;
        bool started = false, stopped = false;
        try
        {
            if (!process.Start()) throw new IOException("The PDF rendering worker could not start.");
            started = true;
            var exited = process.WaitForExitAsync(CancellationToken.None);
            exchange = ExchangeAsync(pipe, request, deadline.Token);
            if (await Task.WhenAny(exchange, exited).WaitAsync(deadline.Token).ConfigureAwait(false) == exited && !exchange.IsCompleted)
                throw WorkerExited(process);
            var result = await exchange.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (started && process.HasExited) throw WorkerExited(process);

            // The complete, validated response is the handoff. Native PDF's process-global
            // D3D state must not enter its unsafe DLL-detach cleanup in this disposable host.
            process.Kill();
            await exited.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            stopped = true;
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (started && process.HasExited) throw WorkerExited(process);
            throw new TimeoutException("PDF rendering exceeded its time limit.");
        }
        finally
        {
            deadline.Cancel();
            if (started && !stopped)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception or TimeoutException) { }
            }
            if (exchange is not null)
            {
                // Observe cancelled pipe work after an early worker failure as well.
                try { await exchange.ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException or JsonException) { }
            }
        }
    }

    /// <summary>Handles only the private worker mode, before normal application startup.</summary>
    public static bool TryRunWorker(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], WorkerArgument, StringComparison.Ordinal)) return false;
        if (args.Length != 2 || !IsPrivatePipeName(args[1])) { Environment.ExitCode = 1; return true; }
        try { Task.Run(() => RunWorkerAsync(args[1])).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException)
        { Environment.ExitCode = 1; }
        finally
        {
            // A broken owner/pipe must not route this native-only disposable process
            // through the same unsafe PDF DLL-detach path either.
            using var self = Process.GetCurrentProcess();
            self.Kill();
        }
        return true;
    }

    private static async Task<WindowsPdfRenderResult> ExchangeAsync(NamedPipeServerStream pipe, RenderRequest request, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request), MaxRequestBytes, token).ConfigureAwait(false);
        var metadata = JsonSerializer.Deserialize<RenderResponse>(await ReadFrameAsync(pipe, MaxResponseBytes, token).ConfigureAwait(false))
            ?? throw new InvalidDataException("The PDF worker returned no metadata.");
        if (metadata.Version != ProtocolVersion) throw new InvalidDataException("The PDF worker protocol version is unsupported.");
        if (metadata.Error is not null) throw new InvalidDataException("The PDF page could not be rendered.");
        if (metadata.PageCount <= 0 || metadata.PngLength is < 0 or > MaxPngBytes ||
            (request.PageNumber == 0
                ? metadata.Page != 0 || metadata.PngLength != 0 || metadata.Width != 0 || metadata.Height != 0
                : metadata.Page != Math.Clamp(request.PageNumber, 1, metadata.PageCount) ||
                  !IsDimension(metadata.Width) || !IsDimension(metadata.Height) || metadata.PngLength < 33))
            throw new InvalidDataException("The PDF worker returned invalid metadata.");
        var png = new byte[metadata.PngLength];
        await pipe.ReadExactlyAsync(png, token).ConfigureAwait(false);
        if (request.PageNumber != 0) ValidatePng(png);
        return new WindowsPdfRenderResult(metadata.Page, metadata.PageCount, metadata.Width, metadata.Height, png);
    }

    private static async Task RunWorkerAsync(string pipeName)
    {
        using var timeout = new CancellationTokenSource(RenderTimeout);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var frame = await ReadFrameAsync(pipe, MaxRequestBytes, timeout.Token).ConfigureAwait(false);
        // Watch the owner before activating any native PDF code, including while a
        // renderer is blocked in native code and cannot honor managed cancellation.
        var ownerExit = WatchOwnerAsync(pipe);
        WindowsPdfRenderResult? result = null;
        try
        {
            var request = JsonSerializer.Deserialize<RenderRequest>(frame)
                ?? throw new InvalidDataException("No PDF rendering request was supplied.");
            ValidateRequest(request);
            result = await RenderInWorkerAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new RenderResponse(ProtocolVersion, 0, 0, 0, 0, 0, "render failed")),
                MaxResponseBytes, timeout.Token).ConfigureAwait(false);
        }
        if (result is not null)
        {
            var response = new RenderResponse(ProtocolVersion, result.Page, result.PageCount, result.Width, result.Height, result.PngBytes.Length, null);
            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(response), MaxResponseBytes, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(result.PngBytes, timeout.Token).ConfigureAwait(false);
        }
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        await ownerExit.ConfigureAwait(false);
    }

    private static async Task WatchOwnerAsync(NamedPipeClientStream pipe)
    {
        using var orphanTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            byte[] ownerSignal = new byte[1];
            await pipe.ReadAsync(ownerSignal, orphanTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
        finally
        {
            using var self = Process.GetCurrentProcess();
            self.Kill();
        }
    }

    private static async Task<WindowsPdfRenderResult> RenderInWorkerAsync(RenderRequest request, CancellationToken token)
    {
        await using var source = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (request.MaxPdfBytes > 0 && source.Length > request.MaxPdfBytes)
            throw new InvalidDataException("The PDF exceeds its size limit.");
        using var nativeSource = source.AsRandomAccessStream();
        var document = await PdfDocument.LoadFromStreamAsync(nativeSource).AsTask(token).ConfigureAwait(false);
        int count = checked((int)document.PageCount);
        if (count <= 0) throw new InvalidDataException("The PDF contains no pages.");
        if (request.PageNumber == 0) return new WindowsPdfRenderResult(0, count, 0, 0, []);
        int number = Math.Clamp(request.PageNumber, 1, count);
        using var page = document.GetPage((uint)number - 1);
        double pageWidth = page.Size.Width, pageHeight = page.Size.Height;
        if (!IsDimension(pageWidth) || !IsDimension(pageHeight)) throw new InvalidDataException("The PDF page size is invalid.");
        double renderWidth = pageWidth, renderHeight = pageHeight;
        if (request.Width > 0)
        {
            if (request.FitBounds)
            {
                double scale = Math.Min(request.Width / pageWidth, request.Height / pageHeight);
                renderWidth = Math.Max(1, Math.Floor(pageWidth * scale));
                renderHeight = Math.Max(1, Math.Floor(pageHeight * scale));
            }
            else { renderWidth = request.Width; renderHeight = request.Height; }
        }
        if (Math.Ceiling(renderWidth) * Math.Ceiling(renderHeight) > MaxPixels)
            throw new InvalidDataException("The PDF page exceeds the render pixel limit.");
        using var rendered = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        if (request.Width == 0)
            await page.RenderToStreamAsync(rendered).AsTask(token).ConfigureAwait(false);
        else
            await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions
            {
                DestinationWidth = checked((uint)renderWidth),
                DestinationHeight = checked((uint)renderHeight),
            }).AsTask(token).ConfigureAwait(false);
        if (rendered.Size is < 33 or > MaxPngBytes) throw new InvalidDataException("The rendered PDF exceeds its byte limit.");
        rendered.Seek(0);
        using var input = rendered.AsStreamForRead();
        var png = new byte[checked((int)rendered.Size)];
        await input.ReadExactlyAsync(png, token).ConfigureAwait(false);
        ValidatePng(png);
        return new WindowsPdfRenderResult(number, count, pageWidth, pageHeight, png);
    }

    private static void ValidateRequest(RenderRequest request)
    {
        if (request.Version != ProtocolVersion || request.Path is null || request.Path.Length > 32_767 || !Path.IsPathFullyQualified(request.Path) ||
            request.PageNumber < 0 || request.Width < 0 || request.Height < 0 || request.MaxPdfBytes < 0 ||
            (request.Width == 0) != (request.Height == 0) || (long)request.Width * request.Height > MaxPixels)
            throw new InvalidDataException("The PDF rendering request is invalid.");
    }

    private static void ValidatePng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("The PDF worker did not return a PNG image.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        if (width == 0 || height == 0 || (ulong)width * height > MaxPixels)
            throw new InvalidDataException("The rendered PDF exceeds its pixel limit.");
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] bytes, int maxLength, CancellationToken token)
    {
        if (bytes.Length <= 0 || bytes.Length > maxLength) throw new InvalidDataException("The PDF protocol frame exceeds its limit.");
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int maxLength, CancellationToken token)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxLength) throw new InvalidDataException("The PDF protocol frame exceeds its limit.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static ProcessStartInfo CreateStartInfo(string pipeName)
    {
        var assembly = Assembly.GetEntryAssembly();
        string? entryName = assembly?.GetName().Name;
        string? processPath = Environment.ProcessPath;
        ProcessStartInfo start;
        if (entryName is not null && HostNames.Contains(entryName, StringComparer.Ordinal) && processPath is not null)
        {
            if (assembly is null || !HasWorkerMarker(assembly.Location))
                throw new IOException("Rebuild this FileSearch host to enable isolated PDF rendering.");
            start = new ProcessStartInfo(processPath);
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(assembly.Location);
        }
        else
        {
            string? host = FindTestHost();
            if (host is null) throw new FileNotFoundException("Rebuild the FileSearch GUI host to enable isolated PDF rendering.");
            start = new ProcessStartInfo(host);
        }
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.ArgumentList.Add(WorkerArgument);
        start.ArgumentList.Add(pipeName);
        return start;
    }

    private static string? FindTestHost()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "FileSearch.Gui.exe");
        if (File.Exists(direct) && HasWorkerMarker(Path.ChangeExtension(direct, ".dll"))) return direct;
        string configuration = typeof(WindowsPdfRenderer).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (string root in new[]
            {
                Path.Combine(directory.FullName, "src", "FileSearch.Gui", "bin", configuration),
                Path.Combine(directory.FullName, "artifacts", "bin", "FileSearch.Gui", configuration.ToLowerInvariant()),
                directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(directory.FullName, "FileSearch.Gui", configuration.ToLowerInvariant()) : string.Empty,
            })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string host in new[]
                {
                    Path.Combine(root, "FileSearch.Gui.exe"),
                    Path.Combine(root, "net10.0-windows10.0.19041.0", "FileSearch.Gui.exe"),
                })
                    if (File.Exists(host) && HasWorkerMarker(Path.ChangeExtension(host, ".dll"))) return host;
            }
        }
        return null;
    }

    private static bool HasWorkerMarker(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var image = new PEReader(stream);
            if (!image.HasMetadata) return false;
            var metadata = image.GetMetadataReader();
            if (!metadata.IsAssembly) return false;
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                if (metadata.GetString(type.Namespace) != "System.Reflection" || metadata.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
                var value = metadata.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() == 1 && value.ReadSerializedString() == "FileSearch.PdfRenderWorker" && value.ReadSerializedString() == "1")
                    return true;
            }
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException) { }
        return false;
    }

    private static bool IsPrivatePipeName(string name) => name.StartsWith(PipePrefix, StringComparison.Ordinal) &&
        name.Length == PipePrefix.Length + 32 && Guid.TryParseExact(name[PipePrefix.Length..], "N", out _);
    private static bool IsDimension(double value) => double.IsFinite(value) && value > 0 && value <= MaxPixels;
    private static IOException WorkerExited(Process process) => new($"The PDF rendering worker exited before completing its response (exit code {process.ExitCode}).");
    private sealed record RenderRequest(int Version, string Path, int PageNumber, int Width, int Height, bool FitBounds, long MaxPdfBytes);
    private sealed record RenderResponse(int Version, int Page, int PageCount, double Width, double Height, int PngLength, string? Error);
}
