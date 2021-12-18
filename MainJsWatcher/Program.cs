using Dapper;
using Npgsql;
using Serilog;
using StackExchange.Redis;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

await using var pg = new NpgsqlConnection(Environment.GetEnvironmentVariable("DatabaseConn") ?? throw new InvalidOperationException("Missing DatabaseConn"));

using var client = new HttpClient();

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

using var redis = ConnectionMultiplexer.Connect(Environment.GetEnvironmentVariable("RedisHost") ?? throw new InvalidOperationException("Missing RedisHost"));
var redisDatabase = redis.GetDatabase();

var responseBytes = await response.Content.ReadAsByteArrayAsync();
var lastModified = response.Content.Headers.LastModified!.Value;
var hash = SHA1.HashData(responseBytes);

var filename = Path.Join("/var/mainjs", Convert.ToHexString(hash).ToLowerInvariant() + ".js");

await File.WriteAllBytesAsync(filename, responseBytes);
File.SetLastWriteTimeUtc(filename, lastModified.UtcDateTime);

await pg.ExecuteAsync("INSERT INTO mainjs VALUES(@timestamp, @hash);", new { timestamp = lastModified, hash });

await redisDatabase.ListRightPushAsync("tasks:logs", "main.js updated");

logger.Information("Saved");
