using System.Globalization;
using GitDash.Application.Abstractions;
using GitDash.Domain.Notifications;
using GitDash.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GitDash.Persistence.Sqlite;

/// <summary>
/// SQLite copy of the monitor's state. Snapshots are stored as JSON next to their ETag and sync time, so the schema
/// never has to follow the domain model. Every statement is parameterised.
/// </summary>
public sealed class SqliteStateCache(SqliteDatabase database) : IStateCache
{
    public async Task<IReadOnlyList<CachedRepository>> LoadRepositoriesAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id, owner, name, etag, synced_at, snapshot_json FROM repositories;";

        var result = new List<CachedRepository>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var snapshot = SnapshotSerializer.ReadSnapshot(reader.GetString(5));
                if (snapshot is null || !Guid.TryParse(reader.GetString(0), out var accountId)) continue;
                result.Add(new CachedRepository(
                    accountId, reader.GetString(1), reader.GetString(2), snapshot,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
            {
                // One unreadable row (for example from an older version) must not hide the others.
            }
        }

        return result;
    }

    public async Task SaveRepositoriesAsync(IReadOnlyList<CachedRepository> items, CancellationToken ct = default)
    {
        await database.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var item in items)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO repositories (account_id, owner, name, etag, synced_at, snapshot_json)
                    VALUES ($account, $owner, $name, $etag, $at, $json)
                    ON CONFLICT (account_id, owner, name) DO UPDATE SET
                        etag = excluded.etag, synced_at = excluded.synced_at, snapshot_json = excluded.snapshot_json;
                    """;
                command.Parameters.AddWithValue("$account", item.AccountId.ToString());
                command.Parameters.AddWithValue("$owner", item.Owner);
                command.Parameters.AddWithValue("$name", item.Name);
                command.Parameters.AddWithValue("$etag", (object?)item.ETag ?? DBNull.Value);
                command.Parameters.AddWithValue("$at", item.SyncedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$json", SnapshotSerializer.Write(item.Snapshot));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteGate.Release();
        }
    }

    public async Task RemoveRepositoryAsync(Guid accountId, string owner, string name, CancellationToken ct = default)
    {
        await database.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM repositories WHERE account_id = $account AND owner = $owner AND name = $name;";
            command.Parameters.AddWithValue("$account", accountId.ToString());
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$name", name);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteGate.Release();
        }
    }

    public async Task<IReadOnlyList<PullRequestNotification>> LoadActivityAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM activity WHERE position = 0;";
        var json = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (json is null) return [];
        try
        {
            return SnapshotSerializer.ReadActivity(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public async Task SaveActivityAsync(IReadOnlyList<PullRequestNotification> items, CancellationToken ct = default)
    {
        await database.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO activity (position, json) VALUES (0, $json) ON CONFLICT (position) DO UPDATE SET json = excluded.json;";
            command.Parameters.AddWithValue("$json", SnapshotSerializer.Write(items));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteGate.Release();
        }
    }
}
