using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace StreamFin;

// Adapted from Seanime-contributions/Seanime-Providers (MIT), AnimeGG by Pal.
// Parse data only: never evaluate scripts or load the upstream page in the client.
public sealed class AnimeGgSource(SafeHttp http) : IAnimeSource
{
    public string Id => "animegg";
    public string Name => "AnimeGG";
    private static readonly Uri Origin = new("https://www.animegg.org/");
    private static MatchCollection Matches(string html, string pattern) => Regex.Matches(html, pattern,
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    // Decode before stripping, so encoded markup cannot turn back into tags; stray brackets go too.
    public static string Clean(string text) => Regex.Replace(WebUtility.HtmlDecode(text), "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1))
        .Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal).Trim();
    public static string Normalize(string title) => Regex.Replace(Clean(title).ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    public static void ValidateId(string id)
    {
        if (id.Contains("..", StringComparison.Ordinal) || !Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9.-]{0,199}\z", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            throw new ProviderException("Invalid source item identifier.");
    }
    public async Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct)
        => ParseSearch(await http.TextAsync(new Uri(Origin, "search/?q=" + Uri.EscapeDataString(title)), ct));

    public static IReadOnlyList<SourceTitle> ParseSearch(string html) => Matches(html,
        "<a\\b[^>]*href=\"/series/([^\"]+)\"[^>]*class=\"mse\"[^>]*>.*?<h2[^>]*>(.*?)</h2>")
        .Select(m => new SourceTitle(m.Groups[1].Value, Clean(m.Groups[2].Value)))
        .DistinctBy(x => x.Id).ToArray();

    public async Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesId, Anime anime, CancellationToken ct)
    {
        ValidateId(seriesId);
        var result = ParseEpisodes(await http.TextAsync(new Uri(Origin, "series/" + seriesId), ct));
        if (result.Count == 0) throw new ProviderException("No episodes were found on AnimeGG for this title.");
        return result;
    }
    public static IReadOnlyList<SourceEpisode> ParseEpisodes(string html) => Matches(html,
        "<a\\b[^>]*href=\"/([^\"]+)\"[^>]*class=\"anm_det_pop\"[^>]*>.*?<strong[^>]*>(.*?)</strong>.*?<i[^>]*class=\"anititle\"[^>]*>(.*?)</i>")
        .Select(m =>
        {
            var number = Regex.Match(m.Groups[1].Value, @"-episode-(\d+(?:\.\d+)?)", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!number.Success) return new SourceEpisode(m.Groups[1].Value, 0, Clean(m.Groups[3].Value));
            // A number too large for an episode is page damage, not an episode.
            return decimal.TryParse(number.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n <= int.MaxValue
                ? new SourceEpisode(m.Groups[1].Value, n, Clean(m.Groups[3].Value)) : null;
        }).OfType<SourceEpisode>().DistinctBy(e => e.Id).OrderBy(e => e.Number).ToArray();

    public static string ParseSubEmbed(string html)
    {
        var match = Matches(html, "<div[^>]*id=\"subbed-Animegg\"[^>]*>\\s*<iframe[^>]*src=\"(/embed/[0-9]+)\"").FirstOrDefault();
        return match?.Groups[1].Value ?? throw new ProviderException("An English-subtitled version is unavailable. Dubbed audio has not been substituted.");
    }

    public static IReadOnlyList<ResolvedVideo> ParseVideos(string html)
    {
        var array = Matches(html, @"var\s+videoSources\s*=\s*(\[.*?\])").FirstOrDefault()?.Groups[1].Value ?? "";
        var result = Matches(array, "\\{[^}]*?file:\\s*\"([^\"]+)\"[^}]*?label:\\s*\"([^\"]+)\"[^}]*\\}")
            .Select(m => new ResolvedVideo(new Uri(Origin, WebUtility.HtmlDecode(m.Groups[1].Value)), m.Groups[2].Value,
                new Dictionary<string, string> { ["Referer"] = Origin.AbsoluteUri }, true))
            .OrderByDescending(v => int.TryParse(Regex.Match(v.Quality, @"\d+").Value, out var n) ? n : 0).ToArray();
        foreach (var video in result) SafeHttp.Validate(video.Url, true);
        if (result.Length == 0) throw new ProviderException("AnimeGG did not return a playable video. The source adapter may need an update.");
        return result;
    }

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string episodeId, CancellationToken ct)
    {
        ValidateId(episodeId);
        var html = await http.TextAsync(new Uri(Origin, episodeId), ct);
        var embed = ParseSubEmbed(html);
        return ParseVideos(await http.TextAsync(new Uri(Origin, embed), ct));
    }
}
