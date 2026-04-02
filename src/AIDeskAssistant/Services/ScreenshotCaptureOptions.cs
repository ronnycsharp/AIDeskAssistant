using AIDeskAssistant.Models;

namespace AIDeskAssistant.Services;

public readonly record struct ScreenshotCaptureOptions(WindowBounds? Bounds = null);