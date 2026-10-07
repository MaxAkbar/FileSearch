using System.Runtime.CompilerServices;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed class SemanticCandidateProvider : IRoutedCandidateProvider
{
    private const int MaxSemanticFiles = SemanticSearchOptions.DefaultMaximumResults;
    private const int MaxSemanticMatches = 50;

    private readonly ITextEmbedder _embedder;
    private readonly IVectorIndex _vectorIndex;
    private readonly IContentUnitReader _contentUnits;
    private readonly ISemanticIndexStatusService? _indexStatus;

    public SemanticCandidateProvider(
        ITextEmbedder embedder,
        IVectorIndex vectorIndex,
        IContentUnitReader contentUnits,
        ISemanticIndexStatusService? indexStatus = null)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
        _contentUnits = contentUnits ?? throw new ArgumentNullException(nameof(contentUnits));
        _indexStatus = indexStatus;
    }

    public CandidateProviderKind Provider => CandidateProviderKind.Semantic;

    public CandidateProviderRoute Route => CandidateProviderRoute.Indexed;

    public async Task<CandidateProviderAvailability> GetAvailabilityAsync(
        SearchPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (!plan.HasEnabledProvider(Provider))
            return CandidateProviderAvailability.Unavailable("Provider is not enabled by the search plan.");

        if (GetSemanticText(plan.Request.Expression) is null)
            return CandidateProviderAvailability.Unavailable("Provider does not support this query.");

        if (!plan.Request.UseIndex)
            return CandidateProviderAvailability.Unavailable("Semantic search requires the index.");

        var availability = await _embedder.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        return availability.IsAvailable
            ? CandidateProviderAvailability.Available
            : CandidateProviderAvailability.Unavailable(availability.Message);
    }

    public async IAsyncEnumerable<SearchCandidate> FindAsync(
        SearchPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var availability = await GetAvailabilityAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
            yield break;

        var semanticText = GetSemanticText(plan.Request.Expression);
        if (semanticText is null)
            yield break;

        var embedding = await _embedder.EmbedAsync(semanticText, TextEmbeddingInputKind.Query, cancellationToken)
            .ConfigureAwait(false);
        var matches = await SearchTwoLevelAsync(embedding, plan.Request.Roots, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            if (plan.Request.Status is not null)
                plan.Request.Status(await GetEmptyIndexMessageAsync(plan.Request.Roots, cancellationToken).ConfigureAwait(false));
            yield break;
        }

        var options = plan.Request.SemanticOptions;
        var minimumScore = options is null ? 0 : SemanticSearchOptions.NormalizeMinimumScore(options.MinimumScore);
        if (!matches.Any(match => match.Score >= minimumScore))
        {
            plan.Request.Status?.Invoke(FormattableString.Invariant(
                $"No semantic results met the minimum score of {minimumScore:0.00}. Lower Minimum score or use a more specific phrase, then search again."));
            yield break;
        }

        var maximumResults = options is null ? MaxSemanticFiles : SemanticSearchOptions.NormalizeMaximumResults(options.MaximumResults);
        var returnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches.OrderByDescending(match => match.Score).ThenBy(match => match.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match.Score < minimumScore)
                continue;
            var candidate = await CreateCandidateAsync(match, cancellationToken).ConfigureAwait(false);
            if (candidate is null || (options is not null && !returnedPaths.Add(candidate.Path)))
                continue;

            yield return candidate;
            if (options is not null && returnedPaths.Count >= maximumResults)
                yield break;
        }
    }

    private async Task<string> GetEmptyIndexMessageAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        if (_indexStatus is not null)
        {
            foreach (var root in roots)
            {
                try
                {
                    var status = await _indexStatus.GetRootStatusAsync(root, cancellationToken).ConfigureAwait(false);
                    if (status.VectorCount == 0)
                        return $"Smart Search: {status.Message} Build the content index, then use Rebuild Smart Search for the selected location.";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return $"Smart Search could not read the content index. Check the selected location's index status and rebuild the content index before rebuilding Smart Search. {ex.Message}";
                }
            }
        }

        return "No Smart Search vectors matched this folder and model. Use Rebuild Smart Search for the selected indexed location.";
    }

    private async Task<SearchCandidate?> CreateCandidateAsync(
        VectorMatch match,
        CancellationToken cancellationToken)
    {
        var path = await _contentUnits.GetFilePathAsync(match.FileId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        ContentUnit? unit = null;
        var contentUnitId = match.ContentUnitIds.Count > 0 ? match.ContentUnitIds[0] : 0;
        if (contentUnitId > 0)
            unit = await _contentUnits.GetContentUnitAsync(contentUnitId, cancellationToken).ConfigureAwait(false);

        var locator = match.Locator ?? unit?.Locator;
        var lineNumber = locator?.StartLine ?? unit?.Locator.StartLine;
        var displayText = string.IsNullOrWhiteSpace(unit?.Text)
            ? "Semantic match"
            : unit.Text;

        return new SearchCandidate(
            Provider,
            "semantic-vector",
            path,
            displayText,
            match.Score,
            HitKind.Content,
            lineNumber,
            fileId: match.FileId,
            contentUnitId: contentUnitId > 0 ? contentUnitId : null,
            route: HitRoute.Indexed,
            locator: locator,
            explanations: new[]
            {
                new SearchResultExplanation(
                    "semantic-vector",
                    match.Kind == VectorDocumentKind.ContentChunk
                        ? "Candidate came from local two-stage vector similarity search."
                        : "Candidate came from local vector similarity search.",
                    Provider,
                    match.Score),
            });
    }

    private async Task<IReadOnlyList<VectorMatch>> SearchTwoLevelAsync(
        TextEmbedding embedding,
        IReadOnlyCollection<string> roots,
        CancellationToken cancellationToken)
    {
        var fileMatches = await _vectorIndex
            .SearchAsync(
                embedding.Vector,
                MaxSemanticFiles,
                cancellationToken,
                embedding.Model,
                VectorDocumentKind.File,
                fileIds: null,
                roots)
            .ConfigureAwait(false);
        if (fileMatches.Count > 0)
        {
            var fileIds = fileMatches
                .Select(match => match.FileId)
                .Distinct()
                .ToArray();
            var chunkMatches = await _vectorIndex
                .SearchAsync(
                    embedding.Vector,
                    MaxSemanticMatches,
                    cancellationToken,
                    embedding.Model,
                    VectorDocumentKind.ContentChunk,
                    fileIds,
                    roots)
                .ConfigureAwait(false);
            if (chunkMatches.Count > 0)
                return BlendFileAndChunkScores(fileMatches, chunkMatches);
        }

        return await _vectorIndex
            .SearchAsync(
                embedding.Vector,
                MaxSemanticMatches,
                cancellationToken,
                embedding.Model,
                VectorDocumentKind.ContentChunk,
                fileIds: null,
                roots)
            .ConfigureAwait(false);
    }

    private static VectorMatch[] BlendFileAndChunkScores(
        IReadOnlyCollection<VectorMatch> fileMatches,
        IReadOnlyCollection<VectorMatch> chunkMatches)
    {
        var fileScores = fileMatches
            .GroupBy(match => match.FileId)
            .ToDictionary(
                group => group.Key,
                group => group.Max(match => match.Score));

        return chunkMatches
            .Select(match =>
            {
                var fileScore = fileScores.TryGetValue(match.FileId, out var score) ? score : 0;
                var blendedScore = (match.Score * 0.8f) + (fileScore * 0.2f);
                return match with { Score = blendedScore };
            })
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Id, StringComparer.Ordinal)
            .Take(MaxSemanticMatches)
            .ToArray();
    }

    private static string? GetSemanticText(Query expression)
    {
        if (expression is not UnifiedQuery unified || unified.Filters.SemanticTerms.Count == 0)
            return null;

        var text = string.Join(" ", unified.Filters.SemanticTerms.Where(term => !string.IsNullOrWhiteSpace(term)));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
