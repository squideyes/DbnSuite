using DbnSuite.Client.Tape;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

public sealed class SessionRulesTests
{
    private const string Es = "ES";
    private const string Cl = "CL";
    private const string UnknownRoot = "ZZ";
    private const ushort PreOpen = 1;
    private const ushort Trading = 7;
    private const ushort Close = 12;
    private const uint Id = 7;
    private const long Second = 1_000_000_000;

    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly Dictionary<uint, Dictionary<DateOnly, long>> NoBars = [];

    [Fact]
    public void ASessionBelongsToTheDateAfterItsEveningOpen()
    {
        var days = DaysOf(Span(At(2026, 10, 28, 17), At(2026, 10, 29, 16)));

        days.Count.ShouldBe(1);
        days[0].TradeDate.ShouldBe(new DateOnly(2026, 10, 29));
        days[0].Open.ShouldBe(At(2026, 10, 28, 17));
        days[0].Close.ShouldBe(At(2026, 10, 29, 16));
        days[0].Breaks.ShouldBeEmpty();
    }

    [Fact]
    public void ASundayOpenBelongsToMonday()
    {
        var days = DaysOf(Span(At(2026, 10, 25, 17), At(2026, 10, 26, 16)));

        days[0].TradeDate.ShouldBe(new DateOnly(2026, 10, 26));
        days[0].TradeDate.DayOfWeek.ShouldBe(DayOfWeek.Monday);
    }

    [Fact]
    public void TheClocksChangingKeepsChicagoHours()
    {
        var days = DaysOf(Span(At(2026, 11, 1, 17), At(2026, 11, 2, 16)));
        var length = TimeSpan.FromTicks((days[0].Close - days[0].Open) / 100);

        days[0].TradeDate.ShouldBe(new DateOnly(2026, 11, 2));
        length.ShouldBe(TimeSpan.FromHours(23));
        days[0].RthOpen.ShouldBe(At(2026, 11, 2, 8, 30));
    }

    [Fact]
    public void TheSessionOverTheClockChangeIsAnHourLonger()
    {
        var days = DaysOf(Span(At(2026, 10, 31, 17), At(2026, 11, 1, 16)));
        var length = TimeSpan.FromTicks((days[0].Close - days[0].Open) / 100);

        length.ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact]
    public void AHolidayClosesEarlyAndCutsRth()
    {
        var days = DaysOf(Span(At(2026, 11, 25, 17), At(2026, 11, 26, 12)));

        days[0].Close.ShouldBe(At(2026, 11, 26, 12));
        days[0].RthOpen.ShouldBe(At(2026, 11, 26, 8, 30));
        days[0].RthClose.ShouldBe(At(2026, 11, 26, 12));
    }

    [Fact]
    public void ADayWithNoRegularSessionHasNoRth()
    {
        var days = DaysOf(Span(At(2026, 11, 25, 17), At(2026, 11, 26, 7)));

        days[0].RthOpen.ShouldBeNull();
        days[0].RthClose.ShouldBeNull();
    }

    [Fact]
    public void ARootWithNoRegularHoursHasNoRth()
    {
        var days = SessionRules.Days(UnknownRoot, [Span(At(2026, 10, 28, 17), At(2026, 10, 29, 16))]).ToList();

        days[0].RthOpen.ShouldBeNull();
    }

    [Fact]
    public void RthHoursComeFromTheRoot()
    {
        var days = SessionRules.Days(Cl, [Span(At(2026, 10, 28, 17), At(2026, 10, 29, 16))]).ToList();

        days[0].RthOpen.ShouldBe(At(2026, 10, 29, 8));
        days[0].RthClose.ShouldBe(At(2026, 10, 29, 13, 30));
    }

    [Fact]
    public void ABreakStaysInsideItsSession()
    {
        var days = DaysOf(
            Span(At(2020, 6, 1, 17), At(2020, 6, 2, 15, 15)),
            Span(At(2020, 6, 2, 15, 30), At(2020, 6, 2, 16)));

        days.Count.ShouldBe(1);
        days[0].Close.ShouldBe(At(2020, 6, 2, 16));
        days[0].Breaks.ShouldBe([new SessionBreak(At(2020, 6, 2, 15, 15), At(2020, 6, 2, 15, 30))]);
    }

    [Fact]
    public void TheStateAFileOpensWithIsNotANewOpen()
    {
        var tracker = new SessionRules.SpanTracker();
        var open = At(2026, 10, 28, 17);
        var close = At(2026, 10, 29, 16);

        tracker.Add(new StatusPoint(open, Trading, true));
        tracker.Add(new StatusPoint(open, Trading, true));
        tracker.Add(new StatusPoint(close, Close, false));

        tracker.Spans.ShouldBe([new TradingSpan(open, close)]);
    }

    [Fact]
    public void ARecordThatDoesNotSayChangesNothing()
    {
        var tracker = new SessionRules.SpanTracker();
        var open = At(2026, 10, 28, 17);
        var close = At(2026, 10, 29, 16);

        tracker.Add(new StatusPoint(open, Trading, true));
        tracker.Add(new StatusPoint(open + 1, 0, null));
        tracker.Add(new StatusPoint(close, Close, false));

        tracker.Spans.ShouldBe([new TradingSpan(open, close)]);
    }

    [Fact]
    public void ASessionStillOpenIsNotYetASpan()
    {
        var tracker = new SessionRules.SpanTracker();

        tracker.Add(new StatusPoint(At(2026, 10, 28, 17), Trading, true));

        tracker.Spans.ShouldBeEmpty();
    }

    [Fact]
    public void StatesDropRepeatsAndNoChanges()
    {
        var open = At(2026, 10, 28, 17);
        var close = At(2026, 10, 29, 16);

        StatusPoint[] points =
        [
            new(open - 1_000, PreOpen, false),
            new(open, Trading, true),
            new(open, Trading, true),
            new(open + 5, 0, true),
            new(close, Close, false),
            new(close + 1_000, PreOpen, false)
        ];

        var states = SessionRules.States(points, open, close);

        states.ShouldBe([(open, (byte)Trading), (close, (byte)Close)]);
    }

    [Fact]
    public void ASpanThatMissedItsCloseSplitsAtTheUsualHours()
    {
        var spans = Spans(Span(At(2020, 6, 29, 17), At(2020, 7, 1, 16)));

        var repaired = SessionRules.Repair(spans, NoBars)[Id];

        repaired.ShouldBe([Span(At(2020, 6, 29, 17), At(2020, 6, 30, 16)), Span(At(2020, 6, 30, 17), At(2020, 7, 1, 16))]);
    }

    [Fact]
    public void ASpanThatMissedFridaysCloseReopensOnSunday()
    {
        var spans = Spans(Span(At(2020, 6, 25, 17), At(2020, 6, 29, 16)));

        var repaired = SessionRules.Repair(spans, NoBars)[Id];

        repaired.ShouldBe([Span(At(2020, 6, 25, 17), At(2020, 6, 26, 16)), Span(At(2020, 6, 28, 17), At(2020, 6, 29, 16))]);
    }

    [Fact]
    public void BarsBeforeTheFirstSpanShowAMissedOpen()
    {
        var spans = Spans(Span(At(2020, 2, 28, 15), At(2020, 2, 28, 16)));
        var bars = Bars(new DateOnly(2020, 2, 28), At(2020, 2, 28, 2));

        var repaired = SessionRules.Repair(spans, bars)[Id];

        repaired.ShouldBe([Span(At(2020, 2, 27, 17), At(2020, 2, 28, 16))]);
    }

    [Fact]
    public void ABarJustBeforeTheOpenIsLeftAlone()
    {
        var open = At(2020, 2, 27, 17);
        var spans = Spans(Span(open, At(2020, 2, 28, 16)));
        var bars = Bars(new DateOnly(2020, 2, 28), open - 3 * Second);

        var repaired = SessionRules.Repair(spans, bars)[Id];

        repaired.ShouldBe(spans[Id]);
    }

    [Fact]
    public void ADateWithBarsAndNoSessionGetsTheUsualHours()
    {
        var spans = Spans(Span(At(2020, 2, 26, 17), At(2020, 2, 27, 16)), Span(At(2020, 2, 28, 17), At(2020, 3, 2, 16)));
        var bars = Bars(new DateOnly(2020, 2, 28), At(2020, 2, 27, 18));

        var repaired = SessionRules.Repair(spans, bars)[Id];

        repaired.ShouldContain(Span(At(2020, 2, 27, 17), At(2020, 2, 28, 16)));
    }

    [Fact]
    public void AnUnfinishedDateIsNotFilledIn()
    {
        var spans = Spans(Span(At(2026, 11, 1, 17), At(2026, 11, 2, 16)));
        var bars = Bars(new DateOnly(2026, 11, 3), At(2026, 11, 2, 18));

        var repaired = SessionRules.Repair(spans, bars)[Id];

        repaired.ShouldBe(spans[Id]);
    }

    private static Dictionary<uint, List<TradingSpan>> Spans(params TradingSpan[] spans) => new() { [Id] = [.. spans] };

    private static Dictionary<uint, Dictionary<DateOnly, long>> Bars(DateOnly date, long first) => new() { [Id] = new() { [date] = first } };

    private static List<SessionDay> DaysOf(params TradingSpan[] spans) => [.. SessionRules.Days(Es, spans)];

    private static TradingSpan Span(long start, long end) => new(start, end);

    [Fact]
    public void ASessionFrom1700To1600IsAFullEthDay()
    {
        SessionRules.NotFullEth(new DateOnly(2026, 10, 29), At(2026, 10, 28, 17), At(2026, 10, 29, 16)).ShouldBeNull();
    }

    [Fact]
    public void AMondayIsFullFromItsSundayOpen()
    {
        SessionRules.NotFullEth(new DateOnly(2026, 10, 26), At(2026, 10, 25, 17), At(2026, 10, 26, 16)).ShouldBeNull();
    }

    [Fact]
    public void TheDayClocksGoBackIsStillFull()
    {
        SessionRules.NotFullEth(new DateOnly(2026, 11, 2), At(2026, 11, 1, 17), At(2026, 11, 2, 16)).ShouldBeNull();
    }

    [Fact]
    public void AnOpenUpToFiveMinutesLateIsStillFull()
    {
        SessionRules.NotFullEth(new DateOnly(2026, 10, 29), At(2026, 10, 28, 17, 5), At(2026, 10, 29, 16)).ShouldBeNull();
    }

    [Fact]
    public void AnOpenMoreThanFiveMinutesLateIsNotFull()
    {
        SessionRules.NotFullEth(new DateOnly(2026, 10, 29), At(2026, 10, 28, 17, 6), At(2026, 10, 29, 16))
            .ShouldBe("its session opened Wed 17:06, not 17:00");
    }

    [Fact]
    public void AnEarlyCloseIsNotFull()
    {
        SessionRules.NotFullEth(new DateOnly(2026, 11, 27), At(2026, 11, 26, 17), At(2026, 11, 27, 12, 15))
            .ShouldBe("its session closed Fri 12:15, not 16:00");
    }

    [Fact]
    public void ASessionThatHasNotClosedIsNotFull()
    {
        SessionRules.NotFullEth(new DateOnly(2026, 10, 29), At(2026, 10, 28, 17), At(2026, 10, 28, 17))
            .ShouldBe("its session has not closed");
    }

    private static long At(int year, int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var utc = new DateTimeOffset(local, Chicago.GetUtcOffset(local));

        return (utc - DateTimeOffset.UnixEpoch).Ticks * 100;
    }
}
