using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public static class Hls
{
    private static readonly Regex Bandwidth = new(@"\A\d{1,12}\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Plain = new(@"\A[A-Za-z0-9.\-]{1,64}\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Number = new(@"\A\d{1,12}\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Range = new(@"\A\d{1,15}(@\d{1,15})?\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Seconds = new(@"\A#EXTINF:\s*(\d{1,6}(?:\.\d{1,6})?)", RegexOptions.None, TimeSpan.FromSeconds(1));

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
    private static string[] Lines(string text)
    {
        if (!text.TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new ProviderException("The source did not return an HLS playlist.");
        return text.Replace("\r\n", "\n").Split('\n');
    }
    private static Uri Absolute(Uri playlist, string reference) =>
        Uri.TryCreate(playlist, reference.Trim(), out var url) ? url : throw new ProviderException("The stream playlist has an invalid address.");

    // Segment names keep a media extension: FFmpeg refuses HLS segments whose name it does not recognise. Some hosts
    // disguise segments as images ("0.m4s.png", "1.jpg"); the extension under the disguise counts, and a fragmented
    // stream's unnamed segments are fMP4, anything else MPEG-TS.
    private static bool IsMedia(string ext) => ext is "m4s" or "mp4" or "aac" or "ts" or "vtt" or "webvtt" or "m4a" or "m4v" or "cmfv" or "cmfa";
    public static string Extension(Uri url, bool fragmented = false)
    {
        var name = Path.GetFileName(url.AbsolutePath);
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (IsMedia(ext)) return ext;
        var inner = Path.GetExtension(Path.GetFileNameWithoutExtension(name)).TrimStart('.').ToLowerInvariant();
        return IsMedia(inner) ? inner : fragmented ? "m4s" : "ts";
    }
    // Only a segment of a fragmented stream is taken as fMP4 when its name says nothing; keys and init files keep the old default.
    private static string Part(int index, bool playlist, Uri url, bool fragmented = true, bool segment = false) =>
        $"p/{index}.{(playlist ? "m3u8" : !fragmented && Extension(url) is "mp4" or "m4s" or "m4v" or "cmfv" ? "ts" : Extension(url, fragmented && segment))}";

    // What a master playlist's variants offer: height (0 when not given), whether the video is H.264, and bandwidth.
    public sealed record Variant(int Height, bool H264, long Bandwidth, string? Codecs);
    public static Variant Read(Dictionary<string, string> attributes)
    {
        // A wide film (1920x800) counts as the 16:9 height of its width (1080), so it ranks with other 1080p versions.
        var height = attributes.TryGetValue("RESOLUTION", out var resolution) && resolution.Split('x') is [var w, var h]
            && int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and < 10000
            ? Math.Max(parsed, int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var width) && width is > 0 and < 20000 ? width * 9 / 16 : 0) : 0;
        var codecs = attributes.GetValueOrDefault("CODECS");
        var bandwidth = attributes.TryGetValue("BANDWIDTH", out var b) && Bandwidth.IsMatch(b) ? long.Parse(b, CultureInfo.InvariantCulture) : 0;
        return new Variant(height, codecs?.Contains("avc1", StringComparison.OrdinalIgnoreCase) == true, bandwidth, codecs);
    }

    // The variant to play: the tallest within the cap (the smallest one when all are taller), H.264 first at equal height
    // when asked, then the highest bandwidth. With no cap and no preference this is the highest bandwidth, as before.
    public static int Pick(IReadOnlyList<Variant> variants, int? maxHeight = null, bool preferH264 = false)
    {
        if (variants.Count == 0) return -1;
        bool Fits(Variant v) => maxHeight is not { } cap || v.Height == 0 || v.Height <= cap;
        var pool = variants.Select((v, i) => (v, i)).Where(x => Fits(x.v)).ToList();
        if (pool.Count == 0) return variants.Select((v, i) => (v, i)).OrderBy(x => x.v.Height).ThenByDescending(x => x.v.Bandwidth).First().i;
        return pool.OrderByDescending(x => maxHeight is null && !preferH264 ? 0 : x.v.Height).ThenByDescending(x => preferH264 && x.v.H264)
            .ThenByDescending(x => x.v.Bandwidth).First().i;
    }

    // The best variant a master playlist offers under the user's quality settings, for ranking sources against each other.
    public static Variant? Best(string text, int? maxHeight = null, bool preferH264 = false)
    {
        var variants = Lines(text).Where(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            .Select(l => Read(Attributes(l["#EXT-X-STREAM-INF:".Length..]))).ToList();
        var at = Pick(variants, maxHeight, preferH264);
        return at < 0 ? null : variants[at];
    }

    // FFmpeg reads attribute lists loosely: bare or quoted values, backslash escapes, the last
    // duplicate wins. Parse them the same way so nothing FFmpeg would act on is overlooked.
    public static Dictionary<string, string> Attributes(string list)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (true)
        {
            while (i < list.Length && (char.IsWhiteSpace(list[i]) || list[i] == ',')) i++;
            if (i >= list.Length) break;
            var equals = list.IndexOf('=', i);
            if (equals < 0) break;
            var key = list[i..equals];
            i = equals + 1;
            var value = new StringBuilder();
            if (i < list.Length && list[i] == '"')
            {
                i++;
                while (i < list.Length && list[i] != '"')
                {
                    if (list[i] == '\\') { if (i + 1 >= list.Length) break; value.Append(list[i + 1]); i += 2; }
                    else value.Append(list[i++]);
                }
                if (i < list.Length) i++;
            }
            else while (i < list.Length && !char.IsWhiteSpace(list[i]) && list[i] != ',') value.Append(list[i++]);
            result[key] = value.ToString();
        }
        return result;
    }

    // Rebuilds a tag from the attributes it may carry; anything else is dropped. A URI is
    // replaced by a relay path, so FFmpeg never sees an upstream address.
    private static string Tag(string name, Dictionary<string, string> attributes, string[] quoted, string[] plain, Func<string, string>? uri = null)
    {
        var parts = new List<string>();
        foreach (var (key, value) in attributes)
        {
            if (key == "URI" && uri is not null) parts.Add($"URI=\"{uri(value)}\"");
            else if (quoted.Contains(key) && value.Length <= 256 && value.All(c => c >= ' ' && c != '"' && c != '\\')) parts.Add($"{key}=\"{value}\"");
            else if (plain.Contains(key) && Plain.IsMatch(value)) parts.Add($"{key}={value}");
        }
        return $"#{name}:{string.Join(',', parts)}";
    }

    // Keep one video variant (the best under the quality settings) and the renditions it uses. Jellyfin transcodes for
    // each client anyway, and every variant kept would be another stream FFprobe has to open.
    public static string RewriteMaster(string text, Uri playlist, HlsLinks links, int? maxHeight = null, bool preferH264 = false)
    {
        var lines = Lines(text);
        var found = new List<(Dictionary<string, string> Attributes, int Uri)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            var next = Array.FindIndex(lines, i + 1, l => l.Trim().Length > 0 && !l.StartsWith('#'));
            if (next < 0) continue;
            found.Add((Attributes(lines[i]["#EXT-X-STREAM-INF:".Length..]), next));
        }
        var at = Pick(found.Select(f => Read(f.Attributes)).ToList(), maxHeight, preferH264);
        if (at < 0) throw new ProviderException("The stream playlist lists no playable variant.");
        var chosen = found[at];
        var audio = chosen.Attributes.GetValueOrDefault("AUDIO");
        var output = new StringBuilder("#EXTM3U\n");
        foreach (var line in lines)
        {
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)) continue;
            var media = Attributes(line["#EXT-X-MEDIA:".Length..]);
            // Subtitle renditions are dropped: Jellyfin cannot extract them from a remote stream.
            if (audio is null || media.GetValueOrDefault("TYPE") != "AUDIO" || media.GetValueOrDefault("GROUP-ID") != audio) continue;
            output.Append(Tag("EXT-X-MEDIA", media, ["GROUP-ID", "NAME", "LANGUAGE", "CHANNELS"], ["TYPE", "DEFAULT", "AUTOSELECT"],
                value => Part(links.Add(Absolute(playlist, value), true), true, playlist))).Append('\n');
        }
        var variant = Absolute(playlist, lines[chosen.Uri]);
        output.Append(Tag("EXT-X-STREAM-INF", chosen.Attributes, ["CODECS", "AUDIO"], ["BANDWIDTH", "AVERAGE-BANDWIDTH", "RESOLUTION", "FRAME-RATE"]))
            .Append('\n').Append(Part(links.Add(variant, true), true, variant)).Append('\n');
        return output.ToString();
    }

    public static string WrapMedia(int index) => $"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\np/{index}.m3u8\n";

    public static int FirstVariant(string master)
    {
        var line = master.Split('\n').First(l => l.StartsWith("p/", StringComparison.Ordinal));
        return int.Parse(line[2..line.IndexOf('.')], CultureInfo.InvariantCulture);
    }

    public static string RewriteMedia(string text, Uri playlist, HlsLinks links)
    {
        var output = new StringBuilder("#EXTM3U\n");
        string Relay(string value) { var url = Absolute(playlist, value); return Part(links.Add(url, false), false, url)[2..]; }
        // Without EXT-X-MAP the segments are MPEG-TS whatever their address says (some hosts call them .mp4), and
        // FFmpeg refuses a segment whose content does not match its extension.
        var fragmented = Lines(text).Any(l => l.TrimStart().StartsWith("#EXT-X-MAP", StringComparison.Ordinal));
        string Segment(string value) { var url = Absolute(playlist, value); return Part(links.Add(url, false), false, url, fragmented, segment: true)[2..]; }
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "#EXTM3U") continue;
            if (!line.StartsWith('#'))
            {
                output.Append(Segment(line)).Append('\n');
                continue;
            }
            var colon = line.IndexOf(':');
            var (name, value) = colon < 0 ? (line[1..], "") : (line[1..colon], line[(colon + 1)..]);
            if (name is "EXT-X-STREAM-INF" or "EXT-X-MEDIA" or "EXT-X-I-FRAME-STREAM-INF" or "EXT-X-SESSION-DATA")
                throw new ProviderException("The stream playlist nests other playlists.");
            var kept = name switch
            {
                "EXTINF" when Seconds.Match(line) is { Success: true } m => $"#EXTINF:{m.Groups[1].Value},",
                "EXT-X-TARGETDURATION" or "EXT-X-MEDIA-SEQUENCE" or "EXT-X-DISCONTINUITY-SEQUENCE" or "EXT-X-VERSION" when Number.IsMatch(value) => $"#{name}:{value}",
                "EXT-X-PLAYLIST-TYPE" when value is "VOD" or "EVENT" => $"#{name}:{value}",
                "EXT-X-BYTERANGE" when Range.IsMatch(value) => $"#{name}:{value}",
                "EXT-X-ENDLIST" or "EXT-X-DISCONTINUITY" or "EXT-X-INDEPENDENT-SEGMENTS" when value.Length == 0 => $"#{name}",
                "EXT-X-KEY" => Tag(name, Attributes(value), [], ["METHOD", "IV"], Relay),
                "EXT-X-MAP" => Tag(name, Attributes(value), ["BYTERANGE"], [], Relay),
                _ => null
            };
            if (kept is not null) output.Append(kept).Append('\n');
        }
        return output.ToString();
    }

    // Some hosts glue a tiny image in front of each segment so it passes for a picture (a 1x1 PNG, then MPEG-TS). FFmpeg
    // refuses that, so the relay cuts the image off. Returns how many bytes to drop: only when a whole PNG or JPEG comes
    // first and video (an MPEG-TS sync byte or an MP4 box) follows straight after.
    public static int ImagePrefix(ReadOnlySpan<byte> head)
    {
        int end;
        if (head.StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            var iend = head.IndexOf("IEND"u8);
            if (iend < 0) return 0;
            end = iend + 8;
        }
        else if (head.StartsWith((byte[])[0xFF, 0xD8, 0xFF]))
        {
            var eoi = head.IndexOf((byte[])[0xFF, 0xD9]);
            if (eoi < 0) return 0;
            end = eoi + 2;
        }
        else return 0;
        if (end >= head.Length) return 0;
        if (head[end] == 0x47 && (end + 188 >= head.Length || head[end + 188] == 0x47)) return end;
        if (end + 8 <= head.Length && (head.Slice(end + 4, 4).SequenceEqual("ftyp"u8) || head.Slice(end + 4, 4).SequenceEqual("styp"u8)
            || head.Slice(end + 4, 4).SequenceEqual("moof"u8) || head.Slice(end + 4, 4).SequenceEqual("sidx"u8))) return end;
        return 0;
    }

    public static double Duration(string text) => Regex.Matches(text, @"#EXTINF:\s*([0-9.]+)", RegexOptions.None, TimeSpan.FromSeconds(2))
        .Sum(m => double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0);

    public static string WebVtt(string text)
    {
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (text.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal)) return text.TrimStart();
        if (!Regex.IsMatch(text, @"\d{2}:\d{2}:\d{2}[,.]\d{3}\s*-->", RegexOptions.None, TimeSpan.FromSeconds(2))) throw new ProviderException("The subtitle file is not SubRip or WebVTT.");
        return "WEBVTT\n\n" + Regex.Replace(text, @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2", RegexOptions.None, TimeSpan.FromSeconds(2));
    }
}
