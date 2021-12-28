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

await using var pg = new NpgsqlConnection(configuration["Database"]);

var latestTimestamp = (DateTimeOffset?)await pg.ExecuteScalarAsync<DateTime?>("SELECT max(timestamp) FROM kcs_const;");

using var request = new HttpRequestMessage(HttpMethod.Get, "http://203.104.209.7/gadget_html5/js/kcs_const.js");
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

await pg.ExecuteAsync("INSERT INTO kcs_const VALUES(@timestamp, @content);", new { timestamp = lastModified, content = responseString });

await bot.SendTextMessageAsync(configuration["Telegram:ChatId"], $@"*kcs_const.js* updated

_Last-Modified: {lastModified.ToOffset(TimeSpan.FromHours(8)):G}_", ParseMode.Markdown);

logger.Information("Saved");
