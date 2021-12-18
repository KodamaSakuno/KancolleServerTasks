using StackExchange.Redis;

namespace ServerAPI.Services;

public sealed class TasksService
{
    private readonly IConnectionMultiplexer _redis;

    private LoadedLuaScript? _getLogsScript;

    public TasksService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async ValueTask<string[]> GetLogsAsync()
    {
        _getLogsScript ??= await EnsureScript();

        var db = _redis.GetDatabase();

        return (string[])await db.ScriptEvaluateAsync(_getLogsScript, new { key = "tasks:logs" });

        Task<LoadedLuaScript> EnsureScript()
        {
            var server = _redis.GetServer(_redis.GetEndPoints()[0]);
            var script = LuaScript.Prepare(@"local result = redis.call('lrange', @key, 0, -1)
redis.call('del', @key)
return result");

            return server.ScriptLoadAsync(script);
        }
    }
}
