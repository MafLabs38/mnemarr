using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The size limit of a note, from the configuration of the host to the pass report and the source (ADR 0043): a note above it is counted and kept, never removed.</summary>
public sealed class LongNotesApiFixture : IClassFixture<SmallNotesFactory>, IAsyncLifetime
{
    private readonly SmallNotesFactory _factory;
    private Callers _callers = null!;

    public LongNotesApiFixture(SmallNotesFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _callers = await Callers.ForAsync(_factory);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task should_count_a_note_above_the_configured_limit_in_the_report_and_on_the_source_and_keep_what_was_indexed()
    {
        var name = Callers.Unique("limit");
        var folder = Path.Combine(_factory.DataFolder, "sources", name);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "talk.md"), "# Talk\nThe word is zymurgy.\n", Token);
        var created = await _callers["Operator"].PostAsJsonAsync("/api/v1/sources", new { name, kind = "Knowledge", mode = "ReadOnly", path = folder }, Token);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        await _callers["Operator"].PostAsync($"/api/v1/sources/{id}/index", null, Token);

        await File.AppendAllTextAsync(Path.Combine(folder, "talk.md"), new string('x', 1024 * 1024 + 10), Token);
        var report = await (await _callers["Operator"].PostAsync($"/api/v1/sources/{id}/index", null, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        var listed = (await _callers["Operator"].GetFromJsonAsync<JsonElement>("/api/v1/sources", Token)).EnumerateArray().Single(source => source.GetProperty("id").GetGuid() == id);
        var hits = await _callers["Reader"].GetFromJsonAsync<JsonElement>($"/api/v1/search?q=zymurgy&source={id}", Token);

        report.GetProperty("tooLarge").GetInt32().Should().Be(1);
        report.GetProperty("removed").GetInt32().Should().Be(0);
        listed.GetProperty("notesTooLarge").GetInt32().Should().Be(1);
        listed.GetProperty("notes").GetInt32().Should().Be(1, "the note is still there");
        hits.GetArrayLength().Should().Be(1, "and still found by what was indexed of it");
    }
}
