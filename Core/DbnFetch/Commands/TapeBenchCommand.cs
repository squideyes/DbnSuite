using System.CommandLine;
using System.Diagnostics;
using DbnClient.Export;
using DbnClient.Tape;

namespace DbnFetch.Commands;

/// <summary><c>dbnf tape bench file</c>: times a replay on one thread, and counts what it allocates.</summary>
internal static class TapeBenchCommand
{
    private const int LeastRounds = 7;
    private const double LeastSeconds = 2;

    public static Command Create(Session session)
    {
        var file = new Argument<string>("file") { Description = "A tape." };
        var command = new Command("bench", "Time a replay on one thread.") { file };

        command.SetAction(async (parse, cancel) =>
        {
            var loaded = await Tape.LoadAsync(parse.GetValue(file)!, cancel).ConfigureAwait(false);

            if (!loaded.IsSuccess)
                return await session.FailAsync(loaded.Error!).ConfigureAwait(false);

            var tape = loaded.Value;

            switch (tape.Header.Kind)
            {
                case TapeKind.L1:
                    await session.Out.WriteLineAsync(Run("L1 replay", tape, () => WalkL1(tape))).ConfigureAwait(false);
                    break;

                case TapeKind.Trade:
                    await session.Out.WriteLineAsync(Run("Trade replay", tape, () => WalkTrades(tape))).ConfigureAwait(false);
                    break;

                default:
                    var minute = BarSpec.Parse("1m").Value;
                    var custom = BarSpec.Parse("7s").Value;

                    await session.Out.WriteLineAsync(Run("S1 replay", tape, () => WalkS1(tape))).ConfigureAwait(false);
                    await session.Out.WriteLineAsync(Run("1m bars", tape, () => WalkBars(tape, minute))).ConfigureAwait(false);
                    await session.Out.WriteLineAsync(Run("7s bars", tape, () => WalkBars(tape, custom))).ConfigureAwait(false);
                    break;
            }

            return Session.Ok;
        });

        return command;
    }

    private static string Run(string name, Tape tape, Func<long> walk)
    {
        var best = double.MaxValue;
        var allocated = long.MaxValue;
        var sum = 0L;
        var total = 0.0;

        for (var round = 0; round < LeastRounds || total < LeastSeconds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();

            sum = walk();

            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            best = Math.Min(best, seconds);
            total += seconds;
        }

        var header = tape.Header;

        return Text.Invariant($"{name}: {header.EventCount / best / 1e6:F1}M events/s, {header.BodyLength / best / 1e6:F0} MB/s, ") +
            Text.Invariant($"{best * 1000:F0} ms for {Text.Count(header.EventCount)} events, {Text.Count(allocated)} bytes allocated, check {sum}");
    }

    private static long WalkL1(Tape tape)
    {
        var cursor = tape.L1Cursor();
        var sum = 0L;

        while (cursor.MoveNext())
            sum += cursor.Kind == L1Kind.Quote ? cursor.BidPrice : cursor.TradeSize;

        return sum;
    }

    private static long WalkTrades(Tape tape)
    {
        var cursor = tape.TradeCursor();
        var sum = 0L;

        while (cursor.MoveNext())
            sum += cursor.Size;

        return sum;
    }

    private static long WalkS1(Tape tape)
    {
        var cursor = tape.S1Cursor();
        var sum = 0L;

        while (cursor.MoveNext())
            sum += cursor.Volume;

        return sum;
    }

    private static long WalkBars(Tape tape, BarSpec spec)
    {
        var bars = tape.Bars(spec);
        var sum = 0L;

        while (bars.MoveNext())
            sum += bars.Volume;

        return sum;
    }
}
