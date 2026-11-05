using System.Text;
using System.Text.RegularExpressions;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Conversations;

/// <summary>A batch of new lines of one conversation, as a collector sends it: where the first line is in the file, and the lines.</summary>
public sealed record IngestBatch(string Format, string Machine, string Application, string Session, long From, IReadOnlyList<string> Lines);

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
    private readonly TimeProvider _clock;

    public ConversationIngestor(SourceCatalog catalog, SourceLocks locks, SourceIndexer indexer, RawConversations raw, IConversationStore store, TimeProvider clock)
    {
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
        var source = _catalog.EnsureConversations();
        var gate = _locks.Of(source.Id);
        if (!await gate.WaitAsync(LockWait, cancellationToken))
        {
            throw new RejectedException(Problem.Busy, "The conversations are being read: try again in a moment.", TimeSpan.FromSeconds(5));
        }

        try
        {
            return await TakeAsync(source, batch, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IngestResult> TakeAsync(Source source, IngestBatch batch, CancellationToken cancellationToken)
    {
        var known = _store.Find(batch.Machine, batch.Session);
        var next = known?.Lines ?? 0;
        if (batch.From > next)
        {
            // A hole: what lies between is missing. The collector is told where to resume from, and sends again from there.
            return new IngestResult(next, 0);
        }

        var fresh = batch.Lines.Skip((int)(next - batch.From)).Select(MaskedLine).ToList();
        if (fresh.Count == 0)
        {
            return new IngestResult(next, 0);
        }

        _raw.Write(batch.Machine, batch.Session, next, fresh);
        var total = next + fresh.Count;
        var turns = ConversationFormats.Turns(batch.Format, _raw.Read(batch.Machine, batch.Session, total));
        var now = _clock.GetUtcNow();
        var startedAt = known?.StartedAt ?? turns.FirstOrDefault(turn => turn.At is not null)?.At ?? now;
        var application = known?.Application ?? Slug(batch.Application);
        var path = known?.NotePath ?? ConversationMarkdown.NotePath(batch.Machine, application, startedAt, batch.Session);
        if (turns.Count > 0)
        {
            Write(source, path, ConversationMarkdown.Render(batch.Format, batch.Machine, application, batch.Session, startedAt, turns));
            await _indexer.IndexNoteAsync(source, path, cancellationToken);
        }

        _store.Save(new ConversationRecord(batch.Machine, batch.Session, application, batch.Format, total, startedAt, now, path));
        return new IngestResult(total, fresh.Count);
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

    private static void Write(Source source, string relativePath, string markdown)
    {
        var full = Path.Combine(source.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, $".mnemarr-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, markdown, new UTF8Encoding(false));
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
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
