using System.Text.Json;
using FileSearch.Core.Extractors;

return args.Contains("--serve", StringComparer.OrdinalIgnoreCase)
    ? await ExtractorHostProgram.RunServerAsync(Console.In, Console.Out, CancellationToken.None).ConfigureAwait(false)
    : await ExtractorHostProgram.RunAsync(Console.In, Console.Out, CancellationToken.None).ConfigureAwait(false);

internal static class ExtractorHostProgram
{
    public static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var requestJson = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var response = await HandleRequestJsonAsync(requestJson, cancellationToken).ConfigureAwait(false);

        var responseJson = JsonSerializer.Serialize(response, ExtractorHostProtocol.JsonOptions);
        await output.WriteAsync(responseJson.AsMemory(), cancellationToken).ConfigureAwait(false);
        return response.Success ? 0 : 1;
    }

    public static async Task<int> RunServerAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } requestJson)
        {
            var response = await HandleRequestJsonAsync(requestJson, cancellationToken).ConfigureAwait(false);
            var responseJson = JsonSerializer.Serialize(response, ExtractorHostProtocol.JsonOptions);
            await output.WriteLineAsync(responseJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<ExtractorHostResponse> HandleRequestJsonAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ExtractorHostRequest>(
                requestJson,
                ExtractorHostProtocol.JsonOptions);
            return request is null
                ? ExtractorHostResponse.Fail("extractor_host_protocol_error", "Extractor request was empty or invalid.")
                : await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return ExtractorHostResponse.Fail("extractor_host_protocol_error", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ExtractorHostResponse.Fail("extractor_failed", ex.Message);
        }
    }

    private static async Task<ExtractorHostResponse> HandleRequestAsync(
        ExtractorHostRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != ExtractorHostProtocol.CurrentVersion)
        {
            return ExtractorHostResponse.Fail(
                "extractor_host_protocol_error",
                $"Unsupported protocol version {request.ProtocolVersion}.");
        }

        if (string.IsNullOrWhiteSpace(request.Path))
            return ExtractorHostResponse.Fail("extractor_host_protocol_error", "Request path is required.");

        if (string.IsNullOrWhiteSpace(request.ExtractorId))
            return ExtractorHostResponse.Fail("extractor_host_protocol_error", "Request extractor id is required.");

        var extractor = CreateExtractors().FirstOrDefault(
            candidate => string.Equals(candidate.ExtractorId, request.ExtractorId, StringComparison.Ordinal));
        if (extractor is null)
        {
            return ExtractorHostResponse.Fail(
                "extractor_host_unknown_extractor",
                $"Extractor '{request.ExtractorId}' is not available in the host.");
        }

        var lines = new List<TextLine>();
        var issues = new ListExtractionIssueSink();
        try
        {
            var extractedLines = extractor is IDiagnosticTextExtractor diagnosticExtractor
                ? diagnosticExtractor.ExtractAsync(request.Path, issues, cancellationToken)
                : extractor.ExtractAsync(request.Path, cancellationToken);

            await foreach (var line in extractedLines.ConfigureAwait(false))
                lines.Add(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ExtractorHostResponse.Fail("extractor_failed", ex.Message);
        }

        return ExtractorHostResponse.Ok(lines, issues.Issues);
    }

    private static ITextExtractor[] CreateExtractors() =>
        new ITextExtractor[]
        {
            new PlainTextExtractor(),
            new PdfExtractor(),
            new WordExtractor(),
            new ExcelExtractor(),
            new PowerPointExtractor(),
            new OpenDocumentExtractor(),
            new EpubExtractor(),
            new RtfExtractor(),
            new HtmlExtractor(),
            new EmlExtractor(),
            new XmlTextExtractor(),
            new CalendarContactExtractor(),
            new ZipExtractor(),
        };
}
