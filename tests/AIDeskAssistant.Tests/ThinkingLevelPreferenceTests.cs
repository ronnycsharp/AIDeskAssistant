using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;
using OpenAI.Chat;

#pragma warning disable OPENAI001

namespace AIDeskAssistant.Tests;

public sealed class ThinkingLevelPreferenceTests
{
    [Fact]
    public void ApplyTo_DoesNotSetReasoningEffort_ForGpt54WhenFunctionToolsArePresent()
    {
        ChatCompletionOptions options = new();
        options.Tools.Add(DesktopToolDefinitions.GetChatTools()[0]);

        ThinkingLevelPreference.ApplyTo(options, "gpt-5.4", ThinkingLevelPreference.High);

        Assert.Null(options.ReasoningEffortLevel);
    }

    [Fact]
    public void ApplyTo_SetsReasoningEffort_ForGpt54WithoutFunctionTools()
    {
        ChatCompletionOptions options = new();

        ThinkingLevelPreference.ApplyTo(options, "gpt-5.4", ThinkingLevelPreference.High);

        Assert.Equal(ChatReasoningEffortLevel.High, options.ReasoningEffortLevel);
    }

    [Fact]
    public void ApplyTo_SetsReasoningEffort_ForO3WhenFunctionToolsArePresent()
    {
        ChatCompletionOptions options = new();
        options.Tools.Add(DesktopToolDefinitions.GetChatTools()[0]);

        ThinkingLevelPreference.ApplyTo(options, "o3", ThinkingLevelPreference.Medium);

        Assert.Equal(ChatReasoningEffortLevel.Medium, options.ReasoningEffortLevel);
    }
}

#pragma warning restore OPENAI001