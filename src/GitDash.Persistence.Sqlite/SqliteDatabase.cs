using Microsoft.Data.Sqlite;

namespace GitDash.Persistence.Sqlite;

/// <summary>
/// Opens the app's SQLite file and keeps its schema up to date. One short-lived connection per operation; SQLite's own
/// connection pool makes that cheap, and nothing stays locked between calls. WAL mode lets the UI-side reads and the
/// write-behind saves overlap.
/// </summary>
public sealed class SqliteDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private bool _ready;

    public SqliteDatabase(string file)
    {
        File = file;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();
    }

    public string File { get; }

    /// <summary>Serialises writers: SQLite allows a single writer, so queueing here avoids "database is locked".</summary>
    public SemaphoreSlim WriteGate { get; } = new(1, 1);

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        return await OpenRawAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_ready) return;
        await _migrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready) return;
            await using var connection = await OpenRawAsync(ct).ConfigureAwait(false);

            long version;
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "PRAGMA user_version;";
                version = Convert.ToInt64(await read.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            if (version < 1)
            {
                await using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS repositories (
                        account_id    TEXT NOT NULL,
                        owner         TEXT NOT NULL COLLATE NOCASE,
                        name          TEXT NOT NULL COLLATE NOCASE,
                        etag          TEXT NULL,
                        synced_at     TEXT NOT NULL,
                        snapshot_json TEXT NOT NULL,
                        PRIMARY KEY (account_id, owner, name)
                    );
                    CREATE TABLE IF NOT EXISTS activity (
                        position INTEGER PRIMARY KEY,
                        json     TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS timer_sessions (
                        position INTEGER PRIMARY KEY,
                        json     TEXT NOT NULL
                    );
                    """;
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var stamp = connection.CreateCommand())
            {
                // PRAGMA cannot take parameters; the value is a compile-time constant.
                stamp.CommandText = $"PRAGMA user_version = {SchemaVersion};";
                await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            _ready = true;
        }
        finally
        {
            _migrationGate.Release();
        }
    }
}
