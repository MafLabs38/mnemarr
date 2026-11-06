namespace Mnemarr.Core.Configuration;

/// <summary>Settings read from appsettings.json, then from environment variables prefixed with <c>MNEMARR__</c>.</summary>
public sealed class MnemarrOptions
{
    /// <summary>Folder that holds the database, the raw conversations and the settings. Mount it as a volume.</summary>
    public string DataFolder { get; set; } = "/config";
}
