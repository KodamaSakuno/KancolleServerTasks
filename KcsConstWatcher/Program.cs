using Dapper;
using Npgsql;
using Serilog;
using System;
using System.Net;
using System.Net.Http;

var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

await using var connection = new NpgsqlConnection();

using var client = new HttpClient();

var latestTimestamp = await connection.ExecuteScalarAsync<DateTimeOffset?>("SELECT version FROM kcs_const ORDER BY version DESC LIMIT 1;");

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

await connection.ExecuteAsync("INSERT INTO kancolle_resources.kcs_const VALUES(@version, @content);", new { version = lastModified, content = responseString });

logger.Information("Saved");
