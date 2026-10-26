namespace StreamFin;

public sealed record Anime(int Id, string Title, string[] Aliases, int? Year, string Overview, string? Image, int? Duration, bool IsAdult, string[] Genres,
    int? MalId = null, int? AvailableEpisodes = null);
public sealed record CatalogPage(IReadOnlyList<Anime> Items, bool HasNext);
public sealed record SourceTitle(string Id, string Title, int? Year = null);
public sealed record SourceEpisode(string Id, decimal Number, string Title);
public sealed record Subtitle(string Label, string Language, Uri Url);
public sealed record ResolvedVideo(Uri Url, string Quality, IReadOnlyDictionary<string, string> Headers, bool HardcodedEnglishSubtitles,
    bool Hls = false, IReadOnlyList<Subtitle>? Subtitles = null);

public interface IAnimeSource
{
    string Id { get; }
    string Name { get; }
    Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct);
    Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesId, Anime anime, CancellationToken ct);
    Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string episodeId, CancellationToken ct);
}

public interface IDirectAnimeSource : IAnimeSource
{
    SourceTitle? Direct(Anime anime);
}

public class ProviderException(string message) : Exception(message);
public sealed class RateLimitedException(string message, TimeSpan? retryAfter) : ProviderException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
