using System.Net;
using AniFin;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
void Reject(Action action, string name) { try { action(); } catch (ProviderException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }

var titles = AnimeGgSource.ParseSearch("<a href=\"/series/test-show\" class=\"mse\"><h2>Test &amp; Show</h2></a><a href=\"/series/test-show\" class=\"mse\"><h2>Duplicate</h2></a>");
Check(titles.Count == 1 && titles[0].Title == "Test & Show", "search decodes entities and deduplicates");
var episodes = AnimeGgSource.ParseEpisodes("<a href=\"/test-episode-10\" class=\"anm_det_pop\"><strong>10</strong><i class=\"anititle\">Ten</i></a><a href=\"/test-episode-2\" class=\"anm_det_pop\"><strong>2</strong><i class=\"anititle\">Two</i></a><a href=\"/test-episode-2.5\" class=\"anm_det_pop\"><strong>2.5</strong><i class=\"anititle\">Special</i></a>");
Check(episodes.Select(e => e.Number).SequenceEqual(new decimal[] { 2, 2.5m, 10 }), "episodes sort numerically and preserve specials");
Reject(() => AnimeGgSource.ParseSubEmbed("<div id=\"dubbed-Animegg\"><iframe src=\"/embed/999\">"), "dub-only episode is rejected");
Check(AnimeGgSource.ParseSubEmbed("<div id=\"subbed-Animegg\" class=\"tab-pane\">\n<iframe src=\"/embed/123\">") == "/embed/123", "selects the subtitled embed");
var videos = AnimeGgSource.ParseVideos("var videoSources = [{file: \"/play/1/video.mp4\", label: \"480p\"},{file: \"/play/2/video.mp4\", label: \"1080p\"}];");
Check(videos[0].Quality == "1080p" && videos[0].Url.Host == "www.animegg.org", "highest source quality and absolute URL");
Reject(() => AnimeGgSource.ParseVideos("<script>throw new Error('not data')</script>"), "malformed embed has an explicit failure");
Reject(() => AnimeGgSource.ParseVideos("var videoSources = [{file: \"https://localhost/private\", label: \"1080p\"}];"), "malicious media destination rejected");
foreach (var id in new[] { "../config", "https://evil.example", "test?override=x", "a/b", "" }) Reject(() => AnimeGgSource.ValidateId(id), "invalid ID " + id);
foreach (var uri in new[] { "http://www.animegg.org/", "https://animegg.org.evil.example/", "https://user@www.animegg.org/", "https://127.0.0.1/", "https://vidcache.net:22/" })
    Reject(() => SafeHttp.Validate(new Uri(uri), true), "unsafe URL " + uri);
foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "172.16.0.1", "192.168.1.1", "169.254.169.254", "100.64.0.1", "::1", "::ffff:127.0.0.1", "fd00::1", "fe80::1" })
    Check(!SafeHttp.IsPublicAddress(IPAddress.Parse(address)), "private address rejected " + address);
Check(SafeHttp.IsPublicAddress(IPAddress.Parse("1.1.1.1")), "public IPv4 accepted");
Check(AniListCatalog.CurrentSeason(new DateTime(2026, 10, 1)) == ("FALL", 2026), "October season boundary");
Check(AniListCatalog.CurrentSeason(new DateTime(2027, 1, 1)) == ("WINTER", 2027), "January season boundary");
Check(!Plugin.Allows(Guid.NewGuid()) && !Plugin.Allows(Guid.Empty), "unconfigured plugin denies all accounts");
var ordinary = new Anime(1, "Example", [], 2026, "", null, 24, false, ["Action"]);
Check(AniListCatalog.IsAllowed(ordinary, true, true), "ordinary anime passes both filters");
Check(!AniListCatalog.IsAllowed(ordinary with { IsAdult = true }, true, false), "adult filter excludes explicit titles");
Check(!AniListCatalog.IsAllowed(ordinary with { Genres = ["Ecchi"] }, false, true), "ecchi filter applies independently of adult flag");
Check(AniListCatalog.IsAllowed(ordinary with { Genres = ["Ecchi"] }, true, false), "ecchi option can be disabled separately");
Check(AniListCatalog.IsAllowed(ordinary with { IsAdult = true }, false, false), "filters respect explicitly disabled configuration");
var heavenTitles = AnimeHeavenSource.ParseSearch("""
<div class='similarimg'><a href='anime.php?abc12'><img alt='Test &amp; Show' src='cover'></a></div>
<a href="anime.php?abc12"><img alt="Duplicate"></a><a href='anime.php?dub12'><img alt='Test Show (Dub)'></a>
<a href='advert.php?x'><img alt='Ad'></a>
""");
Check(heavenTitles.Count == 1 && heavenTitles[0] == new SourceTitle("abc12", "Test & Show"), "AnimeHeaven search decodes, deduplicates and excludes dub entries");
var heavenEpisodes = AnimeHeavenSource.ParseEpisodes("""
<a id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" onclick='ignored()'><div class='watch2'>10</div></a>
<a id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'><div class="watch2">2.5</div></a>
<a id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'><div class='watch2'>2.5</div></a>
""");
Check(heavenEpisodes.Select(e => e.Number).SequenceEqual(new[] { 2.5m, 10m }), "AnimeHeaven episodes use stable keys and numeric order");
foreach (var bad in new[] { "../x", "a&other=1", "a;key=evil", "a/b", "", "https://localhost" })
    Reject(() => AnimeHeavenSource.ValidateId(bad), "AnimeHeaven rejects identifier " + bad);
Reject(() => AnimeHeavenSource.ValidateId("abc12", episode: true), "AnimeHeaven gate requires an episode key");
var heavenVideo = AnimeHeavenSource.ParseVideos("""<video><source src='https://ax.animeheaven.me/video.mp4?token&amp;expiry' type='video/mp4'></video>""")[0];
Check(heavenVideo.Url.Query == "?token&expiry" && heavenVideo.HardcodedEnglishSubtitles, "AnimeHeaven parses escaped MP4 URL");
Check(heavenVideo.Headers["Referer"] == "https://animeheaven.me/" && heavenVideo.Headers["Origin"] == "https://animeheaven.me", "provider-specific media headers");
Reject(() => AnimeHeavenSource.ParseVideos("<script>execute()</script>"), "AnimeHeaven missing video fails explicitly");
foreach (var bad in new[] { "https://localhost/video.mp4", "http://ax.animeheaven.me/video.mp4", "https://ax.animeheaven.me:8161/video.mp4", "https://ax.animeheaven.me.evil.example/video.mp4", "https://ax.animeheaven.me/private" })
    Reject(() => AnimeHeavenSource.ParseVideos($"<source src='{bad}'>"), "AnimeHeaven rejects stream " + bad);
var rotated = AnimeHeavenSource.ParseVideos("""<source src='https://unknown.example/video.mp4'><source src='https://ct.animeheaven.me/video.mp4?valid'>""");
Check(rotated.Count == 1 && rotated[0].Url.Host == "ct.animeheaven.me", "unrecognized rotated CDN cannot hide a validated backup");
SafeHttp.Validate(heavenVideo.Url, true);
Check(true, "AnimeHeaven CDN permitted for media");
Reject(() => SafeHttp.Validate(heavenVideo.Url, false), "AnimeHeaven CDN excluded from metadata requests");
Reject(() => SafeHttp.Validate(new Uri("https://other.animeheaven.me/video.mp4"), true), "unreviewed AnimeHeaven subdomain denied");
var legacyKey = SourceRegistry.Key("animegg", "cowboy-bebop-episode-1");
Check(legacyKey == "cowboy-bebop-episode-1" && SourceRegistry.Split(legacyKey) == ("animegg", legacyKey), "legacy AnimeGG IDs unchanged");
Check(SourceRegistry.Split(SourceRegistry.Key("animeheaven", "abc12")) == ("animeheaven", "abc12"), "provider keys round trip without collisions");
var config = new Configuration { Enabled = true, AnimeGgEnabled = false, AnimeHeavenEnabled = true };
Check(!SourceRegistry.IsEnabled("animegg", config) && SourceRegistry.IsEnabled("animeheaven", config), "provider enable switches are independent");
Check(!SourceRegistry.IsEnabled("unknown", config) && !SourceRegistry.IsEnabled("animeheaven", new Configuration()), "unknown or globally disabled providers denied");
var matched = await SourceRegistry.MatchAsync(new FakeSource((_, _) => Task.FromResult<IReadOnlyList<SourceTitle>>([new("a", "Example"), new("b", "Example Season 2")])), ordinary, default);
Check(matched.Titles.Count == 1 && matched.Titles[0].Id == "a", "source matching prefers exact title over another season");
var unrelated = await SourceRegistry.MatchAsync(new FakeSource((_, _) => Task.FromResult<IReadOnlyList<SourceTitle>>([new("ad", "Unrelated promoted title")])), ordinary, default);
Check(unrelated.Titles.Count == 0, "unrelated promoted titles cannot become source matches");
var failure = await SourceRegistry.MatchAsync(new FakeSource((_, _) => throw new HttpRequestException("upstream failure")), ordinary, default);
Check(failure.Error is not null && failure.Titles.Count == 0, "one provider failure becomes a source-specific status");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await SourceRegistry.MatchAsync(new FakeSource((_, ct) => Task.FromCanceled<IReadOnlyList<SourceTitle>>(ct)), ordinary, cancelled.Token); throw new Exception("Cancellation swallowed"); }
    catch (OperationCanceledException) { Check(true, "navigation cancellation propagates"); }
}
var redirectResponse = AnimeHeavenPages.ParseResponse("HTTP/1.1 302 Found\r\nLocation: /gate.php\r\n\r\n");
Check(redirectResponse.Status == 302 && redirectResponse.Location == "/gate.php", "curl page redirects are parsed for validation before following");
Check(AnimeHeavenPages.ParseResponse("HTTP/2 200\r\nContent-Type: text/html\r\n\r\n<body>ok</body>").Body == "<body>ok</body>", "curl page headers are separate from body");
Reject(() => AnimeHeavenPages.ParseResponse("<html>not an HTTP response</html>"), "malformed curl response rejected");
if (args.Contains("--provider-live"))
{
    using var http = new SafeHttp();
    var heaven = new AnimeHeavenSource();
    var results = await heaven.SearchAsync("Cowboy Bebop", default);
    var series = results.Single(t => t.Title == "Cowboy Bebop");
    Console.WriteLine("Live matched title: " + series.Title + " / " + series.Id);
    var listed = await heaven.EpisodesAsync(series.Id, default);
    Check(listed.Count == 26 && listed[0].Number == 1, "live AnimeHeaven title and episode listing");
    var gate = await AnimeHeavenPages.TextAsync(new Uri("https://animeheaven.me/gate.php"), default, headers: new Dictionary<string, string> { ["Cookie"] = "key=" + listed[0].Id, ["Referer"] = "https://animeheaven.me/anime.php" });
    foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(gate, "<source[^>]+>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    {
        var link = System.Text.RegularExpressions.Regex.Match(tag.Value, @"src=['""]([^'""]+)['""]").Groups[1].Value;
        if (Uri.TryCreate(link, UriKind.Absolute, out var parsed)) Console.WriteLine("Observed media destination: " + parsed.GetLeftPart(UriPartial.Path));
    }
    var video = AnimeHeavenSource.ParseVideos(gate)[0];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var response = await http.OpenAsync(video.Url, true, timeout.Token, range: "bytes=0-1023", headers: video.Headers);
    Check(response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentType?.MediaType == "video/mp4", "live AnimeHeaven range request through SafeHttp");
    var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
    Check(bytes.Length == 1024, "live AnimeHeaven bounded video read");
}
Console.WriteLine($"{count} checks passed.");

sealed class FakeSource(Func<string, CancellationToken, Task<IReadOnlyList<SourceTitle>>> search) : IAnimeSource
{
    public string Id => "fake";
    public string Name => "Fake";
    public Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct) => search(title, ct);
    public Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
}
