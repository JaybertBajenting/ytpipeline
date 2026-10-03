namespace YtPipeline.Stages.Voice;

/// <summary>Removes per-file tags before joining complete MP3 responses, without touching MPEG frames.</summary>
internal static class Mp3ChunkJoiner
{
    internal static ReadOnlySpan<byte> WithoutFileTags(byte[] bytes)
    {
        int start = 0, end = bytes.Length;
        while (end - start >= 3 && bytes.AsSpan(start, 3).SequenceEqual("ID3"u8))
        {
            if (end - start < 10 || bytes[start + 3] is < 2 or > 4 ||
                bytes.AsSpan(start + 6, 4).ContainsAnyInRange((byte)128, (byte)255))
                throw new InvalidDataException("The MP3 chunk has an invalid ID3 header.");

            int size = (bytes[start + 6] << 21) | (bytes[start + 7] << 14) |
                       (bytes[start + 8] << 7) | bytes[start + 9];
            // ID3v2.4's optional footer is outside its advertised payload size.
            int footer = bytes[start + 3] == 4 && (bytes[start + 5] & 0x10) != 0 ? 10 : 0;
            if (10L + size + footer > end - start)
                throw new InvalidDataException("The MP3 chunk has a truncated ID3 tag.");
            start += 10 + size + footer;
        }
        if (end - start >= 128 && bytes.AsSpan(end - 128, 3).SequenceEqual("TAG"u8)) end -= 128;
        if (start == end) throw new InvalidDataException("The MP3 chunk contains tags but no audio.");
        return bytes.AsSpan(start, end - start);
    }
}
