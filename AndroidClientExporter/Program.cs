using Docker.DotNet;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using System.Text.Json;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

var docker = new DockerClientConfiguration(new Uri("unix:/var/run/docker.sock")).CreateClient();

var realStorePrefix = configuration["AndroidClientExporter:StorePrefix"] ?? throw new InvalidOperationException("Missing StorePrefix");
const string RepoPath = "/var/kancolle/android_client_source";

var rabbitMqConnectionFactory = new ConnectionFactory()
{
    HostName = configuration["RabbitMQ:Host"],
    DispatchConsumersAsync = true,
};
using var rabbitMqConnection = rabbitMqConnectionFactory.CreateConnection();
using var rabbitMqChannel = rabbitMqConnection.CreateModel();

const string NewFileExchangeName = "NewFile";

rabbitMqChannel.ExchangeDeclare(NewFileExchangeName, ExchangeType.Direct, true);

const string QueueName = "AndroidClientExportPending";

rabbitMqChannel.QueueDeclare(QueueName, true, false, false, null);
rabbitMqChannel.QueueBind(QueueName, NewFileExchangeName, "AndroidClient");

rabbitMqChannel.BasicQos(0, 1, false);

var consumer = new AsyncEventingBasicConsumer(rabbitMqChannel);

consumer.Received += async (sender, e) =>
{
    var message = JsonSerializer.Deserialize<Message>(e.Body.Span) ?? throw new InvalidOperationException("Bad message");
    var (filename, version) = message.Metadata;

    var sourcePath = Path.Join(RepoPath, filename);

    if (Directory.Exists(sourcePath))
        Directory.Delete(sourcePath, true);
    Directory.CreateDirectory(sourcePath);

    using var repo = new Repository(RepoPath);

    var container = await docker.Containers.CreateContainerAsync(new()
    {
        Image = "ffdec",
        Cmd = new[] { "-export", "script", "/var/output", "/var/input" },
        HostConfig = new()
        {
            AutoRemove = true,
            Binds = new[]
            {
                $"{realStorePrefix}/android_client/{filename}_{version}.swf:/var/input:ro",
                $"{realStorePrefix}/android_client_source/{filename}:/var/output/scripts",
            },
        },
    });

    await docker.Containers.StartContainerAsync(container.ID, new());
    await docker.Containers.WaitContainerAsync(container.ID);

    if (repo.RetrieveStatus().IsDirty)
    {
        var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(message.Filename));

        Commands.Stage(repo, "*");

        var signature = new Signature("神樹桜乃", "kodama@sakuno.moe", timestamp);

        repo.Commit($"{filename} {version}", signature, signature);
    }

    rabbitMqChannel.BasicAck(e.DeliveryTag, false);
};

rabbitMqChannel.BasicConsume(QueueName, false, consumer);

record Message(string Filename, Metadata Metadata);
record Metadata(string Filename, string Version);
