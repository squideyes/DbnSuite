using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DbnClient.Tape;

/// <summary>One session of a Monte Carlo run, ready to replay.</summary>
/// <param name="Index">Its place in the run, from 0.</param>
/// <param name="IsWarmup">The session only warms indicators up; nothing trades in it.</param>
/// <param name="Offset">
/// Ticks to add to this session's prices for anything that spans sessions, such as indicators
/// and stats, so the run reads as one unbroken series. Fills stay on the as-traded prices.
/// It is 0 for a run's first session.
/// </param>
public sealed record StreamSession(int Index, DateOnly TradeDate, bool IsWarmup, Tape Tape, long Offset);

/// <summary>
/// Replays the sessions a <see cref="SessionSampler"/> deals, whole, one tape after another, and
/// stitches each onto the one before it in the run: a session's offset puts its first trade
/// where the previous session's last trade stood, with that one's own offset added. That is
/// back-adjustment done going forward, so it costs nothing per event and needs no history
/// rewritten. Sessions in a run are unrelated days, so there is no real gap between them to keep.
/// </summary>
/// <remarks>
/// The next session's tape loads while the current one replays. The first and last trade of
/// each date are found once and kept, so several runs, in turn or at once, share the work.
/// </remarks>
public sealed class SessionStream(string tapeRoot, string dataset, string root, TapeKind kind)
{
    private readonly ConcurrentDictionary<DateOnly, Ends> ends = new();

    /// <summary>The dates the tape folder holds tapes of this kind for, oldest first: what a sampler deals from.</summary>
    public IReadOnlyList<DateOnly> Days() => TapePaths.Dates(tapeRoot, dataset, kind, root);

    /// <summary>
    /// Run <paramref name="run"/>'s sessions, in the order the sampler deals them. A missing or
    /// damaged tape, or one that can't be stitched, is a failed result, and ends the run.
    /// </summary>
    public async IAsyncEnumerable<Result<StreamSession>> RunAsync(SessionSampler sampler, int run,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(sampler);

        var draws = sampler.Run(run);
        var next = LoadAsync(draws[0].TradeDate, cancel);
        var offset = 0L;
        Ends? previous = null;
        long? tickRaw = null;

        for (var i = 0; i < draws.Count; i++)
        {
            var loaded = await next.ConfigureAwait(false);

            if (!loaded.IsSuccess)
            {
                yield return loaded.Error!;
                yield break;
            }

            if (i + 1 < draws.Count)
                next = LoadAsync(draws[i + 1].TradeDate, cancel);

            var (tape, these) = loaded.Value;

            if (tickRaw is { } tick && tick != tape.Header.TickRaw)
            {
                yield return new Error(TapeErrors.Refused,
                    $"The {draws[i].TradeDate:yyyy-MM-dd} tape's tick is {tape.Header.TickRaw}, and the run's is {tick}, so its sessions can't be stitched in ticks.");
                yield break;
            }

            if (previous is { } before)
                offset += before.Last - these.First;

            previous = these;
            tickRaw = tape.Header.TickRaw;

            yield return new StreamSession(i, draws[i].TradeDate, draws[i].IsWarmup, tape, offset);
        }
    }

    private async Task<Result<(Tape Tape, Ends Ends)>> LoadAsync(DateOnly tradeDate, CancellationToken cancel)
    {
        var path = TapePaths.For(tapeRoot, dataset, kind, root, tradeDate);
        var loaded = await Tape.LoadAsync(path, cancel).ConfigureAwait(false);

        if (!loaded.IsSuccess)
            return loaded.Error!;

        var tape = loaded.Value;

        if (ends.TryGetValue(tradeDate, out var known))
            return (tape, known);

        if (Scan(tape, kind) is not { } found)
            return new Error(TapeErrors.Refused, $"{path} has no trades, so its session can't be stitched.");

        ends[tradeDate] = found;

        return (tape, found);
    }

    /// <summary>A session's first and last traded price, in ticks: the open and close of S1 bars.</summary>
    internal static Ends? Scan(Tape tape, TapeKind kind)
    {
        int? first = null;
        var last = 0;

        switch (kind)
        {
            case TapeKind.S1:
                var bars = tape.S1Cursor();

                while (bars.MoveNext())
                {
                    first ??= bars.Open;
                    last = bars.Close;
                }

                break;

            case TapeKind.Trade:
                var trades = tape.TradeCursor();

                while (trades.MoveNext())
                {
                    first ??= trades.Price;
                    last = trades.Price;
                }

                break;

            default:
                var events = tape.L1Cursor();

                while (events.MoveNext())
                {
                    if (events.Kind != L1Kind.Trade)
                        continue;

                    first ??= events.TradePrice;
                    last = events.TradePrice;
                }

                break;
        }

        return first is { } open ? new Ends(open, last) : null;
    }

    internal readonly record struct Ends(int First, int Last);
}
