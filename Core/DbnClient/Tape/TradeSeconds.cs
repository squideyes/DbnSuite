namespace DbnClient.Tape;

/// <summary>One second of the source's <c>ohlcv-1s</c> bars, in raw price units.</summary>
internal readonly record struct SecondBar(long Second, long Open, long High, long Low, long Close, ulong Volume);

/// <summary>
/// Sums <c>mbo</c> trades into the seconds of their receive time, as the source's 1-second
/// bars are, to compare with those bars. Both sides take every trade by its receive time
/// over the same seconds, so the receive clock's lead cannot put a trade on one side and
/// leave it off the other. Those seconds start before the open by that lead, as
/// "Source inside session" allows. The session's own volume, by event time, is what the
/// Trade tape must hold.
/// </summary>
internal sealed class TradeSeconds
{
    private const long NanosPerSecond = 1_000_000_000;

    private readonly SessionDay session;
    private readonly long first;
    private readonly long last;
    private readonly Dictionary<long, SecondBar> built = [];

    /// <param name="lead">How far the receive clock runs ahead of the exchange's, in nanoseconds; 0 for none.</param>
    public TradeSeconds(SessionDay session, long lead)
    {
        this.session = session;

        var slack = lead > 0 ? lead + NanosPerSecond : 0;

        first = slack == 0 ? session.Open / NanosPerSecond : (session.Open - slack + NanosPerSecond - 1) / NanosPerSecond;
        last = session.Close / NanosPerSecond;
    }

    /// <summary>Volume traded in the session, by event time.</summary>
    public ulong SessionVolume { get; private set; }

    /// <summary>Sees one trade outside a snapshot, at a raw price.</summary>
    public void Trade(ulong tsEvent, ulong tsRecv, long price, uint size)
    {
        if ((long)tsEvent >= session.Open && (long)tsEvent < session.Close)
            SessionVolume += size;

        var second = (long)(tsRecv / NanosPerSecond);

        if (second < first || second > last)
            return;

        built[second] = built.TryGetValue(second, out var bar)
            ? bar with { High = Math.Max(bar.High, price), Low = Math.Min(bar.Low, price), Close = price, Volume = bar.Volume + size }
            : new SecondBar(second, price, price, price, price, size);
    }

    /// <summary>The first way the trades and the bars part, or null when they agree.</summary>
    public string? Against(IEnumerable<SecondBar> source)
    {
        var bars = source.Where(b => b.Second >= first && b.Second <= last).ToList();

        foreach (var bar in bars)
        {
            if (!built.TryGetValue(bar.Second, out var mine))
                return $"the source has a bar at {bar.Second} with no trades";

            if (mine != bar)
                return $"the second {bar.Second} differs: trades {mine}, bar {bar}";
        }

        return built.Count == bars.Count ? null : $"trades fill {built.Count:N0} seconds, bars {bars.Count:N0}";
    }
}
