using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Test.Indexing;

/// <summary>
/// A note that is indexed again keeps the rows of the chunks that did not change: their identifiers, their words and, above all, the vectors that hang on them. Only what is
/// new or different is written, so that a conversation that grows costs what was added and not its whole size.
/// </summary>
public sealed class IncrementalIndexFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly SqliteSourceStore _sources;
    private readonly SourceIndexer _subject;
    private readonly string _folder;
    private readonly Source _source;

    public IncrementalIndexFixture()
    {
        _index = new SqliteNoteIndex(_workspace.Database);
        _sources = new SqliteSourceStore(_workspace.Database);
        _subject = new SourceIndexer(_index, _workspace.Paths(), new ScanOutcomes(), _clock);
        _folder = _workspace.Folder("chats");
        _source = new Source(Guid.NewGuid(), "chats", SourceKind.Knowledge, SourceMode.ReadWrite, _folder, _clock.GetUtcNow());
        _sources.Add(_source);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Section(string heading, string text) => $"## {heading}\n\n{text}\n\n";

    private Dictionary<string, long> ChunkIds(string path)
    {
        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.heading || '|' || c.text, c.id FROM chunks c JOIN notes n ON n.id = c.note_id WHERE n.path = $path";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
        {
            ids[reader.GetString(0)] = reader.GetInt64(1);
        }

        return ids;
    }

    private bool _otherIndexed;

    private async Task WriteAndIndexAsync(string path, string text)
    {
        TestWorkspace.Write(_folder, path, text);
        await _subject.IndexNoteAsync(_source, path, Token);
        if (!_otherIndexed)
        {
            // A note indexed after it takes the next identifiers: if the row ids of the first one were free again, a table that is emptied and filled again would give them back by luck.
            _otherIndexed = true;
            await WriteAndIndexAsync("other.md", "# Other\n\n" + Section("Elsewhere", "an unrelated passage"));
        }
    }

    [Fact]
    public async Task should_keep_the_chunks_that_did_not_change_when_a_note_grows()
    {
        var head = "# Chat\n\n" + Section("User", "first question about redis") + Section("Assistant", "first answer about redis");
        await WriteAndIndexAsync("chat.md", head);
        var before = ChunkIds("chat.md");
        before.Should().HaveCountGreaterThan(1);

        await WriteAndIndexAsync("chat.md", head + Section("User", "second question about postgres"));

        var after = ChunkIds("chat.md");
        after.Should().HaveCount(before.Count + 1);
        foreach (var (key, id) in before)
        {
            after[key].Should().Be(id, "a chunk whose text did not change keeps its row, and the vector that hangs on it");
        }
    }

    [Fact]
    public async Task should_replace_only_the_chunk_that_changed_and_forget_its_old_words()
    {
        var path = "chat.md";
        await WriteAndIndexAsync(path, "# Chat\n\n" + Section("One", "alpha redis") + Section("Two", "beta nginx") + Section("Three", "gamma kafka"));
        var before = ChunkIds(path);

        await WriteAndIndexAsync(path, "# Chat\n\n" + Section("One", "alpha redis") + Section("Two", "beta postgres") + Section("Three", "gamma kafka"));

        var after = ChunkIds(path);
        after.Should().HaveCount(before.Count);
        after.Keys.Where(key => before.TryGetValue(key, out var id) && id == after[key]).Should().HaveCount(before.Count - 1);
        _index.Search(new SearchRequest("nginx"), SourceScope.All).Should().BeEmpty();
        _index.Search(new SearchRequest("postgres"), SourceScope.All).Should().ContainSingle();
        _index.Search(new SearchRequest("redis"), SourceScope.All).Should().ContainSingle();
    }

    [Fact]
    public async Task should_keep_the_chunks_when_a_section_is_added_before_them()
    {
        var path = "chat.md";
        var tail = Section("Two", "beta nginx") + Section("Three", "gamma kafka");
        await WriteAndIndexAsync(path, "# Chat\n\n" + tail);
        var before = ChunkIds(path);

        await WriteAndIndexAsync(path, "# Chat\n\n" + Section("One", "alpha redis") + tail);

        var after = ChunkIds(path);
        foreach (var (key, id) in before)
        {
            after[key].Should().Be(id);
        }

        _index.Search(new SearchRequest("redis"), SourceScope.All).Should().ContainSingle();
    }

    [Fact]
    public async Task should_keep_the_order_of_the_chunks_and_the_counts_right_after_an_incremental_write()
    {
        var path = "chat.md";
        await WriteAndIndexAsync(path, "# Chat\n\n" + Section("Two", "beta nginx"));
        await WriteAndIndexAsync(path, "# Chat\n\n" + Section("One", "alpha redis") + Section("Two", "beta nginx"));

        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.ordinal, c.heading FROM chunks c JOIN notes n ON n.id = c.note_id WHERE n.path = $path ORDER BY c.ordinal";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        var rows = new List<(int Ordinal, string Heading)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        rows.Select(row => row.Ordinal).Should().Equal(Enumerable.Range(0, rows.Count));
        var one = rows.FindIndex(row => row.Heading.Contains("One", StringComparison.Ordinal));
        var two = rows.FindIndex(row => row.Heading.Contains("Two", StringComparison.Ordinal));
        one.Should().BeGreaterThanOrEqualTo(0);
        two.Should().BeGreaterThan(one, "the section added before the other is read first");
        _index.Counts(_source.Id).Chunks.Should().Be(rows.Count + ChunkIds("other.md").Count);
    }

    [Fact]
    public async Task should_pair_equal_chunks_one_by_one_when_a_conversation_grows_and_shrinks()
    {
        var path = "chat.md";
        string Same(int count) => "# Chat\n\n" + string.Concat(Enumerable.Range(0, count).Select(_ => Section("Person", "ok")));
        await WriteAndIndexAsync(path, Same(2));
        var two = ChunkIdList(path);

        await WriteAndIndexAsync(path, Same(3));
        var three = ChunkIdList(path);
        await WriteAndIndexAsync(path, Same(2));
        var back = ChunkIdList(path);

        three.Should().HaveCount(two.Count + 1);
        three.Should().Contain(two, "the chunks that were there keep their rows");
        back.Should().HaveCount(two.Count);
        two.Should().Contain(back, "and the ones that stay after a cut are the ones that were there");
    }

    private List<long> ChunkIdList(string path)
    {
        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.id FROM chunks c JOIN notes n ON n.id = c.note_id WHERE n.path = $path ORDER BY c.ordinal";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    [Fact]
    public async Task should_replace_the_links_and_tags_of_a_note_that_was_indexed_again()
    {
        var path = "chat.md";
        await WriteAndIndexAsync(path, "# Chat\n\nsee [[Old]] #old\n");

        await WriteAndIndexAsync(path, "# Chat\n\nsee [[New]] #fresh\n");

        using var connection = _workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT group_concat(target) FROM note_links), (SELECT group_concat(tag) FROM note_tags)";
        using var reader = command.ExecuteReader();
        reader.Read();
        reader.GetString(0).Should().Be("new");
        reader.GetString(1).Should().Be("fresh");
    }
}
