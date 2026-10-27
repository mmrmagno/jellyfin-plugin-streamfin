using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public sealed record ReplayGain(double? TrackGain, double? AlbumGain, double? TrackPeak, double? AlbumPeak);

// The catalog's FLAC files carry ReplayGain tags in their Vorbis comment, about a kilobyte in. The catalog JSON
// does not, so the tags are read from the first bytes of a stream as they pass through and kept per track.
public static class MusicGain
{
    public const int HeadBytes = 64 * 1024;
    private static readonly MemoryCache Known = new(new MemoryCacheOptions { SizeLimit = 50_000 });

    public static ReplayGain? For(string trackId) => Known.TryGetValue<ReplayGain>(trackId, out var gain) ? gain : null;

    public static void Remember(ReadOnlySpan<byte> head, params string[] trackIds)
    {
        if (Parse(head) is not { } gain) return;
        foreach (var id in trackIds.Distinct())
            Known.Set(id, gain, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromDays(30) });
    }

    // Where the audio frames begin, after the last metadata block; null when this is not FLAC or the blocks run on.
    public static int? AudioStart(ReadOnlySpan<byte> flac)
    {
        if (flac.Length < 8 || !flac[..4].SequenceEqual("fLaC"u8)) return null;
        var at = 4;
        while (at + 4 <= flac.Length)
        {
            var header = flac[at];
            at += 4 + ((flac[at + 1] << 16) | (flac[at + 2] << 8) | flac[at + 3]);
            if ((header & 0x80) != 0) return at <= flac.Length ? at : null;
        }
        return null;
    }

    public static ReplayGain? Parse(ReadOnlySpan<byte> flac)
    {
        if (flac.Length < 8 || !flac[..4].SequenceEqual("fLaC"u8)) return null;
        var at = 4;
        while (at + 4 <= flac.Length)
        {
            var header = flac[at];
            var length = (flac[at + 1] << 16) | (flac[at + 2] << 8) | flac[at + 3];
            at += 4;
            if ((header & 0x7f) == 4) return at + length <= flac.Length ? Comments(flac.Slice(at, length)) : null;
            if ((header & 0x80) != 0) return null;
            at += length;
        }
        return null;
    }

    private static ReplayGain? Comments(ReadOnlySpan<byte> block)
    {
        if (block.Length < 8) return null;
        var vendor = BinaryPrimitives.ReadUInt32LittleEndian(block);
        if (vendor > block.Length - 8) return null;
        var at = 4 + (int)vendor;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(block[at..]);
        at += 4;
        double? trackGain = null, albumGain = null, trackPeak = null, albumPeak = null;
        for (var i = 0; i < count && i < 1000 && at + 4 <= block.Length; i++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(block[at..]);
            at += 4;
            if (size > block.Length - at) return null;
            var entry = Encoding.UTF8.GetString(block.Slice(at, (int)size));
            at += (int)size;
            var split = entry.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0) continue;
            var value = entry[(split + 1)..].Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) continue;
            switch (entry[..split].ToUpperInvariant())
            {
                case "REPLAYGAIN_TRACK_GAIN" when Math.Abs(number) <= 64: trackGain = number; break;
                case "REPLAYGAIN_ALBUM_GAIN" when Math.Abs(number) <= 64: albumGain = number; break;
                case "REPLAYGAIN_TRACK_PEAK" when number is >= 0 and <= 16: trackPeak = number; break;
                case "REPLAYGAIN_ALBUM_PEAK" when number is >= 0 and <= 16: albumPeak = number; break;
            }
        }
        return trackGain is null && albumGain is null ? null : new ReplayGain(trackGain, albumGain, trackPeak, albumPeak);
    }
}
