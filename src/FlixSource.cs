using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public sealed record FlixServer(string Name, Uri Link);

// Movies and TV from the two FlixHQ layouts, ported from my flixdl downloader. Each site lists
// third-party players; the player page is read as text, packed scripts are unpacked as a pure
// string transform, and nothing from the page is ever executed.
public sealed class FlixSource(SafeHttp http) : IDisposable
{
    public static readonly Uri Watch = new("https://flixhqz.net/");
    public static readonly Uri Classic = new("https://flixhq.ws/");
    public static readonly string[] ServerOrder = ["vidmoly", "doodstream", "vidsrc"];
    private readonly MemoryCache pages = new(new MemoryCacheOptions { SizeLimit = 2000 });
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
    private static readonly Regex AnchorTag = Rx(@"<a\b([^>]*)>(.*?)</a>");
    private static readonly Regex M3u8 = Rx(@"https?://[^\s""'<>\\]+?\.m3u8(?:\?[^\s""'<>\\]*)?");
    private static readonly Regex Mp4 = Rx(@"[""']?file[""']?\s*:\s*[""'](https?://[^""']+?\.mp4(?:\?[^""']*)?)[""']");
    private static readonly Regex TrackBlock = Rx(@"\{[^{}]*?\.(?:vtt|srt)[^{}]*?\}");
    private static readonly Regex TrackFile = Rx(@"[""']?file[""']?\s*:\s*[""'](https?://[^""']+?\.(?:vtt|srt)(?:\?[^""']*)?)[""']");
    private static readonly Regex TrackLabel = Rx(@"[""']?label[""']?\s*:\s*[""']([^""']+)[""']");
    private static readonly Regex Iframe = Rx(@"<iframe[^>]+src\s*=\s*[""'](https?://[^""']+)[""']");
    private static readonly Regex Packed = Rx(@"}\s*\(\s*'(.*?)'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*'(.*?)'\.split\('\|'\)");

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var site in new[] { Watch, Classic })
        {
            try
            {
                var page = await FindAsync(site, film, ct);
                if (page is null) { errors.Add(site.Host + ": not listed"); continue; }
                var (servers, referer) = film.Kind == FilmKind.Movie ? (await MovieServersAsync(site, page, ct), page)
                    : await EpisodeServersAsync(site, page, season!.Value, episode!.Value, ct);
                foreach (var server in servers.OrderBy(s => Rank(s.Name)))
                {
                    try
                    {
                        if (await PlayerAsync(server.Link, referer, 0, ct) is not { } video) continue;
                        var known = video.Subtitles!.Select(s => s.Url).ToHashSet();
                        var extra = (await SubtitleIndexAsync(server.Link, referer, ct)).Where(s => !known.Contains(s.Url));
                        return [video with { Subtitles = Languages.Arrange(video.Subtitles!.Concat(extra)) }];
                    }
                    catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        errors.Add(server.Name);
                    }
                }
                errors.Add(site.Host + ": no working player");
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add(site.Host + ": unavailable");
            }
        }
        throw new ProviderException("No source could play this title right now (" + string.Join(", ", errors.Distinct().Take(6)) + "). Please try again later.");
    }

    public static int Rank(string name) => Array.FindIndex(ServerOrder, p => name.Contains(p, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : ServerOrder.Length;


    public static string Slug(string title) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(title).ToLowerInvariant().Replace("&", "and"),
        @"['’]", "", RegexOptions.None, TimeSpan.FromSeconds(1)), @"[^a-z0-9]+", "-", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim('-');

    private async Task<Uri?> FindAsync(Uri site, Film film, CancellationToken ct)
    {
        var key = $"{site.Host}/{film.Kind}/{film.Id}";
        if (pages.TryGetValue<Uri>(key, out var known)) return known;
        var html = await http.TextAsync(new Uri(site, "search?keyword=" + Uri.EscapeDataString(film.Title)), ct);
        var found = site == Watch ? MatchWatch(html, film) : MatchClassic(html, film);
        if (found is not null) pages.Set(key, found, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12) });
        return found;
    }

    // flixhqz.net slugs carry the title and year: /watch-movie/<title>-<year>-watch-online/.
    public static Uri? MatchWatch(string html, Film film)
    {
        var kind = film.Kind == FilmKind.Movie ? "watch-movie" : "watch-series";
        var slugs = Rx($@"href=""https://flixhqz\.net/{kind}/([a-z0-9-]+)/""").Matches(html).Select(m => m.Groups[1].Value).Distinct().ToArray();
        var title = Slug(film.Title);
        var pick = (film.Year is { } year ? slugs.FirstOrDefault(s => s == $"{title}-{year}-watch-online") : null)
            ?? slugs.FirstOrDefault(s => film.Year is { } y && (s == $"{title}-{y - 1}-watch-online" || s == $"{title}-{y + 1}-watch-online"))
            ?? (film.Year is null ? slugs.FirstOrDefault(s => s == $"{title}-watch-online") : null);
        return pick is null ? null : new Uri(Watch, $"{kind}/{pick}/");
    }

    // flixhq.ws search cards: a /movie/ or /series/ link, its title, and a year for movies.
    public static Uri? MatchClassic(string html, Film film)
    {
        var kind = film.Kind == FilmKind.Movie ? "movie" : "series";
        var title = Slug(film.Title);
        var cards = Rx(@"<div class=""flw-item"">(.*?)<div class=""clearfix""").Matches(html).Select(m => m.Groups[1].Value);
        foreach (var card in cards)
        {
            var link = Rx($@"href=""https://flixhq\.ws/{kind}/([a-z0-9-]+)/""[^>]*title=""([^""]*)""").Match(card);
            if (!link.Success || Slug(link.Groups[2].Value) != title) continue;
            var year = Rx(@"<span class=""fdi-item"">(\d{4})</span>").Match(card);
            if (film.Kind == FilmKind.Movie && film.Year is { } wanted && year.Success
                && Math.Abs(int.Parse(year.Groups[1].Value, CultureInfo.InvariantCulture) - wanted) > 1) continue;
            return new Uri(Classic, $"{kind}/{link.Groups[1].Value}/");
        }
        return null;
    }


    public static IEnumerable<(Dictionary<string, string> Attributes, string Text)> Anchors(string html, string cls) => AnchorTag.Matches(html)
        .Select(m => (Attributes: Attributes(m.Groups[1].Value), Text: Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]+>", " ",
            RegexOptions.None, TimeSpan.FromSeconds(1))), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim()))
        .Where(a => a.Attributes.TryGetValue("class", out var c) && c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls));

    private static Dictionary<string, string> Attributes(string tag) => Rx(@"([a-z-]+)\s*=\s*(?:""([^""]*)""|'([^']*)')").Matches(tag)
        .GroupBy(m => m.Groups[1].Value.ToLowerInvariant()).ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups[2].Success && g.First().Groups[2].Length > 0
            ? g.First().Groups[2].Value : g.First().Groups[3].Value));

    private static string? JsString(string html, string name) => Rx($@"\b{name}\s*=\s*['""]([^'""]+)['""]").Match(html) is { Success: true } m ? m.Groups[1].Value : null;

    private static Uri? Http(Uri page, string? link) => Uri.TryCreate(page, link?.Trim(), out var url) && url.Scheme is "https" or "http" ? url : null;

    // flixhq.ws: the page names a listing URL, whose links carry each player in data-id.
    private async Task<IReadOnlyList<FlixServer>> ListedServersAsync(Uri page, Uri referer, CancellationToken ct)
    {
        var html = await http.TextAsync(page, ct, headers: Referer(referer));
        if (JsString(html, "pl_url") is not { } listing || Http(page, listing) is not { } listingUrl) return [];
        return ParseListing(await http.TextAsync(listingUrl, ct, headers: Referer(page), open: listingUrl.Host != page.Host));
    }
    public static IReadOnlyList<FlixServer> ParseListing(string html) => Anchors(html, "sv-item")
        .Select(a => (Name: a.Attributes.GetValueOrDefault("data-srv") is { Length: > 0 } n ? n : a.Text, Link: a.Attributes.GetValueOrDefault("data-id")))
        .Where(s => s.Link is not null && Uri.TryCreate(s.Link.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
        .Select(s => new FlixServer(s.Name.Length > 0 ? s.Name : "unknown", new Uri(s.Link!.Trim()))).ToArray();

    // flixhqz.net: the player element holds a token that its ajax endpoint exchanges for players.
    private async Task<IReadOnlyList<FlixServer>> TokenServersAsync(Uri page, Uri referer, string field, CancellationToken ct)
    {
        var html = await http.TextAsync(page, ct, headers: Referer(referer));
        var shell = Rx(@"<[^>]+id=""series-player""[^>]*>").Match(html);
        var token = Rx(@"data-token=""([^""]+)""").Match(shell.Success ? shell.Value : html);
        if (!token.Success) return [];
        var body = await http.TextAsync(new Uri(page, "/ajax/ajax.php"), ct, headers: Referer(page),
            form: new Dictionary<string, string> { [field] = WebUtility.HtmlDecode(token.Groups[1].Value) });
        return ParsePlayers(body);
    }
    public static IReadOnlyList<FlixServer> ParsePlayers(string json)
    {
        using var document = JsonDocument.Parse(json.TrimStart('﻿'));
        var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
        return items.Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String
                && Uri.TryCreate(l.GetString(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            .Select(i => new FlixServer(i.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "unknown",
                new Uri(i.GetProperty("link").GetString()!))).ToArray();
    }

    private Task<IReadOnlyList<FlixServer>> MovieServersAsync(Uri site, Uri page, CancellationToken ct) =>
        site == Watch ? TokenServersAsync(page, site, "players", ct) : ListedServersAsync(page, site, ct);

    private async Task<(IReadOnlyList<FlixServer>, Uri)> EpisodeServersAsync(Uri site, Uri series, int season, int episode, CancellationToken ct)
    {
        var html = await http.TextAsync(series, ct, headers: Referer(site));
        if (site == Watch)
        {
            var link = Anchors(html, "eps-item").Select(a => Http(series, a.Attributes.GetValueOrDefault("href")))
                .FirstOrDefault(u => u is not null && u.AbsolutePath.TrimEnd('/').EndsWith($"/s{season}-e{episode}", StringComparison.Ordinal));
            if (link is null) throw new ProviderException($"Season {season} episode {episode} is not listed.");
            return (await TokenServersAsync(link, series, "players_show", ct), link);
        }
        var seasons = Anchors(html, "ss-item").Where(a => a.Attributes.ContainsKey("data-ss") && a.Attributes.ContainsKey("data-id"))
            .ToDictionary(a => a.Attributes["data-ss"], a => a.Attributes["data-id"]);
        var seasonId = seasons.GetValueOrDefault(season.ToString(CultureInfo.InvariantCulture));
        if (seasonId is null && seasons.Count == 0 && season == 1 && JsString(html, "current_url") is { } current
            && Http(series, current) is { } currentUrl)
            seasonId = System.Web.HttpUtility.ParseQueryString(currentUrl.Query)["episode"];
        if (string.IsNullOrEmpty(seasonId)) throw new ProviderException($"Season {season} is not listed.");
        var listing = await http.TextAsync(new Uri(site, "ajax/ajax.php?episode=" + Uri.EscapeDataString(seasonId)), ct, headers: Referer(series));
        var page = Anchors(listing, "eps-item").Select(a => Http(site, a.Attributes.GetValueOrDefault("href")))
            .FirstOrDefault(u => u is not null && Regex.IsMatch(u.AbsolutePath, $@"/{season}-{episode}/?\z", RegexOptions.None, TimeSpan.FromSeconds(1)));
        if (page is null) throw new ProviderException($"Season {season} episode {episode} is not listed.");
        return (await ListedServersAsync(page, series, ct), page);
    }

    private static Dictionary<string, string> Referer(Uri page) => new() { ["Referer"] = page.AbsoluteUri };


    private async Task<ResolvedVideo?> PlayerAsync(Uri embed, Uri referer, int depth, CancellationToken ct)
    {
        var (final, html) = await http.PageAsync(embed, ct, headers: Referer(referer), open: true);
        if (FindMedia(html) is { } media)
        {
            var origin = final.GetLeftPart(UriPartial.Authority);
            var headers = new Dictionary<string, string> { ["Referer"] = origin + "/", ["Origin"] = origin };
            var hls = media.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (hls && !(await http.TextAsync(media, ct, headers: headers, open: true)).TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
                throw new ProviderException("The player did not return an HLS playlist.");
            return new ResolvedVideo(media, hls ? "HLS" : "MP4", headers, false, hls, FindSubtitles(html));
        }
        if (depth >= 2) return null;
        foreach (var frame in Iframe.Matches(html).Select(m => Http(final, m.Groups[1].Value)).OfType<Uri>().Distinct().Take(4))
        {
            try { if (await PlayerAsync(frame, final, depth + 1, ct) is { } found) return found; }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException && !ct.IsCancellationRequested) { }
        }
        return null;
    }

    public static Uri? FindMedia(string html)
    {
        var blobs = Unpack(html).Prepend(html).Select(b => b.Replace("\\/", "/", StringComparison.Ordinal)).ToArray();
        foreach (var blob in blobs)
            if (M3u8.Match(blob) is { Success: true } m && Valid(m.Value) is { } url) return url;
        foreach (var blob in blobs)
            if (Mp4.Match(blob) is { Success: true } m && Valid(m.Groups[1].Value) is { } url) return url;
        return null;
    }
    private static Uri? Valid(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var url)) return null;
        try { SafeHttp.ValidateOpen(url); return url; }
        catch (ProviderException) { return null; }
    }

    public static IReadOnlyList<Subtitle> FindSubtitles(string html)
    {
        var found = new List<Subtitle>();
        foreach (var blob in Unpack(html).Prepend(html))
            foreach (Match block in TrackBlock.Matches(blob.Replace("\\/", "/", StringComparison.Ordinal)))
            {
                var file = TrackFile.Match(block.Value);
                var label = TrackLabel.Match(block.Value);
                if (!file.Success || !label.Success || label.Groups[1].Value.Trim().Length == 0
                    || label.Groups[1].Value.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)) continue;
                if (Valid(file.Groups[1].Value) is { } url) found.Add(new(label.Groups[1].Value.Trim(), Languages.Code("", label.Groups[1].Value), url));
            }
        return found.DistinctBy(s => s.Url).ToArray();
    }

    // Some players link a JSON subtitle index from their own URL (sub.info=...).
    private async Task<IReadOnlyList<Subtitle>> SubtitleIndexAsync(Uri player, Uri referer, CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(player.Query);
        foreach (var name in new[] { "sub.info", "subget", "sub_info" })
        {
            if (Valid(query[name] ?? "") is not { } index) continue;
            try { return ParseSubtitleIndex(await http.TextAsync(index, ct, headers: Referer(referer), open: true)); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or JsonException && !ct.IsCancellationRequested) { return []; }
        }
        return [];
    }
    public static IReadOnlyList<Subtitle> ParseSubtitleIndex(string json)
    {
        using var document = JsonDocument.Parse(json.TrimStart('﻿'));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        static string Field(JsonElement e, string a, string b) => e.TryGetProperty(a, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()!
            : e.TryGetProperty(b, out var y) && y.ValueKind == JsonValueKind.String ? y.GetString()! : "";
        return document.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => (Url: Valid(Field(e, "file", "src")), Label: Field(e, "label", "lang").Trim()))
            .Where(s => s.Url is not null && s.Label.Length > 0 && !s.Label.Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
            .Select(s => new Subtitle(s.Label, Languages.Code("", s.Label), s.Url!)).ToArray();
    }

    // Dean Edwards' packer: a word list plus a payload whose base-N tokens index into it.
    // Substituting the words back is a string operation; the script itself never runs.
    public static IReadOnlyList<string> Unpack(string html)
    {
        var output = new List<string>();
        foreach (Match m in Packed.Matches(html))
        {
            if (!int.TryParse(m.Groups[2].Value, out var radix) || !int.TryParse(m.Groups[3].Value, out var count) || radix is < 2 or > 62) continue;
            var words = m.Groups[4].Value.Split('|');
            var payload = m.Groups[1].Value.Replace("\\'", "'", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            // A hostile page could repeat a huge word millions of times; stop well before that.
            var size = 0L;
            output.Add(Regex.Replace(payload, @"\b\w+\b", w =>
            {
                var word = Unbase(w.Value, radix) is { } i && i < words.Length && i < count && words[i].Length > 0 ? words[i] : w.Value;
                if ((size += word.Length) > 8_000_000) throw new ProviderException("The player page is too large.");
                return word;
            }, RegexOptions.None, TimeSpan.FromSeconds(2)));
        }
        return output;
    }
    public static long? Unbase(string word, int radix)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        long value = 0;
        foreach (var ch in word)
        {
            var index = radix <= 36 ? alphabet.IndexOf(char.ToLowerInvariant(ch)) : alphabet.IndexOf(ch);
            if (index < 0 || index >= radix || value > int.MaxValue) return null;
            value = value * radix + index;
        }
        return value;
    }
    public void Dispose() => pages.Dispose();
}
