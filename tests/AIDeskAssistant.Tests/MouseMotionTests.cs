using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class MouseMotionTests
{
    [Fact]
    public void CreateEasedPath_AlwaysEndsAtTarget()
    {
        IReadOnlyList<(int X, int Y)> path = MouseMotion.CreateEasedPath((10, 20), (300, 200));

        Assert.NotEmpty(path);
        Assert.Equal((300, 200), path[^1]);
    }

    [Fact]
    public void CreateEasedPath_ForLongMovement_ContainsMultipleIntermediatePoints()
    {
        IReadOnlyList<(int X, int Y)> path = MouseMotion.CreateEasedPath((0, 0), (600, 300));

        Assert.True(path.Count > 5, $"Expected several eased steps but got {path.Count}.");
    }

    [Fact]
    public void CreateEasedPath_SameStartAndEnd_ReturnsSinglePoint()
    {
        IReadOnlyList<(int X, int Y)> path = MouseMotion.CreateEasedPath((42, 99), (42, 99));

        Assert.Equal([(42, 99)], path);
    }

    [Fact]
    public void CreateEasedPath_MonotonicallyApproachesTargetForPositiveMovement()
    {
        IReadOnlyList<(int X, int Y)> path = MouseMotion.CreateEasedPath((0, 0), (200, 100));

        int previousX = int.MinValue;
        int previousY = int.MinValue;

        foreach (var point in path)
        {
            Assert.True(point.X >= previousX);
            Assert.True(point.Y >= previousY);
            previousX = point.X;
            previousY = point.Y;
        }
    }
}
