using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

Directory.CreateDirectory("/var/android_client/scenes");
Directory.CreateDirectory("/var/android_client/resources");

using var client = new HttpClient();

var bot = new TelegramBotClient(configuration["Telegram:Token"], client);

var connectionStringBuilder = new NpgsqlConnectionStringBuilder(configuration["Database"])
{
    SearchPath = "kancolle_resources",
};
await using var pg = new NpgsqlConnection(connectionStringBuilder.ToString());

var rabbitMqConnectionFactory = new ConnectionFactory()
{
    HostName = configuration["RabbitMQ:Host"],
    DispatchConsumersAsync = true,
};
using var rabbitMqConnection = rabbitMqConnectionFactory.CreateConnection();
using var rabbitMqChannel = rabbitMqConnection.CreateModel();

rabbitMqChannel.QueueDeclare("AndroidClientFile", true, false, false, null);
rabbitMqChannel.BasicQos(0, 1, false);

var consumer = new AsyncEventingBasicConsumer(rabbitMqChannel);

consumer.Received += async (sender, e) =>
{
    var (filename, version) = Parse(e.Body.Span);
    var (content, lastModified) = await FetchFileAsync(client, filename + ".swf");

    var localFilename = Path.Join("/var/android_client", $"{filename}_{version}.swf");

    await File.WriteAllBytesAsync(localFilename, content);
    File.SetLastWriteTimeUtc(localFilename, lastModified.UtcDateTime);

    await pg.ExecuteAsync("INSERT INTO android_client VALUES(@filename || '.swf', @version, @timestamp);", new { filename, version, timestamp = lastModified });

    rabbitMqChannel.BasicAck(e.DeliveryTag, false);

    await bot.SendTextMessageAsync(configuration["Telegram:ChatId"], $"{filename} *({version})* saved", ParseMode.Markdown);

    logger.Information("Saved: {Filename} ({Version})", filename, version);
};

rabbitMqChannel.BasicConsume("AndroidClientFile", false, consumer);

logger.Information("Waiting...");

await Task.Delay(-1);

static (string, string) Parse(ReadOnlySpan<byte> data)
{
    var filenameLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    data = data[4..];

    var filename = Encoding.UTF8.GetString(data[..filenameLength]);
    data = data[filenameLength..];

    var versionLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    data = data[4..];

    var version = Encoding.UTF8.GetString(data);

    return (filename, version);
}
static async ValueTask<(byte[], DateTimeOffset)> FetchFileAsync(HttpClient client, string filename)
{
    var isCore = filename == "scenes/CoreMain.swf";

    using var response = await client.GetAsync("http://203.104.209.71/kca/" + filename);

    response.EnsureSuccessStatusCode();

    if (filename != "scenes/CoreMain.swf")
        return (await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.LastModified!.Value);

    var size = (int)response.Content.Headers.ContentLength!.Value;
    var result = new byte[size];
    var chunkSize = (size - 128) / 8;

    using var inputStream = await response.Content.ReadAsStreamAsync();
    using var outputStream = new MemoryStream(result);

    var input = new BinaryReader(inputStream);
    var output = new BinaryWriter(outputStream);

    output.Write(input.ReadBytes(chunkSize + 128));

    var segments = new byte[7][];
    for (var i = 0; i < 7; i++)
        segments[i] = input.ReadBytes(chunkSize);

    output.Write(segments[6]);
    output.Write(segments[1]);
    output.Write(segments[4]);
    output.Write(segments[3]);
    output.Write(segments[2]);
    output.Write(segments[5]);
    output.Write(segments[0]);

    output.Write(input.ReadBytes(size - 128 - chunkSize * 8));

    return (result, response.Content.Headers.LastModified!.Value);
}
