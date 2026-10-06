namespace AniFin;

public sealed record Anime(int Id, string Title, string[] Aliases, int? Year, string Overview, string? Image, int? Duration);
public sealed record CatalogPage(IReadOnlyList<Anime> Items, bool HasNext);
public sealed record SourceTitle(string Id, string Title, int? Year = null);
public sealed record SourceEpisode(string Id, decimal Number, string Title);
public sealed record ResolvedVideo(Uri Url, string Quality, IReadOnlyDictionary<string, string> Headers, bool HardcodedEnglishSubtitles);

// A provider handles only its own website. Jellyfin, catalog metadata, and permissions live elsewhere.
public interface IAnimeSource
{
    string Id { get; }
    Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct);
    Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesId, CancellationToken ct);
    Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string episodeId, CancellationToken ct);
}

public sealed class ProviderException(string message) : Exception(message);
