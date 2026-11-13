using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DbnSuite.Client.Contracts;
using DbnSuite.Client.Tape;

namespace DbnSuite.Client.Calendars;

/// <summary>A session's usual hours, in the calendar's time zone.</summary>
/// <param name="OpenDayOffset">0 when it opens on its trade date, -1 when it opens the evening before.</param>
public sealed record SessionTemplate(
    TimeOnly Open,
    int OpenDayOffset,
    TimeOnly Close,
    IReadOnlyList<DayOfWeek> TradingDays,
    IReadOnlyList<DailyHalt> DailyHalts);

/// <summary>A pause every trade date, such as CME's old 15:15 to 15:30 halt, between two dates when given.</summary>
public sealed record DailyHalt(TimeOnly From, TimeOnly To, DateOnly? Since, DateOnly? Until, string Note);

/// <summary>What a dated change, or a holiday rule, does to a trade date.</summary>
public enum CalendarChangeKind
{
    Closed = 1,
    EarlyClose = 2,
    LateOpen = 3,
    Halt = 4,

    /// <summary>Usual hours on a date a holiday rule would change: the exchange announced it trades normally.</summary>
    Regular = 5
}

/// <summary>One dated change to the usual hours. Times are local to the calendar's time zone.</summary>
/// <param name="At">When an early close closes or a late open opens.</param>
/// <param name="From">When a halt starts.</param>
/// <param name="To">When a halt ends.</param>
/// <param name="Source">Where it was learned: the exchange's status records, a notice, or <see cref="RuleCalendar.RuleSource"/>.</param>
/// <param name="Verified">True when checked against what the market actually did.</param>
public sealed record CalendarChange(
    DateOnly TradeDate,
    CalendarChangeKind Kind,
    DateTime? At,
    DateTime? From,
    DateTime? To,
    string Note,
    string Source,
    bool Verified);

/// <summary>Dated changes the exchange has announced, read from a file a live host keeps up to date.</summary>
public sealed record Announcements(IReadOnlyList<CalendarChange> Changes);

/// <summary>Error codes a <see cref="RuleCalendar"/> returns.</summary>
public static class CalendarErrors
{
    /// <summary>A calendar or announcements that can't be used. The message lists every problem, a line each.</summary>
    public const string Invalid = "calendar.invalid";

    /// <summary>The calendar has no session that trade date: a weekend, a holiday, or a day closed early before it opened.</summary>
    public const string NoSession = "calendar.no_session";

    /// <summary>The calendar is not for that symbol's root.</summary>
    public const string NotCovered = "calendar.not_covered";

    /// <summary>The file could not be read.</summary>
    public const string Io = "calendar.io";
}

/// <summary>
/// An exchange's schedule as rules: a session's usual hours, holiday rules that project them into any year, and
/// dated changes for what the rules can't know. Unlike <see cref="SessionCalendar"/>, which knows only the full ETH
/// days a catalog has seen, it answers for any date, holidays, early closes and late opens included, so a live
/// host can ask about tomorrow. Ported from AlgoSpec's calendar, itself a port of Flonk's.
/// </summary>
/// <remarks>
/// <para>
/// On a trade date, the changes to the usual hours are the dated changes when there are any that close, shorten
/// or restore the day; otherwise they are what the holiday rules say. Dated halts add to either. So an exchange's
/// announcement, added with <see cref="With"/>, replaces the projection for its date.
/// </para>
/// <list type="bullet">
/// <item>A date that isn't a trading day, or is closed, has no session.</item>
/// <item>An early close only moves the close earlier, and a late open only moves the open later; the earliest
/// close and the latest open win. A day left opening at or after its close has no session.</item>
/// <item>Halts, daily and dated, are cut to the session and become its <see cref="SessionDay.Breaks"/>.</item>
/// <item>RTH is the root's <see cref="Time.Sessions.RegularHours"/>, cut to the session, as
/// <see cref="SessionCalendar"/> does: on an early close at 12:00, RTH ends at 12:00; on one before 08:30, there
/// is none. It doesn't follow the stock market's own holidays.</item>
/// </list>
/// <para>
/// Local times convert through the calendar's time zone, so a daylight-saving change moves the UTC times. An
/// ambiguous fall-back time is read as standard time. A time inside a spring-forward gap gives no session, with
/// the reason; no exchange schedules one.
/// </para>
/// </remarks>
public sealed class RuleCalendar : ISessionCalendar
{
    /// <summary>The <see cref="CalendarChange.Source"/> of what a holiday rule projects.</summary>
    public const string RuleSource = "Holiday rule";

    private const string CmeFile = "cme-us-equity-index.json";

    private static readonly Lazy<RuleCalendar> cmeUsEquityIndex = new(() => Embedded(CmeFile));

    private readonly HashSet<string> roots;
    private readonly ILookup<DateOnly, CalendarChange> byDate;
    private readonly ConcurrentDictionary<int, ILookup<DateOnly, HolidayRule>> rulesByYear = new();
    private readonly ConcurrentDictionary<DateOnly, Result<SessionDay>> found = new();

    private RuleCalendar(
        string id,
        TimeZoneInfo zone,
        IReadOnlyList<string> roots,
        SessionTemplate session,
        IReadOnlyList<HolidayRule> rules,
        IReadOnlyList<CalendarChange> changes)
    {
        Id = id;
        Zone = zone;
        Roots = roots;
        Session = session;
        Rules = rules;
        Changes = changes;
        this.roots = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        byDate = changes.ToLookup(x => x.TradeDate);
    }

    /// <summary>
    /// CME Globex US equity index futures (ES, MES, NQ, MNQ, YM, MYM, RTY, M2K), in Chicago time: 17:00 the evening
    /// before to 16:00, with RTH from 08:30 to 15:00 cut to the session.
    /// </summary>
    /// <remarks>
    /// Its holiday rules project the schedule into any year, and reproduce every closure and early close from
    /// 2020-01-02 to 2026-11-02 on ES, MES, NQ, MNQ, MYM and M2K. Its dated changes are the market-wide halts and
    /// one-off closures no rule could know. A live host adds CME's announcements with <see cref="With"/>. A
    /// product's own short pauses, such as velocity-logic halts, aren't listed.
    /// </remarks>
    public static RuleCalendar CmeUsEquityIndex => cmeUsEquityIndex.Value;

    public string Id { get; }

    public TimeZoneInfo Zone { get; }

    /// <summary>The roots it is the calendar for, such as <c>ES</c>.</summary>
    public IReadOnlyList<string> Roots { get; }

    public SessionTemplate Session { get; }

    public IReadOnlyList<HolidayRule> Rules { get; }

    /// <summary>The dated changes, oldest first. What the rules project isn't listed; see <see cref="ChangesOn"/>.</summary>
    public IReadOnlyList<CalendarChange> Changes { get; }

    /// <summary>Builds a calendar, checking the session, every rule and every change.</summary>
    public static Result<RuleCalendar> Create(
        string id,
        string timeZone,
        IReadOnlyList<string> roots,
        SessionTemplate session,
        IReadOnlyList<HolidayRule> rules,
        IReadOnlyList<CalendarChange> changes)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(id))
            problems.Add("The calendar has no id.");

        TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone);

        if (zone is null)
            problems.Add(Text($"Unknown time zone '{timeZone}'."));

        if (roots.Count == 0)
            problems.Add("The calendar names no roots.");

        foreach (var root in roots.Where(r => !IsRoot(r)))
            problems.Add(Text($"'{root}' is not a root."));

        CheckSession(session, problems);

        foreach (var rule in rules)
            CheckRule(rule, problems);

        foreach (var change in changes)
            CheckChange(change, problems);

        if (problems.Count > 0 || zone is null)
            return Invalid(problems);

        return new RuleCalendar(id, zone, [.. roots.Select(r => r.ToUpperInvariant())], session, rules,
            [.. changes.OrderBy(x => x.TradeDate)]);
    }

    /// <summary>Reads a calendar from JSON: camelCase, enum names, comments allowed, unknown members refused.</summary>
    public static Result<RuleCalendar> Parse(string json)
    {
        var read = Deserialize(json, Info<CalendarFile>());

        if (read.Error is { } error)
            return error;

        var file = read.Value;
        var problems = new List<string>();
        SessionTemplate? session = null;

        if (file.Session is not { } t)
        {
            problems.Add("The calendar has no session.");
        }
        else if (t.Open is not { } open || t.Close is not { } close)
        {
            problems.Add("The session needs an open and a close.");
        }
        else
        {
            var halts = new List<DailyHalt>();

            foreach (var h in t.DailyHalts ?? [])
            {
                if (h.From is { } from && h.To is { } to)
                    halts.Add(new DailyHalt(from, to, h.Since, h.Until, h.Note ?? "Daily halt"));
                else
                    problems.Add("A daily halt needs a from and a to.");
            }

            session = new SessionTemplate(open, t.OpenDayOffset, close, t.TradingDays ?? [], halts);
        }

        var rules = (file.Holidays ?? []).Select(h => ToRule(h, problems)).OfType<HolidayRule>().ToList();
        var changes = ToChanges(file.Changes ?? [], problems);

        if (problems.Count > 0 || session is null)
            return Invalid(problems);

        return Create(file.Id ?? "", file.TimeZone ?? "", file.Roots ?? [], session, rules, changes);
    }

    /// <summary>Reads a calendar file. Each problem names the file.</summary>
    public static async Task<Result<RuleCalendar>> LoadAsync(string path, CancellationToken cancel)
    {
        var json = await ReadAsync(path, cancel).ConfigureAwait(false);

        return json.Error is { } error ? error : Named(path, Parse(json.Value));
    }

    /// <summary>
    /// Reads announcements: <c>{ "changes": [ … ] }</c>, each as in a calendar file, typically with
    /// <c>"source"</c> naming the exchange's notice.
    /// </summary>
    public static Result<Announcements> ParseAnnouncements(string json)
    {
        var read = Deserialize(json, Info<AnnouncementsFile>());

        if (read.Error is { } error)
            return error;

        var problems = new List<string>();
        var changes = ToChanges(read.Value.Changes ?? [], problems);

        foreach (var change in changes)
            CheckChange(change, problems);

        return problems.Count > 0 ? Invalid(problems) : new Announcements(changes);
    }

    /// <summary>Reads an announcements file. Each problem names the file.</summary>
    public static async Task<Result<Announcements>> LoadAnnouncementsAsync(string path, CancellationToken cancel)
    {
        var json = await ReadAsync(path, cancel).ConfigureAwait(false);

        return json.Error is { } error ? error : Named(path, ParseAnnouncements(json.Value));
    }

    /// <summary>
    /// This calendar with the exchange's announced changes. An announced closure, early close, late open or regular
    /// day replaces the calendar's own dated changes of those kinds for its date, and what the rules project for it;
    /// an announced halt adds to them. This calendar is left as it was.
    /// </summary>
    public Result<RuleCalendar> With(Announcements announced)
    {
        var replaced = announced.Changes
            .Where(x => x.Kind != CalendarChangeKind.Halt)
            .Select(x => x.TradeDate)
            .ToHashSet();

        var kept = Changes.Where(x => x.Kind == CalendarChangeKind.Halt || !replaced.Contains(x.TradeDate));

        return Create(Id, Zone.Id, Roots, Session, Rules, [.. kept, .. announced.Changes]);
    }

    /// <summary>The symbol's session on a trade date, or null when it has none or the calendar isn't for its root.</summary>
    /// <param name="symbol">A root, contract or continuous name, such as <c>ES</c>, <c>ESZ6</c> or <c>ES.v.0</c>.</param>
    public SessionDay? For(string symbol, DateOnly tradeDate) =>
        Find(symbol, tradeDate) is { IsSuccess: true } day ? day.Value : null;

    /// <summary>
    /// The symbol's session on a trade date, or why there is none: <see cref="CalendarErrors.NoSession"/>,
    /// <see cref="CalendarErrors.NotCovered"/> or <see cref="Errors.BadSymbol"/>.
    /// </summary>
    public Result<SessionDay> Find(string symbol, DateOnly tradeDate)
    {
        var root = RootOf(symbol);

        if (root.Error is { } error)
            return error;

        var day = found.GetOrAdd(tradeDate, Build);

        if (!day.IsSuccess)
            return day;

        var (rthOpen, rthClose) = SessionRules.Rth(root.Value, tradeDate, day.Value.Open, day.Value.Close);

        return day.Value with { RthOpen = rthOpen, RthClose = rthClose };
    }

    /// <summary>
    /// The session open at a moment, halts included, or null when none is. Times are nanoseconds since the UNIX
    /// epoch; a session runs from its open up to, not including, its close.
    /// </summary>
    public SessionDay? At(string symbol, long nanos)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddTicks(nanos / 100), Zone);
        var date = DateOnly.FromDateTime(local);

        foreach (var candidate in (ReadOnlySpan<DateOnly>)[date, date.AddDays(1)])
        {
            if (For(symbol, candidate) is { } day && nanos >= day.Open && nanos < day.Close)
                return day;
        }

        return null;
    }

    /// <summary>The symbol's sessions on every trade date from <paramref name="from"/> to <paramref name="to"/>, inclusive.</summary>
    public IEnumerable<SessionDay> Days(string symbol, DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (For(symbol, date) is { } day)
                yield return day;
        }
    }

    /// <summary>
    /// What changes the usual hours on a trade date, and why: dated changes, or what the holiday rules project,
    /// whose <see cref="CalendarChange.Source"/> is <see cref="RuleSource"/>.
    /// </summary>
    public IReadOnlyList<CalendarChange> ChangesOn(DateOnly tradeDate)
    {
        var dated = byDate[tradeDate].ToList();

        if (dated.Any(x => x.Kind != CalendarChangeKind.Halt))
            return dated;

        var projected = RulesOn(tradeDate).Select(r => new CalendarChange(
            tradeDate, r.Kind, r.At is { } at ? tradeDate.ToDateTime(at) : null, null, null, r.Name, RuleSource, false));

        return [.. projected, .. dated];
    }

    private Result<string> RootOf(string symbol)
    {
        var spec = SymbolSpec.Parse(symbol);

        if (spec.Error is { } error)
            return error;

        return roots.Contains(spec.Value.Root)
            ? spec.Value.Root
            : new Error(CalendarErrors.NotCovered, Text($"The {Id} calendar is not for {spec.Value.Root}."));
    }

    private static bool IsRoot(string text)
    {
        var spec = SymbolSpec.Parse(text);

        return spec.IsSuccess && spec.Value.Kind == SymbolKind.Root;
    }

    // A rule's date can cross a year end, so look at the years either side.
    private IEnumerable<HolidayRule> RulesOn(DateOnly date) =>
        Enumerable.Range(date.Year - 1, 3).SelectMany(year => RulesIn(year)[date]);

    private ILookup<DateOnly, HolidayRule> RulesIn(int year) =>
        rulesByYear.GetOrAdd(year, y => Rules
            .Select(r => (Rule: r, Date: r.On(y)))
            .Where(x => x.Date is not null)
            .ToLookup(x => x.Date!.Value, x => x.Rule));

    /// <summary>The session on a trade date with no RTH yet, which depends on the root.</summary>
    private Result<SessionDay> Build(DateOnly date)
    {
        var t = Session;

        if (!t.TradingDays.Contains(date.DayOfWeek))
            return NoSession(Text($"{date:yyyy-MM-dd} is a {date.DayOfWeek}, not a trading day."));

        var changes = ChangesOn(date);

        if (changes.FirstOrDefault(x => x.Kind == CalendarChangeKind.Closed) is { } closed)
            return NoSession(Text($"{date:yyyy-MM-dd} is closed: {closed.Note}."));

        var open = date.AddDays(t.OpenDayOffset).ToDateTime(t.Open);
        var close = date.ToDateTime(t.Close);
        var notes = new List<string>();

        foreach (var x in changes)
        {
            if (x.Kind == CalendarChangeKind.EarlyClose && x.At < close)
            {
                close = x.At.Value;
                notes.Add(x.Note);
            }

            if (x.Kind == CalendarChangeKind.LateOpen && x.At > open)
            {
                open = x.At.Value;
                notes.Add(x.Note);
            }
        }

        if (open >= close)
            return NoSession(Text($"{date:yyyy-MM-dd} has no session: {string.Join(", ", notes)} leaves none."));

        var halts = t.DailyHalts
            .Where(h => (h.Since is null || date >= h.Since) && (h.Until is null || date <= h.Until))
            .Select(h => (From: date.ToDateTime(h.From), To: date.ToDateTime(h.To)))
            .Concat(changes
                .Where(x => x.Kind == CalendarChangeKind.Halt)
                .Select(x => (From: x.From!.Value, To: x.To!.Value)))
            .Select(h => (From: Max(h.From, open), To: Min(h.To, close)))
            .Where(h => h.From < h.To)
            .OrderBy(h => h.From)
            .ToArray();

        foreach (var local in halts.SelectMany(h => (DateTime[])[h.From, h.To]).Prepend(close).Prepend(open))
        {
            if (Zone.IsInvalidTime(local))
                return NoSession(Text($"{date:yyyy-MM-dd} has no session: {local:yyyy-MM-dd HH:mm} doesn't exist in {Zone.Id}, whose clocks skip it."));
        }

        var breaks = halts.Select(h => new SessionBreak(Nanos(h.From), Nanos(h.To))).ToArray();

        return new SessionDay(date, Nanos(open), Nanos(close), breaks, null, null);
    }

    private long Nanos(DateTime local) =>
        (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone) - DateTime.UnixEpoch).Ticks * 100;

    private static RuleCalendar Embedded(string file)
    {
        var name = $"DbnSuite.Client.Calendars.{file}";

        using var stream = typeof(RuleCalendar).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The package carries no calendar named {file}.");
        using var reader = new StreamReader(stream);

        var read = Parse(reader.ReadToEnd());

        return read.IsSuccess
            ? read.Value
            : throw new InvalidOperationException($"The package's {file} is broken: {read.Error}");
    }

    private static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)CalendarJson.Strict.GetTypeInfo(typeof(T));

    private static Result<T> Deserialize<T>(string json, JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, info) is { } value
                ? value
                : Invalid(["The file is empty."]);
        }
        catch (JsonException problem)
        {
            return Invalid([problem.Message]);
        }
    }

    private static async Task<Result<string>> ReadAsync(string path, CancellationToken cancel)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancel).ConfigureAwait(false);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return new Error(CalendarErrors.Io, $"Could not read {path}: {problem.Message}");
        }
    }

    private static Result<T> Named<T>(string path, Result<T> read) =>
        read.Error is { Code: CalendarErrors.Invalid } error
            ? Invalid([.. error.Message.Split(Environment.NewLine).Select(p => $"{path}: {p}")])
            : read;

    private static Error Invalid(IEnumerable<string> problems) =>
        new(CalendarErrors.Invalid, string.Join(Environment.NewLine, problems));

    private static Error NoSession(string why) => new(CalendarErrors.NoSession, why);

    private static HolidayRule? ToRule(HolidayFile h, List<string> problems)
    {
        var name = h.Name ?? "";
        var label = name.Length > 0 ? name : "A holiday rule";

        if (h.Kind is not { } kind)
        {
            problems.Add(Text($"{label}: needs a kind."));
            return null;
        }

        var days = (h.Fixed is not null ? 1 : 0) + (h.Nth is not null ? 1 : 0) + (h.Easter == true ? 1 : 0);

        if (days != 1)
        {
            problems.Add(Text($"{label}: give exactly one of fixed, nth or easter."));
            return null;
        }

        HolidayDay day;

        if (h.Fixed is { } fixedText)
        {
            // 2024 is a leap year, so February 29 reads.
            if (!DateOnly.TryParseExact($"2024-{fixedText}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fixedDate))
            {
                problems.Add(Text($"{label}: fixed must be MM-dd, not '{fixedText}'."));
                return null;
            }

            day = new FixedDay(fixedDate.Month, fixedDate.Day, h.Observed ?? Observance.None);
        }
        else if (h.Nth is { } n)
        {
            if (h.Weekday is not { } weekday || h.Month is not { } month)
            {
                problems.Add(Text($"{label}: nth needs a weekday and a month."));
                return null;
            }

            day = new NthWeekday(month, weekday, n);
        }
        else
        {
            day = new Easter();
        }

        (int, int)? dayOfMonth = null;

        if (h.DayOfMonth is { } range)
        {
            if (range.Count != 2)
            {
                problems.Add(Text($"{label}: dayOfMonth is [from, to]."));
                return null;
            }

            dayOfMonth = (range[0], range[1]);
        }

        return new HolidayRule(name, day, h.Offset, kind, h.At, h.Weekdays, dayOfMonth, h.Since, h.Until);
    }

    private static List<CalendarChange> ToChanges(List<ChangeFile> files, List<string> problems)
    {
        var changes = new List<CalendarChange>();

        foreach (var x in files)
        {
            if (x.TradeDate is not { } date || x.Kind is not { } kind)
            {
                problems.Add("Every change needs a tradeDate and a kind.");
                continue;
            }

            changes.Add(new CalendarChange(date, kind, x.At, x.From, x.To, x.Note ?? "", x.Source ?? "", x.Verified));
        }

        return changes;
    }

    private static void CheckSession(SessionTemplate t, List<string> problems)
    {
        if (t.OpenDayOffset is not (0 or -1))
            problems.Add("The session's openDayOffset must be 0 or -1.");

        if (t.OpenDayOffset == 0 && t.Open >= t.Close)
            problems.Add("A session that opens on its trade date must open before it closes.");

        if (t.TradingDays.Count == 0)
            problems.Add("The session has no trading days.");

        foreach (var h in t.DailyHalts)
        {
            if (h.From >= h.To)
                problems.Add(Text($"The daily halt from {h.From:HH:mm} must end after it starts."));

            if (h.Since > h.Until)
                problems.Add(Text($"The daily halt from {h.From:HH:mm} ends before it begins."));
        }
    }

    private static void CheckRule(HolidayRule rule, List<string> problems)
    {
        var at = rule.Name.Length > 0 ? rule.Name : "A holiday rule";

        if (rule.Name.Length == 0)
            problems.Add("Every holiday rule needs a name.");

        switch (rule.Kind)
        {
            case CalendarChangeKind.Closed when rule.At is not null:
                problems.Add(Text($"{at}: a closed day takes no time."));
                break;

            case CalendarChangeKind.EarlyClose or CalendarChangeKind.LateOpen when rule.At is null:
                problems.Add(Text($"{at}: needs at."));
                break;

            case CalendarChangeKind.Halt or CalendarChangeKind.Regular:
                problems.Add(Text($"{at}: a holiday rule closes the day, closes it early or opens it late."));
                break;
        }

        switch (rule.Day)
        {
            case NthWeekday n when n.Month is < 1 or > 12 || n.N is not (>= 1 and <= 5 or -1):
                problems.Add(Text($"{at}: nth is 1 to 5, or -1 for the last, in a month from 1 to 12."));
                break;

            case FixedDay f when f.Month is < 1 or > 12 || f.Day < 1 || f.Day > DateTime.DaysInMonth(2024, f.Month):
                problems.Add(Text($"{at}: {f.Month:00}-{f.Day:00} is not a day of the year."));
                break;
        }

        if (rule.Weekdays is { Count: 0 })
            problems.Add(Text($"{at}: weekdays, when given, can't be empty."));

        if (rule.DayOfMonth is { } range && (range.From < 1 || range.To > 31 || range.From > range.To))
            problems.Add(Text($"{at}: dayOfMonth must run from 1 to 31, low to high."));

        if (rule.Since > rule.Until)
            problems.Add(Text($"{at}: since comes after until."));
    }

    private static void CheckChange(CalendarChange x, List<string> problems)
    {
        var at = Text($"{x.TradeDate:yyyy-MM-dd} {x.Kind}");

        if (string.IsNullOrWhiteSpace(x.Note))
            problems.Add(Text($"{at}: needs a note saying what it is."));

        switch (x.Kind)
        {
            case CalendarChangeKind.Closed or CalendarChangeKind.Regular
                when x.At is not null || x.From is not null || x.To is not null:
                problems.Add(Text($"{at}: takes no times."));
                break;

            case CalendarChangeKind.EarlyClose or CalendarChangeKind.LateOpen when x.At is null:
                problems.Add(Text($"{at}: needs at."));
                break;

            case CalendarChangeKind.EarlyClose or CalendarChangeKind.LateOpen when x.From is not null || x.To is not null:
                problems.Add(Text($"{at}: takes at, not from and to."));
                break;

            case CalendarChangeKind.Halt when x.From is null || x.To is null || x.From >= x.To:
                problems.Add(Text($"{at}: needs from before to."));
                break;

            case CalendarChangeKind.Halt when x.At is not null:
                problems.Add(Text($"{at}: takes from and to, not at."));
                break;
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
