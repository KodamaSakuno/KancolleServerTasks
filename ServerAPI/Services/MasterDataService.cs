using Dapper;

namespace ServerAPI.Services;

public sealed class MasterDataService
{
    private readonly DatabaseService _databaseService;

    public MasterDataService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public async ValueTask<DateTimeOffset> GetLatestVersionAsync()
    {
        var connection = await _databaseService.GetConnectionAsync();

        return await connection.ExecuteScalarAsync<DateTime>("SELECT max(version) FROM kancolle.api_start2_version;");
    }
}
