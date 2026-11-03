namespace Mnemarr.Host.Http;

/// <summary>Where the API lives. Three security rules depend on it (the headers of an answer, the header of a write, the files of the interface): they must stay in step.</summary>
public static class ApiPaths
{
    public const string Prefix = "/api";

    public static bool IsApi(PathString path) => path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase);
}
