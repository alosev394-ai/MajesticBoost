using System;
using System.Globalization;
using MajesticBoost;

internal static class PerformanceCaptureParserHarness
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Fixture path is required.");
            return 2;
        }

        PerformanceCaptureAttemptResult result =
            PerformanceCaptureService.ParseCaptureCsvForTesting(
                args[0],
                4242,
                "GTA5",
                DateTime.UtcNow);

        if (result.Status != PerformanceCaptureStatus.Completed ||
            result.Performance == null ||
            !result.Performance.Available)
        {
            Console.Error.WriteLine(
                "Parser failed: " + result.Status + " / " + result.Message);
            return 3;
        }

        BoostPerformanceResult metrics = result.Performance;
        AssertEqual("Frames", metrics.Frames, 650);
        AssertNear("AverageFps", metrics.AverageFps, 57.4204946996, 0.0001);
        AssertNear("OnePercentLowFps", metrics.OnePercentLowFps, 16.6666666667, 0.0001);
        AssertNear("P95FrameTimeMs", metrics.P95FrameTimeMs, 16.0, 0.0001);
        AssertNear("P99FrameTimeMs", metrics.P99FrameTimeMs, 60.0, 0.0001);
        AssertEqual("FramesOver50Ms", metrics.FramesOver50Ms, 10);
        AssertEqual("FramesOver100Ms", metrics.FramesOver100Ms, 6);
        Console.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "PASS frames={0} avg={1:0.000} 1%low={2:0.000}",
                metrics.Frames,
                metrics.AverageFps,
                metrics.OnePercentLowFps));
        return 0;
    }

    private static void AssertEqual(string name, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                name + ": expected " + expected + ", actual " + actual + ".");
        }
    }

    private static void AssertNear(
        string name,
        double actual,
        double expected,
        double tolerance)
    {
        if (Math.Abs(actual - expected) > tolerance)
        {
            throw new InvalidOperationException(
                name + ": expected " +
                expected.ToString("R", CultureInfo.InvariantCulture) +
                ", actual " +
                actual.ToString("R", CultureInfo.InvariantCulture) +
                ".");
        }
    }
}
