using System.Numerics;

namespace DbnClient.Tape;

/// <summary>
/// A running check over a tape's events. The writer keeps one as it encodes, and a
/// builder decodes the finished file to compare, so a writer bug can never ship a tape.
/// </summary>
public static class TapeChecksum
{
    private const ulong Multiplier = 0x9E3779B97F4A7C15;
    private const int Rotation = 29;
    private const long QuoteEvent = 0;
    private const long TradeEvent = 1;
    private const long StateEvent = 2;

    public static ulong Of(Tape tape) => tape.Header.Kind switch
    {
        TapeKind.S1 => OfS1(tape),
        TapeKind.Trade => OfTrades(tape),
        _ => OfL1(tape)
    };

    internal static ulong Mix(ulong sum, long value) =>
        BitOperations.RotateLeft((sum ^ (ulong)value) * Multiplier, Rotation);

    internal static ulong Bar(ulong sum, long time, int open, int high, int low, int close, uint volume) =>
        Mix(Mix(Mix(Mix(Mix(Mix(sum, time), open), high), low), close), volume);

    internal static ulong Print(ulong sum, long time, int price, uint size, TradeSide side) =>
        Mix(Mix(Mix(Mix(sum, time), price), size), (long)side);

    internal static ulong Quote(ulong sum, long time, int bid, uint bidSize, int ask, uint askSize) =>
        Mix(Mix(Mix(Mix(Mix(Mix(sum, QuoteEvent), time), bid), bidSize), ask), askSize);

    internal static ulong Trade(ulong sum, long time, int price, uint size, TradeSide side) =>
        Print(Mix(sum, TradeEvent), time, price, size, side);

    internal static ulong State(ulong sum, long time, byte action) =>
        Mix(Mix(Mix(sum, StateEvent), time), action);

    private static ulong OfS1(Tape tape)
    {
        var sum = 0UL;
        var cursor = tape.S1Cursor();

        while (cursor.MoveNext())
            sum = Bar(sum, cursor.Time, cursor.Open, cursor.High, cursor.Low, cursor.Close, cursor.Volume);

        return sum;
    }

    private static ulong OfTrades(Tape tape)
    {
        var sum = 0UL;
        var cursor = tape.TradeCursor();

        while (cursor.MoveNext())
            sum = Print(sum, cursor.Time, cursor.Price, cursor.Size, cursor.Side);

        return sum;
    }

    private static ulong OfL1(Tape tape)
    {
        var sum = 0UL;
        var cursor = tape.L1Cursor();

        while (cursor.MoveNext())
        {
            sum = cursor.Kind switch
            {
                L1Kind.Quote => Quote(sum, cursor.Time, cursor.BidPrice, cursor.BidSize, cursor.AskPrice, cursor.AskSize),
                L1Kind.Trade => Trade(sum, cursor.Time, cursor.TradePrice, cursor.TradeSize, cursor.Side),
                _ => State(sum, cursor.Time, cursor.State)
            };
        }

        return sum;
    }
}
