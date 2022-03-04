using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using ShipCGFetcher;
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

const string CallbackExchangeName = "ShipCGCallback";

rabbitMqChannel.ExchangeDeclare(CallbackExchangeName, ExchangeType.Topic, true);

const string DefaultCallbackQueueName = "ShipCGCallback";

rabbitMqChannel.QueueDeclare(DefaultCallbackQueueName, true, false, false, null);
rabbitMqChannel.QueueBind(DefaultCallbackQueueName, CallbackExchangeName, string.Empty);

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'ship_cg') FROM api_start2_item_version WHERE key = 'api_mst_shipgraph';"))
        return;

    await foreach (var graphic in EnumerateDiffs(pg))
    {
        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.ReplyTo = CallbackExchangeName;
        properties.ContentType = "application/json";

        const string Prefix = "http://203.104.209.199/kcs2/resources/ship/";
        const string NormalUrl = Prefix + "{0}/{1:0000}_{2}.png";
        const string DamagedUrl = Prefix + "{0}_dmg/{1:0000}_{2}.png";

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = string.Format(graphic.IsDamaged ? DamagedUrl : NormalUrl, graphic.Type, graphic.Id, graphic.Suffix),
            Directory = "/var/kancolle/ship_cg/pool",
            Extension = ".png",
            Metadata = new Metadata(graphic.Id, graphic.Type, graphic.IsDamaged, graphic.Version),
        });

        rabbitMqChannel.BasicPublish(string.Empty, "AssetFileDownload", properties, body);
    }

    await pg.ExecuteAsync("INSERT INTO downloaded_version VALUES('ship_cg', (SELECT max(version) FROM api_start2_item_version WHERE key = 'api_mst_shipgraph')) ON CONFLICT (name) DO UPDATE SET version = excluded.version;");

    await transaction.CommitAsync();
};

var callbackEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
callbackEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..].ToArray();
    var (id, type, isDamaged, version) = JsonSerializer.Deserialize<Metadata>(e.Body[(8 + 256)..].Span)!;

    await pg.ExecuteAsync("INSERT INTO ship_cg VALUES(@id, @type::ship_cg_type, @isDamaged, @version, @hash, @timestamp);", new
    {
        id,
        type,
        isDamaged,
        version,
        timestamp,
        hash,
    });

    rabbitMqChannel.BasicAck(e.DeliveryTag, false);
};

rabbitMqChannel.BasicConsume(queueName, true, updatedEventConsumer);
rabbitMqChannel.BasicConsume(DefaultCallbackQueueName, false, callbackEventConsumer);

logger.Information("Waiting...");

await Task.Delay(-1);

static async IAsyncEnumerable<Graphic> EnumerateDiffs(NpgsqlConnection pg)
{
    foreach (var (shipId, version, filename) in await pg.QueryAsync<(int, int, string)>("SELECT id, current_version, current_filename FROM ship_cg_diff;"))
    {
        yield return new(shipId, version, "full", false, filename);
        yield return new(shipId, version, "full", true, filename);
        yield return new(shipId, version, "card", false, filename);
        yield return new(shipId, version, "card", true, filename);
        yield return new(shipId, version, "character_up", false, filename);
        yield return new(shipId, version, "character_up", true, filename);
        yield return new(shipId, version, "remodel", false, filename);
        yield return new(shipId, version, "remodel", true, filename);
    }
}
