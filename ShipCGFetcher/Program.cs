using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using ShipCGFetcher;
using StackExchange.Redis;
using System.Buffers.Binary;
using System.Text.Json;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

using var redis = ConnectionMultiplexer.Connect(configuration["Redis:Host"]);

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

const string RedisTopic = "ship_cg";

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'ship_cg') FROM api_start2_item_version WHERE key = 'api_mst_shipgraph';"))
        return;

    var redisDatabase = redis.GetDatabase();

    await foreach (var graphic in EnumerateDiffs(pg))
    {
        var correlationId = Guid.NewGuid().ToString();

        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.CorrelationId = correlationId;
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
        });

        await redisDatabase.HashSetAsync($"download:{RedisTopic}:{correlationId}", new HashEntry[]
        {
            new("id", graphic.Id),
            new("type", graphic.Type),
            new("is_damaged", graphic.IsDamaged),
            new("version", graphic.Version),
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

    var redisDatabase = redis.GetDatabase();

    var correlationId = e.BasicProperties.CorrelationId;

    var values = await redisDatabase.HashGetAsync($"download:{RedisTopic}:{correlationId}", new RedisValue[] { "id", "type", "is_damaged", "version" });
    var shipId = (int)values[0];
    var type = (string)values[1];
    var isDamaged = (bool)values[2];
    var version = (int)values[3];

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..].ToArray();

    await pg.ExecuteAsync("INSERT INTO ship_cg VALUES(@ship, @type::ship_cg_type, @isDamaged, @version, @hash, @timestamp);", new
    {
        ship = shipId,
        type,
        isDamaged,
        version,
        timestamp,
        hash,
    });

    await redisDatabase.KeyDeleteAsync($"download:{RedisTopic}:{correlationId}");

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
