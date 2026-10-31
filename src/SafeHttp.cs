using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StreamFin;

// Source pages control links and redirects. Validate both the hostname and the address
// actually connected to, including on every redirect, so this cannot become a LAN proxy.
public sealed class SafeHttp : IDisposable
{
    public const string UserAgent = "Mozilla/5.0 StreamFin/0.5";
    private readonly HttpClient client;
    public SafeHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a)))
                    throw new HttpRequestException("Source resolved to a non-public address.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        };
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        // NAT64 (64:ff9b::/96) carries an IPv4 address in its last four bytes; judge that one.
        if (b.Length == 16 && b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0))
            return IsPublicAddress(new IPAddress(b[12..]));
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20; // global unicast only
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
            && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 198 && b[1] is 18 or 19);
    }

    // Sites and catalogs StreamFin talks to by name. Embed players and HLS CDNs rotate
    // hostnames weekly, so those use the open policy below instead of a list.
    public static bool IsKnownSite(string host) => host is "www.animegg.org" or "animegg.org" or "graphql.anilist.co" or "animeheaven.me"
        or "zokoanime.video" or "api.themoviedb.org" or "flixhqz.net" or "flixhq.ws" or MusicLyrics.Host
        or MusicRadio.MusicBrainzHost or MusicRadio.ListenBrainzHost or MusicRadio.LastFmHost || host == MusicCatalog.ApiHost;

    private static string Host(Uri uri)
    {
        try { return uri.IdnHost; }
        catch (UriFormatException) { throw new ProviderException("Source returned an unsupported destination."); }
    }

    // Where yt-dlp and deno are published. Only the tool download may go there.
    public static bool IsToolHost(string host) => host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    public static void Validate(Uri uri, bool media, bool tools = false)
    {
        var host = Host(uri).ToLowerInvariant();
        var site = IsKnownSite(host) || tools && IsToolHost(host);
        var heaven = media && AnimeHeavenSource.IsMediaHost(host);
        var cdn = media && (host == "vidcache.net" || host.EndsWith(".vidcache.net", StringComparison.Ordinal));
        // YouTube's audio comes from rotating hosts under this name; yt-dlp only hands out https ones.
        var youtube = media && host.EndsWith(".googlevideo.com", StringComparison.Ordinal);
        if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || (!site && !cdn && !heaven && !youtube)
            || (!cdn && uri.Port != 443) || (cdn && uri.Port is not (443 or 8161 or 8162 or 8163 or 8164 or 8165 or 8166)))
            throw new ProviderException("Source returned an unsupported destination.");
    }

    public static bool Allowed(Uri uri, bool media)
    {
        try { Validate(uri, media); return true; }
        catch (ProviderException) { return false; }
    }

    // Any public web host, still behind the connect-time address check. Ports below 1024
    // other than the web defaults are refused so a page cannot aim requests at mail or SSH.
    public static void ValidateOpen(Uri uri)
    {
        if (uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.HostNameType is UriHostNameType.Unknown
            || !(uri.IsDefaultPort || uri.Port >= 1024))
            throw new ProviderException("Source returned an unsupported destination.");
        if (IPAddress.TryParse(Host(uri).Trim('[', ']'), out var literal) && !IsPublicAddress(literal))
            throw new ProviderException("Source returned an unsupported destination.");
    }

    public async Task<HttpResponseMessage> OpenAsync(Uri uri, bool media, CancellationToken ct,
        string? range = null, string? json = null, bool head = false, IReadOnlyDictionary<string, string>? headers = null,
        bool open = false, IReadOnlyDictionary<string, string>? form = null, bool tools = false)
    {
        var originalHost = uri.IdnHost;
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (open) ValidateOpen(uri);
            else Validate(uri, media, tools);
            using var request = new HttpRequestMessage(head ? HttpMethod.Head : json is null && form is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            else if (form is not null) request.Content = new FormUrlEncodedContent(form);
            if (headers is not null)
                foreach (var pair in headers)
                {
                    if (pair.Key is not ("Referer" or "Origin" or "Cookie" or "Authorization")
                        || (pair.Key == "Authorization" && originalHost != "api.themoviedb.org")) throw new ProviderException("Unsupported source header.");
                    // Gate cookies and API tokens belong to one website, never forward them to a redirect elsewhere.
                    if (pair.Key is not ("Cookie" or "Authorization") || uri.IdnHost == originalHost) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
            // MetaBrainz wants to know whom to contact; the default name stays for everything else.
            if (uri.IdnHost is MusicRadio.MusicBrainzHost or MusicRadio.ListenBrainzHost) request.Headers.TryAddWithoutValidation("User-Agent", MusicRadio.ContactAgent);
            if (range is not null) request.Headers.Range = System.Net.Http.Headers.RangeHeaderValue.Parse(range);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                response.Dispose();
                continue;
            }
            return response;
        }
        throw new ProviderException("Too many source redirects.");
    }

    public async Task<string> TextAsync(Uri uri, CancellationToken ct, string? json = null, IReadOnlyDictionary<string, string>? headers = null,
        bool open = false, IReadOnlyDictionary<string, string>? form = null, TimeSpan? limit = null)
        => (await PageAsync(uri, ct, json, headers, open, form, limit)).Text;

    // Also reports where redirects ended: embed players expect that page as the referrer.
    public async Task<(Uri Final, string Text)> PageAsync(Uri uri, CancellationToken ct, string? json = null, IReadOnlyDictionary<string, string>? headers = null,
        bool open = false, IReadOnlyDictionary<string, string>? form = null, TimeSpan? limit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(30));
        using var response = await OpenAsync(uri, false, timeout.Token, json: json, headers: headers, open: open, form: form);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException($"{uri.IdnHost} is rate limiting requests. Please try again in a minute.", RetryAfter(response));
        if (!response.IsSuccessStatusCode)
            throw new SourceStatusException((int)response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > 4 * 1024 * 1024) throw new ProviderException("Source response is too large.");
            output.Write(buffer, 0, count);
        }
        return (response.RequestMessage?.RequestUri ?? uri, Encoding.UTF8.GetString(output.ToArray()));
    }

    // The music catalog answers short bursts (a probe, a player's range requests, a quick skip) with 429 and
    // no Retry-After. A brief wait clears it, so a player is not handed the 429 as a broken file. With outages,
    // a 5xx (Cloudflare's 522 when the catalog is slow) or a failed connection is tried again the same way.
    public static async Task<HttpResponseMessage> PatientAsync(Func<Task<HttpResponseMessage>> open, CancellationToken ct, int attempts = 3, bool outages = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try { response = await open(); }
            // A lost connection is worth another try; a request that ran out of time would only run out again.
            catch (Exception ex) when (outages && attempt < attempts && ex is HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                continue;
            }
            var again = response.StatusCode == HttpStatusCode.TooManyRequests || outages && (int)response.StatusCode >= 500;
            if (!again || attempt >= attempts) return response;
            var wait = RetryAfter(response) is { } after && after > TimeSpan.Zero && after <= TimeSpan.FromSeconds(5) ? after : TimeSpan.FromSeconds(attempt);
            response.Dispose();
            await Task.Delay(wait, ct);
        }
    }

    // A media request whose headers must arrive within the limit. The body then runs on ct alone: the limit's
    // source is disposed on return, so its timer can no longer cut a long song.
    public async Task<HttpResponseMessage> MediaAsync(Uri uri, TimeSpan limit, CancellationToken ct, string? range = null, bool head = false)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(limit);
        return await OpenAsync(uri, true, headers.Token, range, head: head);
    }

    public static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - DateTimeOffset.UtcNow,
        _ => null
    };
    public void Dispose() => client.Dispose();
}
