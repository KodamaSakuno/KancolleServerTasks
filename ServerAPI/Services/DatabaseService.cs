using Npgsql;
using ServerAPI.Configs;
using System.Data;

namespace ServerAPI.Services;

public sealed class DatabaseService : IAsyncDisposable
{
    private readonly DatabaseConfig _databaseConfig;

    private NpgsqlConnection? _connection;

    public DatabaseService(DatabaseConfig databaseConfig)
    {
        _databaseConfig = databaseConfig;
    }

    public ValueTask DisposeAsync() => _connection?.DisposeAsync() ?? default;

    public async Task<NpgsqlConnection> GetConnectionAsync()
    {
        _connection ??= new(_databaseConfig.ConnectionString);

        if (_connection.State is ConnectionState.Closed)
            await _connection.OpenAsync();

        return _connection;
    }
}
