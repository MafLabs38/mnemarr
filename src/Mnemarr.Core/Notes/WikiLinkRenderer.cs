using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Notes;

/// <summary>
/// Rewrites the <c>[[wikilinks]]</c> and <c>![[embeds]]</c> of one note into plain Markdown links and images that lead to what the caller may
/// read, with the same reading of a link as the index (<see cref="MarkdownNotes"/>), so that the server is the only place that decides what
/// a link means. A link to a note is <c>mnemarr-note:{source}/{path}</c>, an image is <c>mnemarr-file:{path}</c> (in the source of the note), and
/// what leads nowhere that the caller may read is <c>mnemarr-unresolved:</c>: the interface turns the first two into addresses and the last into plain text.
/// </summary>
internal sealed class WikiLinkRenderer
{
    public const string NoteScheme = "mnemarr-note:";
    public const string FileScheme = "mnemarr-file:";
    public const string UnresolvedScheme = "mnemarr-unresolved:";

    private readonly INoteBrowser _browser;
    private readonly Guid _sourceId;
    private readonly string _notePath;
    private readonly SourceScope _scope;
    private readonly Dictionary<string, NoteRef?> _resolved = new(StringComparer.Ordinal);

    public WikiLinkRenderer(INoteBrowser browser, Guid sourceId, string notePath, SourceScope scope)
    {
        _browser = browser;
        _sourceId = sourceId;
        _notePath = notePath;
        _scope = scope;
    }

    public string Render(string body) => MarkdownNotes.Rewrite(body, RenderToken);

    private string? RenderToken(WikiToken token)
    {
        var (target, alias) = MarkdownNotes.Split(token.Inner);
        var file = MarkdownNotes.FileOf(target);
        if (token.Embedded && ImageFormats.Is(file))
        {
            var image = _browser.FindImage(_sourceId, _notePath, file);
            return $"![{Escape(file)}]({(image is null ? UnresolvedScheme : FileScheme + Uri.EscapeDataString(image))})";
        }

        // Anything else that is embedded is a note shown in place: it is a link here, as it is for the index.
        if (MarkdownNotes.NormalizeTarget(target) is not { } normalized)
        {
            return null;
        }

        var text = string.IsNullOrEmpty(alias) ? target : alias;
        return LeadsTo(normalized) is { } note
            ? $"[{Escape(text)}]({NoteScheme}{note.SourceId}/{Uri.EscapeDataString(note.Path)})"
            : $"[{Escape(text)}]({UnresolvedScheme})";
    }

    private NoteRef? LeadsTo(string normalized)
    {
        if (!_resolved.TryGetValue(normalized, out var note))
        {
            note = MarkdownNotes.FromFolder(_notePath, normalized) is { } target ? _browser.Resolve(_sourceId, target, _scope) : null;
            _resolved[normalized] = note;
        }

        return note;
    }

    /// <summary>The text of a link in Markdown: a bracket or a backslash in it cannot end the link early.</summary>
    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
}
