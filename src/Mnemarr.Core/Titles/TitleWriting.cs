using Microsoft.Extensions.Options;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Titles;

/// <summary>The last time the model could not be asked, and why (never what was sent or answered): a provider that is down must show, not fail in silence.</summary>
public sealed record TitleFailure(string Message, DateTimeOffset At);

/// <summary>
/// Has a model write the titles and the summaries of conversations (ADR 0028): in the background for those that have only a first sentence and have
/// been left alone for a while, a few at a time so that the cost of a pass is bounded, and on request for one conversation. Does nothing unless it is
/// turned on.
/// </summary>
public sealed class TitleWriting
{
    private readonly ConversationTitles _titles;
    private readonly IConversationStore _store;
    private readonly RawConversations _raw;
    private readonly ITitleWriter _writer;
    private readonly IOptions<TitleOptions> _options;
    private readonly TimeProvider _clock;
    private TitleFailure? _lastFailure;

    public TitleWriting(ConversationTitles titles, IConversationStore store, RawConversations raw, ITitleWriter writer, IOptions<TitleOptions> options, TimeProvider clock)
    {
        _titles = titles;
        _store = store;
        _raw = raw;
        _writer = writer;
        _options = options;
        _clock = clock;
    }

    public bool Enabled => _options.Value.Enabled;

    /// <summary>Why the last call to the model failed, until a call works again; null when none failed.</summary>
    public TitleFailure? LastFailure => Volatile.Read(ref _lastFailure);

    /// <summary>One pass: the conversations that need a title, a few of them. Returns how many were titled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return 0;
        }

        var options = _options.Value;
        var titled = 0;
        foreach (var record in _store.NeedingTitle(_clock.GetUtcNow() - TimeSpan.FromMinutes(options.IdleMinutes), options.MaxTries, options.PerPass))
        {
            try
            {
                if (await WriteOneAsync(record, replacePerson: false, cancellationToken) is not null)
                {
                    titled++;
                }
            }
            catch (TitleWriterException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Failed(ex);
                _store.CountTry(record.Machine, record.Session);
            }
        }

        return titled;
    }

    /// <summary>Writes the title of one conversation now, on request, whatever its title was (a person's included: asking is what the person does).</summary>
    public async Task<ConversationRecord> RewriteAsync(string notePath, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            throw new RejectedException(Problem.Invalid, "Titles written by a model are not turned on.");
        }

        var record = _store.FindByNote(notePath) ?? throw new RejectedException(Problem.NotFound, "There is no such conversation.");
        try
        {
            // A person's title is replaced only because the person asked, and only once the model has answered: nothing is touched before.
            return await WriteOneAsync(record, replacePerson: true, cancellationToken)
                ?? throw new RejectedException(Problem.Invalid, "The model gave no usable title for this conversation.");
        }
        catch (TitleWriterException ex)
        {
            Failed(ex);
            throw new RejectedException(Problem.Invalid, ex.Message);
        }
    }

    private async Task<ConversationRecord?> WriteOneAsync(ConversationRecord record, bool replacePerson, CancellationToken cancellationToken)
    {
        var turns = ConversationFormats.Turns(record.Format, _raw.Read(record.Machine, record.Session, record.Lines));
        if (turns.Count < 2)
        {
            // Nothing to say of a conversation that is one line long.
            _store.CountTry(record.Machine, record.Session);
            return null;
        }

        var written = await _writer.WriteAsync(TitlePrompt.Transcript(turns, _options.Value.MaxChars), cancellationToken);
        Volatile.Write(ref _lastFailure, null);
        if (written is null)
        {
            _store.CountTry(record.Machine, record.Session);
            return null;
        }

        return await _titles.SetAsync(record.Machine, record.Session, written.Title, TitleSources.Summary, written.Summary, cancellationToken, replacePerson);
    }

    private void Failed(TitleWriterException failure) => Volatile.Write(ref _lastFailure, new TitleFailure(failure.Message, _clock.GetUtcNow()));
}
