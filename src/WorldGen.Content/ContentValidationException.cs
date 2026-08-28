namespace WorldGen.Content;

public sealed class ContentValidationException(string path, string message)
    : Exception($"Некорректный контент: {path}: {message}")
{
    public string ContentPath { get; } = path;
}
