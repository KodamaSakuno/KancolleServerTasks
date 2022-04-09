using Dapper;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using System.Text.Encodings.Web;
using System.Text.Json;

const string RepoPrefix = "/var/kancolle/api_start2";

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

var connectionString = new NpgsqlConnectionStringBuilder(configuration["Database"])
{
    SearchPath = "kancolle",
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

const string QueueName = "ApiStart2Exporter";

rabbitMqChannel.QueueDeclare(QueueName, true, false, false, null);
rabbitMqChannel.QueueBind(QueueName, MasterDataUpdatedExchangeName, string.Empty);

rabbitMqChannel.BasicQos(0, 1, false);

var consumer = new AsyncEventingBasicConsumer(rabbitMqChannel);

consumer.Received += async (sender, e) =>
{
    using var repo = new Repository(RepoPrefix);

    var latestTimestamp = repo.Head.Commits.FirstOrDefault()?.Committer.When ?? DateTimeOffset.MinValue;

    await using var pg = new NpgsqlConnection(connectionString);
    await pg.OpenAsync();

    foreach (var (version, raw) in await pg.QueryAsync<(DateTime, byte[])>(new("SELECT version, raw FROM api_start2_history WHERE version > @latestTimestamp ORDER BY version;", new { latestTimestamp }, flags: CommandFlags.None)))
    {
        using var document = JsonDocument.Parse(raw);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            using var stream = File.Create(Path.Join(RepoPrefix, $"{property.Name}.json"));
            using var writer = new Utf8JsonWriter(stream, new()
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = true,
            });

            property.Value.WriteTo(writer);
        }

        if (!repo.RetrieveStatus().IsDirty)
            continue;

        var timestamp = new DateTimeOffset(version, TimeSpan.FromHours(9));

        Commands.Stage(repo, "*");

        var signature = new Signature("神樹桜乃", "kodama@sakuno.moe", timestamp);

        repo.Commit(timestamp.ToString("yy.MM.dd HH:mm:ss"), signature, signature);
    }

    rabbitMqChannel.BasicAck(e.DeliveryTag, false);
};

rabbitMqChannel.BasicConsume(QueueName, false, consumer);

await Task.Delay(-1);
