using AssetFileDownloader;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Polly;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

var httpClient = new HttpClient();

var connectionString = new NpgsqlConnectionStringBuilder(configuration["Database"])
{
    SearchPath = "kancolle, kancolle_resources",
}.ToString();

var rabbitMqConnectionFactory = new ConnectionFactory()
{
    HostName = configuration["RabbitMQ:Host"],
    DispatchConsumersAsync = true,
};
using var rabbitMqConnection = rabbitMqConnectionFactory.CreateConnection();
using var rabbitMqChannel = rabbitMqConnection.CreateModel();

rabbitMqChannel.QueueDeclare("AssetFileDownload", true, false, false, null);
rabbitMqChannel.BasicQos(0, 1, false);

rabbitMqChannel.QueueDeclare("AssetFileNotFound", true, false, false, null);

var consumer = new AsyncEventingBasicConsumer(rabbitMqChannel);

consumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    var message = JsonSerializer.Deserialize<Message>(e.Body.Span) ?? throw new InvalidOperationException("Bad message");

    using var response = await Policy
        .HandleResult<HttpResponseMessage>(response => !response.IsSuccessStatusCode).Or<TaskCanceledException>().Or<HttpRequestException>()
        .WaitAndRetryAsync(3, count => TimeSpan.FromSeconds(Math.Pow(2, count)), (result, timeSpan, retryCount, context) =>
        {
            if (result.Result is not null)
                logger.Warning("Request {Url} failed with {StatusCode}. Waiting {TimeSpan} before next retry. Retry attempt {RetryCount}", message.Url, result.Result.StatusCode, timeSpan, retryCount);
            else
                logger.Error(result.Exception, "Request {Url} failed with exception. Waiting {TimeSpan} before next retry. Retry attempt {RetryCount}", message.Url, timeSpan, retryCount);
        })
        .ExecuteAsync(() => httpClient.GetAsync(message.Url, HttpCompletionOption.ResponseHeadersRead));

    if (response.StatusCode is HttpStatusCode.NotFound)
    {
        rabbitMqChannel.BasicPublish(string.Empty, "AssetFileNotFound", e.BasicProperties, e.Body);
        rabbitMqChannel.BasicAck(e.DeliveryTag, false);
        return;
    }

    var contentLength = (int)(response.Content.Headers.ContentLength ?? throw new InvalidOperationException("Missing Content-Length"));

    using var responseStream = await response.Content.ReadAsStreamAsync();

    Directory.CreateDirectory(message.Directory);

    var buffer = ArrayPool<byte>.Shared.Rent(contentLength);

    try
    {
        using var sha256 = SHA256.Create();

        using (var memoryStream = new MemoryStream(buffer))
        using (var cryptoStream = new CryptoStream(memoryStream, sha256, CryptoStreamMode.Write))
            await responseStream.CopyToAsync(cryptoStream);

        var filename = Path.Join(message.Directory, Convert.ToHexString(sha256.Hash!).ToLowerInvariant() + message.Extension);

        var timestamp = response.Content.Headers.LastModified!.Value;

        if (!File.Exists(filename))
        {
            using (var fileStream = File.Create(filename))
                await fileStream.WriteAsync(buffer.AsMemory(0, contentLength));

            File.SetLastWriteTimeUtc(filename, timestamp.UtcDateTime);
        }

        await pg.ExecuteAsync("INSERT INTO asset_timestamp VALUES(@hash, @timestamp) ON CONFLICT DO NOTHING;", new { hash = sha256.Hash, timestamp });

        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.CorrelationId = e.BasicProperties.CorrelationId;

        var body = new byte[8 + 32];
        BinaryPrimitives.WriteInt64LittleEndian(body, timestamp.ToUnixTimeSeconds());
        sha256.Hash!.CopyTo(body.AsSpan(8));

        rabbitMqChannel.BasicPublish(string.Empty, e.BasicProperties.ReplyTo, properties, body);

        rabbitMqChannel.BasicAck(e.DeliveryTag, false);
    }
    finally
    {
        buffer.AsSpan(0, contentLength).Clear();
        ArrayPool<byte>.Shared.Return(buffer);
    }

    logger.Information("Downloaded: {Url}", message.Url);
};

rabbitMqChannel.BasicConsume("AssetFileDownload", false, consumer);

await Task.Delay(-1);
