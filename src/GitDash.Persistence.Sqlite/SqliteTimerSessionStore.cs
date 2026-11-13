using GitDash.Application.Abstractions;
using GitDash.Domain.Timers;
using GitDash.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GitDash.Persistence.Sqlite;

/// <summary>
/// Timer history in SQLite. The first time the database is used, sessions from the old timer-sessions.json file are
/// imported so no history is lost.
/// </summary>
public sealed class SqliteTimerSessionStore(SqliteDatabase database, ITimerSessionStore? legacy = null) : ITimerSessionStore
{
    public async Task<IReadOnlyList<TimerSession>> LoadAsync(CancellationToken ct = default)
    {
        await using (var connection = await database.OpenAsync(ct).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM timer_sessions WHERE position = 0;";
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json)
            {
                try
                {
                    return SnapshotSerializer.ReadSessions(json);
                }
                catch (System.Text.Json.JsonException)
                {
                    return [];
                }
            }
        }

        // Empty database: bring over the history from the JSON file, once.
        if (legacy is null) return [];
        var imported = await legacy.LoadAsync(ct).ConfigureAwait(false);
        if (imported.Count > 0) await SaveAsync(imported, ct).ConfigureAwait(false);
        return imported;
    }

    public async Task SaveAsync(IReadOnlyList<TimerSession> sessions, CancellationToken ct = default)
    {
        await database.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO timer_sessions (position, json) VALUES (0, $json) ON CONFLICT (position) DO UPDATE SET json = excluded.json;";
            command.Parameters.AddWithValue("$json", SnapshotSerializer.Write(sessions));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteGate.Release();
        }
    }
}
