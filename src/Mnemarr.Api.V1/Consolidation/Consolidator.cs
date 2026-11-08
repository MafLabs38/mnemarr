using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Consolidation;
using Mnemarr.Core.Sources;

namespace Mnemarr.Api.V1.Consolidation;

/// <summary>Where the consolidation stands, as the page and the health tell it. <paramref name="State"/> is a code: <c>off</c>, <c>no-runner</c>, <c>no-memory-vault</c>, <c>memory-vault-read-only</c>, <c>running</c> or <c>ready</c>.</summary>
public sealed record ConsolidationStatus(
    bool Enabled,
    string State,
    string Model,
    string? MemoryVault,
    int Waiting,
    int Done,
    int Failing,
    int Notes,
    int Today,
    int PerDay,
    string? LastFailure,
    DateTimeOffset? LastFailureAt,
    string? Problem);

/// <summary>What a run of the consolidation came to, for one conversation: whether the model was asked, how many notes it wrote or changed, and why it failed when it did.</summary>
public sealed record ConsolidationOutcome(bool Asked, int Notes, string? Failure);

/// <summary>
/// The consolidation (ADR 0036, D-17, D-22): conversations that have been left alone for a while are turned into durable notes of the memory vault. For each, the new turns are cut to a bound and
/// given to the assistant runner as a job; the model reads what the vault already says and writes or updates notes, with the token of the consolidation account, which the server holds to the
/// memory vault. What was read is kept per conversation, so that the next run reads only what is new.
/// </summary>
public sealed class Consolidator
{
    private readonly IConsolidationStore _store;
    private readonly ConsolidationInput _input;
    private readonly SourceCatalog _catalog;
    private readonly ConsolidationJob _job;
    private readonly IOptions<ConsolidationOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<Consolidator> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private string? _problem;

    public Consolidator(IConsolidationStore store, ConsolidationInput input, SourceCatalog catalog, ConsolidationJob job, IOptions<ConsolidationOptions> options, TimeProvider clock, ILogger<Consolidator> logger)
    {
        _store = store;
        _input = input;
        _catalog = catalog;
        _job = job;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public bool Enabled => _options.Value.Enabled;

    public ConsolidationStatus Status()
    {
        var options = _options.Value;
        var vault = MemoryVault();
        var counts = _store.Counts(DayStart(), options.MaxTries);
        var failure = _store.LastFailure();
        return new ConsolidationStatus(
            options.Enabled,
            StateOf(vault),
            options.Model,
            vault?.Name,
            counts.Waiting,
            counts.Done,
            counts.Failing,
            counts.Notes,
            counts.Today,
            options.PerDay,
            failure?.Error,
            failure?.LastAt,
            Volatile.Read(ref _problem));
    }

    /// <summary>One pass: the conversations that have been left alone and grew since their last consolidation, a few of them. Returns how many were asked of the model.</summary>
    public async Task<int> RunPassAsync(bool ignoreIdle, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled || StateOf(MemoryVault()) is not "ready")
        {
            return 0;
        }

        await _one.WaitAsync(cancellationToken);
        try
        {
            return await PassAsync(ignoreIdle, cancellationToken);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Starts a pass in the background now, without waiting for the conversations to be left alone. False when it cannot run or one is going on: two requests at the same moment start one pass.
    /// It stops with the application.
    /// </summary>
    public bool TryStartNow(CancellationToken stopping)
    {
        if (!_options.Value.Enabled || StateOf(MemoryVault()) is not "ready" || !_one.Wait(0, stopping))
        {
            return false;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await PassAsync(ignoreIdle: true, stopping);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "A run of the consolidation that was asked for failed.");
                }
                finally
                {
                    _one.Release();
                }
            },
            CancellationToken.None);
        return true;
    }

    private async Task<int> PassAsync(bool ignoreIdle, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var room = options.PerDay - _store.Counts(DayStart(), options.MaxTries).Today;
        if (room <= 0)
        {
            return 0;
        }

        var idleBefore = ignoreIdle ? _clock.GetUtcNow() : _clock.GetUtcNow() - TimeSpan.FromMinutes(options.IdleMinutes);
        var asked = 0;
        foreach (var (machine, session) in _store.Pending(idleBefore, options.MaxTries, Math.Min(options.PerPass, room)))
        {
            var outcome = await ConsolidateAsync(machine, session, cancellationToken);
            if (outcome.Asked)
            {
                asked++;
            }

            if (outcome.Failure is not null && Volatile.Read(ref _problem) is not null)
            {
                break; // the runner cannot be reached: the next conversations would meet the same wall
            }
        }

        return asked;
    }

    /// <summary>Consolidates what is new in one conversation. Never throws for a runner that fails: the failure is kept for the page.</summary>
    public async Task<ConsolidationOutcome> ConsolidateAsync(string machine, string session, CancellationToken cancellationToken)
    {
        var record = _input.Find(machine, session) ?? throw new RejectedException(Problem.NotFound, "There is no such conversation.");
        var options = _options.Value;
        var state = _store.Find(machine, session) ?? new ConsolidationRecord(machine, session, 0, 0, 0, 0, null, _clock.GetUtcNow());
        var turns = _input.Turns(record);
        var excerpt = ConsolidationExcerpts.Take(turns, state.TurnsDone, options.MaxChars);
        if (string.IsNullOrWhiteSpace(excerpt.Text))
        {
            // Nothing was said that is worth a model (turns of tools only): read, and done.
            _store.Save(state with { LinesDone = record.Lines, TurnsDone = state.TurnsDone + excerpt.Taken, Tries = 0, Error = null, LastAt = _clock.GetUtcNow() });
            return new ConsolidationOutcome(false, 0, null);
        }

        var vault = MemoryVault() ?? throw new RejectedException(Problem.Invalid, "There is no memory vault to write in.");
        string? failure = null;
        var written = 0;
        try
        {
            written = await _job.RunAsync(ConsolidationPrompt.Build(record, vault.Name, SourceCatalog.ConversationsName, excerpt, state.TurnsDone, _clock.GetUtcNow()), cancellationToken);
            _store.CountRun(_clock.GetUtcNow());
            Volatile.Write(ref _problem, null);
        }
        catch (ConsolidationFailureException ex)
        {
            failure = ex.Message;
            _store.CountRun(_clock.GetUtcNow());
            Volatile.Write(ref _problem, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The runner does not answer: not this conversation's fault, so it does not count as a try.
            Volatile.Write(ref _problem, "The assistant runner does not answer.");
            _logger.LogWarning(ex, "The consolidation could not reach the assistant runner.");
            return new ConsolidationOutcome(true, 0, "The assistant runner does not answer.");
        }

        var now = _clock.GetUtcNow();
        if (failure is not null)
        {
            _store.Save(state with { Tries = state.Tries + 1, Error = failure, LastAt = now });
            return new ConsolidationOutcome(true, 0, failure);
        }

        var remaining = excerpt.Remaining(state.TurnsDone) > 0;
        _store.Save(new ConsolidationRecord(machine, session, remaining ? state.LinesDone : record.Lines, state.TurnsDone + excerpt.Taken, state.Notes + written, 0, null, now));
        return new ConsolidationOutcome(true, written, null);
    }

    private Source? MemoryVault() => _catalog.List(SourceScope.All).FirstOrDefault(source => source.Kind == SourceKind.Memory);

    private string StateOf(Source? vault)
    {
        if (!_options.Value.Enabled)
        {
            return "off";
        }

        if (!_job.RunnerConfigured)
        {
            return "no-runner";
        }

        if (vault is null)
        {
            return "no-memory-vault";
        }

        if (vault.Mode != SourceMode.ReadWrite)
        {
            return "memory-vault-read-only";
        }

        return _one.CurrentCount == 0 ? "running" : "ready";
    }

    private DateTimeOffset DayStart()
    {
        var now = _clock.GetUtcNow();
        return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
    }
}
