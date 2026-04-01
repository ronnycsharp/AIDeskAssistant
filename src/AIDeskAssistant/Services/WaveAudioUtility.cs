using System.Buffers.Binary;

namespace AIDeskAssistant.Services;

internal static class WaveAudioUtility
{
    public static byte[] CreateWaveFile(byte[] pcm16Bytes, int sampleRate, short channels = 1, short bitsPerSample = 16)
    {
        int blockAlign = channels * (bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;
        int dataLength = pcm16Bytes.Length;

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        writer.Write(pcm16Bytes);
        writer.Flush();
        return stream.ToArray();
    }

    public static bool TryExtractPcm16FromWave(byte[] waveBytes, int expectedSampleRate, out byte[] pcm16Bytes, out string error)
    {
        pcm16Bytes = Array.Empty<byte>();
        error = string.Empty;

        if (waveBytes.Length < 44)
        {
            error = "WAV file is too small.";
            return false;
        }

        ReadOnlySpan<byte> bytes = waveBytes;
        if (!bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WAVE"u8))
        {
            error = "Unsupported WAV header.";
            return false;
        }

        int offset = 12;
        short audioFormat = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = 0;

        while (offset + 8 <= bytes.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(bytes[offset..(offset + 4)]);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..(offset + 8)]);
            int chunkDataOffset = offset + 8;
            if (chunkDataOffset + chunkSize > bytes.Length)
            {
                error = "Invalid WAV chunk size.";
                return false;
            }

            if (chunkId == "fmt ")
            {
                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(bytes[chunkDataOffset..(chunkDataOffset + 2)]);
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes[(chunkDataOffset + 2)..(chunkDataOffset + 4)]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes[(chunkDataOffset + 4)..(chunkDataOffset + 8)]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes[(chunkDataOffset + 14)..(chunkDataOffset + 16)]);
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkDataOffset;
                dataLength = chunkSize;
                break;
            }

            offset = chunkDataOffset + chunkSize + (chunkSize % 2);
        }

        if (audioFormat != 1 || channels != 1 || bitsPerSample != 16)
        {
            error = "Expected mono 16-bit PCM WAV input.";
            return false;
        }

        if (sampleRate != expectedSampleRate)
        {
            error = $"Expected sample rate {expectedSampleRate} Hz but received {sampleRate} Hz.";
            return false;
        }

        if (dataOffset < 0)
        {
            error = "WAV data chunk not found.";
            return false;
        }

        pcm16Bytes = waveBytes[dataOffset..(dataOffset + dataLength)];
        return true;
    }
}