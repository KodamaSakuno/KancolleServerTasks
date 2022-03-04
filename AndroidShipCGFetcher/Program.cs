using AndroidShipCGFetcher;
using Dapper;
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

const string CallbackExchangeName = "AndroidShipCGCallback";

rabbitMqChannel.ExchangeDeclare(CallbackExchangeName, ExchangeType.Direct, true);

const string DefaultCallbackQueueName = "AndroidShipCGCallback";

rabbitMqChannel.QueueDeclare(DefaultCallbackQueueName, true, false, false, null);
rabbitMqChannel.QueueBind(DefaultCallbackQueueName, CallbackExchangeName, string.Empty);

var updatedEventConsumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
updatedEventConsumer.Received += async (sender, e) =>
{
    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    await using var transaction = await pg.BeginTransactionAsync();

    if (await pg.ExecuteScalarAsync<bool>("SELECT max(version) = (SELECT version FROM downloaded_version WHERE name = 'android_ship_cg') FROM api_start2_item_version WHERE key = 'api_mst_shipgraph';"))
        return;

    foreach (var (shipId, version, filename) in await pg.QueryAsync<(int, int, string)>("SELECT id, current_version, current_filename FROM android_ship_cg_diff;"))
    {
        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.ReplyTo = CallbackExchangeName;
        properties.ContentType = "application/json";

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = $"http://203.104.209.71/kcs/resources/swf/ships/{filename}.swf",
            Directory = "/var/kancolle/android_ship_cg/pool",
            Extension = ".swf",
            Metadata = new Metadata(shipId, version),
        });

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

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(e.Body.Span));
    var hash = e.Body[8..(8 + 256)].ToArray();
    var (id, version) = JsonSerializer.Deserialize<Metadata>(e.Body[(8 + 256)..].Span)!;

    await pg.ExecuteAsync("INSERT INTO android_ship_cg VALUES(@id, @version, @hash, @timestamp);", new
    {
        id,
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
