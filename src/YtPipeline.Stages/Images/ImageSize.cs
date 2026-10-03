using System.Buffers.Binary;

namespace YtPipeline.Stages.Images;

/// <summary>
/// Reads an image's pixel dimensions from its header, for PNG and JPEG.
/// <para>
/// Both are needed because the file extension lies: gflow writes JPEG bytes into the
/// <c>&lt;channel&gt;.banner.png</c> path it is handed, so a PNG-only reader silently reports
/// "unknown" for every image it produces — which is exactly how the banner resize was skipped the
/// first time it was wired up. Trust the magic bytes, never the name.
/// </para>
/// <para>
/// The project carries no imaging library and this is not a reason to add one: the answer is in the
/// first few dozen bytes of both formats.
/// </para>
/// </summary>
internal static class ImageSize
{
    /// <summary>
    /// The image's size, or <c>(0, 0)</c> for anything unreadable. Zero means "don't know", and
    /// callers skip whatever they were going to do about the size rather than throwing — a corrupt
    /// or missing image is the backend's failure to report, not this helper's.
    /// </summary>
    public static (int Width, int Height) Read(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (fs.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4) return (0, 0);

            fs.Position = 0;
            if (magic[0] == 0x89 && magic[1] == 'P' && magic[2] == 'N' && magic[3] == 'G')
                return ReadPng(fs);
            if (magic[0] == 0xFF && magic[1] == 0xD8)
                return ReadJpeg(fs);

            return (0, 0);
        }
        catch (IOException) { return (0, 0); }
        catch (UnauthorizedAccessException) { return (0, 0); }
    }

    /// <summary>Width and height sit at fixed offsets in the IHDR chunk, right after the signature.</summary>
    private static (int, int) ReadPng(Stream fs)
    {
        Span<byte> head = stackalloc byte[24];
        if (fs.ReadAtLeast(head, 24, throwOnEndOfStream: false) < 24) return (0, 0);

        return (BinaryPrimitives.ReadInt32BigEndian(head[16..20]),
                BinaryPrimitives.ReadInt32BigEndian(head[20..24]));
    }

    /// <summary>
    /// Walks the JPEG marker chain to the start-of-frame, which is the only segment carrying the
    /// dimensions. Every marker but the standalone ones is followed by a big-endian length, so the
    /// chain can be skipped through without decoding any image data.
    /// </summary>
    private static (int, int) ReadJpeg(Stream fs)
    {
        Span<byte> buf = stackalloc byte[8];
        if (fs.ReadAtLeast(buf[..2], 2, throwOnEndOfStream: false) < 2) return (0, 0);  // SOI

        while (true)
        {
            // Markers are 0xFF followed by a type byte; 0xFF is used as padding and is skipped.
            int b;
            do { b = fs.ReadByte(); } while (b == 0xFF);
            if (b < 0) return (0, 0);

            var marker = (byte)b;
            // Standalone markers carry no payload: RSTn (D0-D7), SOI (D8), EOI (D9), TEM (01).
            if (marker is 0x01 or >= 0xD0 and <= 0xD9) continue;

            if (fs.ReadAtLeast(buf[..2], 2, throwOnEndOfStream: false) < 2) return (0, 0);
            var length = BinaryPrimitives.ReadUInt16BigEndian(buf[..2]);
            if (length < 2) return (0, 0);

            // SOF0-SOF15 hold the frame header. C4 (DHT), C8 (JPG) and CC (DAC) share the range
            // but are not frame headers, so they fall through to the skip below.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                // precision (1 byte), then height and width as big-endian 16-bit — height first.
                if (fs.ReadAtLeast(buf[..5], 5, throwOnEndOfStream: false) < 5) return (0, 0);
                return (BinaryPrimitives.ReadUInt16BigEndian(buf[3..5]),
                        BinaryPrimitives.ReadUInt16BigEndian(buf[1..3]));
            }

            if (fs.Seek(length - 2, SeekOrigin.Current) >= fs.Length) return (0, 0);
        }
    }
}
