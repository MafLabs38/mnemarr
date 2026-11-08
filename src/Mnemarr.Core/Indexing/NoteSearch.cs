using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    // The keyword list is at most this long: the list by meaning is as long, and the two are merged and cut to the limit asked.
    private const int Depth = SearchRequest.MaxLimit;

    // The nearest are asked for wider than the list: some are not the caller's to see, or are outside the filters, and the rest must still fill it.
    private const int MeaningWiderBy = 4;

    // How many questions the model reads at once, and how long a question waits for its turn: a flood of searches must not hold every thread of the host. A question that
    // does not get its turn is answered by its words.
    private const int ConcurrentQuestions = 4;
    private static readonly TimeSpan QuestionWaits = TimeSpan.FromMilliseconds(300);

    // A question is read by the model up to this many characters: the rest of a very long one is not a question, and the model's work is bounded.
    private const int MaxQuestionLength = 1000;

    // A question of several words matches, by any one of its words, notes that are not about it: the words weigh less beside the meaning, which reads the whole question.
    private const int LongQuestion = 4;
    private const double LongQuestionWordsWeight = 0.5;

    private readonly INoteIndex _index;
    private readonly IChunkHits _chunks;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IVectorStore _vectors;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<NoteSearch> _logger;
    private readonly SemaphoreSlim _turns = new(ConcurrentQuestions);

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

    /// <summary>The best chunks first, among the sources the caller may touch. The score is an order and nothing more: its scale is not the same when the search by meaning answers and when it does not.</summary>
    public IReadOnlyList<SearchHit> Search(SearchRequest request, SourceScope scope)
    {
        var limit = Math.Clamp(request.Limit, 1, 50);
        var keywords = _index.Search(request with { Limit = Depth }, scope);
        var meaning = ByMeaning(request, scope);
        if (meaning.Count == 0)
        {
            return [.. keywords.Take(limit)];
        }

        var wordsWeight = FtsQuery.Meaningful(request.Query) >= LongQuestion ? LongQuestionWordsWeight : 1.0;
        return Merge(keywords, meaning, limit, wordsWeight);
    }

    private static List<SearchHit> Merge(IReadOnlyList<SearchHit> keywords, IReadOnlyList<SearchHit> meaning, int limit, double wordsWeight)
    {
        var scores = new Dictionary<long, double>();
        var hits = new Dictionary<long, SearchHit>();
        var both = new HashSet<long>();
        for (var rank = 0; rank < keywords.Count; rank++)
        {
            scores[keywords[rank].ChunkId] = wordsWeight / (RankConstant + rank + 1);
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

    private List<SearchHit> ByMeaning(SearchRequest request, SourceScope scope)
    {
        // The model is loaded by the indexer, never on a request: until it is, the answer is by words.
        if (!_options.Wanted || string.IsNullOrWhiteSpace(request.Query) || _embeddings.Known()?.State != EmbeddingState.Ready)
        {
            return [];
        }

        if (!_turns.Wait(QuestionWaits))
        {
            return [];
        }

        try
        {
            if (!_vectors.TryPrepare(out _))
            {
                return [];
            }

            var question = request.Query.Length > MaxQuestionLength ? request.Query[..MaxQuestionLength] : request.Query;
            var query = _embeddings.EmbedQuery(question);
            var maxDistance = _options.MaxDistance ?? _embeddings.DefaultMaxDistance;
            // The nearest are asked for wider than the answer: some are not the caller's to see, or are outside the filters, and the rest must still fill the list.
            var near = _vectors.Nearest(_embeddings.ModelId, _embeddings.Dimension, query, Depth * MeaningWiderBy).Where(match => match.Distance <= maxDistance).ToList();
            var found = _chunks.Chunks(near.Select(match => match.ChunkId).ToList(), request, scope).ToDictionary(hit => hit.ChunkId);
            return [.. near.Where(match => found.ContainsKey(match.ChunkId)).Select(match => found[match.ChunkId]).Take(Depth)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The words still answer: whatever fails in an optional feature (a model, a library, a text that the tokenizer cannot take) is a line in the log, not an error for
            // the person who asked.
            LogMeaningFailed(ex);
            return [];
        }
        finally
        {
            _turns.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The search by meaning failed; the answer is by keywords only.")]
    private partial void LogMeaningFailed(Exception exception);
}
