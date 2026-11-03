namespace Mnemarr.Core.Storage;

/// <summary>The database was written by a newer version than this code: it is not opened, so that an older image cannot damage it.</summary>
public sealed class UnsupportedSchemaException : InvalidOperationException
{
    public UnsupportedSchemaException(int found, int supported)
        : base($"The database has schema version {found}, and this version of Mnemarr knows up to {supported}.")
    {
    }
}
