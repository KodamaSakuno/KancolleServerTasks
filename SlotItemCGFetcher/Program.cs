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

const string CallbackQueueName = "SlotItemCGCallback";

rabbitMqChannel.QueueDeclare(CallbackQueueName, true, false, false, null);

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'slotitem_cg') FROM api_start2_item_version WHERE key = 'api_mst_slotitem';"))
        return;

    var redisDatabase = redis.GetDatabase();

    await foreach (var graphic in EnumerateDiffs(pg))
    {
        var correlationId = Guid.NewGuid().ToString();

        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.CorrelationId = correlationId;
        properties.ReplyTo = CallbackQueueName;

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = $"http://203.104.209.199/kcs2/resources/slot/{graphic.Type}/{graphic.Id:000}_{graphic.Suffix}.png",
            Directory = "/var/kancolle/slotitem/pool",
        });

        await redisDatabase.HashSetAsync($"download:slotitem_cg:{correlationId}", new HashEntry[]
        {
            new("id", graphic.Id),
            new("type", graphic.Type),
            new("version", graphic.Version),
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

    var redisDatabase = redis.GetDatabase();

    var correlationId = e.BasicProperties.CorrelationId;

    var values = await redisDatabase.HashGetAsync($"download:slotitem_cg:{correlationId}", new RedisValue[] { "id", "type", "version" });
    var slotItemId = (int)values[0];
    var type = (string)values[1];
    var version = (int)values[2];

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..].ToArray();

    await pg.ExecuteAsync("INSERT INTO slotitem_cg VALUES(@slotItem, @type::slotitem_cg_type, @version, @hash, @timestamp);", new
    {
        slotItem = slotItemId,
        type,
        version,
        timestamp,
        hash,
    });

    await redisDatabase.KeyDeleteAsync($"download:slotitem_cg:{correlationId}");

    rabbitMqChannel.BasicAck(e.DeliveryTag, false);
};

rabbitMqChannel.BasicConsume(queueName, true, updatedEventConsumer);
rabbitMqChannel.BasicConsume(CallbackQueueName, false, callbackEventConsumer);

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
