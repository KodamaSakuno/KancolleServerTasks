namespace AssetFileDownloader;

sealed class Message
{
    public string Url { get; init; }
    public string Directory { get; init; }
}