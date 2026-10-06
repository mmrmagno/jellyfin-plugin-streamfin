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
Console.WriteLine($"{count} checks passed.");
