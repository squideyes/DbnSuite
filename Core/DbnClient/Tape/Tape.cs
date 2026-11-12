using System.IO.Hashing;
using DbnClient.Export;
using DbnClient.Time;

namespace DbnClient.Tape;

/// <summary>A tape in memory: its header and its unpacked body, ready for a cursor.</summary>
public sealed class Tape
{
    private const long NanosPerMicro = 1_000;
    private const long MicrosPerSecond = 1_000_000;

    private readonly byte[] body;
    private readonly long bodyStart;

    private Tape(TapeHeader header, byte[] body, long bodyStart = 0)
    {
        Header = header;

        this.body = body;
        this.bodyStart = bodyStart;
    }

    public TapeHeader Header { get; }

    /// <summary>Whether only the RTH bytes were loaded, so only <see cref="Window.Rth"/> can be replayed.</summary>
    public bool IsRthOnly => bodyStart != 0;

    internal ReadOnlySpan<byte> Body => body;

    /// <summary>Loads a whole tape. Damage is a failed result, so a bad file never reaches a cursor.</summary>
    /// <param name="stream">The tape, positioned at its header.</param>
    /// <param name="cancel">Stops the load.</param>
    /// <param name="verifyHash">Check the body against the header's hash.</param>
    public static async Task<Result<Tape>> LoadAsync(Stream stream, CancellationToken cancel, bool verifyHash = true)
    {
        try
        {
            var parsed = await HeaderAsync(stream, cancel).ConfigureAwait(false);

            return parsed.IsSuccess
                ? await BodyAsync(stream, parsed.Value, verifyHash, cancel).ConfigureAwait(false)
                : parsed.Error!;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return new Error(TapeErrors.Io, $"Could not read the tape: {problem.Message}");
        }
    }

    /// <summary>
    /// Loads what a window needs. On a seekable, uncompressed tape <see cref="Window.Rth"/> reads
    /// only the RTH bytes, which the body's hash cannot cover. Anything else loads the whole tape.
    /// </summary>
    public static async Task<Result<Tape>> LoadAsync(Stream stream, Window window, CancellationToken cancel)
    {
        try
        {
            var parsed = await HeaderAsync(stream, cancel).ConfigureAwait(false);

            if (!parsed.IsSuccess)
                return parsed.Error!;

            var header = parsed.Value;
            var partial = window.Kind == WindowKind.Rth && stream.CanSeek && !header.IsCompressed && header.HasRth && header.RthStart.BodyOffset > 0;

            return partial
                ? await RthAsync(stream, header, cancel).ConfigureAwait(false)
                : await BodyAsync(stream, header, true, cancel).ConfigureAwait(false);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return new Error(TapeErrors.Io, $"Could not read the tape: {problem.Message}");
        }
    }

    /// <summary>Loads a tape from a file.</summary>
    public static async Task<Result<Tape>> LoadAsync(string path, CancellationToken cancel, bool verifyHash = true)
    {
        try
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

            await using (file.ConfigureAwait(false))
                return await LoadAsync(file, cancel, verifyHash).ConfigureAwait(false);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return new Error(TapeErrors.Io, $"Could not read {path}: {problem.Message}");
        }
    }

    /// <summary>Walks an S1 tape.</summary>
    public S1Cursor S1Cursor(Window window = default)
    {
        var span = Span(TapeKind.S1, window);

        return new S1Cursor(body, span);
    }

    /// <summary>Walks a Trade tape.</summary>
    public TradeCursor TradeCursor(Window window = default)
    {
        var span = Span(TapeKind.Trade, window);

        return new TradeCursor(body, span);
    }

    /// <summary>Walks an L1 tape.</summary>
    public L1Cursor L1Cursor(Window window = default)
    {
        var span = Span(TapeKind.L1, window);

        return new L1Cursor(body, span);
    }

    /// <summary>Streams time bars from an S1 tape.</summary>
    /// <param name="fillGaps">Repeat the last close at zero volume through empty periods.</param>
    public BarStream<S1Cursor, SpecRule> Bars(BarSpec spec, Window window = default, bool fillGaps = false)
    {
        if (spec.FromTrades)
            throw new ArgumentException($"{spec.Text} bars need a Trade tape: use TradeBars.", nameof(spec));

        return new BarStream<S1Cursor, SpecRule>(S1Cursor(window), Roller(spec, window, fillGaps), flushAtEnd: true);
    }

    /// <summary>Streams bars of your own rule from an S1 tape.</summary>
    public BarStream<S1Cursor, TRule> Bars<TRule>(TRule rule, Window window = default)
        where TRule : struct, IBarRule =>
        new(S1Cursor(window), new BarRoller<TRule>(rule), flushAtEnd: true);

    /// <summary>Streams bars from a Trade tape, which is what tick and volume bars need.</summary>
    public BarStream<TradeBarSource, SpecRule> TradeBars(BarSpec spec, Window window = default) =>
        new(new TradeBarSource(TradeCursor(window)), Roller(spec, window, fillGaps: false), flushAtEnd: true);

    /// <summary>Streams bars of your own rule from a Trade tape.</summary>
    public BarStream<TradeBarSource, TRule> TradeBars<TRule>(TRule rule, Window window = default)
        where TRule : struct, IBarRule =>
        new(new TradeBarSource(TradeCursor(window)), new BarRoller<TRule>(rule), flushAtEnd: true);

    /// <summary>Feeds several sizes from one pass over an S1 tape.</summary>
    public BarFan BarFan(IReadOnlyList<BarSpec> specs, Window window = default)
    {
        if (specs.FirstOrDefault(s => s.FromTrades) is { } spec)
            throw new ArgumentException($"{spec.Text} bars need a Trade tape.", nameof(specs));

        return new BarFan(S1Cursor(window), [.. specs.Select(s => Roller(s, window, fillGaps: false))]);
    }

    /// <summary>Where a window opens and closes on this tape, in microseconds since the epoch.</summary>
    internal (long Open, long Close) Bounds(Window window)
    {
        switch (window.Kind)
        {
            case WindowKind.Rth:
                var (open, close) = SessionRules.Rth(Header.Root, Header.TradeDate, Header.OpenTime, Header.CloseTime);

                return ((open ?? 0) / NanosPerMicro, (close ?? 0) / NanosPerMicro);

            case WindowKind.Hours:
                // A trade date starts at 17:00 the evening before, so hours from then on, and hours that run
                // past midnight, open on the day before the trade date's name.
                var sameDay = window.From < window.To;
                var firstDay = sameDay && window.From < Sessions.Open ? Header.TradeDate : Header.TradeDate.AddDays(-1);

                return (Micros(firstDay, window.From), Micros(sameDay ? firstDay : firstDay.AddDays(1), window.To));

            default:
                // A delayed open keeps the day's usual grid, so bars line up with every other day's.
                var usual = (Sessions.SessionStart(Header.TradeDate) - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond;

                return (Math.Min(usual, Header.OpenTime / NanosPerMicro), Header.CloseTime / NanosPerMicro);
        }
    }

    private BarRoller<SpecRule> Roller(BarSpec spec, Window window, bool fillGaps)
    {
        var (open, close) = Bounds(window);
        var rule = new SpecRule(spec);

        rule.Begin(open, close, Header.TradeDate);

        return new BarRoller<SpecRule>(rule, fillGaps);
    }

    private ReplaySpan Span(TapeKind kind, Window window)
    {
        if (Header.Kind != kind)
            throw new InvalidOperationException($"This is a {Header.Kind} tape, not {kind}.");

        if (IsRthOnly && window.Kind != WindowKind.Rth)
            throw new InvalidOperationException("Only the RTH bytes of this tape were loaded.");

        var opening = RthMark.Opening(kind, Header.OpenTime, Header.BasePrice);
        var rthAfter = Header.HasRth ? Header.RthStart.EventIndex : 0;
        var rthThrough = Header.HasRth ? Header.RthEndIndex : 0;

        switch (window.Kind)
        {
            case WindowKind.Rth when !Header.HasRth:
                return ReplaySpan.Whole(opening, 0, 0);

            case WindowKind.Rth:
                var start = Header.RthStart with { BodyOffset = Header.RthStart.BodyOffset - bodyStart };

                return new ReplaySpan(start, Header.RthEndIndex, Header.RthEndOffset - bodyStart, long.MinValue, long.MaxValue, rthAfter, rthThrough);

            case WindowKind.Hours:
                var (from, until) = Bounds(window);
                var unit = kind == TapeKind.S1 ? MicrosPerSecond : 1;

                // Everything before the mark is older than its time, so hours after it need not decode that far.
                var skip = Header.HasRth && from > Header.RthStart.Time * unit;

                return new ReplaySpan(skip ? Header.RthStart : opening, Header.EventCount, Header.BodyLength, from, until, rthAfter, rthThrough);

            default:
                return new ReplaySpan(opening, Header.EventCount, Header.BodyLength, long.MinValue, long.MaxValue, rthAfter, rthThrough);
        }
    }

    private static long Micros(DateOnly date, TimeOnly time)
    {
        var chicago = Zones.Find(Zones.Default).Value;
        var local = date.ToDateTime(time);
        var utc = new DateTimeOffset(local, chicago.GetUtcOffset(local));

        return (utc - DateTimeOffset.UnixEpoch).Ticks / 10;
    }

    private static async Task<Result<TapeHeader>> HeaderAsync(Stream stream, CancellationToken cancel)
    {
        var front = new byte[TapeHeader.Size];
        var read = await stream.ReadAtLeastAsync(front, front.Length, throwOnEndOfStream: false, cancel).ConfigureAwait(false);

        return TapeHeader.Parse(front.AsSpan(0, read));
    }

    private static async Task<Result<Tape>> BodyAsync(Stream stream, TapeHeader header, bool verifyHash, CancellationToken cancel)
    {
        if (stream.CanSeek && stream.Length - stream.Position != header.StoredLength)
            return Corrupt("The file's length does not match its header.");

        var stored = GC.AllocateUninitializedArray<byte>((int)header.StoredLength);
        var read = await stream.ReadAtLeastAsync(stored, stored.Length, throwOnEndOfStream: false, cancel).ConfigureAwait(false);

        if (read != stored.Length)
            return Corrupt("The body is truncated.");

        if (await stream.ReadAsync(new byte[1], cancel).ConfigureAwait(false) != 0)
            return Corrupt("Bytes follow the end of the body.");

        if (verifyHash && XxHash3.HashToUInt64(stored) != header.BodyHash)
            return Corrupt("The body's hash does not match its header.");

        return header.IsCompressed
            ? Unpack(header, stored)
            : new Tape(header, stored);
    }

    private static async Task<Result<Tape>> RthAsync(Stream stream, TapeHeader header, CancellationToken cancel)
    {
        if (stream.Length - stream.Position != header.StoredLength)
            return Corrupt("The file's length does not match its header.");

        var from = header.RthStart.BodyOffset;

        stream.Seek(from, SeekOrigin.Current);

        var bytes = GC.AllocateUninitializedArray<byte>((int)(header.RthEndOffset - from));
        var read = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancel).ConfigureAwait(false);

        return read == bytes.Length ? new Tape(header, bytes, from) : Corrupt("The body is truncated.");
    }

    private static Result<Tape> Unpack(TapeHeader header, byte[] stored)
    {
        var body = GC.AllocateUninitializedArray<byte>((int)header.BodyLength);

        try
        {
            if (!Zstd.TryDecompress(stored, body, out var written) || written != body.Length)
                return Corrupt("The body unpacks to the wrong length.");
        }
        catch (InvalidDataException problem)
        {
            return Corrupt($"The body cannot be unpacked: {problem.Message}");
        }

        return new Tape(header, body);
    }

    private static Error Corrupt(string message) => new(TapeErrors.Corrupt, message);
}
