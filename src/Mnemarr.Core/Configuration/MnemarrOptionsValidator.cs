using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Configuration;

/// <summary>Refuses to start on a setting that cannot work, instead of showing it later as a probe that fails.</summary>
public sealed class MnemarrOptionsValidator : IValidateOptions<MnemarrOptions>
{
    public ValidateOptionsResult Validate(string? name, MnemarrOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DataFolder))
        {
            return ValidateOptionsResult.Fail("DataFolder (MNEMARR__DATAFOLDER) must name the folder that holds the data.");
        }

        return Path.IsPathFullyQualified(options.DataFolder)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("DataFolder (MNEMARR__DATAFOLDER) must be an absolute path.");
    }
}
