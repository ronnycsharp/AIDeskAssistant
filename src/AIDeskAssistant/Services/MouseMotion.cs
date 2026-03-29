namespace AIDeskAssistant.Services;

/// <summary>Builds smooth cursor paths for more realistic mouse motion.</summary>
internal static class MouseMotion
{
    public const int StepDelayMs = 12;

    public static IReadOnlyList<(int X, int Y)> CreateEasedPath((int X, int Y) start, (int X, int Y) end)
    {
        if (start == end)
            return [(end.X, end.Y)];

        double distance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        int durationMs = Math.Clamp((int)(distance * 0.5), 120, 450);
        int steps = Math.Clamp(durationMs / StepDelayMs, 8, 40);

        var path = new List<(int X, int Y)>(steps);
        for (int step = 1; step <= steps; step++)
        {
            double t = (double)step / steps;
            double eased = EaseOutCubic(t);
            int x = (int)Math.Round(Lerp(start.X, end.X, eased));
            int y = (int)Math.Round(Lerp(start.Y, end.Y, eased));

            if (path.Count == 0 || path[^1] != (x, y))
                path.Add((x, y));
        }

        if (path.Count == 0 || path[^1] != end)
            path.Add(end);

        return path;
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double Lerp(int start, int end, double t) => start + ((end - start) * t);
}
