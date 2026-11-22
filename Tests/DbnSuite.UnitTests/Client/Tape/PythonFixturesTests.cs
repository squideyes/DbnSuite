using System.Globalization;
using System.Text;
using DbnSuite.Client.Tape;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

/// <summary>
/// The contract between C# and the Python client in <c>Clients/python</c>. The fixtures there hold
/// synthetic tapes and what C# makes of them: every event a cursor gives, the sessions a sampler
/// deals for given seeds, and the offsets a session stream stitches with. The Python tests must get
/// the same; these tests check C# still does. Set <c>DBNSUITE_WRITE_FIXTURES=1</c> to write them afresh.
/// </summary>
public sealed class PythonFixturesTests
{
    private const string WriteVariable = "DBNSUITE_WRITE_FIXTURES";
    private const string Dataset = TapeFixture.Dataset;
    private const string Root = TapeFixture.Root;
    private const string Rewrite = $"Run with {WriteVariable}=1 to write the fixtures afresh, then check the Python tests still pass.";

    private static readonly DateOnly[] TapeDates = [new(2026, 10, 27), new(2026, 10, 28), new(2026, 10, 29)];
    private static readonly DateOnly CompressedDate = new(2026, 10, 28);
    private static readonly TapeKind[] Kinds = [TapeKind.S1, TapeKind.Trade, TapeKind.L1];

    private static bool Writing => Environment.GetEnvironmentVariable(WriteVariable) == "1";

    private static string Fixtures
    {
        get
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);

            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "DbnSuite.slnx")))
                folder = folder.Parent;

            folder.ShouldNotBeNull("The repository root, with DbnSuite.slnx, was not found.");

            return Path.Combine(folder.FullName, "Clients", "python", "tests", "fixtures");
        }
    }

    private static string Tapes => Path.Combine(Fixtures, "tapes");

    [Fact]
    public async Task Each_fixture_tape_decodes_as_its_expected_events_say()
    {
        if (Writing)
            await WriteTapesAsync();

        foreach (var kind in Kinds)
        {
            foreach (var date in TapeDates)
            {
                var path = TapePaths.For(Tapes, Dataset, kind, Root, date);
                var loaded = await Tape.LoadAsync(path, CancellationToken.None);
                loaded.IsSuccess.ShouldBeTrue(loaded.Error?.Message);

                var json = Decoded(loaded.Value);

                Check(Path.Combine(Fixtures, "expected", Path.GetFileNameWithoutExtension(path) + ".json"), json);
            }
        }
    }

    [Fact]
    public void The_sampler_deals_the_sessions_the_vectors_say()
    {
        var json = new StringBuilder("{\n\"random\": [\n");
        var generators = new (ulong Seed, int Run)[] { (0, 0), (42, 0), (42, 3), (ulong.MaxValue, 1) };

        for (var i = 0; i < generators.Length; i++)
        {
            var (seed, run) = generators[i];
            var random = SeededRandom.ForRun(seed, run);
            var next = Enumerable.Range(0, 6).Select(_ => Quote(random.Next().ToString(CultureInfo.InvariantCulture)));
            var below = Enumerable.Range(1, 12).Select(n => random.NextBelow(n).ToString(CultureInfo.InvariantCulture));

            json.Append(CultureInfo.InvariantCulture,
                $"  {{\"seed\": \"{seed}\", \"run\": {run}, \"next\": [{string.Join(", ", next)}], \"below_1_to_12\": [{string.Join(", ", below)}]}}");
            json.Append(i < generators.Length - 1 ? ",\n" : "\n");
        }

        json.Append("],\n\"cases\": [\n");

        var days = Enumerable.Range(0, 12).Select(i => new DateOnly(2026, 10, 1).AddDays(i * 2)).ToArray();
        var cases = new (ulong Seed, Sampling Sampling, int Warmup, int? Sessions)[]
        {
            (0, Sampling.Permutation, 0, null),
            (42, Sampling.Permutation, 3, null),
            (9_223_372_036_854_775_809, Sampling.Bootstrap, 2, 7),
            (ulong.MaxValue, Sampling.Bootstrap, 0, null)
        };

        for (var c = 0; c < cases.Length; c++)
        {
            var (seed, sampling, warmup, sessions) = cases[c];
            var sampler = new SessionSampler(days, seed, sampling, warmup, sessions);

            json.Append(CultureInfo.InvariantCulture,
                $"  {{\"days\": [{string.Join(", ", days.Select(d => Quote(Iso(d))))}], \"seed\": \"{seed}\", \"sampling\": \"{sampling.ToString().ToLowerInvariant()}\", ");
            json.Append(CultureInfo.InvariantCulture,
                $"\"warmup\": {warmup}, \"sessions\": {(sessions is { } n ? n.ToString(CultureInfo.InvariantCulture) : "null")}, \"fingerprint\": \"{sampler.Fingerprint}\", \"runs\": {{");

            var runs = new[] { 0, 1, 5 };

            for (var r = 0; r < runs.Length; r++)
            {
                var draws = sampler.Run(runs[r]).Select(d => $"[\"{Iso(d.TradeDate)}\", {(d.IsWarmup ? "true" : "false")}]");

                json.Append(CultureInfo.InvariantCulture, $"\"{runs[r]}\": [{string.Join(", ", draws)}]");
                json.Append(r < runs.Length - 1 ? ", " : "");
            }

            json.Append(c < cases.Length - 1 ? "}},\n" : "}}\n");
        }

        json.Append("]\n}\n");

        Check(Path.Combine(Fixtures, "sampler.json"), json.ToString());
    }

    [Fact]
    public async Task A_stream_stitches_the_fixture_tapes_with_the_offsets_the_vectors_say()
    {
        if (Writing)
            await WriteTapesAsync();

        var json = new StringBuilder("{\n");

        for (var k = 0; k < Kinds.Length; k++)
        {
            var stream = new SessionStream(Tapes, Dataset, Root, Kinds[k]);
            var sampler = new SessionSampler(stream.Days(), 7, Sampling.Permutation, warmup: 1);

            json.Append(CultureInfo.InvariantCulture, $"\"{TapePaths.Kinds[k]}\": {{\"seed\": \"7\", \"warmup\": 1, \"runs\": {{");

            for (var run = 0; run < 3; run++)
            {
                var sessions = new List<string>();

                await foreach (var session in stream.RunAsync(sampler, run, CancellationToken.None))
                {
                    session.IsSuccess.ShouldBeTrue(session.Error?.Message);

                    var s = session.Value;
                    sessions.Add(string.Create(CultureInfo.InvariantCulture,
                        $"[{s.Index}, \"{Iso(s.TradeDate)}\", {(s.IsWarmup ? "true" : "false")}, {s.Offset}]"));
                }

                json.Append(CultureInfo.InvariantCulture, $"\"{run}\": [{string.Join(", ", sessions)}]");
                json.Append(run < 2 ? ", " : "");
            }

            json.Append(k < Kinds.Length - 1 ? "}},\n" : "}}\n");
        }

        json.Append("}\n");

        Check(Path.Combine(Fixtures, "stream.json"), json.ToString());
    }

    private static void Check(string path, string json)
    {
        if (Writing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json, new UTF8Encoding(false));

            return;
        }

        File.Exists(path).ShouldBeTrue($"{path} is missing. {Rewrite}");
        File.ReadAllText(path).ReplaceLineEndings("\n").ShouldBe(json, $"{path} no longer matches what C# gives. {Rewrite}");
    }

    /// <summary>A tape's header and every event its cursor gives, one event to a line.</summary>
    private static string Decoded(Tape tape)
    {
        var h = tape.Header;
        var json = new StringBuilder();

        json.Append("{\n\"header\": {");
        json.Append(CultureInfo.InvariantCulture,
            $"\"kind\": {(byte)h.Kind}, \"compressed\": {Bool(h.IsCompressed)}, \"has_rth\": {Bool(h.HasRth)}, \"time_unit\": {h.TimeUnit}, ");
        json.Append(CultureInfo.InvariantCulture,
            $"\"trade_date\": \"{Iso(h.TradeDate)}\", \"tick_raw\": {h.TickRaw}, \"open_time\": {h.OpenTime}, \"close_time\": {h.CloseTime}, ");
        json.Append(CultureInfo.InvariantCulture,
            $"\"base_price\": {h.BasePrice}, \"instrument_id\": {h.InstrumentId}, \"event_count\": {h.EventCount}, \"body_length\": {h.BodyLength}, ");
        json.Append(CultureInfo.InvariantCulture,
            $"\"stored_length\": {h.StoredLength}, \"body_hash\": \"{h.BodyHash:x16}\", \"dataset\": \"{h.Dataset}\", \"symbol\": \"{h.Symbol}\", \"root\": \"{h.Root}\", ");
        json.Append(CultureInfo.InvariantCulture,
            $"\"rth_end_offset\": {h.RthEndOffset}, \"rth_end_index\": {h.RthEndIndex}, \"rth_start\": {{\"body_offset\": {h.RthStart.BodyOffset}, ");
        json.Append(CultureInfo.InvariantCulture,
            $"\"event_index\": {h.RthStart.EventIndex}, \"time\": {h.RthStart.Time}, \"bid_price\": {h.RthStart.BidPrice}, \"ask_price\": {h.RthStart.AskPrice}, ");
        json.Append(CultureInfo.InvariantCulture,
            $"\"bid_size\": {h.RthStart.BidSize}, \"ask_size\": {h.RthStart.AskSize}, \"last_price\": {h.RthStart.LastPrice}, \"market_state\": {h.RthStart.MarketState}}}}},\n");
        json.Append("\"events\": [\n");

        var lines = new List<string>();

        switch (h.Kind)
        {
            case TapeKind.S1:
                var bars = tape.S1Cursor();

                while (bars.MoveNext())
                    lines.Add(string.Create(CultureInfo.InvariantCulture,
                        $"[{bars.Time}, {bars.Open}, {bars.High}, {bars.Low}, {bars.Close}, {bars.Volume}, {Bool(bars.InRth)}]"));

                break;

            case TapeKind.Trade:
                var trades = tape.TradeCursor();

                while (trades.MoveNext())
                    lines.Add(string.Create(CultureInfo.InvariantCulture,
                        $"[{trades.Time}, {trades.Price}, {trades.Size}, {(byte)trades.Side}, {Bool(trades.InRth)}]"));

                break;

            default:
                var events = tape.L1Cursor();

                while (events.MoveNext())
                    lines.Add(string.Create(CultureInfo.InvariantCulture,
                        $"[{(byte)events.Kind}, {events.Time}, {events.BidPrice}, {events.AskPrice}, {events.BidSize}, {events.AskSize}, {events.TradePrice}, {events.TradeSize}, {(byte)events.Side}, {events.State}, {Bool(events.InRth)}]"));

                break;
        }

        json.Append(string.Join(",\n", lines));
        json.Append("\n]\n}\n");

        return json.ToString();
    }

    private static async Task WriteTapesAsync()
    {
        foreach (var date in TapeDates)
        {
            var days = TapeFixture.TradeDate.DayNumber - date.DayNumber;
            var info = TapeFixture.Info(compress: date == CompressedDate) with
            {
                TradeDate = date,
                OpenTime = (TapeFixture.OpenSeconds - days * 86_400L) * TapeFixture.NanosPerSecond,
                CloseTime = (TapeFixture.CloseSeconds - days * 86_400L) * TapeFixture.NanosPerSecond
            };
            var open = info.OpenTime / TapeFixture.NanosPerMicro;
            var random = new Lcg((ulong)date.DayNumber);

            await SaveAsync(S1(info, open, random), TapeKind.S1, date);
            await SaveAsync(Trades(info, open, random), TapeKind.Trade, date);
            await SaveAsync(L1(info, open, random), TapeKind.L1, date);
        }
    }

    private static async Task SaveAsync(TapeWriter writer, TapeKind kind, DateOnly date)
    {
        writer.Refusal.ShouldBeNull(writer.Refusal?.Message);

        var saved = await writer.SaveAsync(TapePaths.For(Tapes, Dataset, kind, Root, date), CancellationToken.None);

        saved.IsSuccess.ShouldBeTrue(saved.Error?.Message);
    }

    /// <summary>Bars that use every field: gaps, opens away from the close, highs, lows, and volumes inline and not.</summary>
    private static S1Writer S1(TapeInfo info, long open, Lcg random)
    {
        var writer = new S1Writer(info);
        var second = open;
        var close = info.BasePrice;

        for (var i = 0; i < 160; i++)
        {
            if (i == 40)
                writer.MarkRthStart();

            if (i == 120)
                writer.MarkRthEnd();

            second += TapeFixture.MicrosPerSecond * (1 + random.Below(4));

            var o = random.Below(3) == 0 ? close + random.Between(-3, 3) : close;
            var c = o + random.Between(-4, 4);
            var high = Math.Max(o, c) + random.Below(3);
            var low = Math.Min(o, c) - random.Below(3);
            var volume = (uint)(random.Below(4) == 0 ? 8 + random.Below(3_000) : 1 + random.Below(7));

            writer.Add(second, o, high, low, c, volume);
            close = c;
        }

        return writer;
    }

    /// <summary>Trades with every side, gaps and none, prices that move and don't, and sizes inline and not.</summary>
    private static TradeWriter Trades(TapeInfo info, long open, Lcg random)
    {
        var writer = new TradeWriter(info);
        var time = open;
        var price = info.BasePrice;

        for (var i = 0; i < 180; i++)
        {
            if (i == 30)
                writer.MarkRthStart();

            if (i == 150)
                writer.MarkRthEnd();

            time += random.Below(3) == 0 ? 0 : random.Below(2_000_000);
            price += random.Below(3) == 0 ? random.Between(-2, 2) : 0;

            var size = (uint)(random.Below(5) == 0 ? 16 + random.Below(500) : 1 + random.Below(15));

            writer.Add(time, price, size, (TradeSide)random.Below(3));
        }

        return writer;
    }

    /// <summary>
    /// A session's L1: a state to open, quotes that move each side's price and size (and empty a
    /// side), trades at the opposite best (implied) and elsewhere, a halt and a reopening.
    /// </summary>
    private static L1Writer L1(TapeInfo info, long open, Lcg random)
    {
        var writer = new L1Writer(info);
        var time = open;
        var bid = info.BasePrice - 1;
        var ask = info.BasePrice;
        uint bidSize = 5;
        uint askSize = 7;

        writer.State(time, 1);
        writer.Quote(time, bid, bidSize, ask, askSize);
        writer.State(time += 500, 7);

        for (var i = 0; i < 220; i++)
        {
            if (i == 50)
                writer.MarkRthStart();

            if (i == 190)
                writer.MarkRthEnd();

            time += random.Below(4) == 0 ? 0 : random.Below(300_000);

            if (i == 100)
            {
                writer.State(time, 8);
                writer.State(time += 5_000_000, 7);

                continue;
            }

            switch (random.Below(10))
            {
                case < 4:
                    if (random.Below(2) == 0)
                    {
                        var step = random.Between(-1, 1);
                        bid += step;
                        ask += step;
                    }

                    bidSize = (uint)(random.Below(12) == 0 ? 0 : 1 + random.Below(40));
                    askSize = (uint)(random.Below(12) == 0 ? 0 : 1 + random.Below(40));
                    writer.Quote(time, bid, bidSize, ask, askSize);
                    break;

                case < 8:
                    var buyer = random.Below(2) == 0;
                    writer.Trade(time, buyer ? ask : bid, (uint)(1 + random.Below(12)), buyer ? TradeSide.Buyer : TradeSide.Seller);
                    break;

                default:
                    writer.Trade(time, bid + random.Between(-2, 3), (uint)(1 + random.Below(300)), (TradeSide)random.Below(3));
                    break;
            }
        }

        return writer;
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Quote(string text) => $"\"{text}\"";

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>A small, fixed generator for the fixtures' synthetic prices: none of it is market data.</summary>
    private sealed class Lcg(ulong seed)
    {
        private ulong state = seed;

        public int Below(int count)
        {
            state = state * 6_364_136_223_846_793_005 + 1_442_695_040_888_963_407;

            return (int)((state >> 33) % (ulong)count);
        }

        public int Between(int low, int high) => low + Below(high - low + 1);
    }
}
