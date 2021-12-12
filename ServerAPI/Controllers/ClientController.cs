using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using StackExchange.Redis;

namespace ServerAPI.Controllers;

[ApiController]
[Route("client")]
public class ClientController : ControllerBase
{
    private readonly IConnectionMultiplexer _redis;

    public ClientController(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    [HttpGet("token/timestamp")]
    public async Task<DateTimeOffset> GetCurrentTokenTimestamp()
    {
        var db = _redis.GetDatabase();

        var url = new Uri(await db.HashGetAsync("game", "url"));
        var query = QueryHelpers.ParseQuery(url.Query);
        var timestamp = long.Parse(query["api_starttime"]);

        return DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
    }
}
