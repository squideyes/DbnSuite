using DbnSuite.Client.Dbn;

namespace DbnSuite.Client.Export;

/// <summary>
/// Builds, from <c>mbo</c>, the schemas Databento sells separately: trades,
/// tbbo, mbp-1, mbp-10, bbo-1s and bbo-1m. Every event feeds the book, but
/// rows are written only for the trade dates and hours asked for, and none
/// while a snapshot is being replayed.
/// </summary>
public sealed class MboTransform
{
    public static readonly IReadOnlyList<string> Names = ["trades", "tbbo", "mbp-1", "mbp-10", "bbo-1s", "bbo-1m"];

    private readonly string target;
    private readonly int depth;
    private readonly ulong interval;
    private readonly bool series;
    private readonly Dictionary<uint, State> states = [];

    private MboTransform(string target, bool series)
    {
        this.target = target;
        this.series = series;
        depth = target == "mbp-10" ? 10 : 1;
        interval = target switch
        {
            "bbo-1s" => 1_000_000_000UL,
            "bbo-1m" => 60_000_000_000UL,
            _ => 0
        };

        Columns = Build(target, depth, series);
    }

    public IReadOnlyList<Column> Columns { get; }

    public static Result<MboTransform> Create(string target, bool series) =>
        Names.Contains(target)
            ? new MboTransform(target, series)
            : new Error("input.bad_as", $"\"{target}\" is not something mbo becomes; use {string.Join(", ", Names)}.");

    /// <summary>
    /// Feeds one event. <paramref name="tradeDate"/> is null when the event is
    /// outside what was asked for: it still moves the book, but writes nothing.
    /// </summary>
    /// <param name="emit">Given each row: its trade date and cells, which it must copy if it keeps them.</param>
    public void Process(MboRecord m, DateOnly? tradeDate, string symbol, string? seriesName, Func<decimal?, decimal?> price,
        Action<DateOnly, Cell[]> emit)
    {
        if (!states.TryGetValue(m.InstrumentId, out var state))
            states[m.InstrumentId] = state = new State();

        var snapshot = (m.Flags & MboRecord.SnapshotFlag) != 0;

        state.Symbol = symbol;
        state.SeriesName = seriesName;
        state.Price = price;

        // Interval rows describe the book as it stood when each interval closed.
        // A snapshot's first record closes one; the rest of it are still replaying.
        if (interval > 0 && state.Date is { } open && !(snapshot && state.Replaying) && m.TsRecv / interval != state.Bucket)
        {
            if (state.Changed)
                emit(open, BboRow(state, m.InstrumentId, (state.Bucket + 1) * interval, symbol, seriesName, price));

            state.Changed = false;
        }

        state.Replaying = snapshot;

        if (m.Action == 'T' && !snapshot && tradeDate is { } traded)
        {
            state.LastPrice = m.Price;
            state.LastSize = m.Quantity;
            state.Changed = true;

            if (target is "trades" or "tbbo")
                emit(traded, TradeRow(state, m, symbol, seriesName, price));
        }

        // Levels are compared across a whole event, which can take several records.
        var before = target is "mbp-1" or "mbp-10" ? state.EventStart ?? Top(state.Book) : null;

        state.Book.Apply(m);

        if (interval > 0)
        {
            state.Changed |= m.Action != 'T';
            state.Bucket = m.TsRecv / interval;
            state.Date = tradeDate;
        }

        var last = (m.Flags & MboRecord.LastFlag) != 0;

        state.EventStart = last || snapshot ? null : before;

        if (before is null || snapshot || tradeDate is not { } date)
            return;

        // The book is consistent only after an event's last record, but a trade
        // moves nothing, and it opens an event rather than ending one.
        if (m.Action == 'T' || last && !before.SequenceEqual(Top(state.Book)))
            emit(date, BookRow(state, m, symbol, seriesName, price));
    }

    /// <summary>
    /// Writes the intervals still open, which no later event will close: those of
    /// trade dates before <paramref name="before"/>, or all of them when it is null.
    /// </summary>
    /// <param name="emit">Given each row, as for <see cref="Process"/>.</param>
    public void Flush(DateOnly? before, Action<DateOnly, Cell[]> emit)
    {
        if (interval == 0)
            return;

        foreach (var (instrument, state) in states)
        {
            if (state.Date is not { } open || !state.Changed || open >= before)
                continue;

            emit(open, BboRow(state, instrument, (state.Bucket + 1) * interval, state.Symbol, state.SeriesName, state.Price));

            state.Changed = false;
        }
    }

    private Level[] Top(OrderBook book) =>
        [.. book.Side('B', depth).Concat(Enumerable.Repeat(default(Level), depth)).Take(depth),
         .. book.Side('A', depth).Concat(Enumerable.Repeat(default(Level), depth)).Take(depth)];

    private Cell[] TradeRow(State state, MboRecord m, string symbol, string? seriesName, Func<decimal?, decimal?> price)
    {
        var cells = new Cell[Columns.Count];
        var i = Head(cells, m, symbol, price);

        if (target == "tbbo")
            i = Levels(cells, i, state.Book, 1, price);

        if (series)
            cells[i] = Cell.Of(seriesName);

        return cells;
    }

    private Cell[] BookRow(State state, MboRecord m, string symbol, string? seriesName, Func<decimal?, decimal?> price)
    {
        var cells = new Cell[Columns.Count];
        var i = Head(cells, m, symbol, price);

        cells[i++] = Cell.Of(m.Action.ToString());
        i = Levels(cells, i, state.Book, depth, price);

        if (series)
            cells[i] = Cell.Of(seriesName);

        return cells;
    }

    private Cell[] BboRow(State state, uint instrument, ulong at, string symbol, string? seriesName, Func<decimal?, decimal?> price)
    {
        var cells = new Cell[Columns.Count];
        var i = 0;

        cells[i++] = Cell.Of(at);
        cells[i++] = Cell.Of(symbol);
        cells[i++] = Cell.Of(instrument);
        cells[i++] = Cell.Of(price(state.LastPrice));
        cells[i++] = state.LastSize is { } size ? Cell.Of(size) : Cell.Empty;
        i = Levels(cells, i, state.Book, 1, price);

        if (series)
            cells[i] = Cell.Of(seriesName);

        return cells;
    }

    /// <summary>The columns every trade and book row opens with.</summary>
    private static int Head(Cell[] cells, MboRecord m, string symbol, Func<decimal?, decimal?> price)
    {
        cells[0] = Cell.Of(m.TsRecv);
        cells[1] = Cell.Of(m.TsEvent);
        cells[2] = Cell.Of(symbol);
        cells[3] = Cell.Of(m.InstrumentId);
        cells[4] = Cell.Of(m.Side.ToString());
        cells[5] = Cell.Of(price(m.Price));
        cells[6] = Cell.Of(m.Quantity);
        cells[7] = Cell.Of(m.Sequence);

        return 8;
    }

    private static int Levels(Cell[] cells, int i, OrderBook book, int depth, Func<decimal?, decimal?> price)
    {
        var bids = book.Side('B', depth).ToList();
        var asks = book.Side('A', depth).ToList();

        for (var level = 0; level < depth; level++)
        {
            i = Level(cells, i, level < bids.Count ? bids[level] : null, price);
            i = Level(cells, i, level < asks.Count ? asks[level] : null, price);
        }

        return i;
    }

    private static int Level(Cell[] cells, int i, Level? level, Func<decimal?, decimal?> price)
    {
        cells[i++] = level is { } l ? Cell.Of(price(l.Price)) : Cell.Empty;
        cells[i++] = level is { } s ? Cell.Of(s.Size) : Cell.Empty;
        cells[i++] = level is { } c ? Cell.Of(c.Count) : Cell.Empty;

        return i;
    }

    private static IReadOnlyList<Column> Build(string target, int depth, bool series)
    {
        var columns = new List<Column>();

        if (target.StartsWith("bbo", StringComparison.Ordinal))
        {
            columns.AddRange([new("ts_recv", CellKind.Time), new("symbol", CellKind.Text), new("instrument_id", CellKind.Integer),
                new("price", CellKind.Price), new("size", CellKind.Integer)]);
        }
        else
        {
            columns.AddRange([new("ts_recv", CellKind.Time), new("ts_event", CellKind.Time), new("symbol", CellKind.Text),
                new("instrument_id", CellKind.Integer), new("side", CellKind.Text), new("price", CellKind.Price),
                new("size", CellKind.Integer), new("sequence", CellKind.Integer)]);

            if (target.StartsWith("mbp", StringComparison.Ordinal))
                columns.Add(new("action", CellKind.Text));
        }

        var levels = target == "trades" ? 0 : depth;

        for (var level = 0; level < levels; level++)
        {
            foreach (var side in new[] { "bid", "ask" })
            {
                columns.Add(new($"{side}_px_{level:D2}", CellKind.Price));
                columns.Add(new($"{side}_sz_{level:D2}", CellKind.Integer));
                columns.Add(new($"{side}_ct_{level:D2}", CellKind.Integer));
            }
        }

        if (series)
            columns.Add(new("series", CellKind.Text));

        return columns;
    }

    private sealed class State
    {
        public OrderBook Book { get; } = new();
        public decimal? LastPrice { get; set; }
        public long? LastSize { get; set; }
        public ulong Bucket { get; set; }
        public DateOnly? Date { get; set; }
        public bool Changed { get; set; }
        public bool Replaying { get; set; }
        public Level[]? EventStart { get; set; }
        public string Symbol { get; set; } = "";
        public string? SeriesName { get; set; }
        public Func<decimal?, decimal?> Price { get; set; } = p => p;
    }
}
