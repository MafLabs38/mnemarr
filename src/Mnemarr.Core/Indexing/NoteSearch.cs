using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Embeddings;

namespace Mnemarr.Core.Indexing;

/// <summary>
/// The search of the notes (F-SEA-01, ADR 0034): the keyword search of the index, and, when the model and the vectors are ready, the search by meaning, merged by
/// reciprocal rank: a chunk is as good as its places in the two lists, so that neither list's scores have to be compared. When the search by meaning is off, not ready
/// or failing, the answer is the keyword one, and the question is not lost.
/// </summary>
public sealed partial class NoteSearch
{
    private const int RankConstant = 60;
    private const int MinimumDepth = 50;

    private readonly INoteIndex _index;
    private readonly IChunkHits _chunks;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IVectorStore _vectors;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<NoteSearch> _logger;

    public NoteSearch(INoteIndex index, IChunkHits chunks, IEmbeddingProvider embeddings, IVectorStore vectors, IOptions<EmbeddingOptions> options, ILogger<NoteSearch> logger)
    {
        _index = index;
        _chunks = chunks;
        _embeddings = embeddings;
        _vectors = vectors;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>What is indexed of a source.</summary>
    public IndexCounts Counts(Guid sourceId) => _index.Counts(sourceId);

    /// <summary>The best chunks first, among the sources the caller may touch.</summary>
    public IReadOnlyList<SearchHit> Search(SearchRequest request, SourceScope scope)
    {
        var limit = Math.Clamp(request.Limit, 1, 50);
        var depth = Math.Max(limit * 3, MinimumDepth);
        var keywords = _index.Search(request with { Limit = depth }, scope);
        var meaning = ByMeaning(request, scope, depth);
        return meaning.Count == 0 ? [.. keywords.Take(limit)] : Merge(keywords, meaning, limit);
    }

    private static List<SearchHit> Merge(IReadOnlyList<SearchHit> keywords, IReadOnlyList<SearchHit> meaning, int limit)
    {
        var scores = new Dictionary<long, double>();
        var hits = new Dictionary<long, SearchHit>();
        var both = new HashSet<long>();
        for (var rank = 0; rank < keywords.Count; rank++)
        {
            scores[keywords[rank].ChunkId] = 1.0 / (RankConstant + rank + 1);
            hits[keywords[rank].ChunkId] = keywords[rank];
        }

        for (var rank = 0; rank < meaning.Count; rank++)
        {
            var id = meaning[rank].ChunkId;
            scores[id] = scores.GetValueOrDefault(id) + (1.0 / (RankConstant + rank + 1));
            if (!hits.TryAdd(id, meaning[rank]))
            {
                both.Add(id);
            }
        }

        return
        [
            .. scores.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(limit).Select(pair =>
                hits[pair.Key] with { Score = pair.Value, Match = both.Contains(pair.Key) ? SearchMatch.Both : hits[pair.Key].Match }),
        ];
    }

    private List<SearchHit> ByMeaning(SearchRequest request, SourceScope scope, int depth)
    {
        if (!_options.Wanted || string.IsNullOrWhiteSpace(request.Query) || _embeddings.Status().State != EmbeddingState.Ready || !_vectors.TryPrepare(out _))
        {
            return [];
        }

        try
        {
            var query = _embeddings.EmbedQuery(request.Query);
            // The nearest are asked for wider than the answer: some are not the caller's to see, or are outside the filters, and the rest must still fill the list.
            var near = _vectors.Nearest(_embeddings.ModelId, _embeddings.Dimension, query, depth * 4).Where(match => match.Distance <= _options.MaxDistance).ToList();
            var found = _chunks.Chunks(near.Select(match => match.ChunkId).ToList(), request, scope, _embeddings.ModelId).ToDictionary(hit => hit.ChunkId);
            return [.. near.Where(match => found.ContainsKey(match.ChunkId)).Select(match => found[match.ChunkId]).Take(depth)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException or IOException)
        {
            // The words still answer: a model or a library that fails is a reason on the health page and in the log, not an error for the person who asked.
            LogMeaningFailed(ex);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The search by meaning failed; the answer is by keywords only.")]
    private partial void LogMeaningFailed(Exception exception);
}
