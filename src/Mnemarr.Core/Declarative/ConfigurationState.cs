namespace Mnemarr.Core.Declarative;

/// <summary>What became of the configuration file: whether it was applied, and why not. What the readiness probe reports.</summary>
public sealed class ConfigurationState
{
    private readonly Lock _gate = new();
    private string? _appliedHash;
    private string? _error;
    private bool _attempted;

    /// <summary>The hash of the document that was applied last, so that an unchanged file is not applied again.</summary>
    public string? AppliedHash
    {
        get
        {
            lock (_gate)
            {
                return _appliedHash;
            }
        }
    }

    /// <summary>Why the file could not be applied, in words that give no path; null when it was, or has not been tried.</summary>
    public string? Error
    {
        get
        {
            lock (_gate)
            {
                return _error;
            }
        }
    }

    public bool Attempted
    {
        get
        {
            lock (_gate)
            {
                return _attempted;
            }
        }
    }

    public void Applied(string hash)
    {
        lock (_gate)
        {
            _appliedHash = hash;
            _error = null;
            _attempted = true;
        }
    }

    public void Failed(string error)
    {
        lock (_gate)
        {
            _error = error;
            _attempted = true;
        }
    }
}
