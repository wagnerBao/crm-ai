using System.Data;
using Npgsql;

namespace CrmAi.Infrastructure.Persistence;

internal sealed class PostgresConversationPersistenceLock(NpgsqlConnection connection, string key) : IAsyncDisposable
{
    private bool _disposed;

    internal static async Task<PostgresConversationPersistenceLock> AcquireAsync(NpgsqlConnection connection,
        Guid? companyId, Guid conversationId, CancellationToken cancellationToken)
    {
        var key = $"crm-ai:whatsapp-persistence:{companyId ?? Guid.Empty:N}:{conversationId:N}";
        try
        {
            await using var command = new NpgsqlCommand("select pg_advisory_lock(hashtextextended(@key, 0));", connection);
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new(connection, key);
        }
        catch
        {
            // Cancellation can race with server acquisition; clean up this key before returning the session to its pool.
            try { await UnlockAsync(connection, key); } catch { }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await UnlockAsync(connection, key);
    }

    private static async Task UnlockAsync(NpgsqlConnection connection, string key)
    {
        if (connection.State != ConnectionState.Open) return;
        try
        {
            await using var command = new NpgsqlCommand("select pg_advisory_unlock(hashtextextended(@key, 0));", connection)
            {
                CommandTimeout = 5
            };
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch
        {
            // Never return a session with an uncertain advisory lock to the connection pool.
            NpgsqlConnection.ClearPool(connection);
            try { await connection.CloseAsync(); } catch { }
            throw;
        }
    }
}
