using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AniFin;

// On the target host this site returns unrelated pages to .NET's HTTP transport.
// curl is already included in the pinned Jellyfin image. Use it only for this
// site's bounded metadata pages, with the same URL/DNS policy as the relay.
public static class AnimeHeavenPages
{
    public static async Task<string> TextAsync(Uri uri, CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        for (var redirect = 0; redirect < 6; redirect++)
        {
            SafeHttp.Validate(uri, false);
            if (uri.IdnHost != "animeheaven.me") throw new ProviderException("Unsupported AnimeHeaven page destination.");
            var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, timeout.Token);
            if (addresses.Length == 0 || addresses.Any(a => !SafeHttp.IsPublicAddress(a)))
                throw new ProviderException("AnimeHeaven resolved to a non-public address.");
            var address = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();
            var ip = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            var start = new ProcessStartInfo("curl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            // -q must come first: ignore user curl configuration. No shell, proxy, automatic
            // redirects, credential jar, or files; pin the validated address before connecting.
            foreach (var arg in new[] { "-q", "--silent", "--globoff", "--noproxy", "*", "--proto", "=https", "--compressed", "--include",
                "--connect-timeout", "15", "--max-time", "30", "--max-filesize", "4194304", "--resolve", $"{uri.IdnHost}:443:{ip}",
                "--user-agent", "Mozilla/5.0 AniFin/0.4", "--url", uri.AbsoluteUri }) start.ArgumentList.Add(arg);
            if (headers is not null)
                foreach (var header in headers)
                {
                    if (header.Key is not ("Cookie" or "Referer") || header.Value.Contains('\r') || header.Value.Contains('\n'))
                        throw new ProviderException("Unsupported AnimeHeaven page header.");
                    start.ArgumentList.Add("--header");
                    start.ArgumentList.Add(header.Key + ": " + header.Value);
                }
            using var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new ProviderException("Could not start the AnimeHeaven page transport.");
            }
            catch (System.ComponentModel.Win32Exception) { throw new ProviderException("AnimeHeaven requires curl on the Jellyfin server. AnimeGG is still available."); }
            try
            {
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                using var output = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (output.Length + count > 4 * 1024 * 1024) throw new ProviderException("AnimeHeaven page is too large.");
                    output.Write(buffer, 0, count);
                }
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                if (process.ExitCode != 0) throw new ProviderException("AnimeHeaven could not be reached. Please try the other provider.");
                var response = ParseResponse(Encoding.UTF8.GetString(output.ToArray()));
                if (response.Status is >= 300 and < 400 && response.Location is not null)
                {
                    uri = new Uri(uri, response.Location);
                    continue;
                }
                if (response.Status is < 200 or >= 300) throw new ProviderException($"AnimeHeaven returned HTTP {response.Status}. Please try the other provider.");
                return response.Body;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        throw new ProviderException("Too many AnimeHeaven redirects.");
    }

    public static (int Status, string? Location, string Body) ParseResponse(string response)
    {
        var end = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end is < 0 or > 32768) throw new ProviderException("Invalid AnimeHeaven response headers.");
        var lines = response[..end].Split("\r\n");
        var statusLine = lines[0].Split(' ');
        if (statusLine.Length < 2 || !statusLine[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(statusLine[1], out var status))
            throw new ProviderException("Invalid AnimeHeaven response status.");
        var location = lines.Skip(1).FirstOrDefault(line => line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
        return (status, location, response[(end + 4)..]);
    }
}
