using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class AIDebugLoggerTests
{
    [Fact]
    public void LogUiContext_WritesDedicatedUiContextFile()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"aideskassistant-tests-{Guid.NewGuid():N}");
        string? originalDebugFlag = Environment.GetEnvironmentVariable("AIDESK_DEBUG_MODEL_IO");
        string? originalDebugDir = Environment.GetEnvironmentVariable("AIDESK_DEBUG_DIR");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_DEBUG_MODEL_IO", "1");
            Environment.SetEnvironmentVariable("AIDESK_DEBUG_DIR", tempDirectory);

            AIDebugLogger? logger = AIDebugLogger.CreateFromArgsAndEnvironment(Array.Empty<string>());

            Assert.NotNull(logger);

            logger.LogUiContext("Frontmost app: Word\nVisible UI elements:\n- AXWindow | title=Document1 | x=0,y=25,w=1440,h=900");

            string uiContextFile = Path.Combine(logger.SessionDirectoryPath, "01-ui-context.txt");
            Assert.True(File.Exists(uiContextFile));
            Assert.Contains("Frontmost app: Word", File.ReadAllText(uiContextFile));

            string historyFile = Path.Combine(logger.SessionDirectoryPath, "history.log");
            Assert.True(File.Exists(historyFile));
            Assert.Contains("UI:", File.ReadAllText(historyFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_DEBUG_MODEL_IO", originalDebugFlag);
            Environment.SetEnvironmentVariable("AIDESK_DEBUG_DIR", originalDebugDir);

            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void LogScreenshotAttachment_WritesSupplementalImages()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"aideskassistant-tests-{Guid.NewGuid():N}");
        string? originalDebugFlag = Environment.GetEnvironmentVariable("AIDESK_DEBUG_MODEL_IO");
        string? originalDebugDir = Environment.GetEnvironmentVariable("AIDESK_DEBUG_DIR");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_DEBUG_MODEL_IO", "1");
            Environment.SetEnvironmentVariable("AIDESK_DEBUG_DIR", tempDirectory);

            AIDebugLogger? logger = AIDebugLogger.CreateFromArgsAndEnvironment(Array.Empty<string>());

            Assert.NotNull(logger);

            var attachment = new ScreenshotModelAttachment(
                "Screenshot taken.",
                [1, 2, 3],
                "image/png",
                [new ScreenshotSupplementalImage("mouse-detail", [4, 5, 6], "image/png")]);

            logger.LogScreenshotAttachment("call-1", attachment, retainedInHistory: true, similarityToPrevious: null);

            string[] files = Directory.GetFiles(logger.SessionDirectoryPath);
            Assert.Contains(files, path => Path.GetFileName(path) == "01-screenshot-call-1.png");
            Assert.Contains(files, path => Path.GetFileName(path) == "01-screenshot-call-1-mouse-detail.png");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_DEBUG_MODEL_IO", originalDebugFlag);
            Environment.SetEnvironmentVariable("AIDESK_DEBUG_DIR", originalDebugDir);

            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }
}