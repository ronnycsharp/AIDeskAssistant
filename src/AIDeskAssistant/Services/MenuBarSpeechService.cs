using OpenAI.Chat;

namespace AIDeskAssistant.Services;

internal sealed class MenuBarSpeechService
{
    private static readonly string[] BuiltInVoiceIds = ["alloy", "ash", "ballad", "cedar", "coral", "echo", "marin", "sage", "shimmer", "verse"];
    private const int OutputSampleRate = 24_000;
    private const string TranscriptionPrompt =
        """
        Transkribiere die Nutzeraudio exakt.
        Antworte nur mit dem erkannten deutschen Klartext.
        Keine Einleitung, keine Zusammenfassung, keine Interpretation.
        Wenn keine verständliche Sprache vorhanden ist, antworte nur mit: <no-speech>
        """;
    private const string SpeechPrompt =
        """
        Sprich den vom Nutzer gegebenen Text genau so aus.
        Füge nichts hinzu und lasse nichts weg.
        Antworte ausschließlich mit Audio und dem identischen Transkript.
        """;

    private readonly ChatClient _client;
    private string _voice;

    public MenuBarSpeechService(string apiKey, string model)
    {
        _client = new ChatClient(model, apiKey);
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
        UserChatMessage userMessage = new(
            ChatMessageContentPart.CreateTextPart("Transkribiere diese Spracheingabe."),
            ChatMessageContentPart.CreateInputAudioPart(BinaryData.FromBytes(waveBytes), ChatInputAudioFormat.Wav));

        ChatCompletion completion = await _client.CompleteChatAsync(
            [new SystemChatMessage(TranscriptionPrompt), userMessage],
            cancellationToken: ct);

        string transcript = FlattenText(completion).Trim();
        return string.Equals(transcript, "<no-speech>", StringComparison.OrdinalIgnoreCase) ? string.Empty : transcript;
    }

    public async Task<byte[]?> GenerateSpeechWavAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        ChatCompletionOptions options = new()
        {
            ResponseModalities = ChatResponseModalities.Text | ChatResponseModalities.Audio,
            AudioOptions = new(new ChatOutputAudioVoice(_voice), ChatOutputAudioFormat.Pcm16),
        };

        ChatCompletion completion = await _client.CompleteChatAsync(
            [new SystemChatMessage(SpeechPrompt), new UserChatMessage(text)],
            options,
            ct);

        byte[]? pcmBytes = completion.OutputAudio?.AudioBytes.ToArray();
        return pcmBytes is null || pcmBytes.Length == 0 ? null : WaveAudioUtility.CreateWaveFile(pcmBytes, OutputSampleRate);
    }

    public static bool TryExtractStreamingPcm(byte[]? audioWavBytes, out byte[] pcmBytes)
    {
        pcmBytes = Array.Empty<byte>();
        if (audioWavBytes is null || audioWavBytes.Length == 0)
            return false;

        return WaveAudioUtility.TryExtractPcm16FromWave(audioWavBytes, OutputSampleRate, out pcmBytes, out _);
    }

    private static string FlattenText(ChatCompletion completion)
        => string.Concat(completion.Content.Where(static part => part.Kind == ChatMessageContentPartKind.Text).Select(static part => part.Text));

    private static string NormalizeVoiceId(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice is required.", nameof(voiceId));

        string trimmedVoiceId = voiceId.Trim();
        string? builtInVoice = BuiltInVoiceIds.FirstOrDefault(candidate => candidate.Equals(trimmedVoiceId, StringComparison.OrdinalIgnoreCase));
        return builtInVoice ?? trimmedVoiceId;
    }
}