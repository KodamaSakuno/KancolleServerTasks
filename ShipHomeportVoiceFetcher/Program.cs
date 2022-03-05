using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using ShipHomeportVoiceFetcher;
using System.Buffers.Binary;
using System.Text.Json;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

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

const string MasterDataUpdatedExchangeName = "MasterDataUpdated";

rabbitMqChannel.ExchangeDeclare(MasterDataUpdatedExchangeName, ExchangeType.Fanout, true);

var queueName = rabbitMqChannel.QueueDeclare().QueueName;
rabbitMqChannel.QueueBind(queueName, MasterDataUpdatedExchangeName, string.Empty);

const string CallbackExchangeName = "AssetDownloadCallback";

rabbitMqChannel.ExchangeDeclare(CallbackExchangeName, ExchangeType.Direct, true);

const string InfoComitterQueueName = "ShipHomeportVoiceInfoComitter";

rabbitMqChannel.QueueDeclare(InfoComitterQueueName, true, false, false, null);
rabbitMqChannel.QueueBind(InfoComitterQueueName, CallbackExchangeName, InfoComitterQueueName);

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'ship_homeport_voice') FROM api_start2_item_version WHERE key = 'api_mst_shipgraph';"))
        return;

    await foreach (var voice in EnumerateDiffs(pg))
    {
        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.ReplyTo = InfoComitterQueueName;
        properties.ContentType = "application/json";

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = $"http://203.104.209.199/kcs/sound/kc{voice.UniqueKey}/{voice.Filename}.mp3",
            Directory = "/var/kancolle/ship_homeport_voice/pool",
            Extension = ".mp3",
            Metadata = new Metadata(voice.ShipId, voice.VoiceId, voice.Version),
        });

        rabbitMqChannel.BasicPublish(string.Empty, "AssetFileDownload", properties, body);
    }

    await pg.ExecuteAsync("INSERT INTO downloaded_version VALUES('ship_homeport_voice', (SELECT max(version) FROM api_start2_item_version WHERE key = 'api_mst_shipgraph')) ON CONFLICT (name) DO UPDATE SET version = excluded.version;");

    await transaction.CommitAsync();
};

var callbackEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
callbackEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..(8 + 256)].ToArray();
    var (shipId, voiceId, date) = JsonSerializer.Deserialize<Metadata>(e.Body[(8 + 256)..].Span)!;

    await pg.ExecuteAsync("INSERT INTO ship_homeport_voice VALUES(@shipId, (SELECT max(version) FROM api_start2_item_version WHERE key = 'api_mst_shipgraph'), @voiceId, @date, @hash, @timestamp);", new
    {
        shipId,
        voiceId,
        date,
        timestamp,
        hash,
    });

    rabbitMqChannel.BasicAck(e.DeliveryTag, false);
};

rabbitMqChannel.BasicConsume(queueName, true, updatedEventConsumer);
rabbitMqChannel.BasicConsume(InfoComitterQueueName, false, callbackEventConsumer);

logger.Information("Waiting...");

await Task.Delay(-1);

static async IAsyncEnumerable<Voice> EnumerateDiffs(NpgsqlConnection pg)
{
    foreach (var (shipId, version, uniqueKey) in await pg.QueryAsync<(int, int, string)>("SELECT id, current_version, current_filename FROM ship_homeport_voice_diff;"))
    {
        yield return new(shipId, 2, version, uniqueKey);
        yield return new(shipId, 3, version, uniqueKey);
    }
}
