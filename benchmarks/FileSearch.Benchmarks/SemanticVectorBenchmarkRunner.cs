using System.Diagnostics;
using System.Globalization;
using FileSearch.Core.Engine;

namespace FileSearch.Benchmarks;

internal sealed record SemanticVectorBenchmarkReport(
    int DocumentCount,
    int Dimension,
    long PayloadBytes,
    LatencySummary Latency,
    long AllocatedBytesPerQuery,
    bool RequiresHnsw);

internal sealed class SemanticVectorBenchmarkRunner
{
    private const int Dimension = 384;
    private const double ExactSearchP95GateMilliseconds = 250;
    private static readonly EmbeddingModelInfo s_model = new("semantic-benchmark", "1", Dimension);
    private static readonly string[] s_roots = [@"C:\semantic-benchmark"];

    public SemanticVectorBenchmarkReport Run(int documentCount, int measuredQueries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(measuredQueries);

        var documents = CreateDocuments(documentCount);
        var index = new VectorSearchIndex(
            documents,
            new VectorIndexOptions { UseApproximateSearch = false });
        var queries = CreateQueries(measuredQueries + 3);
        for (var i = 0; i < 3; i++)
            _ = Search(index, queries[i]);

        var latencies = new double[measuredQueries];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < measuredQueries; i++)
        {
            var started = Stopwatch.GetTimestamp();
            _ = Search(index, queries[i + 3]);
            latencies[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var summary = LatencySummary.From(latencies);
        return new SemanticVectorBenchmarkReport(
            documentCount,
            Dimension,
            (long)documentCount * Dimension,
            summary,
            allocated / measuredQueries,
            summary.P95Milliseconds > ExactSearchP95GateMilliseconds);
    }

    private static VectorSearchResult Search(VectorSearchIndex index, float[] query) =>
        index.Search(
            query,
            count: 50,
            s_model,
            VectorDocumentKind.ContentChunk,
            fileIds: null,
            roots: s_roots);

    private static VectorDocument[] CreateDocuments(int count)
    {
        var random = new Random(0x4D595DF4);
        var documents = new VectorDocument[count];
        for (var documentIndex = 0; documentIndex < documents.Length; documentIndex++)
        {
            var values = new sbyte[Dimension];
            long squaredNorm = 0;
            for (var dimensionIndex = 0; dimensionIndex < values.Length; dimensionIndex++)
            {
                var value = (sbyte)random.Next(sbyte.MinValue, sbyte.MaxValue + 1);
                values[dimensionIndex] = value;
                squaredNorm += (long)value * value;
            }

            documents[documentIndex] = new VectorDocument(
                $"semantic-{documentIndex:0000000}",
                VectorDocumentKind.ContentChunk,
                documentIndex + 1L,
                Array.Empty<long>(),
                new QuantizedVector(values, 1f / sbyte.MaxValue, squaredNorm),
                s_model,
                "benchmark",
                documentIndex.ToString("x8", CultureInfo.InvariantCulture),
                locator: null,
                @"C:\semantic-benchmark");
        }

        return documents;
    }

    private static float[][] CreateQueries(int count)
    {
        var random = new Random(0x1B873593);
        var queries = new float[count][];
        for (var queryIndex = 0; queryIndex < queries.Length; queryIndex++)
        {
            var vector = new float[Dimension];
            double squaredNorm = 0;
            for (var dimensionIndex = 0; dimensionIndex < vector.Length; dimensionIndex++)
            {
                vector[dimensionIndex] = (random.NextSingle() * 2) - 1;
                squaredNorm += vector[dimensionIndex] * vector[dimensionIndex];
            }

            var norm = Math.Sqrt(squaredNorm);
            for (var dimensionIndex = 0; dimensionIndex < vector.Length; dimensionIndex++)
                vector[dimensionIndex] = (float)(vector[dimensionIndex] / norm);
            queries[queryIndex] = vector;
        }

        return queries;
    }
}
