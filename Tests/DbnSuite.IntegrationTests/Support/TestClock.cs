using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>
/// The worker's clock (the one keyed <c>SyncClock.Key</c>), which moves only
/// when a scenario moves it. Its <c>Task.Delay</c> between passes is how a
/// scenario knows a pass, and the corpus scan after it, have finished, without
/// sleeping or polling.
/// </summary>
/// <remarks>
/// That delay must be the only timer on this clock. Anything else on it fires
/// when a scenario jumps a day or a week, once for every period in the jump and
/// while the pass that jump woke is running, and one due when a pass delay is
/// would be taken for a pass that has not finished. Service discovery's
/// 10-second cleanup was such a timer while this clock replaced the app's
/// unkeyed one: run thousands of times beside a pass, it crashed one now and
/// then in CI. The framework now keeps the real clock, and any other timer set
/// here is recorded and refused by the scenario.
/// </remarks>
public sealed class TestClock(DateTimeOffset now) : FakeTimeProvider(now)
{
    public static readonly TimeSpan BusyInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private const int StackFrames = 8;

    private readonly Channel<TimeSpan> delays = Channel.CreateUnbounded<TimeSpan>();
    private readonly ConcurrentQueue<string> strangers = new();

    /// <summary>Every timer set on this clock that is not the worker's delay between passes, and who set it.</summary>
    public IReadOnlyCollection<string> Strangers => strangers;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);

        if (period == Timeout.InfiniteTimeSpan && (dueTime == BusyInterval || dueTime == Interval))
            delays.Writer.TryWrite(dueTime);
        else
            strangers.Enqueue($"due {dueTime}, every {period}, set by:{Environment.NewLine}{SetBy()}");

        return timer;
    }

    /// <summary>Waits for the worker to settle into its delay, and says how long it is.</summary>
    public ValueTask<TimeSpan> PassDelayAsync(CancellationToken cancel) => delays.Reader.ReadAsync(cancel);

    private static string SetBy() =>
        string.Join(Environment.NewLine, new StackTrace(2).GetFrames().Take(StackFrames).Select(f => $"   at {f.GetMethod()?.DeclaringType}.{f.GetMethod()?.Name}"));

    /// <summary>Drops a delay a stopped worker never finished.</summary>
    public void ForgetDelays()
    {
        while (delays.Reader.TryRead(out _))
        {
        }
    }
}
