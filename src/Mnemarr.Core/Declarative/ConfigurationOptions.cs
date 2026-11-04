using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Declarative;

/// <summary>Settings of the configuration file, from the <c>Config</c> section (see docs/configuration.md).</summary>
public sealed class ConfigurationOptions
{
    /// <summary>The configuration document to apply at start and whenever it changes (configuration as code). Empty: none.</summary>
    public string? File { get; set; }

    /// <summary>How often the file is looked at again, in seconds.</summary>
    public int ReloadSeconds { get; set; } = 30;
}

public sealed class ConfigurationOptionsValidator : IValidateOptions<ConfigurationOptions>
{
    public ValidateOptionsResult Validate(string? name, ConfigurationOptions options)
    {
        if (options.ReloadSeconds is < 1 or > 86_400)
        {
            return ValidateOptionsResult.Fail("Config:ReloadSeconds (MNEMARR__CONFIG__RELOADSECONDS) must be between 1 and 86400.");
        }

        return string.IsNullOrWhiteSpace(options.File) || Path.IsPathFullyQualified(options.File)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Config:File (MNEMARR__CONFIG__FILE) must be an absolute path.");
    }
}
