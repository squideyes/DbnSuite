using System.Runtime.CompilerServices;

namespace DbnSuite.Client.Tape;

/// <summary>
/// The field readers the cursors share. A value that cannot be real means a damaged
/// body, and throws rather than handing a strategy a wrapped number.
/// </summary>
internal static class TapeReading
{
    private const int ReservedSide = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Control(ReadOnlySpan<byte> body, ref int position)
    {
        if ((uint)position >= (uint)body.Length)
            Varint.Corrupt("The body ends before its last event.");

        return body[position++];
    }

    public static bool AtEnd(int position, int endOffset)
    {
        if (position != endOffset)
            Varint.Corrupt("The events do not end where the body says they do.");

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Gap(ReadOnlySpan<byte> body, ref int position, int bias)
    {
        var value = Varint.Read(body, ref position);

        if (value > long.MaxValue - (ulong)bias)
            Varint.Corrupt("A time gap is too large.");

        return (long)value + bias;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long AddTime(long time, long gap)
    {
        if (gap > long.MaxValue - time)
            Varint.Corrupt("A time gap runs past the end of time.");

        return time + gap;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AddPrice(int price, ReadOnlySpan<byte> body, ref int position)
    {
        var change = Varint.FromZigzag(Varint.Read(body, ref position));

        return ToPrice(unchecked(price + change));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Above(int price, ReadOnlySpan<byte> body, ref int position)
    {
        var distance = Varint.Read(body, ref position);

        if (distance > uint.MaxValue)
            Varint.Corrupt("A price is out of range.");

        return ToPrice(price + (long)distance + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Below(int price, ReadOnlySpan<byte> body, ref int position)
    {
        var distance = Varint.Read(body, ref position);

        if (distance > uint.MaxValue)
            Varint.Corrupt("A price is out of range.");

        return ToPrice(price - (long)distance - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Size(ReadOnlySpan<byte> body, ref int position)
    {
        var size = Varint.Read(body, ref position);

        if (size > uint.MaxValue)
            Varint.Corrupt("A size is out of range.");

        return (uint)size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AddSize(uint size, ReadOnlySpan<byte> body, ref int position)
    {
        var change = Varint.FromZigzag(Varint.Read(body, ref position));
        var value = unchecked(size + change);

        if (value is < 0 or > uint.MaxValue)
            Varint.Corrupt("A size is out of range.");

        return (uint)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TradeSide Side(int bits)
    {
        if (bits == ReservedSide)
            Varint.Corrupt("A trade uses the reserved side.");

        return (TradeSide)bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToPrice(long value)
    {
        if (value is < int.MinValue or > int.MaxValue)
            Varint.Corrupt("A price is out of range.");

        return (int)value;
    }
}
