using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
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

const string CallbackQueueName = "AndroidShipCGCallback";

rabbitMqChannel.QueueDeclare(CallbackQueueName, true, false, false, null);

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'android_ship_cg') FROM api_start2_item_version WHERE key = 'api_mst_shipgraph';"))
        return;

    var redisDatabase = redis.GetDatabase();

    foreach (var (shipId, version, filename) in await pg.QueryAsync<(int, int, string)>("SELECT id, current_version, current_filename FROM android_ship_cg_diff;"))
    {
        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.CorrelationId = shipId.ToString();
        properties.ReplyTo = CallbackQueueName;

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = $"http://203.104.209.71/kcs/resources/swf/ships/{filename}.swf",
            Directory = "/var/kancolle/android_ship_cg/pool",
            Extension = ".swf",
        });

        await redisDatabase.HashSetAsync($"download:android:ship_cg:{shipId}", "version", version);

        rabbitMqChannel.BasicPublish(string.Empty, "AssetFileDownload", properties, body);
    }

    await pg.ExecuteAsync("INSERT INTO downloaded_version VALUES('android_ship_cg', (SELECT max(version) FROM api_start2_item_version WHERE key = 'api_mst_shipgraph')) ON CONFLICT (name) DO UPDATE SET version = excluded.version;");

    await transaction.CommitAsync();
};

var callbackEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
callbackEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    var redisDatabase = redis.GetDatabase();

    var shipId = int.Parse(e.BasicProperties.CorrelationId);
    var version = (int)await redisDatabase.HashGetAsync($"download:android:ship_cg:{shipId}", "version");

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..].ToArray();

    await pg.ExecuteAsync("INSERT INTO android_ship_cg VALUES(@ship, @version, @hash, @timestamp);", new
    {
        ship = shipId,
        version,
        timestamp,
        hash,
    });

    await redisDatabase.KeyDeleteAsync($"download:android:ship_cg:{shipId}");

    rabbitMqChannel.BasicAck(e.DeliveryTag, false);
};

rabbitMqChannel.BasicConsume(queueName, true, updatedEventConsumer);
rabbitMqChannel.BasicConsume(CallbackQueueName, false, callbackEventConsumer);

logger.Information("Waiting...");

await Task.Delay(-1);
