using DbnClient.Dbn;

namespace DbnClient.Time;

/// <summary>Which part of each CME session to keep.</summary>
public enum SessionKind
{
    /// <summary>
    /// The whole Globex session, 17:00 to 16:00 Chicago time, and the hour from
    /// 16:00 to 17:00 after it, where settlement and status messages land.
    /// </summary>
    Eth,

    /// <summary>The root's regular trading hours.</summary>
    Rth,

    /// <summary>Hours of your own, in a zone of your own.</summary>
    Custom
}

/// <summary>A daily window of local time; one that ends before it starts runs past midnight.</summary>
public sealed record Hours(TimeOnly Start, TimeOnly End)
{
    public bool Contains(TimeOnly time) =>
        Start <= End ? time >= Start && time < End : time >= Start || time < End;

    /// <summary>Reads <c>09:30-16:00</c>.</summary>
    public static Result<Hours> Parse(string text)
    {
        var parts = text.Split('-', StringSplitOptions.TrimEntries);

        return parts.Length == 2
            && TimeOnly.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var start)
            && TimeOnly.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var end)
            && start != end
                ? new Hours(start, end)
                : new Error("input.bad_hours", $"\"{text}\" is not a window of hours such as 09:30-16:00.");
    }
}

/// <summary>
/// CME's trading day. Globex opens at 17:00 Chicago time and the session it
/// opens belongs to the next day: Tuesday's session runs from Monday 17:00 to
/// Tuesday 16:00, and Monday's opens on Sunday.
/// </summary>
public static class Sessions
{
    /// <summary>
    /// The only dataset whose trading day this version knows. Exports and rolls
    /// of any other dataset are refused with <see cref="Errors.UnsupportedDataset"/>
    /// rather than cut to the wrong day; its raw files can still be listed and pulled.
    /// </summary>
    public const string Dataset = "GLBX.MDP3";

    public static readonly TimeOnly Open = new(17, 0);
    public static readonly TimeOnly Close = new(16, 0);

    private static readonly TimeZoneInfo Chicago = Zones.Find(Zones.Default).Value;

    /// <summary>
    /// <see cref="Errors.UnsupportedDataset"/> for a dataset other than <see cref="Dataset"/>,
    /// or null when trade dates are known for it.
    /// </summary>
    public static Error? Unsupported(string dataset) =>
        dataset == Dataset
            ? null
            : new Error(Errors.UnsupportedDataset,
                $"Trade dates, bars and rolls are defined for {Dataset} only, not {dataset}. " +
                "Its raw files can still be listed and pulled.");

    /// <summary>Regular trading hours, Chicago time, for the roots DbnSync keeps.</summary>
    private static readonly Dictionary<string, Hours> Regular = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ES"] = Equity, ["MES"] = Equity, ["NQ"] = Equity, ["MNQ"] = Equity,
        ["YM"] = Equity, ["MYM"] = Equity, ["RTY"] = Equity, ["M2K"] = Equity,
        ["CL"] = new(new(8, 0), new(13, 30)), ["MCL"] = new(new(8, 0), new(13, 30)),
        ["GC"] = new(new(7, 20), new(12, 30)), ["MGC"] = new(new(7, 20), new(12, 30))
    };

    private static Hours Equity => new(new(8, 30), new(15, 0));

    /// <summary>The regular hours of a root, Chicago time, or null for a root DbnSync has none for.</summary>
    public static Hours? RegularHours(string root) => Regular.GetValueOrDefault(root);

    /// <summary>The trade date a DBN time belongs to.</summary>
    public static DateOnly TradeDate(ulong nanos)
    {
        var local = Local(nanos);
        var date = DateOnly.FromDateTime(local);

        return TimeOnly.FromDateTime(local) >= Open ? date.AddDays(1) : date;
    }

    /// <summary>
    /// The first trade date on or after a calendar day. A Saturday or Sunday is
    /// none: Globex shuts on Friday, and Sunday's 17:00 open belongs to Monday.
    /// Exchange holidays are not known here.
    /// </summary>
    public static DateOnly FirstTradeDate(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Saturday => day.AddDays(2),
        DayOfWeek.Sunday => day.AddDays(1),
        _ => day
    };

    /// <summary>When a trade date's session opens: 17:00 Chicago time the day before, as UTC.</summary>
    public static DateTimeOffset SessionStart(DateOnly tradeDate)
    {
        var local = tradeDate.AddDays(-1).ToDateTime(Open);

        return new DateTimeOffset(local, Chicago.GetUtcOffset(local)).ToUniversalTime();
    }


    /// <summary>When a trade date's session closes: 16:00 Chicago time, as UTC.</summary>
    public static DateTimeOffset SessionEnd(DateOnly tradeDate)
    {
        var local = tradeDate.ToDateTime(Close);

        return new DateTimeOffset(local, Chicago.GetUtcOffset(local)).ToUniversalTime();
    }
    /// <summary>The UTC days whose files hold a span of trade dates.</summary>
    public static DateSpan UtcDays(DateSpan tradeDates) => new(tradeDates.From.AddDays(-1), tradeDates.To);

    /// <summary>The local time of day in Chicago.</summary>
    public static DateTime Local(ulong nanos) =>
        TimeZoneInfo.ConvertTime(DbnTime.ToTime(nanos), Chicago).DateTime;
}

/// <summary>Keeps the records of the trade dates and hours asked for.</summary>
public sealed class SessionFilter
{
    private readonly DateSpan tradeDates;
    private readonly Hours? hours;
    private readonly TimeZoneInfo zone;

    private SessionFilter(DateSpan tradeDates, Hours? hours, TimeZoneInfo zone)
    {
        this.tradeDates = tradeDates;
        this.hours = hours;
        this.zone = zone;
    }

    /// <param name="custom">For <see cref="SessionKind.Custom"/>: the hours and the zone they are in.</param>
    public static Result<SessionFilter> Create(DateSpan tradeDates, SessionKind kind, string root,
        (Hours Hours, TimeZoneInfo Zone)? custom = null)
    {
        var chicago = Zones.Find(Zones.Default).Value;

        return kind switch
        {
            SessionKind.Eth => new SessionFilter(tradeDates, null, chicago),
            SessionKind.Rth => Sessions.RegularHours(root) is { } rth
                ? new SessionFilter(tradeDates, rth, chicago)
                : new Error("input.no_rth", $"There are no regular hours for {root}; give them with --hours."),
            _ => custom is { } c
                ? new SessionFilter(tradeDates, c.Hours, c.Zone)
                : new Error("input.no_hours", "Custom hours need --hours.")
        };
    }

    /// <summary>
    /// The trade date a time belongs to, or null when it is outside what was
    /// asked for. Records come in time order, so the current trade date's
    /// bounds are kept and most times need only two comparisons.
    /// </summary>
    public DateOnly? Accept(ulong nanos)
    {
        var t = (long)nanos;

        if (t < dayStart || t >= dayEnd)
        {
            day = Sessions.TradeDate(nanos);
            dayStart = Nanos(Sessions.SessionStart(day));
            dayEnd = Nanos(Sessions.SessionStart(day.AddDays(1)));
            windowStart = hours is null ? dayStart : Nanos(Anchor(day));
            windowEnd = hours is null ? dayEnd : Nanos(Close(day));
        }

        return tradeDates.Contains(day) && t >= windowStart && t < windowEnd ? day : null;
    }

    private DateOnly day;
    private long dayStart = long.MaxValue;
    private long dayEnd = long.MinValue;
    private long windowStart;
    private long windowEnd;

    private static long Nanos(DateTimeOffset time) => (time - DateTimeOffset.UnixEpoch).Ticks * 100;

    /// <summary>Where a trade date's bars start counting from: the session's open, or the window's.</summary>
    public DateTimeOffset Anchor(DateOnly tradeDate)
    {
        if (hours is null)
            return Sessions.SessionStart(tradeDate);

        return At(OpeningDay(tradeDate), hours.Start);
    }

    /// <summary>When a trade date's session, or its window of hours, closes.</summary>
    public DateTimeOffset Close(DateOnly tradeDate)
    {
        if (hours is null)
            return Sessions.SessionEnd(tradeDate);

        // A window that runs past midnight closes on the day after it opens.
        var opening = OpeningDay(tradeDate);

        return At(hours.Start < hours.End ? opening : opening.AddDays(1), hours.End);
    }

    /// <summary>
    /// The calendar day, in the window's zone, on which a trade date's window opens. A trade date starts
    /// the evening before, so hours such as 18:00 to 23:00 Chicago time belong to the day before the
    /// trade date's name, and hours in a zone far from Chicago can fall on either side of it.
    /// </summary>
    private DateOnly OpeningDay(DateOnly tradeDate)
    {
        // A window that runs past midnight opens on the day before.
        if (hours!.Start >= hours.End)
            return tradeDate.AddDays(-1);

        var from = Sessions.SessionStart(tradeDate);
        var until = Sessions.SessionStart(tradeDate.AddDays(1));

        // The day of the trade date's own name first, which is where daytime hours are.
        foreach (var day in new[] { tradeDate, tradeDate.AddDays(-1), tradeDate.AddDays(1) })
        {
            var opens = At(day, hours.Start);

            if (opens >= from && opens < until)
                return day;
        }

        return tradeDate;
    }

    private DateTimeOffset At(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time);

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
