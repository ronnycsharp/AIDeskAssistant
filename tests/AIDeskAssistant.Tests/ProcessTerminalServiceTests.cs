using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class ProcessTerminalServiceTests
{
    [Fact]
    public void ExecuteCommand_AllowsAbsoluteExecutablePath()
    {
        var sut = new ProcessTerminalService();

        var result = sut.ExecuteCommand("/usr/bin/printf", ["ok"], 1_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.StandardOutput.TrimEnd());
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void ExecuteCommand_RejectsCommandWithShellMetacharacters()
    {
        var sut = new ProcessTerminalService();

        var ex = Assert.Throws<ArgumentException>(() => sut.ExecuteCommand("peekaboo;rm", Array.Empty<string>(), 1_000));

        Assert.Contains("Invalid command name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteCommand_AllowsNewlinesInsideArguments()
    {
        var sut = new ProcessTerminalService();

        var result = sut.ExecuteCommand("/usr/bin/printf", ["Line 1\nLine 2"], 1_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Line 1\nLine 2", result.StandardOutput.TrimEnd());
        Assert.False(result.TimedOut);
    }
}