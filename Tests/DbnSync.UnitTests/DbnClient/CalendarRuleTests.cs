using DbnClient.Contracts;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

public sealed class CalendarRuleTests
{
    private const string Gold = "GC";
    private const string MicroGold = "MGC";
    private const string Index = "ES";
    private const string Crude = "CL";
    private const string Unknown = "ZZ";
    private const string GoldMonths = "GJMQZ";

    private static readonly DateTimeOffset GoldExpires = new(2027, 2, 24, 18, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset IndexExpires = new(2026, 12, 18, 14, 30, 0, TimeSpan.Zero);

    private static Contract Outright(string symbol, DateTimeOffset expires) =>
        new(1, symbol, symbol, symbol[..^3], false, null, expires, 0.1m);

    [Theory]
    [InlineData(Gold)]
    [InlineData(MicroGold)]
    [InlineData("gc")]
    public void Gold_holds_its_five_active_months_and_counts_back_from_delivery(string root)
    {
        var rule = CalendarRule.For(root);

        rule.Months.ShouldBe(GoldMonths);
        rule.Anchor.ShouldBe(RollAnchor.DeliveryMonth);
        rule.Days.ShouldBe(RollSchedule.DefaultRollDays);
    }

    [Theory]
    [InlineData(Index)]
    [InlineData(Crude)]
    [InlineData(Unknown)]
    public void Other_roots_roll_eight_days_before_expiry_in_any_month(string root) =>
        CalendarRule.For(root).ShouldBe(CalendarRule.Default);

    [Fact]
    public void Gold_skips_its_thin_months()
    {
        var rule = CalendarRule.For(Gold);

        rule.Holds(Outright("GCG27", GoldExpires)).ShouldBeTrue();
        rule.Holds(Outright("GCH27", GoldExpires)).ShouldBeFalse();
        rule.Holds(Outright("GCV27", GoldExpires)).ShouldBeFalse();
    }

    [Fact]
    public void The_default_holds_every_month() =>
        CalendarRule.Default.Holds(Outright("CLV27", IndexExpires)).ShouldBeTrue();

    [Fact]
    public void Gold_leaves_eight_days_before_its_delivery_month_begins() =>
        CalendarRule.For(Gold).LeavesOn(Outright("GCG27", GoldExpires)).ShouldBe(new DateOnly(2027, 1, 24));

    [Fact]
    public void The_default_leaves_eight_days_before_expiry() =>
        CalendarRule.Default.LeavesOn(Outright("ESZ26", IndexExpires)).ShouldBe(new DateOnly(2026, 12, 10));

    [Fact]
    public void Roll_days_given_replace_the_rules_own_but_keep_its_anchor()
    {
        var rule = CalendarRule.For(Gold).WithDays(3);

        rule.Days.ShouldBe(3);
        rule.LeavesOn(Outright("GCG27", GoldExpires)).ShouldBe(new DateOnly(2027, 1, 29));
    }

    [Fact]
    public void No_roll_days_keeps_the_rules_own() =>
        CalendarRule.For(Gold).WithDays(null).ShouldBe(CalendarRule.For(Gold));
}
