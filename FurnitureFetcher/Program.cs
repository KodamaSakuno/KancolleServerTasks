using Dapper;
using FurnitureFetcher;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
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

const string InfoComitterQueueName = "FurnitureAssetInfoComitter";

rabbitMqChannel.QueueDeclare(InfoComitterQueueName, true, false, false, null);
rabbitMqChannel.QueueBind(InfoComitterQueueName, CallbackExchangeName, InfoComitterQueueName);

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'furniture') FROM api_start2_item_version WHERE key = 'api_mst_furniture';"))
        return;

    await foreach (var asset in EnumerateDiffs(pg))
    {
        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.ReplyTo = InfoComitterQueueName;
        properties.ContentType = "application/json";

        var extension = asset.Type != "scripts" ? ".png" : ".json";
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = $"http://203.104.209.199/kcs2/resources/furniture/{asset.Type}/{asset.Id:000}_{asset.Suffix}{extension}",
            Directory = "/var/kancolle/furniture/pool",
            Extension = extension,
            Metadata = new Metadata(asset.Id, asset.Version, asset.DatabaseType),
        });

        rabbitMqChannel.BasicPublish(string.Empty, "AssetFileDownload", properties, body);
    }

    await pg.ExecuteAsync("INSERT INTO downloaded_version VALUES('furniture', (SELECT max(version) FROM api_start2_item_version WHERE key = 'api_mst_furniture')) ON CONFLICT (name) DO UPDATE SET version = excluded.version;");

    await transaction.CommitAsync();
};

var callbackEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
callbackEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..(8 + 32)].ToArray();
    var (id, version, type) = JsonSerializer.Deserialize<Metadata>(e.Body[(8 + 32 + 1)..].Span)!;

    await pg.ExecuteAsync("INSERT INTO furniture VALUES(@id, @type::furniture_asset_kind, @version, @hash, @timestamp);", new
    {
        id,
        type,
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

static async IAsyncEnumerable<Asset> EnumerateDiffs(NpgsqlConnection pg)
{
    foreach (var (id, isActive, version) in await pg.QueryAsync<(int, bool, int)>("SELECT id, is_active, current_version FROM furniture_diff;"))
    {
        if (!isActive)
        {
            yield return new(id, version, "normal", "static_image");
            continue;
        }

        yield return new(id, version, "thumbnail");
        yield return new(id, version, "movable", "animation_sprite");
        yield return new(id, version, "scripts", "animation_script");
    }
}
