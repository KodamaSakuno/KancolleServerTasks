using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using ShipVoiceFetcher;
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

const string InfoComitterQueueName = "ShipVoiceInfoComitter";

rabbitMqChannel.QueueDeclare(InfoComitterQueueName, true, false, false, null);
rabbitMqChannel.QueueBind(InfoComitterQueueName, CallbackExchangeName, InfoComitterQueueName);

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'ship_voice') FROM api_start2_item_version WHERE key = 'api_mst_shipgraph';"))
        return;

    await foreach (var (voice, uniqueKey) in EnumerateDiffs(pg))
    {
        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.ReplyTo = InfoComitterQueueName;
        properties.ContentType = "application/json";

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = $"http://203.104.209.199/kcs/sound/kc{uniqueKey}/{voice.Filename}.mp3",
            Directory = "/var/kancolle/ship_voice/pool",
            Extension = ".mp3",
            Metadata = new Metadata(voice.ShipId, voice.Id, voice.Version),
        });

        rabbitMqChannel.BasicPublish(string.Empty, "AssetFileDownload", properties, body);
    }

    await pg.ExecuteAsync("INSERT INTO downloaded_version VALUES('ship_voice', (SELECT max(version) FROM api_start2_item_version WHERE key = 'api_mst_shipgraph')) ON CONFLICT (name) DO UPDATE SET version = excluded.version;");

    await transaction.CommitAsync();
};

var callbackEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
callbackEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..(8 + 32)].ToArray();
    var (id, voiceId, version) = JsonSerializer.Deserialize<Metadata>(e.Body[(8 + 32 + 1)..].Span)!;

    await pg.ExecuteAsync("INSERT INTO ship_voice VALUES(@id, @voiceId, @version, @hash, @timestamp) ON CONFLICT DO NOTHING;", new
    {
        id,
        voiceId,
        version,
        timestamp,
        hash,
    });

    rabbitMqChannel.BasicAck(e.DeliveryTag, false);
};

rabbitMqChannel.BasicConsume(queueName, true, updatedEventConsumer);
rabbitMqChannel.BasicConsume(InfoComitterQueueName, false, callbackEventConsumer);

logger.Information("Waiting...");

await Task.Delay(-1);

static async IAsyncEnumerable<(Voice, string)> EnumerateDiffs(NpgsqlConnection pg)
{
    foreach (var (shipId, uniqueKey, previousVersion, currentVersion, currentFlag) in await pg.QueryAsync<(int, string, int, int, VoiceFlag)>("SELECT id, current_filename, previous_version, current_version, current_flag FROM ship_voice_diff;"))
    {
        if (previousVersion != currentVersion)
        {
            yield return (new Voice(shipId, 1, currentVersion), uniqueKey);
            yield return (new Voice(shipId, 4, currentVersion), uniqueKey);
            yield return (new Voice(shipId, 5, currentVersion), uniqueKey);

            for (var i = 7; i <= 28; i++)
                yield return (new Voice(shipId, i, currentVersion), uniqueKey);
        }

        if (currentFlag.HasFlag(VoiceFlag.Idle))
            yield return (new Voice(shipId, 29, currentVersion), uniqueKey);

        if (currentFlag.HasFlag(VoiceFlag.HourlyNotification))
            for (var i = 30; i <= 53; i++)
                yield return (new Voice(shipId, i, currentVersion), uniqueKey);

        if (currentFlag.HasFlag(VoiceFlag.SpecialIdle))
            yield return (new Voice(shipId, 129, currentVersion), uniqueKey);
    }
}
