using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>
/// The worker's clock, which moves only when a scenario moves it. Its
/// <c>Task.Delay</c> between passes is how a scenario knows a pass, and the
/// corpus scan after it, have finished, without sleeping or polling.
/// </summary>
public sealed class TestClock(DateTimeOffset now) : FakeTimeProvider(now)
{
    public static readonly TimeSpan BusyInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly Channel<TimeSpan> delays = Channel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);

        if (period == Timeout.InfiniteTimeSpan && (dueTime == BusyInterval || dueTime == Interval))
            delays.Writer.TryWrite(dueTime);

        return timer;
    }

    /// <summary>Waits for the worker to settle into its delay, and says how long it is.</summary>
    public ValueTask<TimeSpan> PassDelayAsync(CancellationToken cancel) => delays.Reader.ReadAsync(cancel);

    /// <summary>Drops a delay a stopped worker never finished.</summary>
    public void ForgetDelays()
    {
        while (delays.Reader.TryRead(out _))
        {
        }
    }
}
