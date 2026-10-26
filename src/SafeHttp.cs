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
        or "zokoanime.video" or "api.themoviedb.org" or "flixhqz.net" or "flixhq.ws";

    public static void Validate(Uri uri, bool media)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        var site = IsKnownSite(host);
        var heaven = media && AnimeHeavenSource.IsMediaHost(host);
        var cdn = media && (host == "vidcache.net" || host.EndsWith(".vidcache.net", StringComparison.Ordinal));
        if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || (!site && !cdn && !heaven)
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
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal) && !IsPublicAddress(literal))
            throw new ProviderException("Source returned an unsupported destination.");
    }

    public async Task<HttpResponseMessage> OpenAsync(Uri uri, bool media, CancellationToken ct,
        string? range = null, string? json = null, bool head = false, IReadOnlyDictionary<string, string>? headers = null,
        bool open = false, IReadOnlyDictionary<string, string>? form = null)
    {
        var originalHost = uri.IdnHost;
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (open) ValidateOpen(uri);
            else Validate(uri, media);
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
        bool open = false, IReadOnlyDictionary<string, string>? form = null)
        => (await PageAsync(uri, ct, json, headers, open, form)).Text;

    // Also reports where redirects ended: embed players expect that page as the referrer.
    public async Task<(Uri Final, string Text)> PageAsync(Uri uri, CancellationToken ct, string? json = null, IReadOnlyDictionary<string, string>? headers = null,
        bool open = false, IReadOnlyDictionary<string, string>? form = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await OpenAsync(uri, false, timeout.Token, json: json, headers: headers, open: open, form: form);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException($"{uri.IdnHost} is rate limiting requests. Please try again in a minute.", RetryAfter(response));
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"Source returned HTTP {(int)response.StatusCode}. Please try again later.");
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

    public static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - DateTimeOffset.UtcNow,
        _ => null
    };
    public void Dispose() => client.Dispose();
}
