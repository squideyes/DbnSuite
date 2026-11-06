using System.Buffers.Binary;
using DbnClient.Archive;
using DbnClient.Dbn;
using DbnClient.Export;
using DbnClient.Time;

namespace DbnClient.Tape;

/// <summary>One check on one contract's tapes.</summary>
/// <param name="Detail">The first difference, or something a pass should still report.</param>
public sealed record ProofResult(string Symbol, string Check, bool Passed, string? Detail);

/// <summary>
/// Proves a trade date's tapes through paths independent of the builder: the archive's
/// own bars, and DbnClient's decimal book. It reads and reports, and writes nothing.
/// </summary>
public sealed class TapeProof(DbnArchive archive, Catalog catalog, string tapeRoot)
{
    private const string BarSchema = "ohlcv-1s";
    private const string MboSchema = "mbo";
    private const long NanosPerMicro = 1_000;
    private const long NanosPerSecond = 1_000_000_000;
    private const long MicrosPerSecond = 1_000_000;
    private const byte TradingAction = 7;
    private const decimal RawUnits = 1_000_000_000m;

    private static readonly string[] TimeSizes = ["5s", "7s", "1m", "90s", "5m", "13m", "15m", "1h", "4h", "1d"];
    private static readonly string[] ActivitySizes = ["500t", "1000v"];

    private readonly SessionCalendar calendar = new(catalog);

    public async Task<IReadOnlyList<ProofResult>> RunAsync(DateOnly tradeDate, CancellationToken cancel)
    {
        var results = new List<ProofResult>();
        var bars = await BarsAsync(tradeDate, cancel).ConfigureAwait(false);
        var ticks = new List<TickSubject>();

        if (catalog.Day(tradeDate) is null)
        {
            results.AddRange(NoSession(tradeDate, bars));

            return results;
        }

        var lead = await StatusLog.ClockLeadAsync(archive, catalog.Root, tradeDate, cancel).ConfigureAwait(false);

        foreach (var contract in catalog.Contracts)
        {
            // Names repeat, so only one contract of a name is the one this date's tapes hold.
            if (catalog.Contract(contract.Symbol, tradeDate)?.InstrumentId != contract.InstrumentId)
                continue;

            if (calendar.For(contract.Symbol, tradeDate) is not { } session)
                continue;

            var s1 = await LoadAsync(TapeKind.S1, contract, tradeDate, cancel).ConfigureAwait(false);
            var trade = await LoadAsync(TapeKind.Trade, contract, tradeDate, cancel).ConfigureAwait(false);
            var l1 = await LoadAsync(TapeKind.L1, contract, tradeDate, cancel).ConfigureAwait(false);

            if (s1 is not null)
            {
                results.Add(S1BarStreams(contract, tradeDate, s1));
                results.Add(S1AgainstSource(contract, session, s1, bars.GetValueOrDefault(contract.InstrumentId) ?? []));
                results.Add(S1Marks(contract, s1, session));
                results.Add(SessionBounds(contract, TapeKind.S1, s1, session, lead));
            }

            if (bars.TryGetValue(contract.InstrumentId, out var source))
                results.Add(SourceCovered(contract, tradeDate, session, s1, source, lead));

            if (trade is not null)
            {
                results.Add(TradeBarStreams(contract, tradeDate, trade));
                results.Add(TradeMarks(contract, trade, session));
                results.Add(SessionBounds(contract, TapeKind.Trade, trade, session, lead));
            }

            if (l1 is not null)
            {
                results.Add(L1AgainstTrade(contract, l1, trade));
                results.Add(Crossed(contract, l1));
                results.Add(L1Marks(contract, l1, session));
                results.Add(SessionBounds(contract, TapeKind.L1, l1, session, lead));

                ticks.Add(new TickSubject(contract, session, l1, trade, bars.GetValueOrDefault(contract.InstrumentId) ?? []));
            }
        }

        results.AddRange(await AgainstMboAsync(tradeDate, ticks, cancel).ConfigureAwait(false));

        return results;
    }

    private async Task<Tape?> LoadAsync(TapeKind kind, CatalogContract contract, DateOnly tradeDate, CancellationToken cancel)
    {
        var path = TapePaths.For(tapeRoot, catalog.Dataset, kind, catalog.Root, contract.Symbol, tradeDate);

        if (!File.Exists(path))
            return null;

        var loaded = await Tape.LoadAsync(path, cancel).ConfigureAwait(false);

        return loaded.IsSuccess ? loaded.Value : throw new InvalidDataException($"{path}: {loaded.Error}");
    }

    private async Task<Dictionary<uint, List<SourceBar>>> BarsAsync(DateOnly tradeDate, CancellationToken cancel)
    {
        var bars = new Dictionary<uint, List<SourceBar>>();

        foreach (var day in new[] { tradeDate.AddDays(-1), tradeDate })
        {
            var opened = await archive.OpenAsync(BarSchema, catalog.Root, day, cancel).ConfigureAwait(false);

            if (!opened.IsSuccess)
                continue;

            var reader = opened.Value;

            await using (reader.ConfigureAwait(false))
            {
                await foreach (var raw in reader.ReadRawAsync(cancel).ConfigureAwait(false))
                {
                    if (raw.Span[1] != OhlcvRecord.FirstType)
                        continue;

                    var id = DbnRecord.InstrumentOf(raw.Span);

                    if (!bars.TryGetValue(id, out var list))
                        bars[id] = list = [];

                    list.Add(SourceBar.Read(raw.Span));
                }
            }
        }

        return bars;
    }

    /// <summary>A date the catalog has no session for must have had no trading either.</summary>
    private IEnumerable<ProofResult> NoSession(DateOnly tradeDate, Dictionary<uint, List<SourceBar>> bars)
    {
        const string check = "Date has a session";

        foreach (var (id, list) in bars)
        {
            if (catalog.Contract(id) is not { } contract)
                continue;

            var traded = list.Count(b => Sessions.TradeDate((ulong)(b.Second * NanosPerSecond)) == tradeDate);

            if (traded > 0)
                yield return Fail(contract, check, $"{traded:N0} source bars, and the catalog has no session");
        }
    }

    /// <summary>
    /// Everything the source traded on the date must lie in its session, or the tape lost it.
    /// The only allowance is the receive clock's lead, which can put a session's first
    /// seconds before its open. Those bars are reported, because the tape does not hold them.
    /// </summary>
    private static ProofResult SourceCovered(CatalogContract contract, DateOnly tradeDate, SessionDay session, Tape? tape, List<SourceBar> bars, long lead)
    {
        const string check = "Source inside session";

        var slack = lead > 0 ? lead + NanosPerSecond : 0;
        var open = session.Open / NanosPerSecond;
        var close = session.Close / NanosPerSecond;
        var early = 0;
        var inside = 0;

        foreach (var bar in bars)
        {
            var nanos = bar.Second * NanosPerSecond;

            if (Sessions.TradeDate((ulong)(nanos + slack)) != tradeDate)
                continue;

            if (bar.Second > close)
                return Fail(contract, check, $"a source bar at {bar.Second} is after the close");

            if (bar.Second >= open)
                inside++;
            else if (nanos + slack >= session.Open)
                early++;
            else
                return Fail(contract, check, $"a source bar at {bar.Second} is before the open");
        }

        if (inside > 0 && tape is null)
            return Fail(contract, check, $"{inside:N0} source bars, and no S1 tape");

        return early == 0
            ? Pass(contract, check)
            : new ProofResult(contract.Symbol, check, true, $"{early:N0} bars precede the open by the receive clock's lead of {lead:N0} ns");
    }

    private static ProofResult S1AgainstSource(CatalogContract contract, SessionDay session, Tape tape, List<SourceBar> bars)
    {
        const string check = "S1 against source";

        var open = session.Open / NanosPerSecond;
        var close = session.Close / NanosPerSecond;
        var expected = bars.Where(b => b.Second >= open && b.Second <= close).ToList();
        var cursor = tape.S1Cursor();
        var index = 0;

        while (cursor.MoveNext())
        {
            if (index == expected.Count)
                return Fail(contract, check, $"the tape has more bars than the source's {expected.Count:N0}");

            var bar = expected[index++];
            var tick = contract.TickRaw;

            if (cursor.Time != bar.Second * MicrosPerSecond || cursor.Open * tick != bar.Open || cursor.High * tick != bar.High
                || cursor.Low * tick != bar.Low || cursor.Close * tick != bar.Close || cursor.Volume != bar.Volume)
            {
                return Fail(contract, check, $"bar {index - 1:N0} at {bar.Second} differs");
            }
        }

        return index == expected.Count
            ? Pass(contract, check)
            : Fail(contract, check, $"the tape has {index:N0} bars, the source {expected.Count:N0}");
    }

    /// <summary>Every size, in both windows, must equal what BarBuilder makes from the same bars.</summary>
    private ProofResult S1BarStreams(CatalogContract contract, DateOnly tradeDate, Tape tape)
    {
        const string check = "Bar streams S1";

        foreach (var (window, kind) in new[] { (Window.Eth, SessionKind.Eth), (Window.Rth, SessionKind.Rth) })
        {
            if (kind == SessionKind.Rth && !tape.Header.HasRth)
                continue;

            foreach (var text in TimeSizes)
            {
                var spec = BarSpec.Parse(text).Value;
                var built = new List<Bar>();
                var filter = SessionFilter.Create(new DateSpan(tradeDate, tradeDate), kind, catalog.Root).Value;
                var builder = new BarBuilder(spec, filter, built.Add);

                FeedBars(tape, contract, filter, builder);

                builder.Flush();

                var bars = tape.Bars(spec, window);
                var close = tape.Bounds(window).Close;
                var index = 0;

                while (bars.MoveNext())
                {
                    if (index == built.Count || !Same(bars.Bar, built[index++], contract, close))
                        return Fail(contract, check, $"{text} {kind}: bar {index} differs");
                }

                if (index != built.Count)
                    return Fail(contract, check, $"{text} {kind}: {index:N0} bars streamed, {built.Count:N0} built");
            }
        }

        return Pass(contract, check);
    }

    private ProofResult TradeBarStreams(CatalogContract contract, DateOnly tradeDate, Tape tape)
    {
        const string check = "Bar streams Trade";

        foreach (var text in ActivitySizes)
        {
            var spec = BarSpec.Parse(text).Value;
            var built = new List<Bar>();
            var filter = SessionFilter.Create(new DateSpan(tradeDate, tradeDate), SessionKind.Eth, catalog.Root).Value;
            var builder = new BarBuilder(spec, filter, built.Add);

            FeedTrades(tape, contract, tradeDate, builder);

            builder.Flush();

            var bars = tape.TradeBars(spec);
            var index = 0;

            while (bars.MoveNext())
            {
                if (index == built.Count || !Same(bars.Bar, built[index++], contract, long.MaxValue))
                    return Fail(contract, check, $"{text}: bar {index} differs");
            }

            if (index != built.Count)
                return Fail(contract, check, $"{text}: {index:N0} bars streamed, {built.Count:N0} built");
        }

        return Pass(contract, check);
    }

    private static void FeedBars(Tape tape, CatalogContract contract, SessionFilter filter, BarBuilder builder)
    {
        var cursor = tape.S1Cursor();
        var tick = contract.TickRaw / RawUnits;

        while (cursor.MoveNext())
        {
            var nanos = (ulong)(cursor.Time * NanosPerMicro);

            if (filter.Accept(nanos) is { } date)
                builder.Add(contract.Symbol, contract.Symbol, null, date, nanos, cursor.Open * tick, cursor.High * tick, cursor.Low * tick, cursor.Close * tick, cursor.Volume, 0);
        }
    }

    private static void FeedTrades(Tape tape, CatalogContract contract, DateOnly tradeDate, BarBuilder builder)
    {
        var cursor = tape.TradeCursor();
        var tick = contract.TickRaw / RawUnits;

        while (cursor.MoveNext())
            builder.AddTrade(contract.Symbol, contract.Symbol, null, tradeDate, (ulong)(cursor.Time * NanosPerMicro), cursor.Price * tick, cursor.Size);
    }

    /// <summary>
    /// BarBuilder knows only CME's usual hours. A contract's last day closes earlier, and a
    /// stream ends that day's last bar at the real close, so that is the one thing allowed to differ.
    /// </summary>
    private static bool Same(in TapeBar mine, Bar theirs, CatalogContract contract, long close)
    {
        var tick = contract.TickRaw / RawUnits;
        var start = (long)theirs.Start / NanosPerMicro;
        var end = (long)theirs.End / NanosPerMicro;

        return mine.Start == start && mine.End == (start < close ? Math.Min(end, close) : end)
            && mine.Open * tick == theirs.Open && mine.High * tick == theirs.High && mine.Low * tick == theirs.Low
            && mine.Close * tick == theirs.Close && mine.Volume == theirs.Volume;
    }

    private static ProofResult L1AgainstTrade(CatalogContract contract, Tape l1, Tape? trade)
    {
        const string check = "L1 against Trade";

        var cursor = l1.L1Cursor();

        if (trade is null)
        {
            while (cursor.MoveNext())
            {
                if (cursor.Kind == L1Kind.Trade)
                    return Fail(contract, check, "L1 has trades, but no Trade tape exists");
            }

            return Pass(contract, check);
        }

        var prints = trade.TradeCursor();

        while (cursor.MoveNext())
        {
            if (cursor.Kind != L1Kind.Trade)
                continue;

            if (!prints.MoveNext())
                return Fail(contract, check, "L1 has more trades than the Trade tape");

            if (prints.Time != cursor.Time || prints.Price != cursor.TradePrice || prints.Size != cursor.TradeSize || prints.Side != cursor.Side)
                return Fail(contract, check, $"the trade at {cursor.Time} differs");
        }

        return prints.MoveNext() ? Fail(contract, check, "the Trade tape has more trades than L1") : Pass(contract, check);
    }

    private static ProofResult Crossed(CatalogContract contract, Tape tape)
    {
        const string check = "Crossed book";

        var cursor = tape.L1Cursor();

        while (cursor.MoveNext())
        {
            if (cursor.Kind == L1Kind.Quote && cursor.State == TradingAction && cursor.BidSize > 0 && cursor.AskSize > 0 && cursor.BidPrice >= cursor.AskPrice)
                return Fail(contract, check, $"{cursor.BidPrice} bid, {cursor.AskPrice} offered at {cursor.Time}");
        }

        return Pass(contract, check);
    }

    private static ProofResult SessionBounds(CatalogContract contract, TapeKind kind, Tape tape, SessionDay session, long lead)
    {
        var check = $"Sessions {kind}";
        var low = kind == TapeKind.S1 ? session.Open / NanosPerSecond * MicrosPerSecond : session.Open / NanosPerMicro;
        var high = kind == TapeKind.S1 ? session.Close / NanosPerSecond * MicrosPerSecond : session.Close / NanosPerMicro;

        var (first, last, inBreak) = kind switch
        {
            TapeKind.S1 => WalkS1(tape, session, lead),
            TapeKind.Trade => WalkTrades(tape, session),
            _ => WalkL1(tape, session)
        };

        if (first < low || last > high)
            return Fail(contract, check, $"events run from {first} to {last}, outside {low} to {high}");

        return inBreak == 0 ? Pass(contract, check) : Fail(contract, check, $"{inBreak:N0} trades fall in a stated break");
    }

    private static (long First, long Last, long InBreak) WalkS1(Tape tape, SessionDay session, long lead)
    {
        var cursor = tape.S1Cursor();
        var first = long.MaxValue;
        var last = long.MinValue;
        var inBreak = 0L;

        while (cursor.MoveNext())
        {
            first = Math.Min(first, cursor.Time);
            last = cursor.Time;

            if (WholeSecondInBreak(session, cursor.Time, lead))
                inBreak++;
        }

        return (first, last, inBreak);
    }

    private static (long First, long Last, long InBreak) WalkTrades(Tape tape, SessionDay session)
    {
        var cursor = tape.TradeCursor();
        var first = long.MaxValue;
        var last = long.MinValue;
        var inBreak = 0L;

        while (cursor.MoveNext())
        {
            first = Math.Min(first, cursor.Time);
            last = cursor.Time;

            if (InBreak(session, cursor.Time))
                inBreak++;
        }

        return (first, last, inBreak);
    }

    private static (long First, long Last, long InBreak) WalkL1(Tape tape, SessionDay session)
    {
        var cursor = tape.L1Cursor();
        var first = long.MaxValue;
        var last = long.MinValue;
        var inBreak = 0L;

        while (cursor.MoveNext())
        {
            first = Math.Min(first, cursor.Time);
            last = cursor.Time;

            if (cursor.Kind == L1Kind.Trade && InBreak(session, cursor.Time))
                inBreak++;
        }

        return (first, last, inBreak);
    }

    /// <summary>
    /// A bar is cut on receive time, so the second a stop starts in, the one after it, and the
    /// second it ends in can all hold real trades. Only a second wholly past those cannot.
    /// </summary>
    private static bool WholeSecondInBreak(SessionDay session, long micros, long lead)
    {
        foreach (var stop in session.Breaks)
        {
            // A receive clock running ahead labels the reopening's first trades before the stop's end.
            if (micros - MicrosPerSecond > stop.Start / NanosPerMicro && micros + MicrosPerSecond + lead / NanosPerMicro <= stop.End / NanosPerMicro)
                return true;
        }

        return false;
    }

    private static bool InBreak(SessionDay session, long micros)
    {
        foreach (var stop in session.Breaks)
        {
            if (micros > stop.Start / NanosPerMicro && micros < stop.End / NanosPerMicro)
                return true;
        }

        return false;
    }

    private static ProofResult S1Marks(CatalogContract contract, Tape tape, SessionDay session)
    {
        const string check = "RTH marks S1";

        if (!tape.Header.HasRth)
            return Pass(contract, check);

        var (open, close) = RthBounds(session, NanosPerSecond, MicrosPerSecond);
        var header = tape.Header;
        var whole = tape.S1Cursor();
        var marked = new S1Cursor(tape.Body, header.RthStart, header.RthEndIndex, header.RthEndOffset);

        while (whole.MoveNext())
        {
            if (whole.Time < open || whole.Time >= close)
                continue;

            if (!marked.MoveNext() || marked.Time != whole.Time || marked.Open != whole.Open || marked.High != whole.High
                || marked.Low != whole.Low || marked.Close != whole.Close || marked.Volume != whole.Volume)
            {
                return Fail(contract, check, $"the bar at {whole.Time} differs");
            }
        }

        return marked.MoveNext() ? Fail(contract, check, "the mark runs past RTH") : Pass(contract, check);
    }

    private static ProofResult TradeMarks(CatalogContract contract, Tape tape, SessionDay session)
    {
        const string check = "RTH marks Trade";

        if (!tape.Header.HasRth)
            return Pass(contract, check);

        var (open, close) = RthBounds(session, NanosPerMicro, 1);
        var header = tape.Header;
        var whole = tape.TradeCursor();
        var marked = new TradeCursor(tape.Body, header.RthStart, header.RthEndIndex, header.RthEndOffset);

        while (whole.MoveNext())
        {
            if (whole.Time < open || whole.Time >= close)
                continue;

            if (!marked.MoveNext() || marked.Time != whole.Time || marked.Price != whole.Price || marked.Size != whole.Size || marked.Side != whole.Side)
                return Fail(contract, check, $"the trade at {whole.Time} differs");
        }

        return marked.MoveNext() ? Fail(contract, check, "the mark runs past RTH") : Pass(contract, check);
    }

    private static ProofResult L1Marks(CatalogContract contract, Tape tape, SessionDay session)
    {
        const string check = "RTH marks L1";

        if (!tape.Header.HasRth)
            return Pass(contract, check);

        var (open, close) = RthBounds(session, NanosPerMicro, 1);
        var header = tape.Header;
        var whole = tape.L1Cursor();
        var marked = new L1Cursor(tape.Body, header.RthStart, header.RthEndIndex, header.RthEndOffset);

        while (whole.MoveNext())
        {
            if (whole.Time < open || whole.Time >= close)
                continue;

            if (!marked.MoveNext() || marked.Kind != whole.Kind || marked.Time != whole.Time || marked.BidPrice != whole.BidPrice
                || marked.BidSize != whole.BidSize || marked.AskPrice != whole.AskPrice || marked.AskSize != whole.AskSize
                || marked.TradePrice != whole.TradePrice || marked.State != whole.State)
            {
                return Fail(contract, check, $"the event at {whole.Time} differs");
            }
        }

        return marked.MoveNext() ? Fail(contract, check, "the mark runs past RTH") : Pass(contract, check);
    }

    private static (long Open, long Close) RthBounds(SessionDay session, long unit, long toMicros) =>
        (session.RthOpen!.Value / unit * toMicros, session.RthClose!.Value / unit * toMicros);

    /// <summary>The checks that need the source's <c>mbo</c> records, for every contract in one pass.</summary>
    private async Task<IReadOnlyList<ProofResult>> AgainstMboAsync(DateOnly tradeDate, List<TickSubject> subjects, CancellationToken cancel)
    {
        var checks = subjects.ToDictionary(s => s.Contract.InstrumentId, s => new MboCheck(s));
        var filesHolding = new Dictionary<uint, int>();

        foreach (var day in new[] { tradeDate.AddDays(-1), tradeDate })
        {
            var opened = await archive.OpenAsync(MboSchema, catalog.Root, day, cancel).ConfigureAwait(false);

            // Before the archive's first mbo day, a date has S1 tapes only.
            if (!opened.IsSuccess)
            {
                if (opened.Error!.Code is Errors.NotStored or Errors.NoData)
                    return [];

                throw new ArchiveException(opened.Error);
            }

            var reader = opened.Value;
            var seen = new HashSet<uint>();

            await using (reader.ConfigureAwait(false))
            {
                await foreach (var raw in reader.ReadRawAsync(cancel).ConfigureAwait(false))
                {
                    if (raw.Span[1] != MboRecord.Type)
                        continue;

                    var id = DbnRecord.InstrumentOf(raw.Span);

                    seen.Add(id);

                    if (checks.TryGetValue(id, out var check))
                        check.Feed((MboRecord)DbnRecord.Decode(raw.Span));
                }
            }

            foreach (var id in seen)
                filesHolding[id] = filesHolding.GetValueOrDefault(id) + 1;
        }

        return [.. checks.Values.SelectMany(c => c.Results()), .. MissingTapes(tradeDate, filesHolding, checks)];
    }

    /// <summary>A contract mbo holds for the whole date must have its tape, or a refusal would go unnoticed.</summary>
    private IEnumerable<ProofResult> MissingTapes(DateOnly tradeDate, Dictionary<uint, int> filesHolding, Dictionary<uint, MboCheck> checks)
    {
        const string check = "Tape exists";

        foreach (var (id, files) in filesHolding)
        {
            if (catalog.Contract(id) is not { } contract || calendar.For(contract.Symbol, tradeDate) is null)
                continue;

            if (files == 2)
            {
                yield return checks.ContainsKey(id)
                    ? Pass(contract, check)
                    : Fail(contract, check, "mbo holds this contract all day, and it has no L1 tape");
            }
        }
    }

    private static ProofResult Pass(CatalogContract contract, string check, string? detail = null) => new(contract.Symbol, check, true, detail);

    private static ProofResult Fail(CatalogContract contract, string check, string detail) => new(contract.Symbol, check, false, detail);

    private readonly record struct SourceBar(long Second, long Open, long High, long Low, long Close, ulong Volume)
    {
        public static SourceBar Read(ReadOnlySpan<byte> raw) => new(
            (long)(BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]) / NanosPerSecond),
            BinaryPrimitives.ReadInt64LittleEndian(raw[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(raw[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(raw[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(raw[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(raw[48..]));
    }

    private sealed record TickSubject(CatalogContract Contract, SessionDay Session, Tape L1, Tape? Trade, List<SourceBar> Bars);

    /// <summary>One event of the expected L1 stream, as DbnClient's own transform gives it.</summary>
    private readonly record struct Expected(bool IsTrade, long Time, int Price, uint Size, TradeSide Side, int Bid, uint BidSize, int Ask, uint AskSize);

    /// <summary>
    /// Replays a contract's source records through MboTransform and DbnClient's OrderBook,
    /// and walks the L1 tape beside them, event for event.
    /// </summary>
    private sealed class MboCheck
    {
        private const string TradesCheck = "Trade against bars";
        private const string TransformCheck = "L1 against DbnClient";
        private const string BoundaryCheck = "File boundary";
        private const int BidPriceCell = 9;
        private const int BidSizeCell = 10;
        private const int AskPriceCell = 12;
        private const int AskSizeCell = 13;

        private readonly TickSubject subject;
        private readonly MboTransform transform = MboTransform.Create("mbp-1", series: false).Value;
        private readonly OrderBook book = new();
        private readonly List<Expected> expected = [];
        private readonly Dictionary<long, (long O, long H, long L, long C, ulong V)> built = [];
        private readonly long tick;
        private readonly SnapshotWatch boundary = new();
        private long tradeVolume;

        public MboCheck(TickSubject subject)
        {
            this.subject = subject;

            tick = subject.Contract.TickRaw;
        }

        public void Feed(MboRecord record)
        {
            var session = subject.Session;
            var snapshot = (record.Flags & MboRecord.SnapshotFlag) != 0 || record.Action == 'R';
            var inside = !snapshot && (long)record.TsEvent >= session.Open && (long)record.TsEvent < session.Close;

            boundary.See(record.Action == 'R', snapshot, (long)record.TsEvent >= session.Open, Top());

            if (inside && record.Action == 'T' && record.Price is { } price)
                Bucket(record, (long)(price * RawUnits));

            transform.Process(record, inside ? session.TradeDate : null, subject.Contract.Symbol, null, p => p, Row);

            book.Apply(record);
        }

        public IEnumerable<ProofResult> Results()
        {
            yield return TradesAgainstBars();
            yield return TapeAgainstTransform();

            yield return boundary.Changes == 0
                ? Pass(subject.Contract, BoundaryCheck)
                : Pass(subject.Contract, BoundaryCheck, $"the top changed across a snapshot {boundary.Changes} time(s)");
        }

        private void Row(DateOnly date, Cell[] cells)
        {
            var isTrade = cells[8].Text == "T";
            var time = cells[1].Number / NanosPerMicro;
            var side = cells[4].Text switch { "B" => TradeSide.Buyer, "A" => TradeSide.Seller, _ => TradeSide.None };

            expected.Add(new Expected(isTrade, time, Ticks(cells[5].Price), (uint)cells[6].Number, side,
                Ticks(cells[BidPriceCell].Price), (uint)cells[BidSizeCell].Number, Ticks(cells[AskPriceCell].Price), (uint)cells[AskSizeCell].Number));
        }

        private int Ticks(decimal? price) => price is { } p ? (int)(p * RawUnits / tick) : 0;

        private (decimal?, long?, decimal?, long?) Top()
        {
            var bid = book.Best('B');
            var ask = book.Best('A');

            return (bid?.Price, bid?.Size, ask?.Price, ask?.Size);
        }

        private void Bucket(MboRecord record, long price)
        {
            var second = (long)(record.TsRecv / NanosPerSecond);
            var size = record.Quantity;

            tradeVolume += size;

            built[second] = built.TryGetValue(second, out var bar)
                ? (bar.O, Math.Max(bar.H, price), Math.Min(bar.L, price), price, bar.V + size)
                : (price, price, price, price, size);
        }

        private ProofResult TradesAgainstBars()
        {
            var open = subject.Session.Open / NanosPerSecond;
            var close = subject.Session.Close / NanosPerSecond;
            var bars = subject.Bars.Where(b => b.Second >= open && b.Second <= close).ToList();
            var barVolume = 0UL;

            foreach (var bar in bars)
            {
                barVolume += bar.Volume;

                if (!built.TryGetValue(bar.Second, out var mine))
                    return Fail(subject.Contract, TradesCheck, $"the source has a bar at {bar.Second} with no trades");

                if (mine != (bar.Open, bar.High, bar.Low, bar.Close, bar.Volume))
                    return Fail(subject.Contract, TradesCheck, $"the second {bar.Second} differs: trades {mine}, bar {bar}");
            }

            if (built.Count != bars.Count)
                return Fail(subject.Contract, TradesCheck, $"trades fill {built.Count:N0} seconds, bars {bars.Count:N0}");

            var tapeVolume = 0UL;

            if (subject.Trade is { } tape)
            {
                var cursor = tape.TradeCursor();

                while (cursor.MoveNext())
                    tapeVolume += cursor.Size;
            }

            return tapeVolume == barVolume
                ? Pass(subject.Contract, TradesCheck)
                : Fail(subject.Contract, TradesCheck, $"the tape's volume is {tapeVolume:N0}, the bars' {barVolume:N0}");
        }

        private ProofResult TapeAgainstTransform()
        {
            var cursor = subject.L1.L1Cursor();
            var bid = 0;
            var ask = 0;
            var bidSize = 0u;
            var askSize = 0u;
            var index = 0;
            var opening = true;

            while (cursor.MoveNext())
            {
                if (cursor.Kind == L1Kind.State)
                    continue;

                // The tape's opening quote is the book the transform only ever kept to itself.
                if (opening && cursor.Kind == L1Kind.Quote)
                {
                    opening = false;
                    (bid, bidSize, ask, askSize) = (cursor.BidPrice, cursor.BidSize, cursor.AskPrice, cursor.AskSize);

                    continue;
                }

                opening = false;

                while (index < expected.Count && Unwritten(expected[index], cursor.State, bid, bidSize, ask, askSize))
                    index++;

                if (index == expected.Count)
                    return Fail(subject.Contract, TransformCheck, $"the tape runs on past the transform at {cursor.Time}");

                var next = expected[index++];

                if (cursor.Kind == L1Kind.Trade)
                {
                    if (!next.IsTrade || next.Time != cursor.Time || next.Price != cursor.TradePrice || next.Size != cursor.TradeSize || next.Side != cursor.Side)
                        return Fail(subject.Contract, TransformCheck, $"the trade at {cursor.Time} differs: {next}");

                    continue;
                }

                (bid, bidSize, ask, askSize) = (cursor.BidPrice, cursor.BidSize, cursor.AskPrice, cursor.AskSize);

                if (next.IsTrade || next.Time != cursor.Time || !SameTop(next, bid, bidSize, ask, askSize))
                    return Fail(subject.Contract, TransformCheck, $"the quote at {cursor.Time} differs: {next}");
            }

            while (index < expected.Count && Unwritten(expected[index], TradingAction, bid, bidSize, ask, askSize))
                index++;

            return index == expected.Count
                ? Pass(subject.Contract, TransformCheck)
                : Fail(subject.Contract, TransformCheck, $"the transform has {expected.Count - index:N0} rows past the tape's end");
        }

        /// <summary>A row the tape rightly has no event for: only its order count moved, or the open's match is still clearing it.</summary>
        private static bool Unwritten(in Expected row, byte state, int bid, uint bidSize, int ask, uint askSize) =>
            !row.IsTrade && (SameTop(row, bid, bidSize, ask, askSize)
                || (state == TradingAction && row.BidSize > 0 && row.AskSize > 0 && row.Bid >= row.Ask));

        private static bool SameTop(in Expected row, int bid, uint bidSize, int ask, uint askSize) =>
            row.BidSize == bidSize && row.AskSize == askSize
            && (bidSize == 0 || row.Bid == bid)
            && (askSize == 0 || row.Ask == ask);
    }
}
