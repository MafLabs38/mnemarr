using System.Text;
using System.Text.RegularExpressions;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Conversations;

/// <summary>
/// A batch of new lines of one conversation, as a collector sends it: where the first line is in the file, and the lines. The account is the one of the
/// key that sent it: it is kept with the conversation, so that what a key wrote can be found when it is revoked.
/// </summary>
public sealed record IngestBatch(string Format, string Machine, string Application, string Session, long From, IReadOnlyList<string> Lines, string Account = "");

/// <summary>What was taken of a batch: how many lines, and the position of the next line the server wants (what the collector resumes from).</summary>
public sealed record IngestResult(long Next, int Accepted);

/// <summary>
/// Takes what a collector sends (ADR 0024): checks it, masks the secrets again (D-10), keeps the raw lines, writes the conversation as a note of
/// the Conversations source and reads it into the index at once, so that an assistant can find it right away. Sending a batch again changes
/// nothing, and a batch that starts further than the server has is not taken: the answer says where to resume.
/// </summary>
public sealed partial class ConversationIngestor
{
    /// <summary>The most lines in a batch.</summary>
    public const int MaxLines = 1000;

    /// <summary>The longest line, in characters. A line is a message and what its tools answered: a longer one is not a message.</summary>
    public const int MaxLineChars = 2 * 1024 * 1024;

    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    private readonly SourceCatalog _catalog;
    private readonly SourceLocks _locks;
    private readonly SourceIndexer _indexer;
    private readonly RawConversations _raw;
    private readonly IConversationStore _store;
    private readonly ConversationRelocator _relocator;
    private readonly TimeProvider _clock;

    public ConversationIngestor(SourceCatalog catalog, SourceLocks locks, SourceIndexer indexer, RawConversations raw, IConversationStore store, ConversationRelocator relocator, TimeProvider clock)
    {
        _relocator = relocator;
        _catalog = catalog;
        _locks = locks;
        _indexer = indexer;
        _raw = raw;
        _store = store;
        _clock = clock;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafeName();

    [GeneratedRegex(@"[^a-z0-9._-]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NotSlug();

    /// <summary>A folder name for an application: lower case, plain characters, never hidden, never empty.</summary>
    public static string Slug(string application)
    {
        var slug = NotSlug().Replace(application.Trim().ToLowerInvariant(), "-").Trim('-', '.');
        slug = slug.Length > 60 ? slug[..60].TrimEnd('-', '.') : slug;
        return slug.Length == 0 ? "unknown" : slug;
    }

    public async Task<IngestResult> IngestAsync(IngestBatch batch, CancellationToken cancellationToken)
    {
        Check(batch);
        // The secrets are looked for before the lock is taken: a pattern is not worth making every machine wait for.
        var masked = batch.Lines.Select(MaskedLine).ToList();
        var source = _catalog.EnsureConversations();
        var gate = _locks.Of(source.Id);
        if (!await gate.WaitAsync(LockWait, cancellationToken))
        {
            throw new RejectedException(Problem.Busy, "The conversations are being read: try again in a moment.", TimeSpan.FromSeconds(5));
        }

        try
        {
            return await TakeAsync(source, batch, masked, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IngestResult> TakeAsync(Source source, IngestBatch batch, IReadOnlyList<string> masked, CancellationToken cancellationToken)
    {
        var known = await FindKnownAsync(source, batch, cancellationToken);
        if (known is not null && !string.Equals(known.Format, batch.Format, StringComparison.Ordinal))
        {
            // Two assistants never share a session: lines of another format would be read by the wrong parser.
            throw new RejectedException(Problem.Conflict, "This conversation was collected in another format.");
        }

        var next = known?.Lines ?? 0;
        if (batch.From > next)
        {
            // A hole: what lies between is missing. The collector is told where to resume from, and sends again from there.
            return new IngestResult(next, 0);
        }

        var fresh = masked.Skip((int)Math.Min(next - batch.From, masked.Count)).ToList();
        if (fresh.Count == 0)
        {
            return new IngestResult(next, 0);
        }

        _raw.Write(batch.Machine, batch.Session, next, fresh);
        var total = next + fresh.Count;
        _raw.CompactIfNeeded(batch.Machine, batch.Session, total);
        var freshTurns = ConversationFormats.Turns(batch.Format, fresh);
        var now = _clock.GetUtcNow();
        var startedAt = known?.StartedAt ?? freshTurns.FirstOrDefault(turn => turn.At is not null)?.At ?? now;
        var application = known?.Application ?? Slug(batch.Application);
        var path = known?.NotePath ?? ConversationMarkdown.NotePath(batch.Machine, application, startedAt, batch.Session);
        var noteBytes = known?.NoteBytes ?? 0;
        var title = known?.Title;
        if (known is null)
        {
            _raw.WriteInfo(batch.Machine, batch.Session, new RawInfo(batch.Format, application, startedAt));
        }

        if (freshTurns.Count > 0)
        {
            // The new turns go to the end of the note, after what was last written (what a failed attempt added is cut off): the cost of a pass does
            // not grow with the conversation. A note that is not what was recorded is written again whole, from the raw lines.
            var appended = noteBytes > 0 ? NoteFiles.AppendTo(source, path, noteBytes, ConversationMarkdown.Sections(freshTurns)) : null;
            if (appended is null)
            {
                // Written whole, from the raw lines when the note is not what was recorded: with the title the conversation has, not a new first sentence.
                var turns = noteBytes > 0 ? ConversationFormats.Turns(batch.Format, _raw.Read(batch.Machine, batch.Session, total)) : freshTurns;
                title ??= ConversationMarkdown.Title(turns, batch.Session);
                noteBytes = NoteFiles.Write(source, path, ConversationMarkdown.Render(new ConversationHeading(batch.Format, batch.Machine, application, batch.Session, startedAt), turns, title, known?.Summary));
            }
            else
            {
                noteBytes = appended.Value;
            }

            await _indexer.IndexNoteAsync(source, path, cancellationToken);
        }

        _store.Save(new ConversationRecord(batch.Machine, batch.Session, application, batch.Format, total, startedAt, now, path, noteBytes, batch.Account, title, known?.TitleSource ?? TitleSources.FirstSentence, known?.Summary));
        return new IngestResult(total, fresh.Count);
    }

    /// <summary>The conversation as the server holds it, taking over one that was held under a name that is not a machine (ADR 0031) rather than making it twice.</summary>
    private async Task<ConversationRecord?> FindKnownAsync(Source source, IngestBatch batch, CancellationToken cancellationToken)
    {
        var known = _store.Find(batch.Machine, batch.Session);
        if (known is null && await _relocator.AdoptAsync(source, batch.Session, batch.Machine, cancellationToken))
        {
            known = _store.Find(batch.Machine, batch.Session);
        }

        return known;
    }

    private static string MaskedLine(string line)
    {
        try
        {
            return SecretMasker.Mask(line).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        }
        catch (RegexMatchTimeoutException)
        {
            // A line that cannot be checked is not kept: a secret must not get in because the check gave up.
            throw new RejectedException(Problem.Invalid, "A line could not be checked for secrets: it was refused.");
        }
    }

    private static void Check(IngestBatch batch)
    {
        if (!ConversationFormats.Supports(batch.Format))
        {
            throw new RejectedException(Problem.Invalid, "This format of conversation is not one Mnemarr reads.");
        }

        if (!SafeName().IsMatch(batch.Machine) || !SafeName().IsMatch(batch.Session))
        {
            throw new RejectedException(Problem.Invalid, "The machine and the session are names of letters, digits, dots, hyphens and underscores, up to 100 characters.");
        }

        if (string.IsNullOrWhiteSpace(batch.Application) || batch.Application.Length > 200)
        {
            throw new RejectedException(Problem.Invalid, "The application is a name of up to 200 characters.");
        }

        if (batch.From < 0 || batch.Lines.Count is 0 or > MaxLines || batch.Lines.Any(line => line is null || line.Length > MaxLineChars))
        {
            throw new RejectedException(Problem.Invalid, $"A batch holds 1 to {MaxLines} lines of at most {MaxLineChars} characters, from a position that is not negative.");
        }
    }
}
