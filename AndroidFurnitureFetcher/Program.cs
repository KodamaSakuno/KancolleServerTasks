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

const string CallbackExchangeName = "AndroidFurnitureCallback";

rabbitMqChannel.ExchangeDeclare(CallbackExchangeName, ExchangeType.Topic, true);

const string DefaultCallbackQueueName = "AndroidFurnitureCallback";

rabbitMqChannel.QueueDeclare(DefaultCallbackQueueName, true, false, false, null);
rabbitMqChannel.QueueBind(DefaultCallbackQueueName, CallbackExchangeName, string.Empty);

const string RedisTopic = "android:furniture";

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'android_furniture') FROM api_start2_item_version WHERE key = 'api_mst_furnituregraph';"))
        return;

    var redisDatabase = redis.GetDatabase();

    foreach (var (furnitureId, type, subId, version, filename) in await pg.QueryAsync<(int, string, int, int?, string?)>("SELECT id, type, sub_id, current_version, current_filename FROM android_furniture_diff;"))
    {
        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.CorrelationId = furnitureId.ToString();
        properties.ReplyTo = CallbackExchangeName;

        var extension = version.HasValue ? ".swf" : ".png";

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = $"http://203.104.209.71/kcs/resources/image/furniture/{type}/{filename ?? (subId + 1).ToString("000")}{extension}",
            Directory = "/var/kancolle/android_furniture/pool",
            Extension = extension,
        });

        await redisDatabase.HashSetAsync($"download:{RedisTopic}:{furnitureId}", "version", version ?? 1);

        rabbitMqChannel.BasicPublish(string.Empty, "AssetFileDownload", properties, body);
    }

    await pg.ExecuteAsync("INSERT INTO downloaded_version VALUES('android_furniture', (SELECT max(version) FROM api_start2_item_version WHERE key = 'api_mst_furnituregraph')) ON CONFLICT (name) DO UPDATE SET version = excluded.version;");

    await transaction.CommitAsync();
};

var callbackEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
callbackEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    var redisDatabase = redis.GetDatabase();

    var furnitureId = int.Parse(e.BasicProperties.CorrelationId);
    var version = (int)await redisDatabase.HashGetAsync($"download:{RedisTopic}:{furnitureId}", "version");

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..].ToArray();

    await pg.ExecuteAsync("INSERT INTO android_furniture VALUES(@furniture, @version, @hash, @timestamp);", new
    {
        furniture = furnitureId,
        version,
        timestamp,
        hash,
    });

    await redisDatabase.KeyDeleteAsync($"download:{RedisTopic}:{furnitureId}");

    rabbitMqChannel.BasicAck(e.DeliveryTag, false);
};

rabbitMqChannel.BasicConsume(queueName, true, updatedEventConsumer);
rabbitMqChannel.BasicConsume(DefaultCallbackQueueName, false, callbackEventConsumer);

logger.Information("Waiting...");

await Task.Delay(-1);
