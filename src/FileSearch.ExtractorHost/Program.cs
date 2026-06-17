using System.Text.Json;
using FileSearch.Core.Extractors;

return await ExtractorHostProgram.RunAsync(Console.In, Console.Out, CancellationToken.None).ConfigureAwait(false);

internal static class ExtractorHostProgram
{
    public static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ExtractorHostResponse response;
        try
        {
            var requestJson = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<ExtractorHostRequest>(
                requestJson,
                ExtractorHostProtocol.JsonOptions);
            response = request is null
                ? ExtractorHostResponse.Fail("extractor_host_protocol_error", "Extractor request was empty or invalid.")
                : await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            response = ExtractorHostResponse.Fail("extractor_host_protocol_error", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            response = ExtractorHostResponse.Fail("extractor_failed", ex.Message);
        }

        var responseJson = JsonSerializer.Serialize(response, ExtractorHostProtocol.JsonOptions);
        await output.WriteAsync(responseJson.AsMemory(), cancellationToken).ConfigureAwait(false);
        return response.Success ? 0 : 1;
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
