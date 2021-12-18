using Dapper;
using Npgsql;
using Serilog;
using StackExchange.Redis;
using System;
using System.Net;
using System.Net.Http;

var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("DatabaseConn") ?? throw new InvalidOperationException("Missing DatabaseConn"));

using var client = new HttpClient();

var latestTimestamp = (DateTimeOffset?)await connection.ExecuteScalarAsync<DateTime?>("SELECT max(timestamp) FROM kcs_const;");

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

using var redis = ConnectionMultiplexer.Connect(Environment.GetEnvironmentVariable("RedisHost") ?? throw new InvalidOperationException("Missing RedisHost"));
var redisDatabase = redis.GetDatabase();

var responseString = await response.Content.ReadAsStringAsync();
var lastModified = response.Content.Headers.LastModified!.Value;

await connection.ExecuteAsync("INSERT INTO kcs_const VALUES(@timestamp, @content);", new { timestamp = lastModified, content = responseString });

await redisDatabase.ListRightPushAsync("tasks:logs", "kcs_const.js updated");

logger.Information("Saved");
