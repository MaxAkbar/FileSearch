namespace FileSearch.Core.Engine;

internal sealed record VectorIndexSearchDiagnostics(
    int TotalDocuments,
    int CandidateDocuments,
    bool UsedApproximateIndex,
    bool UsedFileIdIndex,
    string Strategy);

internal sealed record VectorSearchResult(
    IReadOnlyList<VectorMatch> Matches,
    VectorIndexSearchDiagnostics Diagnostics);

internal sealed class VectorSearchIndex
{
    private const int ProjectionBits = 64;
    private const int BandCount = 8;
    private const int BandBits = ProjectionBits / BandCount;

    public static VectorSearchIndex Empty { get; } = new(Array.Empty<VectorDocument>(), new VectorIndexOptions());

    private readonly VectorSearchRecord[] _records;
    private readonly Dictionary<VectorPartitionKey, int[]> _partitions;
    private readonly Dictionary<long, int[]> _fileBuckets;
    private readonly Dictionary<VectorPartitionKey, LshPartitionIndex> _annPartitions;
    private readonly VectorIndexOptions _options;

    public VectorSearchIndex(
        IReadOnlyCollection<VectorDocument> documents,
        VectorIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _options = options ?? new VectorIndexOptions();
        _records = documents
            .OrderBy(document => document.Id, StringComparer.Ordinal)
            .Select((document, index) => new VectorSearchRecord(index, document, CreateUnitVector(document.Vector)))
            .ToArray();
        _partitions = BuildPartitions(_records);
        _fileBuckets = BuildFileBuckets(_records);
        _annPartitions = BuildApproximatePartitions(_records, _partitions, NormalizeMinimumDocuments(_options));
    }

    public VectorSearchResult Search(
        ReadOnlyMemory<float> queryVector,
        int count,
        EmbeddingModelInfo? model = null,
        VectorDocumentKind? kind = null,
        IReadOnlyCollection<long>? fileIds = null)
    {
        if (count <= 0 || queryVector.Length == 0 || _records.Length == 0)
            return EmptyResult("empty");

        var query = CreateUnitVector(queryVector.ToArray());
        if (query.Length == 0)
            return EmptyResult("zero-query");

        var fileIdSet = NormalizeFileIds(fileIds);
        if (fileIdSet is not null)
            return SearchFileBuckets(query, count, model, kind, fileIdSet);

        var partitionKey = TryCreatePartitionKey(query.Length, model, kind);
        if (partitionKey is not null &&
            _partitions.TryGetValue(partitionKey.Value, out var partitionIndices))
        {
            if (_annPartitions.TryGetValue(partitionKey.Value, out var approximateIndex))
            {
                var approximateCandidates = approximateIndex.Search(query, NormalizeTargetCandidates(_options, count));
                if (approximateCandidates.Count >= count)
                {
                    return SearchCandidates(
                        query,
                        count,
                        approximateCandidates,
                        model,
                        kind,
                        fileIds: null,
                        new VectorIndexSearchDiagnostics(
                            _records.Length,
                            approximateCandidates.Count,
                            UsedApproximateIndex: true,
                            UsedFileIdIndex: false,
                            "lsh"));
                }
            }

            return SearchCandidates(
                query,
                count,
                partitionIndices,
                model,
                kind,
                fileIds: null,
                new VectorIndexSearchDiagnostics(
                    _records.Length,
                    partitionIndices.Length,
                    UsedApproximateIndex: false,
                    UsedFileIdIndex: false,
                    "partition-exact"));
        }

        return SearchCandidates(
            query,
            count,
            Enumerable.Range(0, _records.Length),
            model,
            kind,
            fileIds: null,
            new VectorIndexSearchDiagnostics(
                _records.Length,
                _records.Length,
                UsedApproximateIndex: false,
                UsedFileIdIndex: false,
                "filtered-exact"));
    }

    private VectorSearchResult SearchFileBuckets(
        float[] query,
        int count,
        EmbeddingModelInfo? model,
        VectorDocumentKind? kind,
        HashSet<long> fileIds)
    {
        var candidates = new HashSet<int>();
        foreach (var fileId in fileIds)
        {
            if (_fileBuckets.TryGetValue(fileId, out var indices))
            {
                foreach (var index in indices)
                    candidates.Add(index);
            }
        }

        return SearchCandidates(
            query,
            count,
            candidates,
            model,
            kind,
            fileIds,
            new VectorIndexSearchDiagnostics(
                _records.Length,
                candidates.Count,
                UsedApproximateIndex: false,
                UsedFileIdIndex: true,
                "file-filter-exact"));
    }

    private VectorSearchResult SearchCandidates(
        float[] query,
        int count,
        IEnumerable<int> candidateIndices,
        EmbeddingModelInfo? model,
        VectorDocumentKind? kind,
        HashSet<long>? fileIds,
        VectorIndexSearchDiagnostics diagnostics)
    {
        var top = new List<ScoredVectorRecord>(Math.Min(count, _records.Length));
        foreach (var index in candidateIndices)
        {
            if ((uint)index >= (uint)_records.Length)
                continue;

            var record = _records[index];
            if (record.UnitVector.Length != query.Length)
                continue;

            if (!MatchesFilters(record.Document, query.Length, model, kind, fileIds))
                continue;

            var score = Dot(query, record.UnitVector);
            if (score <= 0)
                continue;

            AddTopCandidate(top, new ScoredVectorRecord(record, score), count);
        }

        return new VectorSearchResult(
            top
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Record.Document.Id, StringComparer.Ordinal)
                .Select(item => ToMatch(item.Record.Document, item.Score))
                .ToArray(),
            diagnostics);
    }

    private VectorSearchResult EmptyResult(string strategy) =>
        new(
            Array.Empty<VectorMatch>(),
            new VectorIndexSearchDiagnostics(
                _records.Length,
                0,
                UsedApproximateIndex: false,
                UsedFileIdIndex: false,
                strategy));

    private static Dictionary<VectorPartitionKey, int[]> BuildPartitions(IReadOnlyList<VectorSearchRecord> records) =>
        records
            .GroupBy(record => VectorPartitionKey.FromDocument(record.Document))
            .ToDictionary(
                group => group.Key,
                group => group.Select(record => record.Index).ToArray());

    private static Dictionary<long, int[]> BuildFileBuckets(IReadOnlyList<VectorSearchRecord> records) =>
        records
            .GroupBy(record => record.Document.FileId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(record => record.Index).ToArray());

    private Dictionary<VectorPartitionKey, LshPartitionIndex> BuildApproximatePartitions(
        VectorSearchRecord[] records,
        IReadOnlyDictionary<VectorPartitionKey, int[]> partitions,
        int minimumDocuments)
    {
        if (!_options.UseApproximateSearch)
            return new Dictionary<VectorPartitionKey, LshPartitionIndex>();

        var indexes = new Dictionary<VectorPartitionKey, LshPartitionIndex>();
        foreach (var pair in partitions)
        {
            if (pair.Value.Length < minimumDocuments)
                continue;

            indexes[pair.Key] = new LshPartitionIndex(records, pair.Value, pair.Key.Dimension);
        }

        return indexes;
    }

    private static VectorPartitionKey? TryCreatePartitionKey(
        int dimension,
        EmbeddingModelInfo? model,
        VectorDocumentKind? kind)
    {
        if (model is null || kind is null)
            return null;

        var normalized = model.Normalize();
        if (normalized.Dimension != dimension)
            return null;

        return new VectorPartitionKey(
            normalized.ModelId,
            normalized.ModelVersion,
            normalized.QuantizationVersion,
            normalized.Dimension,
            kind.Value);
    }

    private static VectorMatch ToMatch(VectorDocument document, float score) =>
        new(
            document.Id,
            document.Kind,
            document.FileId,
            document.ContentUnitIds,
            score,
            document.Model,
            document.ChunkerVersion,
            document.ContentChecksum,
            document.Locator);

    private static bool MatchesFilters(
        VectorDocument document,
        int dimension,
        EmbeddingModelInfo? model,
        VectorDocumentKind? kind,
        HashSet<long>? fileIds) =>
        document.Vector.Count == dimension &&
        (kind is null || document.Kind == kind) &&
        (fileIds is null || fileIds.Contains(document.FileId)) &&
        MatchesModel(document.Model, model);

    private static bool MatchesModel(EmbeddingModelInfo documentModel, EmbeddingModelInfo? queryModel) =>
        queryModel is null ||
        documentModel.Dimension == queryModel.Dimension &&
        string.Equals(documentModel.ModelId, queryModel.ModelId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(documentModel.ModelVersion, queryModel.ModelVersion, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(documentModel.QuantizationVersion, queryModel.QuantizationVersion, StringComparison.OrdinalIgnoreCase);

    private static HashSet<long>? NormalizeFileIds(IReadOnlyCollection<long>? fileIds)
    {
        if (fileIds is null || fileIds.Count == 0)
            return null;

        var normalized = fileIds.Where(id => id > 0).ToHashSet();
        return normalized.Count == 0 ? null : normalized;
    }

    private static int NormalizeMinimumDocuments(VectorIndexOptions options) =>
        Math.Clamp(options.ApproximateSearchMinimumDocuments, 16, 1_000_000);

    private static int NormalizeTargetCandidates(VectorIndexOptions options, int count) =>
        Math.Clamp(
            Math.Max(options.ApproximateSearchTargetCandidates, count > 12_500 ? 100_000 : count * 8),
            Math.Max(count, 16),
            Math.Max(Math.Max(count, 16), 100_000));

    private static float[] CreateUnitVector(IReadOnlyList<float> vector)
    {
        if (vector.Count == 0)
            return Array.Empty<float>();

        double sum = 0;
        for (var i = 0; i < vector.Count; i++)
            sum += vector[i] * vector[i];

        var norm = Math.Sqrt(sum);
        if (norm <= 0)
            return Array.Empty<float>();

        var unit = new float[vector.Count];
        for (var i = 0; i < vector.Count; i++)
            unit[i] = (float)(vector[i] / norm);
        return unit;
    }

    private static float Dot(IReadOnlyList<float> left, float[] right)
    {
        double dot = 0;
        for (var i = 0; i < left.Count; i++)
            dot += left[i] * right[i];
        return (float)dot;
    }

    private static void AddTopCandidate(
        List<ScoredVectorRecord> top,
        ScoredVectorRecord candidate,
        int count)
    {
        if (top.Count < count)
        {
            top.Add(candidate);
            return;
        }

        var worstIndex = 0;
        for (var i = 1; i < top.Count; i++)
            if (IsWorse(top[i], top[worstIndex]))
                worstIndex = i;

        if (IsBetter(candidate, top[worstIndex]))
            top[worstIndex] = candidate;
    }

    private static bool IsBetter(ScoredVectorRecord left, ScoredVectorRecord right) =>
        left.Score > right.Score ||
        left.Score.Equals(right.Score) &&
        string.Compare(left.Record.Document.Id, right.Record.Document.Id, StringComparison.Ordinal) < 0;

    private static bool IsWorse(ScoredVectorRecord left, ScoredVectorRecord right) =>
        left.Score < right.Score ||
        left.Score.Equals(right.Score) &&
        string.Compare(left.Record.Document.Id, right.Record.Document.Id, StringComparison.Ordinal) > 0;

    private sealed class LshPartitionIndex
    {
        private readonly float[][] _planes;
        private readonly Dictionary<ulong, int[]> _buckets;

        public LshPartitionIndex(
            VectorSearchRecord[] records,
            IReadOnlyCollection<int> indices,
            int dimension)
        {
            _planes = CreatePlanes(dimension);
            var buckets = new Dictionary<ulong, List<int>>();
            foreach (var index in indices)
            {
                var signature = CreateSignature(records[index].UnitVector, _planes);
                for (var band = 0; band < BandCount; band++)
                {
                    var key = CreateBandKey(signature, band);
                    if (!buckets.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        buckets[key] = list;
                    }

                    list.Add(index);
                }
            }

            _buckets = buckets.ToDictionary(pair => pair.Key, pair => pair.Value.Distinct().ToArray());
        }

        public HashSet<int> Search(float[] query, int targetCandidates)
        {
            var signature = CreateSignature(query, _planes);
            var candidates = new HashSet<int>();
            AddBandMatches(signature, candidates);
            if (candidates.Count >= targetCandidates)
                return candidates;

            for (var band = 0; band < BandCount && candidates.Count < targetCandidates; band++)
            {
                var bandValue = (byte)((signature >> (band * BandBits)) & 0xffUL);
                for (var bit = 0; bit < BandBits && candidates.Count < targetCandidates; bit++)
                    AddBucket(CreateBandKey((byte)(bandValue ^ (1 << bit)), band), candidates);
            }

            return candidates;
        }

        private void AddBandMatches(ulong signature, HashSet<int> candidates)
        {
            for (var band = 0; band < BandCount; band++)
                AddBucket(CreateBandKey(signature, band), candidates);
        }

        private void AddBucket(ulong key, HashSet<int> candidates)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
                return;

            foreach (var index in bucket)
                candidates.Add(index);
        }
    }

    private static float[][] CreatePlanes(int dimension)
    {
        var planes = new float[ProjectionBits][];
        for (var planeIndex = 0; planeIndex < planes.Length; planeIndex++)
        {
            var random = new Random(unchecked(0x5EED1234 + (planeIndex * 7919) + (dimension * 104729)));
            var plane = new float[dimension];
            for (var i = 0; i < plane.Length; i++)
                plane[i] = (random.NextSingle() * 2) - 1;
            planes[planeIndex] = plane;
        }

        return planes;
    }

    private static ulong CreateSignature(IReadOnlyList<float> vector, IReadOnlyList<float[]> planes)
    {
        ulong signature = 0;
        for (var i = 0; i < planes.Count; i++)
        {
            if (Dot(vector, planes[i]) >= 0)
                signature |= 1UL << i;
        }

        return signature;
    }

    private static ulong CreateBandKey(ulong signature, int band) =>
        CreateBandKey((byte)((signature >> (band * BandBits)) & 0xffUL), band);

    private static ulong CreateBandKey(byte bandValue, int band) =>
        ((ulong)(byte)band << 8) | bandValue;

    private readonly record struct VectorPartitionKey(
        string ModelId,
        string ModelVersion,
        string QuantizationVersion,
        int Dimension,
        VectorDocumentKind Kind)
    {
        public static VectorPartitionKey FromDocument(VectorDocument document) =>
            new(
                document.Model.ModelId,
                document.Model.ModelVersion,
                document.Model.QuantizationVersion,
                document.Model.Dimension,
                document.Kind);
    }

    private sealed record VectorSearchRecord(
        int Index,
        VectorDocument Document,
        float[] UnitVector);

    private sealed record ScoredVectorRecord(
        VectorSearchRecord Record,
        float Score);
}
