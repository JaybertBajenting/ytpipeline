namespace YtPipeline.Stages.Voice;

/// <summary>
/// Reads an mp3's playback duration by summing MPEG audio frame durations.
/// Dependency-free (no ffprobe), handles CBR and VBR. Skips an ID3v2 tag if present.
/// </summary>
public static class Mp3Duration
{
    private static readonly int[,] BitRates =
    {
        // MPEG1 L3,           MPEG2/2.5 L3   (kbps), index 0 = free, 15 = bad
        { 0,  32,  40,  48,  56,  64,  80,  96, 112, 128, 160, 192, 224, 256, 320, -1 },
        { 0,   8,  16,  24,  32,  40,  48,  56,  64,  80,  96, 112, 128, 144, 160, -1 },
    };

    private static readonly int[,] SampleRates =
    {
        { 44100, 48000, 32000, 0 }, // MPEG1
        { 22050, 24000, 16000, 0 }, // MPEG2
        { 11025, 12000,  8000, 0 }, // MPEG2.5
    };

    public static double GetSeconds(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int i = SkipId3v2(bytes);
        double total = 0;

        while (i + 4 <= bytes.Length)
        {
            // Frame sync: 11 set bits.
            if (bytes[i] != 0xFF || (bytes[i + 1] & 0xE0) != 0xE0) { i++; continue; }

            int versionBits = (bytes[i + 1] >> 3) & 0b11; // 0=2.5,2=2,3=1
            int layerBits = (bytes[i + 1] >> 1) & 0b11;   // 1=L3,2=L2,3=L1
            if (versionBits == 1 || layerBits == 0) { i++; continue; } // reserved

            int brIndex = (bytes[i + 2] >> 4) & 0x0F;
            int srIndex = (bytes[i + 2] >> 2) & 0x03;
            int padding = (bytes[i + 2] >> 1) & 0x01;
            if (brIndex is 0 or 15 || srIndex == 3) { i++; continue; }

            bool mpeg1 = versionBits == 3;
            int bitrate = BitRates[mpeg1 ? 0 : 1, brIndex] * 1000;
            int srRow = versionBits switch { 3 => 0, 2 => 1, _ => 2 };
            int sampleRate = SampleRates[srRow, srIndex];
            if (bitrate <= 0 || sampleRate <= 0) { i++; continue; }

            int samplesPerFrame = mpeg1 ? 1152 : 576; // Layer III
            int frameLen = (samplesPerFrame / 8 * bitrate / sampleRate) + padding;
            if (frameLen <= 0) { i++; continue; }

            total += (double)samplesPerFrame / sampleRate;
            i += frameLen;
        }

        return total;
    }

    private static int SkipId3v2(byte[] b)
    {
        if (b.Length < 10 || b[0] != 'I' || b[1] != 'D' || b[2] != '3') return 0;
        // Syncsafe 28-bit size, plus the 10-byte header.
        int size = (b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9];
        return 10 + size;
    }
}
