using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using Serilog;
using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
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

var rabbitMqConnectionFactory = new ConnectionFactory() { HostName = configuration["RabbitMQ:Host"] };
using var rabbitMqConnection = rabbitMqConnectionFactory.CreateConnection();
using var rabbitMqChannel = rabbitMqConnection.CreateModel();

rabbitMqChannel.QueueDeclare("AndroidClientFile", true, false, false, null);

await using var pg = new NpgsqlConnection(configuration["Database"]);

var latestTimestamp = (DateTimeOffset?)await pg.ExecuteScalarAsync<DateTime?>("SELECT max(timestamp) FROM android_client_version;");

using var request = new HttpRequestMessage(HttpMethod.Get, "http://203.104.209.71/kca/version.json");
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

await pg.ExecuteAsync("INSERT INTO android_client_version VALUES(@timestamp, @content::jsonb);", new { timestamp = lastModified, content = responseString });

foreach (var (filename, version) in await pg.QueryAsync<(string, string)>(@"WITH
previous AS (
    SELECT content->'scene' scenes, content->'resource' resources FROM android_client_version ORDER BY timestamp DESC OFFSET 1 LIMIT 1
),
previous_scene AS (
    SELECT scene FROM previous, jsonb_each_text(previous.scenes) AS scene WHERE (scene).key != '_'
),
previous_resource AS (
    SELECT resource FROM previous, jsonb_each_text(previous.resources) AS resource WHERE (resource).key != '_'
),
latest AS (
    SELECT content->'scene' scenes, content->'resource' resources FROM android_client_version ORDER BY timestamp DESC LIMIT 1
),
latest_scene AS (
    SELECT scene FROM latest, jsonb_each_text(latest.scenes) AS scene WHERE (scene).key != '_'
),
latest_resource AS (
    SELECT resource FROM latest, jsonb_each_text(latest.resources) AS resource WHERE (resource).key != '_'
)

SELECT 'scenes/' || (latest_scene.scene).key, (latest_scene.scene).value
FROM latest_scene
LEFT JOIN previous_scene ON (latest_scene.scene).key = (previous_scene.scene).key
WHERE (latest_scene.scene).value != (previous_scene.scene).value
UNION ALL
SELECT 'resources/' || (latest_resource.resource).key, (latest_resource.resource).value
FROM latest_resource
LEFT JOIN previous_resource ON (latest_resource.resource).key = (previous_resource.resource).key
WHERE (latest_resource.resource).value != (previous_resource.resource).value;"))
{
    var buffer = new byte[4 + filename.Length + 4 + version.Length];

    BinaryPrimitives.WriteInt32LittleEndian(buffer, filename.Length);
    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4 + filename.Length), version.Length);
    Encoding.UTF8.GetBytes(filename, buffer.AsSpan(4));
    Encoding.UTF8.GetBytes(version, buffer.AsSpan(4 + filename.Length + 4));

    rabbitMqChannel.BasicPublish(string.Empty, "AndroidClientFile", null, buffer);
}

await bot.SendTextMessageAsync(configuration["Telegram:ChatId"], $@"Android client *version.json* updated

_Last-Modified: {lastModified.ToOffset(TimeSpan.FromHours(8)):G}_", ParseMode.Markdown);

logger.Information("Saved");
