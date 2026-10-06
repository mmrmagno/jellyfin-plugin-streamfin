using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AniFin;

// Source pages control links and redirects. Validate both the hostname and the address
// actually connected to, including on every redirect, so this cannot become a LAN proxy.
public sealed class SafeHttp : IDisposable
{
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AniFin/0.1");
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20; // global unicast only
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
            && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 198 && b[1] is 18 or 19);
    }

    public static void Validate(Uri uri, bool media)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        var site = host is "www.animegg.org" or "animegg.org";
        var catalog = host == "graphql.anilist.co";
        var cdn = media && (host == "vidcache.net" || host.EndsWith(".vidcache.net", StringComparison.Ordinal));
        if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || (!site && !catalog && !cdn)
            || (!cdn && uri.Port != 443) || (cdn && uri.Port is not (443 or 8161 or 8162 or 8163 or 8164 or 8165 or 8166)))
            throw new ProviderException("Source returned an unsupported destination.");
    }

    public async Task<HttpResponseMessage> OpenAsync(Uri uri, bool media, CancellationToken ct,
        string? range = null, string? json = null, bool head = false)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            Validate(uri, media);
            using var request = new HttpRequestMessage(head ? HttpMethod.Head : json is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (media) request.Headers.Referrer = new Uri("https://www.animegg.org/");
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

    public async Task<string> TextAsync(Uri uri, CancellationToken ct, string? json = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await OpenAsync(uri, false, timeout.Token, json: json);
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
        return Encoding.UTF8.GetString(output.ToArray());
    }
    public void Dispose() => client.Dispose();
}
