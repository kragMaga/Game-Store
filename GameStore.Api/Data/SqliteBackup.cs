using Microsoft.Data.Sqlite;

namespace GameStore.Api.Data;

// Maintenance-only API: deliberately not exposed as an HTTP endpoint. Backups
// contain password hashes, token history, and account data and need private storage.
public static class SqliteBackup
{
    public static async Task CreateVerifiedAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backup destination must differ from the source.");

        // ReadOnly prevents a typo from silently creating a new source database.
        await using var sourceConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await sourceConnection.OpenAsync(cancellationToken);
        // Reserve a new file; never overwrite an existing backup or live database.
        using (var reserved = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        await using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await backup.OpenAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // SQLite's online backup API produces a consistent snapshot, including
            // committed WAL contents. Copying the live .db file alone is unsafe.
            // This synchronous operation can briefly block writers; schedule accordingly.
            sourceConnection.BackupDatabase(backup);
        }
        // Reopen the snapshot independently, as a restore rehearsal would. Failed
        // snapshots are retained for investigation but the command returns failure.
        await VerifyAsync(destination, cancellationToken);
    }

    public static async Task VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "ok" ||
                await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Backup integrity verification failed.");
        }
        command.CommandText = "PRAGMA foreign_key_check";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Backup foreign-key verification failed.");

        // Validate application tables, migrations, outbox, and search artifacts too;
        // an unrelated SQLite database can otherwise pass an integrity check.
        command.CommandText = """
            SELECT count(*) FROM sqlite_master WHERE
                (type = 'table' AND name IN ('Users', 'Games', 'Genres', 'AuthSessions',
                  'RefreshTokens', 'EmailOutbox', '__EFMigrationsHistory', 'GameSearch', 'GenreSearch'))
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 9)
            throw new InvalidOperationException("Backup does not contain the expected application schema.");
        command.CommandText = """
            SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name IN (
                'GameSearch_Insert', 'GameSearch_Update', 'GameSearch_Delete',
                'GenreSearch_Insert', 'GenreSearch_Update', 'GenreSearch_Delete')
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 6)
            throw new InvalidOperationException("Backup is missing catalog search maintenance triggers.");
    }
}
