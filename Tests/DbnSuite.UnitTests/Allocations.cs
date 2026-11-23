namespace DbnSuite.UnitTests;

/// <summary>Measures what code allocates on the test's own thread.</summary>
internal static class Allocations
{
    private const int Tries = 5;

    /// <summary>
    /// The fewest bytes <paramref name="run"/> allocated in any of several runs, after one run to warm it up. Now and then
    /// the runtime allocates on the thread while it settles, as when it tiers up a method, so one run can show a few
    /// kilobytes that aren't the code's. Code that allocates for every record it reads does so on every run.
    /// </summary>
    public static long Least(Action run)
    {
        run();

        var least = long.MaxValue;

        for (var i = 0; i < Tries && least > 0; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();

            run();

            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }
}
