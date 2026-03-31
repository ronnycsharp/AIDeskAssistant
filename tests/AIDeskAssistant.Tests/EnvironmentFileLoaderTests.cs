using AIDeskAssistant;

namespace AIDeskAssistant.Tests;

public sealed class EnvironmentFileLoaderTests
{
    [Theory]
    [InlineData("OPENAI_API_KEY=sk-test", "OPENAI_API_KEY", "sk-test")]
    [InlineData("export OPENAI_MODEL=gpt-4o-mini", "OPENAI_MODEL", "gpt-4o-mini")]
    [InlineData("AIDESK_MAX_TOOL_ROUNDS=120", "AIDESK_MAX_TOOL_ROUNDS", "120")]
    public void TryParseAssignment_ParsesSupportedFormats(string line, string expectedKey, string expectedValue)
    {
        bool parsed = EnvironmentFileLoader.TryParseAssignment(line, out string? key, out string? value);

        Assert.True(parsed);
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# comment")]
    [InlineData("NOT_AN_ASSIGNMENT")]
    public void TryParseAssignment_IgnoresUnsupportedLines(string line)
    {
        bool parsed = EnvironmentFileLoader.TryParseAssignment(line, out _, out _);

        Assert.False(parsed);
    }

    [Fact]
    public void LoadFile_SetsVariablesFromEnvFile()
    {
        string variableName = "AIDESKASSISTANT_TEST_ENV_FILE_SET";
        string filePath = CreateTempEnvFile($"{variableName}=loaded-value");
        string? originalValue = Environment.GetEnvironmentVariable(variableName);

        try
        {
            Environment.SetEnvironmentVariable(variableName, null);

            EnvironmentFileLoader.LoadFile(filePath);

            Assert.Equal("loaded-value", Environment.GetEnvironmentVariable(variableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
            File.Delete(filePath);
        }
    }

    [Fact]
    public void LoadFile_DoesNotOverwriteExistingVariablesByDefault()
    {
        string variableName = "AIDESKASSISTANT_TEST_ENV_FILE_PRESERVE";
        string filePath = CreateTempEnvFile($"{variableName}=from-file");
        string? originalValue = Environment.GetEnvironmentVariable(variableName);

        try
        {
            Environment.SetEnvironmentVariable(variableName, "existing-value");

            EnvironmentFileLoader.LoadFile(filePath);

            Assert.Equal("existing-value", Environment.GetEnvironmentVariable(variableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
            File.Delete(filePath);
        }
    }

    [Fact]
    public void LoadFile_CanOverwriteExistingVariablesWhenRequested()
    {
        string variableName = "AIDESKASSISTANT_TEST_ENV_FILE_OVERWRITE";
        string filePath = CreateTempEnvFile($"{variableName}=from-file");
        string? originalValue = Environment.GetEnvironmentVariable(variableName);

        try
        {
            Environment.SetEnvironmentVariable(variableName, "existing-value");

            EnvironmentFileLoader.LoadFile(filePath, overwriteExisting: true);

            Assert.Equal("from-file", Environment.GetEnvironmentVariable(variableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
            File.Delete(filePath);
        }
    }

    private static string CreateTempEnvFile(string content)
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.env");
        File.WriteAllText(filePath, content + Environment.NewLine);
        return filePath;
    }
}