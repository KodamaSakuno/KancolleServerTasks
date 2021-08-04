using Dapper;
using Npgsql;
using Serilog;
using System;
using System.Net;
using System.Net.Http;

var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

await using var pg = new NpgsqlConnection(Environment.GetEnvironmentVariable("DatabaseConn") ?? throw new InvalidOperationException("Missing DatabaseConn"));

using var client = new HttpClient();

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

logger.Information("Saved");


