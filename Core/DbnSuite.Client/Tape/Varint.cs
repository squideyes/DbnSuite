using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace DbnSuite.Client.Tape;

/// <summary>LEB128 varints and zigzag, the two encodings every event is made of.</summary>
internal static class Varint
{
    /// <summary>The longest a 64-bit varint can be.</summary>
    public const int MaxBytes = 10;

    public static int Write(Span<byte> target, ulong value)
    {
        var count = 0;

        while (value >= 0x80)
        {
            target[count++] = (byte)(value | 0x80);
            value >>= 7;
        }

        target[count++] = (byte)value;

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read(ReadOnlySpan<byte> source, ref int position)
    {
        if ((uint)position < (uint)source.Length && source[position] < 0x80)
            return source[position++];

        return ReadLong(source, ref position);
    }

    public static ulong ToZigzag(long value) => (ulong)((value << 1) ^ (value >> 63));

    public static long FromZigzag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    private static ulong ReadLong(ReadOnlySpan<byte> source, ref int position)
    {
        ulong value = 0;

        for (var shift = 0; shift < 70; shift += 7)
        {
            if ((uint)position >= (uint)source.Length)
                Corrupt("The body ends inside a number.");

            var next = source[position++];

            if (shift == 63 && next > 1)
                Corrupt("A number is too large for 64 bits.");

            value |= (ulong)(next & 0x7F) << shift;

            if (next < 0x80)
                return value;
        }

        Corrupt("A number is longer than ten bytes.");

        return 0;
    }

    [DoesNotReturn]
    public static void Corrupt(string message) => throw new InvalidDataException(message);
}
