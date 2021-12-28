using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using System;
using System.Net;
using System.Net.Http;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

using var client = new HttpClient();

var bot = new TelegramBotClient(configuration["Telegram:Token"], client);

var connectionStringBuilder = new NpgsqlConnectionStringBuilder(configuration["Database"])
{
    SearchPath = "kancolle_resources",
};
await using var pg = new NpgsqlConnection(connectionStringBuilder.ToString());

var latestTimestamp = (DateTimeOffset?)await pg.ExecuteScalarAsync<DateTime?>("SELECT max(timestamp) FROM client_version;");

using var request = new HttpRequestMessage(HttpMethod.Get, "http://203.104.209.71/kcs2/version.json");
request.Headers.IfModifiedSince = latestTimestamp;

using var response = await client.SendAsync(request);
if (response.StatusCode == HttpStatusCode.NotModified)
{
    logger.Information("Not Modified");
    return;
}
if (response.StatusCode == HttpStatusCode.Forbidden)
{
    logger.Information("Forbidden");
    return;
}

var responseString = await response.Content.ReadAsStringAsync();
var lastModified = response.Content.Headers.LastModified!.Value;

await pg.ExecuteAsync("INSERT INTO client_version VALUES(@timestamp, @content::jsonb);", new { timestamp = lastModified, content = responseString });

await bot.SendTextMessageAsync(configuration["Telegram:ChatId"], $@"HTML5 client *version.json* updated

_Last-Modified: {lastModified.ToOffset(TimeSpan.FromHours(8)):G}_", ParseMode.Markdown);

logger.Information("Saved");


