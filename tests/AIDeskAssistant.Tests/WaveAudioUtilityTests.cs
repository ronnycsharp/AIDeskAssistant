using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class WaveAudioUtilityTests
{
    [Fact]
    public void CreateWaveFile_And_ExtractPcm16_RoundTripsAudioBytes()
    {
        byte[] pcm = [1, 0, 2, 0, 3, 0, 4, 0];

        byte[] wave = WaveAudioUtility.CreateWaveFile(pcm, 24_000);

        bool ok = WaveAudioUtility.TryExtractPcm16FromWave(wave, 24_000, out byte[] extracted, out string error);

        Assert.True(ok, error);
        Assert.Equal(pcm, extracted);
    }

    [Fact]
    public void TryExtractPcm16FromWave_RejectsUnexpectedSampleRate()
    {
        byte[] pcm = [1, 0, 2, 0];
        byte[] wave = WaveAudioUtility.CreateWaveFile(pcm, 16_000);

        bool ok = WaveAudioUtility.TryExtractPcm16FromWave(wave, 24_000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("Expected sample rate", error);
    }
}