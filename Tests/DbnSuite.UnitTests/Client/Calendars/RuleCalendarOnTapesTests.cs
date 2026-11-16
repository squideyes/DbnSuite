using DbnSuite.Client.Calendars;
using DbnSuite.Client.Tape;
using DbnSuite.Client.Time;
using Shouldly;

namespace DbnSuite.UnitTests.CalendarTests;

/// <summary>A test that needs real tapes: it runs only where <c>DBNSUITE_TAPES</c> names a tape folder, as <c>dbnf tape --tapes</c> takes.</summary>
/// <remarks>Tapes are licensed data, so CI has none and skips these. It reads only each root's catalog.</remarks>
public sealed class TapesFactAttribute : FactAttribute
{
    public const string Variable = "DBNSUITE_TAPES";

    public TapesFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } folder || !Directory.Exists(folder))
            Skip = $"Set {Variable} to a tape folder to run this.";
    }

    public static string Folder => Environment.GetEnvironmentVariable(Variable)!;
}

/// <summary>
/// The CME calendar's rules against what the market actually did, as each root's catalog tells it: every weekday
/// the catalog covers, its session's open, close, halts and RTH, and on every full ETH day, what
/// <see cref="SessionCalendar"/> says.
/// </summary>
public sealed class RuleCalendarOnTapesTests
{
    // The Nasdaq and Russell contracts reopened two minutes late, at 17:02, for trade date 2025-04-03. ES, MES and
    // MYM opened on time, so it isn't the market's schedule.
    private static readonly HashSet<(string Root, DateOnly Date)> ProductLateOpens =
    [
        ("NQ", new DateOnly(2025, 4, 3)),
        ("MNQ", new DateOnly(2025, 4, 3)),
        ("RTY", new DateOnly(2025, 4, 3)),
        ("M2K", new DateOnly(2025, 4, 3)),
    ];

    // MYM's status records lack the daily 15:15 halt on two days that every other product shows it.
    private static readonly HashSet<(string Root, DateOnly Date)> MissingStatus =
    [
        ("MYM", new DateOnly(2020, 2, 27)),
        ("MYM", new DateOnly(2020, 6, 30)),
    ];

    // Each product's status records stamp the same halt up to a few seconds apart.
    private const long Slack = 60_000_000_000;

    private const long Second = 1_000_000_000;

    private static RuleCalendar Cme => RuleCalendar.CmeUsEquityIndex;

    [TapesFact]
    public async Task Every_es_weekday_on_the_tapes_matches_the_rules() => (await MismatchesAsync("ES")).ShouldBeEmpty();

    [TapesFact]
    public async Task Every_mes_weekday_on_the_tapes_matches_the_rules() => (await MismatchesAsync("MES")).ShouldBeEmpty();

    [TapesFact]
    public async Task Every_nq_weekday_on_the_tapes_matches_the_rules() => (await MismatchesAsync("NQ")).ShouldBeEmpty();

    [TapesFact]
    public async Task Every_mnq_weekday_on_the_tapes_matches_the_rules() => (await MismatchesAsync("MNQ")).ShouldBeEmpty();

    [TapesFact]
    public async Task Every_ym_weekday_on_the_tapes_matches_the_rules() => (await MismatchesAsync("YM")).ShouldBeEmpty();

    [TapesFact]
    public async Task Every_mym_weekday_on_the_tapes_matches_the_rules() => (await MismatchesAsync("MYM")).ShouldBeEmpty();

    [TapesFact]
    public async Task Every_rty_weekday_on_the_tapes_matches_the_rules() => (await MismatchesAsync("RTY")).ShouldBeEmpty();

    [TapesFact]
    public async Task Every_m2k_weekday_on_the_tapes_matches_the_rules() => (await MismatchesAsync("M2K")).ShouldBeEmpty();

    private static async Task<List<string>> MismatchesAsync(string root)
    {
        var loaded = await Catalog.LoadAsync(Catalog.PathOf(TapesFactAttribute.Folder, Sessions.Dataset, root), CancellationToken.None);

        loaded.Error.ShouldBeNull();

        var catalog = loaded.Value;
        var tapes = new SessionCalendar(catalog);
        var contracts = catalog.Contracts.OrderBy(c => c.Year).ThenBy(c => c.Month).ToList();
        var mismatches = new List<string>();

        for (var date = catalog.Days[0].TradeDate; date <= catalog.Days[^1].TradeDate; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            var at = $"{root} {date:yyyy-MM-dd}";
            var rules = Cme.Find(root, date);
            var rule = rules.IsSuccess ? rules.Value : null;
            var ruleFull = rule is not null && SessionRules.NotFullEth(date, rule.Open, rule.Close) is null;

            if (catalog.IsFullDay(date) != ruleFull)
                mismatches.Add($"{at}: the catalog says {catalog.Excluded(date) ?? "a full ETH day"}, the rules {(ruleFull ? "a full ETH day" : rules.Error?.Message ?? "a short one")}");

            if (Actual(catalog, contracts, date) is not { } actual)
            {
                if (rule is not null)
                    mismatches.Add($"{at}: the tapes have no session, the rules have one");

                continue;
            }

            if (rule is null)
            {
                mismatches.Add($"{at}: the tapes have a session, the rules have none: {rules.Error}");
                continue;
            }

            if (!ProductLateOpens.Contains((root, date)))
                Compare(mismatches, $"{at} open", rule.Open, actual.Open);

            Compare(mismatches, $"{at} close", rule.Close, actual.Close);

            // Every market-wide halt shows on every product. A product also pauses on its own, which the rules
            // leave out.
            foreach (var halt in rule.Breaks.Where(h => !actual.Breaks.Any(b => Near(b.Start, h.Start) && Near(b.End, h.End))))
            {
                if (!MissingStatus.Contains((root, date)))
                    mismatches.Add($"{at}: the rules' halt {Text(halt.Start)} to {Text(halt.End)} isn't on the tapes");
            }

            var (rthOpen, rthClose) = SessionRules.Rth(root, date, actual.Open, actual.Close);

            Compare(mismatches, $"{at} rth open", rule.RthOpen, rthOpen);
            Compare(mismatches, $"{at} rth close", rule.RthClose, rthClose);

            // On a full ETH day, the catalog's own calendar, which the tapes are made from.
            if (catalog.HeldOn(date) is { } held && tapes.For(held.Symbol, date) is { } full)
            {
                if (!ProductLateOpens.Contains((root, date)))
                    Compare(mismatches, $"{at} catalog open", rule.Open, full.Open);

                Compare(mismatches, $"{at} catalog close", rule.Close, full.Close);
                Compare(mismatches, $"{at} catalog rth open", rule.RthOpen, full.RthOpen);
                Compare(mismatches, $"{at} catalog rth close", rule.RthClose, full.RthClose);
            }
            else if (catalog.IsFullDay(date))
            {
                mismatches.Add($"{at}: a full ETH day the catalog's calendar has no session for");
            }
        }

        return mismatches;
    }

    /// <summary>
    /// The market's session on a date, full or not. The held contract's when it traded the whole session; the
    /// contract the roll rule holds can expire partway through, at the 08:30 settlement, and then a contract still
    /// trading at the close shows it.
    /// </summary>
    private static SessionDay? Actual(Catalog catalog, List<CatalogContract> contracts, DateOnly date)
    {
        if (catalog.Day(date) is not { Sessions.Count: > 0 } day)
            return null;

        var open = Nanos(Sessions.SessionStart(date));
        var close = Nanos(Sessions.SessionEnd(date));

        return contracts
            .Where(c => c.FirstTrade <= open)
            .Prepend(catalog.HeldOn(date))
            .OfType<CatalogContract>()
            .Where(c => c.LastTrade >= close)
            .Select(c => SessionOf(day, c))
            .FirstOrDefault(s => s is not null);
    }

    private static SessionDay? SessionOf(CatalogDay day, CatalogContract contract)
    {
        var entry = day.Contracts.FirstOrDefault(c => c.InstrumentId == contract.InstrumentId);
        var session = day.Sessions[entry?.Session ?? 0];

        if (entry is null && (contract.FirstTrade > session.Open || contract.LastTrade < session.Open))
            return null;

        return new SessionDay(day.TradeDate, session.Open, session.Close, session.Breaks, null, null);
    }

    // The rules keep whole seconds.
    private static void Compare(List<string> mismatches, string what, long? rules, long? tapes)
    {
        if (rules / Second != tapes / Second)
            mismatches.Add($"{what}: rules {Text(rules)}, tapes {Text(tapes)}");
    }

    private static bool Near(long a, long b) => Math.Abs(a - b) <= Slack;

    private static string Text(long? nanos) =>
        nanos is { } n ? Zones.Format((ulong)n, Zones.Find(Zones.Default).Value) : "none";

    private static long Nanos(DateTimeOffset time) => (time - DateTimeOffset.UnixEpoch).Ticks * 100;
}
