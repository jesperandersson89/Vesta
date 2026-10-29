using Microsoft.Data.Sqlite;

namespace Vesta;

/// <summary>
/// SQLite-backed <see cref="IChannelKeyStore"/> (the .NET default). Two tables keyed by channel:
/// epoch keys and the members an admin has seen. Keys are stored as raw bytes; protect the database
/// file like any other secret on the device.
/// </summary>
public sealed class SqliteChannelKeyStore : IChannelKeyStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Open a key store at the given connection string (e.g. <c>"Data Source=keys.db"</c>).</summary>
    public SqliteChannelKeyStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        InitializeSchema();
    }

    /// <summary>Convenience factory for an in-memory store (tests).</summary>
    public static SqliteChannelKeyStore CreateInMemory() => new("Data Source=:memory:");

    private void InitializeSchema()
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_keys (
                channel_id TEXT    NOT NULL,
                epoch      INTEGER NOT NULL,
                key        BLOB    NOT NULL,
                PRIMARY KEY (channel_id, epoch)
            );
            CREATE TABLE IF NOT EXISTS channel_members (
                channel_id        TEXT NOT NULL,
                client_id         TEXT NOT NULL,
                x25519_public_key BLOB NOT NULL,
                PRIMARY KEY (channel_id, client_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<ChannelKey>> LoadKeysAsync(string channelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT epoch, key FROM channel_keys WHERE channel_id = $channel_id ORDER BY epoch";
        cmd.Parameters.AddWithValue("$channel_id", channelId);
        List<ChannelKey> keys = [];
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            keys.Add(new ChannelKey(reader.GetInt32(0), (byte[])reader.GetValue(1)));
        return keys;
    }

    public async Task SaveKeyAsync(string channelId, ChannelKey key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(key);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO channel_keys (channel_id, epoch, key) VALUES ($channel_id, $epoch, $key)
                ON CONFLICT(channel_id, epoch) DO UPDATE SET key = excluded.key
                """;
            cmd.Parameters.AddWithValue("$channel_id", channelId);
            cmd.Parameters.AddWithValue("$epoch", key.Epoch);
            cmd.Parameters.AddWithValue("$key", key.Key);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> LoadMembersAsync(string channelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT client_id, x25519_public_key FROM channel_members WHERE channel_id = $channel_id";
        cmd.Parameters.AddWithValue("$channel_id", channelId);
        Dictionary<string, byte[]> members = [];
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            members[reader.GetString(0)] = (byte[])reader.GetValue(1);
        return members;
    }

    public async Task SaveMemberAsync(string channelId, string clientId, byte[] x25519PublicKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(x25519PublicKey);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO channel_members (channel_id, client_id, x25519_public_key) VALUES ($channel_id, $client_id, $key)
                ON CONFLICT(channel_id, client_id) DO UPDATE SET x25519_public_key = excluded.x25519_public_key
                """;
            cmd.Parameters.AddWithValue("$channel_id", channelId);
            cmd.Parameters.AddWithValue("$client_id", clientId);
            cmd.Parameters.AddWithValue("$key", x25519PublicKey);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RemoveMemberAsync(string channelId, string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM channel_members WHERE channel_id = $channel_id AND client_id = $client_id";
            cmd.Parameters.AddWithValue("$channel_id", channelId);
            cmd.Parameters.AddWithValue("$client_id", clientId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _writeLock.Dispose();
    }
}
