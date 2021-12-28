using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

using var client = new HttpClient();

var bot = new TelegramBotClient(configuration["Telegram:Token"]);

var connectionStringBuilder = new NpgsqlConnectionStringBuilder(configuration["Database"])
{
    SearchPath = "kancolle_resources",
};
await using var pg = new NpgsqlConnection(connectionStringBuilder.ToString());

var latestTimestamp = (DateTimeOffset?)await pg.ExecuteScalarAsync<DateTime?>("SELECT max(timestamp) FROM mainjs;");

using var request = new HttpRequestMessage(HttpMethod.Get, "http://203.104.209.71/kcs2/js/main.js");
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

var responseBytes = await response.Content.ReadAsByteArrayAsync();
var lastModified = response.Content.Headers.LastModified!.Value;
var hash = SHA1.HashData(responseBytes);

var filename = Path.Join("/var/mainjs", Convert.ToHexString(hash).ToLowerInvariant() + ".js");

await File.WriteAllBytesAsync(filename, responseBytes);
File.SetLastWriteTimeUtc(filename, lastModified.UtcDateTime);

await pg.ExecuteAsync("INSERT INTO mainjs VALUES(@timestamp, @hash);", new { timestamp = lastModified, hash });

await bot.SendTextMessageAsync(configuration["Telegram:ChatId"], $@"HTML5 client *main.js* updated

_Last-Modified: {lastModified.ToOffset(TimeSpan.FromHours(8)):G}_", ParseMode.Markdown);

logger.Information("Saved");
