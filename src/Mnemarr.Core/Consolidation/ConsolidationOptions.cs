using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Consolidation;

/// <summary>
/// Settings of the consolidation, from the <c>Consolidation</c> section (ADR 0036). Off by default: turning it on sends the conversations (already masked) to the model of the
/// assistant runner, and spends the operator's subscription, a little for each conversation.
/// </summary>
public sealed class ConsolidationOptions
{
    /// <summary>Whether conversations are turned into notes of the memory vault. It needs the assistant runner (<c>Agent:Url</c>) and a memory vault open for writing.</summary>
    public bool Enabled { get; set; }

    /// <summary>The agent of the runner that does the job (<c>claude-code</c>, <c>openai</c>); empty: the first agent that can run. The default model is a Claude one: with another agent, set <see cref="Model"/> too.</summary>
    public string? Agent { get; set; }

    /// <summary>The model of the job, a setting: a light one is enough to write notes (the subscription's usage is shared with the operator's own).</summary>
    public string Model { get; set; } = "haiku";

    /// <summary>A conversation is consolidated once nothing has been added to it for this many minutes.</summary>
    public int IdleMinutes { get; set; } = 5;

    /// <summary>How often the background job looks for conversations to consolidate.</summary>
    public int PassSeconds { get; set; } = 60;

    /// <summary>How many conversations one pass consolidates: the cost of a pass is bounded.</summary>
    public int PerPass { get; set; } = 3;

    /// <summary>How many times the model is asked in a day (UTC): the cost of a day is bounded. A conversation read in several runs, or tried again, costs each time.</summary>
    public int PerDay { get; set; } = 60;

    /// <summary>How much of a conversation one run reads, in characters: the rest is read by the next run.</summary>
    public int MaxChars { get; set; } = 40_000;

    /// <summary>A conversation that failed this many times is left alone until it grows again.</summary>
    public int MaxTries { get; set; } = 3;

    /// <summary>A run that lasts longer is stopped.</summary>
    public int TimeoutMinutes { get; set; } = 10;

    /// <summary>How often a run in progress is looked at, in seconds.</summary>
    public int PollSeconds { get; set; } = 3;
}

public sealed class ConsolidationOptionsValidator : IValidateOptions<ConsolidationOptions>
{
    public ValidateOptionsResult Validate(string? name, ConsolidationOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!IsName(options.Model, 64, allowDot: true))
        {
            return ValidateOptionsResult.Fail("Consolidation:Model (MNEMARR__CONSOLIDATION__MODEL) is a model name: letters, digits, dots, dashes and underscores.");
        }

        if (!string.IsNullOrEmpty(options.Agent) && !IsName(options.Agent, 32, allowDot: false))
        {
            return ValidateOptionsResult.Fail("Consolidation:Agent (MNEMARR__CONSOLIDATION__AGENT) is the id of an agent of the runner, such as claude-code or openai, or empty.");
        }

        if (options.IdleMinutes is < 1 or > 1440 || options.PassSeconds is < 10 or > 86_400 || options.PerPass is < 1 or > 20 || options.PerDay is < 1 or > 1000
            || options.MaxChars is < 2000 or > 100_000 || options.MaxTries is < 1 or > 10 || options.TimeoutMinutes is < 1 or > 60 || options.PollSeconds is < 1 or > 60)
        {
            return ValidateOptionsResult.Fail("Consolidation: IdleMinutes is 1 to 1440, PassSeconds 10 to 86400, PerPass 1 to 20, PerDay 1 to 1000, MaxChars 2000 to 100000, MaxTries 1 to 10, TimeoutMinutes 1 to 60, PollSeconds 1 to 60.");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool IsName(string? value, int maxLength, bool allowDot) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' || (allowDot && character == '.'));
}
