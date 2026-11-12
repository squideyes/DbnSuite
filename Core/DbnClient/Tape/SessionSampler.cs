using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;

namespace DbnClient.Tape;

/// <summary>How a Monte Carlo run deals its trading sessions.</summary>
public enum Sampling
{
    /// <summary>Every session exactly once, in a random order: the days are fixed, the path is not.</summary>
    Permutation,

    /// <summary>Sessions drawn at random with replacement: a day can come twice, or not at all.</summary>
    Bootstrap
}

/// <summary>One session of a run, in the order it is replayed.</summary>
/// <param name="IsWarmup">The session only warms indicators up; nothing trades in it.</param>
public readonly record struct SessionDraw(DateOnly TradeDate, bool IsWarmup);

/// <summary>
/// Deals the sessions of Monte Carlo runs. A run is its warm-up sessions, drawn at random with
/// replacement, then its trading sessions, dealt as <see cref="Sampling"/> says. The same days,
/// seed and run always give the same sessions in the same order, on any machine.
/// </summary>
public sealed class SessionSampler
{
    private readonly DateOnly[] days;

    /// <param name="days">The trade dates to deal from; order and repeats don't matter.</param>
    /// <param name="seed">Decides every run.</param>
    /// <param name="sampling">How trading sessions are dealt.</param>
    /// <param name="warmup">Warm-up sessions before each run's trading sessions.</param>
    /// <param name="sessions">Trading sessions a bootstrap run draws; all the days' count when null. A permutation always uses every day.</param>
    public SessionSampler(IEnumerable<DateOnly> days, ulong seed, Sampling sampling = Sampling.Permutation, int warmup = 0, int? sessions = null)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);

        this.days = [.. days.Distinct().Order()];

        if (this.days.Length == 0)
            throw new ArgumentException("There are no days to deal from.", nameof(days));

        if (sessions is not null && sampling == Sampling.Permutation)
            throw new ArgumentException("A permutation uses every day once, so it takes no count of sessions.", nameof(sessions));

        if (sessions is <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessions), sessions, "A bootstrap run needs at least one session.");

        Seed = seed;
        Sampling = sampling;
        Warmup = warmup;
        Sessions = sessions ?? this.days.Length;
        Fingerprint = FingerprintOf(this.days);
    }

    /// <summary>The days dealt from, oldest first.</summary>
    public IReadOnlyList<DateOnly> Days => days;

    public ulong Seed { get; }

    public Sampling Sampling { get; }

    public int Warmup { get; }

    /// <summary>Trading sessions in each run.</summary>
    public int Sessions { get; }

    /// <summary>
    /// A hash of <see cref="Days"/>. Keep it with a seed: the same seed deals differently once
    /// the days change, such as when new tapes arrive.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>Run <paramref name="run"/>'s sessions: <see cref="Warmup"/> warm-up ones, then <see cref="Sessions"/> trading ones.</summary>
    public IReadOnlyList<SessionDraw> Run(int run)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(run);

        var random = SeededRandom.ForRun(Seed, run);
        var draws = new SessionDraw[Warmup + Sessions];

        for (var i = 0; i < Warmup; i++)
            draws[i] = new SessionDraw(days[random.NextBelow(days.Length)], IsWarmup: true);

        if (Sampling == Sampling.Bootstrap)
        {
            for (var i = Warmup; i < draws.Length; i++)
                draws[i] = new SessionDraw(days[random.NextBelow(days.Length)], IsWarmup: false);

            return draws;
        }

        var deck = (DateOnly[])days.Clone();

        for (var i = deck.Length - 1; i > 0; i--)
        {
            var j = random.NextBelow(i + 1);

            (deck[i], deck[j]) = (deck[j], deck[i]);
        }

        for (var i = 0; i < deck.Length; i++)
            draws[Warmup + i] = new SessionDraw(deck[i], IsWarmup: false);

        return draws;
    }

    private static string FingerprintOf(DateOnly[] days)
    {
        var bytes = new byte[days.Length * sizeof(int)];

        for (var i = 0; i < days.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(int)), days[i].DayNumber);

        return XxHash3.HashToUInt64(bytes).ToString("x16", CultureInfo.InvariantCulture);
    }
}
