using System.Diagnostics;
using System.Text;

namespace AIDeskAssistant.Services;

/// <summary>Executes CLI processes without invoking a shell.</summary>
internal sealed class ProcessTerminalService : ITerminalService
{
    private const int MinTimeoutMs  = 100;
    private const int MaxTimeoutMs  = 60_000;
    private const int MaxOutputSize = 4_000;

    public (int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
        ExecuteCommand(string command, IReadOnlyList<string> arguments, int timeoutMs)
    {
        ValidateCommand(command);

        foreach (string argument in arguments)
        {
            if (argument.Contains('\0'))
                throw new ArgumentException("Command arguments must not contain NUL characters.", nameof(arguments));
        }

        int effectiveTimeout = Math.Clamp(timeoutMs, MinTimeoutMs, MaxTimeoutMs);

        var psi = new ProcessStartInfo
        {
            FileName               = command,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => AppendLine(stdout, e.Data);
        process.ErrorDataReceived  += (_, e) => AppendLine(stderr, e.Data);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start command '{command}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(effectiveTimeout))
        {
            TryKill(process);
            return (-1, stdout.ToString(), stderr.ToString(), true);
        }

        process.WaitForExit();

        return (process.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    private static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command is required.", nameof(command));

        if (Path.IsPathFullyQualified(command))
        {
            foreach (char c in command)
            {
                if (c is '\0' or '\r' or '\n')
                    throw new ArgumentException($"Invalid command name: '{command}'", nameof(command));
            }

            return;
        }

        foreach (char c in command)
        {
            if (!char.IsLetterOrDigit(c) && c is not '-' and not '_' and not '.')
                throw new ArgumentException($"Invalid command name: '{command}'", nameof(command));
        }
    }

    private static void AppendLine(StringBuilder builder, string? line)
    {
        if (string.IsNullOrEmpty(line) || builder.Length >= MaxOutputSize)
            return;

        int remaining = MaxOutputSize - builder.Length;
        if (line.Length + Environment.NewLine.Length > remaining)
        {
            builder.Append(line[..Math.Max(0, remaining - 1)]);
            return;
        }

        builder.AppendLine(line);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
