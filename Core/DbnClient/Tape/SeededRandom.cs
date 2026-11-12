namespace DbnClient.Tape;

/// <summary>
/// xoshiro256**, seeded by SplitMix64. It is written out here, rather than taken from
/// <see cref="Random"/>, because .NET doesn't promise a seed gives the same numbers in every
/// version: this gives the same ones on any machine and runtime, for good.
/// </summary>
internal struct SeededRandom
{
    private const ulong Golden = 0x9E3779B97F4A7C15;

    private ulong s0;
    private ulong s1;
    private ulong s2;
    private ulong s3;

    public SeededRandom(ulong seed)
    {
        var state = seed;

        s0 = SplitMix(ref state);
        s1 = SplitMix(ref state);
        s2 = SplitMix(ref state);
        s3 = SplitMix(ref state);
    }

    /// <summary>The generator for one run of a seed: each run is its own stream, and needs no other.</summary>
    public static SeededRandom ForRun(ulong seed, int run) => new(seed ^ Mix((ulong)run));

    public ulong Next()
    {
        var result = ulong.RotateLeft(s1 * 5, 7) * 9;
        var t = s1 << 17;

        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = ulong.RotateLeft(s3, 45);

        return result;
    }

    /// <summary>A number from 0 to <paramref name="count"/> - 1, each equally likely.</summary>
    public int NextBelow(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var n = (ulong)count;
        var threshold = (0UL - n) % n;

        while (true)
        {
            var x = Next();

            if (x >= threshold)
                return (int)(x % n);
        }
    }

    /// <summary>One step of SplitMix64.</summary>
    internal static ulong SplitMix(ref ulong state)
    {
        state += Golden;

        return Mix(state);
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;

        return z ^ (z >> 31);
    }
}
