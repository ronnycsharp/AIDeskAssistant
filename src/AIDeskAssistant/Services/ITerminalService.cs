namespace AIDeskAssistant.Services;

public interface ITerminalService
{
    /// <summary>
    /// Executes a CLI command directly (without shell interpolation) and returns
    /// its captured output.
    /// </summary>
    (int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
        ExecuteCommand(string command, IReadOnlyList<string> arguments, int timeoutMs);
}
