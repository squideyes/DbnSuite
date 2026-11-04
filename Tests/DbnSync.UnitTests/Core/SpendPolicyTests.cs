using DbnSync.Core;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Core;

public sealed class SpendPolicyTests
{
    private static SpendPolicy Paid(decimal perJob = 5m, decimal perDay = 20m, decimal perMonth = 100m) =>
        new() { AllowPaidJobs = true, MaxPerJob = perJob, MaxPerDay = perDay, MaxPerMonth = perMonth };

    [Fact]
    public void The_default_spends_nothing()
    {
        var policy = new SpendPolicy();

        policy.Paid.ShouldBeFalse();
        policy.Allowance(0m, 0m).ShouldBe(0m);
    }

    [Fact]
    public void Limits_without_AllowPaidJobs_spend_nothing()
    {
        var policy = Paid();
        policy.AllowPaidJobs = false;

        policy.Allowance(0m, 0m).ShouldBe(0m);
    }

    [Theory]
    [InlineData(0, 20, 100)]
    [InlineData(5, 0, 100)]
    [InlineData(5, 20, 0)]
    public void Any_limit_left_at_zero_spends_nothing(int perJob, int perDay, int perMonth)
    {
        var policy = Paid(perJob, perDay, perMonth);

        policy.Paid.ShouldBeFalse();
        policy.Allowance(0m, 0m).ShouldBe(0m);
    }

    [Theory]
    [InlineData(0, 0, 5)]
    [InlineData(17, 17, 3)]
    [InlineData(0, 98, 2)]
    [InlineData(20, 20, 0)]
    [InlineData(0, 100, 0)]
    public void The_allowance_is_the_tightest_of_the_three_limits(int spentToday, int spentMonth, int allowance)
    {
        Paid().Allowance(spentToday, spentMonth).ShouldBe(allowance);
    }

    [Fact]
    public void Overspending_never_makes_the_allowance_negative()
    {
        Paid().Allowance(25m, 120m).ShouldBe(0m);
    }

    [Fact]
    public void The_ledger_totals_today_within_the_month()
    {
        SpendEntry[] month =
        [
            Entry(Now.AddDays(-3), 4m),
            Entry(Now, 1.5m),
            Entry(Now.AddHours(-1), 2m)
        ];

        SpendLedger.Totals(month, Today).ShouldBe((3.5m, 7.5m));
    }

    private static SpendEntry Entry(DateTimeOffset at, decimal cost) =>
        new($"{at:yyyyMMddHHmmss}", at, Root, Mbo, new DayRange(Day(10, 1), Day(10, 5)), cost);
}
