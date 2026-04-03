using OpenAI.Realtime;

namespace AIDeskAssistant.Services;

internal sealed class MenuBarSpeechService
{
    private static readonly string[] BuiltInVoiceIds = ["alloy", "ash", "ballad", "cedar", "coral", "echo", "marin", "sage", "shimmer", "verse"];
    private const int OutputSampleRate = 24_000;
    private const string SpeechInstructions =
        """
        Du bist nur für Sprachausgabe zuständig.
        Sprich den erhaltenen Text auf Deutsch exakt vor.
        Füge nichts hinzu und lasse nichts weg.
        """;

    private readonly RealtimeClient _client;
    private readonly string _speechModel;
    private readonly string _transcriptionModel;
    private string _voice;

    public MenuBarSpeechService(string apiKey, string speechModel, string transcriptionModel)
    {
        _client = new RealtimeClient(apiKey);
        _speechModel = speechModel;
        _transcriptionModel = transcriptionModel;
        string configuredVoice = Environment.GetEnvironmentVariable("AIDESK_REALTIME_VOICE")
            ?? RealtimeVoicePreferenceStore.TryLoadVoice()
            ?? "alloy";
        _voice = NormalizeVoiceId(configuredVoice);
    }

    public string CurrentVoice => _voice;

    public IReadOnlyList<string> GetAvailableVoices()
    {
        string currentVoice = _voice;
        if (BuiltInVoiceIds.Contains(currentVoice, StringComparer.OrdinalIgnoreCase))
            return BuiltInVoiceIds;

        return [currentVoice, .. BuiltInVoiceIds];
    }

    public Task<string> SetVoiceAsync(string voiceId, CancellationToken ct = default)
    {
        string normalizedVoiceId = NormalizeVoiceId(voiceId);
        _voice = normalizedVoiceId;
        Environment.SetEnvironmentVariable("AIDESK_REALTIME_VOICE", normalizedVoiceId);
        RealtimeVoicePreferenceStore.SaveVoice(normalizedVoiceId);
        return Task.FromResult(normalizedVoiceId);
    }

    public async Task<string> TranscribeWaveAsync(byte[] waveBytes, CancellationToken ct = default)
    {
        if (!WaveAudioUtility.TryExtractPcm16FromWave(waveBytes, OutputSampleRate, out byte[] pcmBytes, out string error))
            throw new InvalidOperationException(error);

        RealtimeSessionClient session = await _client.StartTranscriptionSessionAsync(new RealtimeSessionClientOptions(), ct);
        try
        {
            RealtimeTranscriptionSessionOptions options = new()
            {
                AudioOptions = new RealtimeTranscriptionSessionAudioOptions
                {
                    InputAudioOptions = new RealtimeTranscriptionSessionInputAudioOptions
                    {
                        AudioFormat = new RealtimePcmAudioFormat(),
                        AudioTranscriptionOptions = new RealtimeAudioTranscriptionOptions
                        {
                            Model = _transcriptionModel,
                            Language = "de",
                            Prompt = "Transkribiere deutsche Sprache exakt. Wenn keine verständliche Sprache erkannt wird, gib einen leeren Transkriptionsstring zurück.",
                        },
                    },
                },
            };

            await session.ConfigureTranscriptionSessionAsync(options, ct);
            await session.SendInputAudioAsync(BinaryData.FromBytes(pcmBytes), ct);
            await session.CommitPendingAudioAsync(ct);

            await foreach (RealtimeServerUpdate update in session.ReceiveUpdatesAsync(ct))
            {
                if (update is RealtimeServerUpdateConversationItemInputAudioTranscriptionCompleted completed)
                    return completed.Transcript?.Trim() ?? string.Empty;
            }

            return string.Empty;
        }
        finally
        {
            session.Dispose();
        }
    }

    public async Task<byte[]?> GenerateSpeechWavAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        RealtimeSessionClient session = await _client.StartConversationSessionAsync(_speechModel, new RealtimeSessionClientOptions(), ct);
        try
        {
            RealtimeConversationSessionOptions options = new()
            {
                Instructions = SpeechInstructions,
                AudioOptions = new RealtimeConversationSessionAudioOptions
                {
                    OutputAudioOptions = new RealtimeConversationSessionOutputAudioOptions
                    {
                        AudioFormat = new RealtimePcmAudioFormat(),
                        Voice = new RealtimeVoice(_voice),
                    },
                },
            };

            options.OutputModalities.Add(RealtimeOutputModality.Audio);

            await session.ConfigureConversationSessionAsync(options, ct);
            await session.AddItemAsync(new RealtimeMessageItem(new RealtimeMessageRole("user"), [new RealtimeInputTextMessageContentPart(text)]), ct);
            await session.StartResponseAsync(new RealtimeResponseOptions
            {
                OutputModalities = { RealtimeOutputModality.Audio }
            }, ct);

            using var audioBuffer = new MemoryStream();
            await foreach (RealtimeServerUpdate update in session.ReceiveUpdatesAsync(ct))
            {
                if (update is RealtimeServerUpdateResponseOutputAudioDelta audioDelta)
                {
                    byte[] chunk = audioDelta.Delta.ToArray();
                    audioBuffer.Write(chunk, 0, chunk.Length);
                    continue;
                }

                if (update is RealtimeServerUpdateResponseDone)
                    break;
            }

            byte[] pcmBytes = audioBuffer.ToArray();
            return pcmBytes.Length == 0 ? null : WaveAudioUtility.CreateWaveFile(pcmBytes, OutputSampleRate);
        }
        finally
        {
            session.Dispose();
        }
    }

    public static bool TryExtractStreamingPcm(byte[]? audioWavBytes, out byte[] pcmBytes)
    {
        pcmBytes = Array.Empty<byte>();
        if (audioWavBytes is null || audioWavBytes.Length == 0)
            return false;

        return WaveAudioUtility.TryExtractPcm16FromWave(audioWavBytes, OutputSampleRate, out pcmBytes, out _);
    }

    private static string NormalizeVoiceId(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice is required.", nameof(voiceId));

        string trimmedVoiceId = voiceId.Trim();
        string? builtInVoice = BuiltInVoiceIds.FirstOrDefault(candidate => candidate.Equals(trimmedVoiceId, StringComparison.OrdinalIgnoreCase));
        return builtInVoice ?? trimmedVoiceId;
    }
}