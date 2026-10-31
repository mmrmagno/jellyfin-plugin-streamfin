using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// yt-dlp finds songs on YouTube and turns a video into an audio address; since late 2025 it needs deno to read
// YouTube's player code. Both come from their official GitHub releases into the plugin's own folder, and each file
// must match the SHA-256 published with it before it is ever run. A path in the settings replaces the download.
public sealed class YtDlp(SafeHttp http, ILogger<YtDlp> logger)
{
    private const long ToolLimit = 300L * 1024 * 1024;
    private const int OutputLimit = 2 * 1024 * 1024;
    private static readonly SemaphoreSlim Runs = new(2, 2);
    private static readonly SemaphoreSlim Installing = new(1, 1);
    private static readonly Regex VideoId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    public static string? Problem { get; private set; }
    private static bool checkedTools;
    private static DateTime lastTry;

    private static Configuration? Config => Plugin.Instance?.Configuration;
    private static string? Folder => Plugin.Instance is { } plugin ? Path.Combine(plugin.DataFolderPath, "bin") : null;
    public static string? YtDlpFile => Config is { YtDlpPath.Length: > 0 } c ? c.YtDlpPath : Folder is { } f ? Path.Combine(f, "yt-dlp") : null;
    public static string? DenoFile => Config is { DenoPath.Length: > 0 } c ? c.DenoPath : Folder is { } f ? Path.Combine(f, "deno") : null;
    public static bool Wanted => Config is { MusicEnabled: true, MusicYouTube: true, MusicAllowLossy: true };
    public static bool Ready => Wanted && checkedTools && File.Exists(YtDlpFile) && File.Exists(DenoFile);

    private static string Arch => !OperatingSystem.IsLinux() ? throw new ProviderException("The YouTube tools can only be installed on Linux servers; set their paths instead.") : RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        var other => throw new ProviderException($"YouTube is not available on {other} servers.")
    };
    private static string YtDlpAsset => Arch == "aarch64" ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
    private static string DenoAsset => $"deno-{Arch}-unknown-linux-gnu.zip";

    // Turning YouTube on in the settings should not wait for the daily task.
    public void Kick()
    {
        if (!Wanted || Ready || DateTime.UtcNow - lastTry < TimeSpan.FromMinutes(10)) return;
        lastTry = DateTime.UtcNow;
        _ = Task.Run(() => EnsureAsync(CancellationToken.None));
    }

    // Fetches or refreshes both tools, then checks yt-dlp starts. Safe to call often: an unchanged release
    // costs two small checksum requests.
    public async Task EnsureAsync(CancellationToken ct)
    {
        if (!Wanted) return;
        lastTry = DateTime.UtcNow;
        await Installing.WaitAsync(ct);
        try
        {
            if (Folder is { } folder && Config is { } config)
            {
                Directory.CreateDirectory(folder);
                if (config.YtDlpPath.Length == 0)
                    await InstallAsync(new Uri($"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{YtDlpAsset}"),
                        new Uri("https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS"), YtDlpAsset, Path.Combine(folder, "yt-dlp"), null, ct);
                if (config.DenoPath.Length == 0)
                    await InstallAsync(new Uri($"https://github.com/denoland/deno/releases/latest/download/{DenoAsset}"),
                        new Uri($"https://github.com/denoland/deno/releases/latest/download/{DenoAsset}.sha256sum"), DenoAsset, Path.Combine(folder, "deno"), "deno", ct);
            }
            var version = (await RunAsync(["--version"], TimeSpan.FromSeconds(30), ct)).Trim();
            checkedTools = true;
            Problem = null;
            logger.LogInformation("StreamFin music: yt-dlp {Version} is ready for YouTube", version.Length > 40 ? version[..40] : version);
        }
        catch (Exception ex) when (ex is ProviderException or IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            checkedTools = false;
            Problem = ex is ProviderException ? ex.Message : $"The YouTube tools could not be set up ({ex.GetType().Name}).";
            logger.LogWarning("StreamFin music: {Problem}", Problem);
        }
        finally { Installing.Release(); }
    }

    private async Task InstallAsync(Uri download, Uri sums, string asset, string target, string? zipEntry, CancellationToken ct)
    {
        var expected = Expected(await TextAsync(sums, ct), asset) ?? throw new ProviderException($"The checksum for {asset} is not published.");
        // The file in place is hashed again too, so one damaged or replaced since its install is fetched afresh.
        if (File.Exists(target) && File.Exists(target + ".sha256")
            && (await File.ReadAllTextAsync(target + ".sha256", ct)).Split(' ') is [var release, var installed]
            && release == expected && await HashAsync(target, ct) == installed) return;
        var part = target + ".download";
        var fresh = target + ".new";
        try
        {
            using (var response = await http.OpenAsync(download, true, ct, tools: true))
            {
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > ToolLimit)
                    throw new ProviderException($"{asset} could not be downloaded (HTTP {(int)response.StatusCode}).");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                await CopyLimitedAsync(input, file, ToolLimit, ct);
            }
            // Never run a file that differs from what its project published.
            if (await HashAsync(part, ct) != expected)
                throw new ProviderException($"{asset} did not match its published checksum and was deleted.");
            if (zipEntry is null) File.Move(part, fresh, overwrite: true);
            else
            {
                using var zip = ZipFile.OpenRead(part);
                var entry = zip.Entries.SingleOrDefault(e => e.FullName == zipEntry) ?? throw new ProviderException($"{asset} does not contain {zipEntry}.");
                if (entry.Length > ToolLimit) throw new ProviderException($"{asset} is too large.");
                await using var input = entry.Open();
                await using var output = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None);
                await CopyLimitedAsync(input, output, ToolLimit, ct);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            // The release's hash, and the installed file's own (deno's differs from its archive's).
            var marker = expected + " " + await HashAsync(fresh, ct);
            File.Move(fresh, target, overwrite: true);
            await File.WriteAllTextAsync(target + ".sha256", marker, ct);
            logger.LogInformation("StreamFin music: installed {Asset}, checksum verified", asset);
        }
        finally
        {
            foreach (var leftover in new[] { part, fresh })
                try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // yt-dlp's last words, kept short however much it writes.
    private static async Task<string> TailAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var tail = new StringBuilder();
        for (int read; (read = await reader.ReadAsync(buffer, ct)) > 0;)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > 16384) tail.Remove(0, tail.Length - 8192);
        }
        return tail.ToString();
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }

    private async Task<string> TextAsync(Uri uri, CancellationToken ct)
    {
        using var response = await http.OpenAsync(uri, true, ct, tools: true);
        if (response.StatusCode != HttpStatusCode.OK) throw new ProviderException($"The YouTube tools' checksums could not be read (HTTP {(int)response.StatusCode}).");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        await CopyLimitedAsync(input, output, 64 * 1024, ct);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        long total = 0;
        for (int read; (read = await input.ReadAsync(buffer, ct)) > 0;)
        {
            if ((total += read) > limit) throw new ProviderException("A YouTube tool download is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    // The hash for one file from a sha256sum listing ("<hex>  <name>" or "<hex> *<name>").
    public static string? Expected(string sums, string asset)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == asset && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    // Options every run starts with: no configuration, plugins, cookies or cache from anywhere on the server.
    public static List<string> Base(string deno) =>
        ["--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--no-warnings", "--no-progress", "--js-runtimes", "deno:" + deno];

    // The search text goes after "--", so a title starting with a dash cannot become an option.
    public static List<string> SearchArguments(string deno, string query, int count = 8) =>
        [.. Base(deno), "--flat-playlist", "--dump-single-json", "--", $"ytsearch{count}:{Clean(query)}"];

    public static List<string> AudioArguments(string deno, string videoId) => VideoId.IsMatch(videoId)
        ? [.. Base(deno), "-f", "ba[ext=m4a]", "--print", "urls", "--", "https://www.youtube.com/watch?v=" + videoId]
        : throw new ProviderException("Invalid YouTube video.");

    private static string Short(string line) => line.Length > 200 ? line[..200] : line;

    private static string Clean(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, 200));
        foreach (var c in text)
        {
            if (builder.Length >= 200) break;
            builder.Append(char.IsControl(c) ? ' ' : c);
        }
        return builder.ToString().Trim();
    }

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken ct)
    {
        if (YtDlpFile is not { } tool || Folder is not { } folder) throw new ProviderException("YouTube is not set up on this server.");
        if (!await Runs.WaitAsync(TimeSpan.FromSeconds(10), ct)) throw new ProviderException("YouTube is busy finding other songs.");
        try
        {
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            // Its own home, so nothing from a user's yt-dlp or deno setup on this server takes part.
            var home = Path.Combine(folder, "home");
            // yt-dlp unpacks itself before it runs, and /tmp is often mounted noexec in containers.
            var temp = Path.Combine(home, "tmp");
            Directory.CreateDirectory(temp);
            // Nothing from Jellyfin's own environment (proxies, keys) is passed on.
            var path = Environment.GetEnvironmentVariable("PATH");
            start.Environment.Clear();
            start.Environment["PATH"] = path ?? "/usr/local/bin:/usr/bin:/bin";
            start.Environment["LANG"] = "C.UTF-8";
            start.Environment["HOME"] = home;
            start.Environment["XDG_CONFIG_HOME"] = home;
            start.Environment["XDG_CACHE_HOME"] = home;
            start.Environment["DENO_DIR"] = Path.Combine(home, "deno");
            start.Environment["TMPDIR"] = temp;
            using var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new ProviderException("yt-dlp could not be started.");
            }
            catch (System.ComponentModel.Win32Exception) { throw new ProviderException("yt-dlp could not be started. Check that the plugin's data folder allows running programs."); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            try
            {
                var errors = TailAsync(process.StandardError, timeout.Token);
                using var output = new MemoryStream();
                var buffer = new byte[16384];
                for (int count; (count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0;)
                {
                    if (output.Length + count > OutputLimit) throw new ProviderException("yt-dlp answered with too much data.");
                    output.Write(buffer, 0, count);
                }
                await process.WaitForExitAsync(timeout.Token);
                var error = await errors;
                if (process.ExitCode != 0)
                {
                    var lines = error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    // No ERROR line means yt-dlp itself did not start properly, which is about the server, not the song.
                    var line = lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal));
                    throw new ToolFailedException(line is not null ? "YouTube could not find this song. " + Short(line)
                        : "yt-dlp failed to run: " + Short(lines.LastOrDefault() ?? $"exit code {process.ExitCode}"));
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
            finally
            {
                if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            }
        }
        finally { Runs.Release(); }
    }
}

public sealed class ToolFailedException(string message) : ProviderException(message);

public sealed class YtDlpTask(YtDlp tools) : IScheduledTask
{
    public string Name => "Update YouTube tools";
    public string Key => "StreamFinYtDlp";
    public string Description => "Downloads or updates yt-dlp and deno for StreamFin's YouTube music fallback, checking each file against its published checksum.";
    public string Category => "StreamFin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await tools.EnsureAsync(cancellationToken);
        if (YtDlp.Wanted && YtDlp.Problem is { } problem) throw new ProviderException(problem);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(1).Ticks }
    ];
}
