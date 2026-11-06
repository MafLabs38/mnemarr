using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Test;

/// <summary>A data folder of its own with a real SQLite database, and folders to use as sources; all removed afterwards.</summary>
internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Directory.CreateTempSubdirectory("mnemarr-test-").FullName;
        Directory.CreateDirectory(SourcesFolder);
        Options = Microsoft.Extensions.Options.Options.Create(new MnemarrOptions { DataFolder = DataFolder });
        Database = new Database(Options);
    }

    public string Root { get; }

    public string DataFolder => Path.Combine(Root, "data");

    public string SourcesFolder => Path.Combine(DataFolder, "sources");

    public IOptions<MnemarrOptions> Options { get; }

    public IDatabase Database { get; }

    /// <summary>Where a source may be: the given roots, or the sources folder.</summary>
    public SourcePaths Paths(params string[] roots) => new(Options, Microsoft.Extensions.Options.Options.Create(new SourcesOptions { Roots = roots }));

    /// <summary>A folder under the sources folder, created.</summary>
    public string Folder(string name) => Directory.CreateDirectory(Path.Combine(SourcesFolder, name)).FullName;

    public static void Write(string folder, string relativePath, string content)
    {
        var path = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}
