using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Core.Storage;

public sealed class Database : IDatabase
{
    private readonly string _folder;
    private readonly string _connectionString;
    private readonly Lock _gate = new();
    private bool _ready;

    public Database(IOptions<MnemarrOptions> options)
    {
        _folder = options.Value.DataFolder;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_folder, "mnemarr.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        // SQLite would create the folder itself: a volume that was never mounted would then look like a fresh, empty one.
        if (!Directory.Exists(_folder))
        {
            throw new DirectoryNotFoundException("The data folder does not exist.");
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            EnsureSchema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        lock (_gate)
        {
            if (_ready)
            {
                return;
            }

            Schema.Apply(connection);
            _ready = true;
        }
    }
}
