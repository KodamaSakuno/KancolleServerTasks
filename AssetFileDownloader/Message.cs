namespace AssetFileDownloader;

sealed class Message
{
    public string Url { get; init; } = default!;
    public string Directory { get; init; } = default!;
    public string? Extension { get; init; }
}