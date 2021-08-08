using Dapper;
using Npgsql;
using Serilog;
using System;
using System.Net;
using System.Net.Http;

var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

await using var pg = new NpgsqlConnection(Environment.GetEnvironmentVariable("DatabaseConn") ?? throw new InvalidOperationException("Missing DatabaseConn"));
await using var transaction = await pg.BeginTransactionAsync();

using var client = new HttpClient();

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
await pg.ExecuteAsync(@"WITH
previous AS (
    SELECT json->'scene' scenes, json->'resource' resources FROM android_client_version ORDER BY version DESC OFFSET 1 LIMIT 1
),
previous_scene AS (
    SELECT scene FROM previous, json_each_text(previous.scenes) AS scene WHERE (scene).key != '_'
),
previous_resource AS (
    SELECT resource FROM previous, json_each_text(previous.resources) AS resource WHERE (resource).key != '_'
),
latest AS (
    SELECT json->'scene' scenes, json->'resource' resources FROM android_client_version ORDER BY version DESC LIMIT 1
),
latest_scene AS (
    SELECT scene FROM latest, json_each_text(latest.scenes) AS scene WHERE (scene).key != '_'
),
latest_resource AS (
    SELECT resource FROM latest, json_each_text(latest.resources) AS resource WHERE (resource).key != '_'
),
diff(filename, version) AS (
    SELECT 'scenes/' || (latest_scene.scene).key || '.swf', (latest_scene.scene).value
    FROM latest_scene
    LEFT JOIN previous_scene ON (latest_scene.scene).key = (previous_scene.scene).key
    WHERE (latest_scene.scene).value != (previous_scene.scene).value
    UNION ALL
    SELECT 'resources/' || (latest_resource.resource).key || '.swf', (latest_resource.resource).value
    FROM latest_resource
    LEFT JOIN previous_resource ON (latest_resource.resource).key = (previous_resource.resource).key
    WHERE (latest_resource.resource).value != (previous_resource.resource).value
)

INSERT INTO android_client SELECT filename, version, NULL FROM diff;");

await transaction.CommitAsync();

logger.Information("Saved");
