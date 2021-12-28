using Dapper;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Serilog;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

using var redis = ConnectionMultiplexer.Connect(configuration["Redis:Host"]);
var redisDatabase = redis.GetDatabase();

using var client = new HttpClient();

var bot = new TelegramBotClient(configuration["Telegram:Token"], client);

var connectionStringBuilder = new NpgsqlConnectionStringBuilder(configuration["Database"])
{
    SearchPath = "kancolle",
};
await using var pg = new NpgsqlConnection(connectionStringBuilder.ToString());

var gameUrl = (string)await redisDatabase.HashGetAsync("game", "url");

var token = Regex.Match(gameUrl, @"api_token=(\w{40})").Groups[1].Value!;
var startTime = long.Parse(Regex.Match(gameUrl, @"api_starttime=(\d+)").Groups[1].Value);

var duration = DateTimeOffset.Now - DateTimeOffset.FromUnixTimeMilliseconds(startTime);
if (duration >= TimeSpan.FromMinutes(115))
{
    logger.Information("Token outdated");
    return;
}

await pg.OpenAsync();
await using var transaction = await pg.BeginTransactionAsync();

using var request = new HttpRequestMessage(HttpMethod.Post, "http://125.6.189.167/kcsapi/api_start2/getData");
request.Headers.Referrer = new Uri(gameUrl);
request.Content = new FormUrlEncodedContent(new[] {
    KeyValuePair.Create("api_token", token),
    KeyValuePair.Create("api_verno", "1"),
});

using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
using var stream = await response.Content.ReadAsStreamAsync();

await stream.ReadAsync(new byte[7]);

using var reader = new JsonTextReader(new StreamReader(stream));
var json = await JObject.LoadAsync(reader);

var obj = (JObject)json["api_data"]["api_mst_const"];

json["api_data"]["api_mst_const"] = new JObject(obj.Properties().OrderBy(r => r.Name));

var argument = new { json = json["api_data"].ToString(Formatting.None) };
var ra = await pg.ExecuteAsync("INSERT INTO api_start2_history VALUES(now(), @json::jsonb, convert_to(@json, 'UTF8')) ON CONFLICT DO NOTHING;", argument);
if (ra == 0)
{
    logger.Information("Not Modified");
    return;
}

await pg.ExecuteAsync(@"INSERT INTO current_api_start2 SELECT key, value FROM jsonb_each(@json::jsonb)
ON CONFLICT (key) DO UPDATE SET value = excluded.value
WHERE current_api_start2.value != excluded.value;", argument);
await pg.ExecuteAsync("REFRESH MATERIALIZED VIEW api_start2_item_version;");

await transaction.CommitAsync();

await bot.SendTextMessageAsync(configuration["Telegram:ChatId"], $@"*api_start2* updated", ParseMode.Markdown);

logger.Information("Saved");
