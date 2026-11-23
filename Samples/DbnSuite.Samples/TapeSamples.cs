using DbnSuite.Client.Export;
using DbnSuite.Client.Tape;

namespace DbnSuite.Samples;

/// <summary>
/// The tape samples on the DbnTape docs page, word for word. They read the tapes folder in the
/// current folder, as the page's paths do. SamplesTests runs them on the synthetic tapes in
/// Clients/python/tests/fixtures and checks the page still quotes them.
/// </summary>
public static class TapeSamples
{
    /// <summary>One day's regular hours, event by event, from an L1 tape.</summary>
    public static async Task ReplayAsync(CancellationToken cancel)
    {
        var strategy = new Strategy();

        await ReplayAsync(strategy, cancel);

        Console.WriteLine($"replay: {strategy}");
    }

    private static async Task ReplayAsync(Strategy strategy, CancellationToken cancel)
    {
        var loaded = await Tape.LoadAsync("tapes/GLBX.MDP3/l1/ES/2026/ES.l1.20261029.tape", cancel);

        if (!loaded.IsSuccess)
            return;

        var cursor = loaded.Value.L1Cursor(Window.Rth);

        while (cursor.MoveNext())
        {
            switch (cursor.Kind)
            {
                case L1Kind.Quote:
                    strategy.OnQuote(cursor.Time, cursor.BidPrice, cursor.BidSize, cursor.AskPrice, cursor.AskSize);
                    break;

                case L1Kind.Trade:
                    strategy.OnTrade(cursor.Time, cursor.TradePrice, cursor.TradeSize, cursor.Side);
                    break;

                case L1Kind.State:
                    strategy.OnState(cursor.Time, cursor.State);
                    break;
            }
        }
    }

    /// <summary>5-minute bars rolled up from an S1 tape as it streams.</summary>
    public static void Bars(Tape tape)
    {
        var strategy = new Strategy();

        var bars = tape.Bars(BarSpec.Parse("5m").Value, Window.Rth);

        while (bars.MoveNext())
            strategy.OnBar(bars.End, bars.Open, bars.High, bars.Low, bars.Close, bars.Volume);

        Console.WriteLine($"bars: {strategy}");
    }

    /// <summary>Seeded Monte Carlo runs of whole sessions, each stitched onto the one before.</summary>
    public static async Task MonteCarloAsync(CancellationToken cancel)
    {
        var stream = new SessionStream("tapes", "GLBX.MDP3", "ES", TapeKind.L1);
        var sampler = new SessionSampler(stream.Days(), seed: 42, Sampling.Permutation, warmup: 5);

        for (var run = 0; run < 1_000; run++)
        {
            var strategy = new Strategy();

            await foreach (var session in stream.RunAsync(sampler, run, cancel))
            {
                if (!session.IsSuccess)
                    return;

                var day = session.Value;
                var cursor = day.Tape.L1Cursor();

                while (cursor.MoveNext())
                {
                    if (cursor.Kind == L1Kind.Trade)
                        strategy.OnTrade(cursor.TradePrice, cursor.TradePrice + day.Offset, canTrade: !day.IsWarmup);
                }
            }

            strategy.OnRunEnd(run);
        }
    }
}
