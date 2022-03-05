using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using SlotItemCGFetcher;
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

const string InfoComitterQueueName = "SlotItemCGInfoComitter";

rabbitMqChannel.QueueDeclare(InfoComitterQueueName, true, false, false, null);
rabbitMqChannel.QueueBind(InfoComitterQueueName, CallbackExchangeName, InfoComitterQueueName);

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'slotitem_cg') FROM api_start2_item_version WHERE key = 'api_mst_slotitem';"))
        return;

    await foreach (var graphic in EnumerateDiffs(pg))
    {
        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.ReplyTo = InfoComitterQueueName;
        properties.ContentType = "application/json";

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = $"http://203.104.209.199/kcs2/resources/slot/{graphic.Type}/{graphic.Id:000}_{graphic.Suffix}.png",
            Directory = "/var/kancolle/slotitem/pool",
            Extension = ".png",
            Metadata = new Metadata(graphic.Id, graphic.Type, graphic.Version),
        });

        rabbitMqChannel.BasicPublish(string.Empty, "AssetFileDownload", properties, body);
    }

    await pg.ExecuteAsync("INSERT INTO downloaded_version VALUES('slotitem_cg', (SELECT max(version) FROM api_start2_item_version WHERE key = 'api_mst_slotitem')) ON CONFLICT (name) DO UPDATE SET version = excluded.version;");

    await transaction.CommitAsync();
};

var callbackEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
callbackEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..(8 + 32)].ToArray();
    var (id, type, version) = JsonSerializer.Deserialize<Metadata>(e.Body[(8 + 32)..].Span)!;

    await pg.ExecuteAsync("INSERT INTO slotitem_cg VALUES(@id, @type::slotitem_cg_type, @version, @hash, @timestamp);", new
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

static async IAsyncEnumerable<Graphic> EnumerateDiffs(NpgsqlConnection pg)
{
    foreach (var (id, isPlane, version) in await pg.QueryAsync<(int, bool, int)>("SELECT id, is_plane, current_version FROM slotitem_cg_diff;"))
    {
        yield return new(id, version, "card");
        if ((id, version) is not (42, 1))
            yield return new(id, version, "item_character");
        yield return new(id, version, "item_on");
        yield return new(id, version, "item_up");

        if (!isPlane)
            continue;

        yield return new(id, version, "airunit_fairy");
    }
}
