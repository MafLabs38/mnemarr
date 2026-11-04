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
    private int _rewritten;
    private LinkAnswers? _answers;
    private readonly HashSet<string> _targets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _embeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notePaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _imagePaths = new(StringComparer.Ordinal);

    /// <summary>The most links and embeds of a note that are looked up: the index keeps as many, and a note can hold far more. The rest stay as written.</summary>
    public const int MaxLookups = 400;

    public WikiLinkRenderer(INoteBrowser browser, Guid sourceId, string notePath, SourceScope scope)
    {
        _browser = browser;
        _sourceId = sourceId;
        _notePath = notePath;
        _scope = scope;
    }

    /// <summary>
    /// The body with its links rewritten. In two passes over the same text: the first only collects what the links ask, then the index answers
    /// all of it in one call, and the second writes the answers: no question to the index per link.
    /// </summary>
    public string Render(string body)
    {
        MarkdownNotes.Rewrite(body, RenderToken, RenderDestination);
        _answers = _browser.Answer(_sourceId, _notePath, new LinkQuestions(_targets, _embeds, _notePaths, _imagePaths), _scope);
        _rewritten = 0;
        return MarkdownNotes.Rewrite(body, RenderToken, RenderDestination);
    }

    private string? RenderToken(WikiToken token)
    {
        if (++_rewritten > MaxLookups)
        {
            return null;
        }

        var (target, alias) = MarkdownNotes.Split(token.Inner);
        var file = MarkdownNotes.FileOf(target);
        if (token.Embedded && ImageFormats.Is(file))
        {
            if (_answers is null)
            {
                _embeds.Add(file);
                return null;
            }

            var image = _answers.Embeds[file];
            return $"![{Escape(file)}]({(image is null ? UnresolvedScheme : FileScheme + Uri.EscapeDataString(image))})";
        }

        // Anything else that is embedded is a note shown in place: it is a link here, as it is for the index.
        if (MarkdownNotes.NormalizeTarget(target) is not { } normalized)
        {
            return null;
        }

        var resolved = NotePaths.FromFolderTarget(_notePath, normalized);
        if (_answers is null)
        {
            if (resolved is not null)
            {
                _targets.Add(resolved);
            }

            return null;
        }

        var text = string.IsNullOrEmpty(alias) ? target : alias;
        return resolved is not null && _answers.Targets[resolved] is { } note
            ? $"[{Escape(text)}]({NoteScheme}{note.SourceId}/{Uri.EscapeDataString(note.Path)})"
            : $"[{Escape(text)}]({UnresolvedScheme})";
    }

    /// <summary>
    /// The destination of an ordinary Markdown link or image, rewritten when it is relative and leads to a note or an image of the source:
    /// the same addresses as for a wikilink. A web address, or a file that is neither, is left as it is.
    /// </summary>
    private string? RenderDestination(string destination)
    {
        if (NotePaths.RelativePath(_notePath, destination) is not { } path)
        {
            return null;
        }

        var isNote = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
        if ((!isNote && !ImageFormats.Is(path)) || ++_rewritten > MaxLookups)
        {
            return null;
        }

        if (_answers is null)
        {
            (isNote ? _notePaths : _imagePaths).Add(path);
            return null;
        }

        if (isNote)
        {
            return _answers.NotePaths[path] is { } note ? $"{NoteScheme}{note.SourceId}/{Uri.EscapeDataString(note.Path)}" : UnresolvedScheme;
        }

        return _answers.ImagePaths.Contains(path) ? FileScheme + Uri.EscapeDataString(path) : UnresolvedScheme;
    }

    /// <summary>The text of a link in Markdown: a bracket or a backslash in it cannot end the link early.</summary>
    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
}
