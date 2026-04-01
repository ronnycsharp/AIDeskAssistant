using System.Diagnostics;
using System.Reflection;

namespace AIDeskAssistant.Platform.MacOS;

internal static class MacOSStatusBarLauncher
{
    public static void LaunchDetachedHost(IReadOnlyList<string> parentArgs)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The status bar launcher is only available on macOS.");

        using Process process = Process.Start(CreateDetachedHostStartInfo(parentArgs))
            ?? throw new InvalidOperationException("Failed to launch the detached macOS menu bar host.");
    }

    public static async Task<int> RunAsync(Uri serverUri, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The status bar launcher is only available on macOS.");

        string scriptPath = ResolveScriptPath();

        ProcessStartInfo startInfo = new("xcrun", ["swift", scriptPath, serverUri.AbsoluteUri])
        {
            UseShellExecute = false,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch the macOS status bar helper.");

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    private static ProcessStartInfo CreateDetachedHostStartInfo(IReadOnlyList<string> parentArgs)
    {
        List<string> childArgs = parentArgs
            .Where(static arg => !string.Equals(arg, "--menu-bar", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(arg, "--menu-bar-host", StringComparison.OrdinalIgnoreCase))
            .ToList();
        childArgs.Add("--menu-bar-host");

        string workingDirectory = Directory.GetCurrentDirectory();

        if (TryResolveAppHostPath(out string appHostPath))
        {
            ProcessStartInfo appHostStartInfo = new(appHostPath)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };

            foreach (string argument in childArgs)
                appHostStartInfo.ArgumentList.Add(argument);

            return appHostStartInfo;
        }

        string assemblyPath = ResolveAssemblyPath();
        ProcessStartInfo dotnetStartInfo = new("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        dotnetStartInfo.ArgumentList.Add(assemblyPath);
        foreach (string argument in childArgs)
            dotnetStartInfo.ArgumentList.Add(argument);

        return dotnetStartInfo;
    }

    private static string ResolveScriptPath()
    {
        string[] candidatePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "AIDeskAssistantStatusBar.swift"),
            Path.Combine(AppContext.BaseDirectory, "Platform", "MacOS", "AIDeskAssistantStatusBar.swift"),
        ];

        string? existingPath = candidatePaths.FirstOrDefault(File.Exists);
        if (existingPath is not null)
            return existingPath;

        throw new FileNotFoundException(
            $"The macOS status bar script was not found in the application output. Checked: {string.Join(", ", candidatePaths)}");
    }

    private static bool TryResolveAppHostPath(out string appHostPath)
    {
        string assemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? "AIDeskAssistant";
        string candidatePath = Path.Combine(AppContext.BaseDirectory, assemblyName);
        if (File.Exists(candidatePath))
        {
            appHostPath = candidatePath;
            return true;
        }

        appHostPath = string.Empty;
        return false;
    }

    private static string ResolveAssemblyPath()
    {
        string assemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? "AIDeskAssistant";
        string candidatePath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        if (File.Exists(candidatePath))
            return candidatePath;

        throw new FileNotFoundException($"The application assembly was not found at '{candidatePath}'.");
    }
}