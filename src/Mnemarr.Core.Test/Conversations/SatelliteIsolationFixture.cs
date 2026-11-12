using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Conversations;

/// <summary>
/// What a satellite's conversations do not do (ADR 0041): they are never told to a session of a project (the pack selects by application, and <c>gemini</c> is not a project), a search finds them
/// or leaves them by the machine that is the first folder of their note, and whoever may not read conversations does not see them.
/// </summary>
public sealed class SatelliteIsolationFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly SourceLocks _locks = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly SqliteConversationStore _store;
    private readonly ConversationIngestor _ingestor;
    private readonly SourceCatalog _catalog;

    public SatelliteIsolationFixture()
    {
        var paths = _workspace.Paths();
        _index = new SqliteNoteIndex(_workspace.Database);
        _store = new SqliteConversationStore(_workspace.Database);
        _catalog = new SourceCatalog(new SqliteSourceStore(_workspace.Database), new SqliteAuditLog(_workspace.Database, _clock), paths, _clock);
        var indexer = new SourceIndexer(_index, paths, new ScanOutcomes(), _clock, _locks);
        var raw = new RawConversations(_workspace.Options);
        _ingestor = new ConversationIngestor(_catalog, _locks, indexer, raw, _store, new ConversationRelocator(_catalog, _locks, indexer, raw, _store, new Mnemarr.Core.Collectors.SqliteMachineStore(_workspace.Database)), _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task SendAsync(string format, string machine, string application, string session, params string[] lines) =>
        await _ingestor.IngestAsync(new IngestBatch(format, machine, application, session, 0, lines), Token);

    private static string ClaudeLine(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new { type = "user", timestamp = "2026-11-12T08:00:00Z", message = new { role = "user", content = text } });

    private static string[] WebConversation(string text) =>
    [
        System.Text.Json.JsonSerializer.Serialize(new { v = 1, meta = new { title = "An idea", source = "web", id = "x1" } }),
        System.Text.Json.JsonSerializer.Serialize(new { v = 1, role = "user", at = "2026-11-12T08:01:00Z", text }),
    ];

    [Fact]
    public async Task should_never_offer_a_conversation_of_a_satellite_to_a_session_of_a_project()
    {
        await SendAsync(ConversationFormats.ClaudeCode, "pc", "mnemarr", "project-session", ClaudeLine("work on the project"));
        await SendAsync(ConversationFormats.ChatTurns, "gemini", "gemini", "gemini-x1", WebConversation("an idea about a distributed assistant"));

        _store.RecentOfApplication("mnemarr", 10).Select(found => found.Session).Should().Equal("project-session");
        _store.RecentOfApplication("gemini", 10).Select(found => found.Session).Should().Equal("gemini-x1");
    }

    [Fact]
    public async Task should_find_the_conversations_of_a_satellite_by_their_machine_and_leave_them_out_when_another_topic_is_asked()
    {
        await SendAsync(ConversationFormats.ClaudeCode, "pc", "mnemarr", "project-session", ClaudeLine("a note about pelicans on the project"));
        await SendAsync(ConversationFormats.ChatTurns, "gemini", "gemini", "gemini-x1", WebConversation("an idea about pelicans in the lake"));

        _index.Search(new SearchRequest("pelicans", Topic: "gemini"), SourceScope.All).Select(hit => hit.Topic).Should().OnlyContain(topic => topic == "gemini").And.NotBeEmpty();
        _index.Search(new SearchRequest("pelicans", Topic: "pc"), SourceScope.All).Select(hit => hit.Topic).Should().OnlyContain(topic => topic == "pc").And.NotBeEmpty();
        _index.Search(new SearchRequest("pelicans"), SourceScope.All).Select(hit => hit.Topic).Should().BeEquivalentTo(["gemini", "pc"]);
    }

    [Fact]
    public async Task should_hide_the_conversations_of_a_satellite_from_whoever_may_not_read_conversations()
    {
        await SendAsync(ConversationFormats.ChatTurns, "gemini", "gemini", "gemini-x1", WebConversation("an idea about lighthouses"));

        _index.Search(new SearchRequest("lighthouses"), SourceScope.All).Should().NotBeEmpty();
        _index.Search(new SearchRequest("lighthouses"), SourceScope.All with { ConversationsHidden = true }).Should().BeEmpty();
    }
}
