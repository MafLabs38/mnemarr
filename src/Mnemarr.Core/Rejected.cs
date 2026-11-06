namespace Mnemarr.Core;

/// <summary>Why an operation was refused: what the API turns into a status and a stable name (<c>urn:mnemarr:problem:...</c>).</summary>
public enum Problem
{
    Invalid,
    Conflict,
    NotFound,
    Locked,
    Busy,
    Forbidden,
}

/// <summary>An operation that cannot be done, with the reason to give to the person who tried. Every vertical refuses with this one type.</summary>
public sealed class RejectedException : Exception
{
    public RejectedException(Problem problem, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        Problem = problem;
        RetryAfter = retryAfter;
    }

    public Problem Problem { get; }

    /// <summary>When the caller may try again, for a refusal that is only for a while.</summary>
    public TimeSpan? RetryAfter { get; }
}
