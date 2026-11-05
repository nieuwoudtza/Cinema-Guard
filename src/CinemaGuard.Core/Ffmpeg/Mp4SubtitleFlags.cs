using System.Buffers.Binary;

namespace CinemaGuard.Core.Ffmpeg;

/// <summary>
/// Makes VLC show an MP4's subtitle track when the video opens. VLC 3 ignores the default flag FFmpeg writes on
/// an MP4 subtitle track; it starts one by itself only when the track's text sample entry (tx3g) says its
/// samples are forced. FFmpeg has no option for those display flags, so they are set in the finished file: four
/// bytes in the movie header, which is read and written in place without touching the media data.
/// </summary>
public static class Mp4SubtitleFlags
{
    /// <summary>The tx3g displayFlags bit "all samples are forced".</summary>
    internal const uint AllSamplesForced = 0x80000000;

    private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl"];

    /// <summary>
    /// Flags the <paramref name="subtitleOrdinal"/>th text subtitle track (0-based, in stream order) to be shown.
    /// False when the file has no such track.
    /// </summary>
    public static bool Show(string path, int subtitleOrdinal)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var entries = new List<long>();
        FindTextEntries(stream, 0, stream.Length, entries);
        if (subtitleOrdinal < 0 || subtitleOrdinal >= entries.Count) return false;

        var flags = new byte[4];
        stream.Position = entries[subtitleOrdinal];
        stream.ReadExactly(flags);
        BinaryPrimitives.WriteUInt32BigEndian(flags, BinaryPrimitives.ReadUInt32BigEndian(flags) | AllSamplesForced);

        stream.Position = entries[subtitleOrdinal];
        stream.Write(flags);
        return true;
    }

    /// <summary>The displayFlags of each text subtitle track, in stream order.</summary>
    internal static List<uint> Read(string path)
    {
        using var stream = File.OpenRead(path);

        var entries = new List<long>();
        FindTextEntries(stream, 0, stream.Length, entries);

        var flags = new List<uint>();
        var buffer = new byte[4];
        foreach (var offset in entries)
        {
            stream.Position = offset;
            stream.ReadExactly(buffer);
            flags.Add(BinaryPrimitives.ReadUInt32BigEndian(buffer));
        }

        return flags;
    }

    /// <summary>Where each tx3g sample entry's displayFlags are, walking moov/trak/mdia/minf/stbl/stsd.</summary>
    private static void FindTextEntries(Stream stream, long start, long end, List<long> entries)
    {
        foreach (var (position, size, type, header) in Boxes(stream, start, end))
        {
            if (Containers.Contains(type))
            {
                FindTextEntries(stream, position + header, position + size, entries);
            }
            else if (type == "stsd")
            {
                // Version and flags, the entry count, then the sample entries.
                foreach (var entry in Boxes(stream, position + header + 8, position + size))
                {
                    // Six reserved bytes and the data reference index come before the display flags.
                    if (entry.Type == "tx3g" && entry.Size >= entry.Header + 12) entries.Add(entry.Position + entry.Header + 8);
                }
            }
        }
    }

    private static IEnumerable<(long Position, long Size, string Type, int Header)> Boxes(Stream stream, long start, long end)
    {
        var buffer = new byte[16];
        var position = start;

        while (position + 8 <= end)
        {
            stream.Position = position;
            stream.ReadExactly(buffer, 0, 8);

            long size = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            var type = System.Text.Encoding.ASCII.GetString(buffer, 4, 4);
            var header = 8;

            if (size == 1)
            {
                stream.ReadExactly(buffer, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < header || position + size > end) yield break;

            yield return (position, size, type, header);
            position += size;
        }
    }
}
