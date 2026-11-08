using System.Globalization;
using System.Text;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Consolidation;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Notes;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Context;

/// <summary>What an assistant is told when it opens a session in an application: the text, and how many notes and conversations it draws on.</summary>
public sealed record ContextPackResult(string Text, int Notes, int Conversations);

/// <summary>
/// The context of a new session (D-12, ADR 0037): the memory knows what happened in an application, and an assistant that opens a session there is told so without having to ask. The notes of
/// the memory vault in the folder of the application (consolidated, so short), the most recently updated first and not the superseded ones; then the titles of the latest conversations held
/// there, when the caller may see conversations; then how to dig deeper. All within a budget of characters, because it is read at every start.
/// </summary>
public sealed class ContextPackBuilder
{
    public const int DefaultBudget = 6000;
    public const int MinBudget = 500;
    public const int MaxBudget = 20_000;

    private const int MaxNotesRead = 40;
    private const int MaxNoteChars = 1500;
    private const int MaxConversations = 5;

    private readonly SourceCatalog _catalog;
    private readonly NoteReader _notes;
    private readonly IConversationStore _conversations;

    public ContextPackBuilder(SourceCatalog catalog, NoteReader notes, IConversationStore conversations)
    {
        _catalog = catalog;
        _notes = notes;
        _conversations = conversations;
    }

    /// <summary>The context for an application, within <paramref name="budget"/> characters; empty text when the memory knows nothing of it.</summary>
    public ContextPackResult Build(string application, int budget, SourceScope scope)
    {
        budget = Math.Clamp(budget, MinBudget, MaxBudget) - Footer.Length;
        var (notesText, notes) = NotesSection(ConsolidationPrompt.Slug(application), budget, scope);
        var (conversationsText, conversations) = scope.ConversationsHidden ? (string.Empty, 0) : ConversationsSection(application, budget - notesText.Length);
        if (notes + conversations == 0)
        {
            return new ContextPackResult(string.Empty, 0, 0);
        }

        return new ContextPackResult($"Mnemarr, the long-term memory, knows this about the application \"{application}\" (consolidated notes, then the latest conversations):\n\n{notesText}{conversationsText}{Footer}", notes, conversations);
    }

    private (string Text, int Count) NotesSection(string slug, int budget, SourceScope scope)
    {
        var memory = _catalog.List(scope).FirstOrDefault(source => source.Kind == SourceKind.Memory);
        if (memory is null)
        {
            return (string.Empty, 0);
        }

        var entries = _notes.List(scope, memory.Id, tag: null).Notes.Where(note => note.Path.StartsWith(slug + "/", StringComparison.OrdinalIgnoreCase)).Take(MaxNotesRead);
        var ranked = entries.Select(entry => Parsed(entry, scope)).OfType<ParsedNote>().Where(note => note.Active).OrderByDescending(note => note.Updated);
        var text = new StringBuilder();
        var count = 0;
        foreach (var note in ranked)
        {
            var piece = $"### {note.Title} ({note.Path}, updated {note.Updated:yyyy-MM-dd})\n{note.Body}\n\n";
            if (text.Length + piece.Length > budget)
            {
                break;
            }

            text.Append(piece);
            count++;
        }

        return (text.ToString(), count);
    }

    private (string Text, int Count) ConversationsSection(string application, int room)
    {
        var lines = new StringBuilder();
        var count = 0;
        foreach (var record in _conversations.RecentOfApplication(application, MaxConversations))
        {
            var title = string.IsNullOrWhiteSpace(record.Title) ? record.NotePath : record.Title;
            var summary = record.Summary is { Length: > 0 } ? " — " + OneLine(record.Summary, 200) : string.Empty;
            var line = string.Create(CultureInfo.InvariantCulture, $"- {record.UpdatedAt:yyyy-MM-dd} {record.Machine}: {OneLine(title, 140)}{summary} (`{record.NotePath}`)\n");
            if (lines.Length + line.Length + Heading.Length > room)
            {
                break;
            }

            lines.Append(line);
            count++;
        }

        return count == 0 ? (string.Empty, 0) : (Heading + lines + "\n", count);
    }

    private const string Heading = "### Latest conversations in this application\n";

    private const string Footer = "More: the Mnemarr MCP tools. search (source of kind Memory first, then Conversations for a detail), read_note (a window: offset, tail, around). A note may be out of date: it carries its date and the conversations it came from.\n";

    private ParsedNote? Parsed(NoteEntry entry, SourceScope scope)
    {
        try
        {
            var raw = _notes.Read(entry.SourceId, entry.Path, scope).Raw;
            var (front, body) = SplitFrontMatter(raw);
            var updated = front.TryGetValue("updated", out var date) && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : DateTime.MinValue;
            var active = !(front.TryGetValue("status", out var status) && string.Equals(status, "superseded", StringComparison.OrdinalIgnoreCase));
            var trimmed = WithoutTitleLine(body.Trim());
            return new ParsedNote(entry.Path, string.IsNullOrWhiteSpace(entry.Title) ? entry.Path : entry.Title, updated, active, trimmed.Length > MaxNoteChars ? trimmed[..MaxNoteChars] + "\n[...]" : trimmed);
        }
        catch (RejectedException)
        {
            return null; // a note that went away between the listing and the reading
        }
    }

    /// <summary>The front matter between two lines of <c>---</c> at the top, read as simple <c>key: value</c> lines, and the rest.</summary>
    internal static (Dictionary<string, string> Front, string Body) SplitFrontMatter(string raw)
    {
        var front = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "---")
        {
            return (front, raw);
        }

        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0)
        {
            return (front, raw);
        }

        foreach (var line in lines[1..end])
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && !char.IsWhiteSpace(line[0]))
            {
                front[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return (front, string.Join('\n', lines[(end + 1)..]));
    }

    /// <summary>The first line of a note is its title (<c># ...</c>), and the heading of the section already says it.</summary>
    private static string WithoutTitleLine(string body)
    {
        if (!body.StartsWith("# ", StringComparison.Ordinal))
        {
            return body;
        }

        var newline = body.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? string.Empty : body[(newline + 1)..].TrimStart();
    }

    private static string OneLine(string value, int max)
    {
        var line = string.Join(' ', value.Split(['\n', '\r', '`'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= max ? line : line[..max];
    }

    private sealed record ParsedNote(string Path, string Title, DateTime Updated, bool Active, string Body);
}
