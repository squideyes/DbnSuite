using DbnSuite.Client.Contracts;
using DbnSuite.Client.Dbn;
using Shouldly;

namespace DbnSuite.UnitTests.ClientTests;

public sealed class ContractTests
{
    private const string Raw = "ESZ6";
    private const string Root = "ES";
    private const uint Instrument = 42;
    private const ushort Publisher = 1;

    private static readonly ulong Listed = DbnBytes.Utc(2025, 9, 19, 14);
    private static readonly ulong Expires = DbnBytes.Utc(2026, 12, 18, 14, 30);

    private static DefinitionRecord Definition(string raw = Raw, char instrumentClass = DefinitionRecord.Future, ushort year = 2026,
        ulong? activation = null, ulong? expiration = null, decimal? tick = 0.25m) =>
        new(Publisher, Instrument, 0, tick, expiration ?? Expires, activation ?? Listed, raw, Root, instrumentClass, year, 12);

    [Fact]
    public void A_definition_says_what_a_contract_is()
    {
        var contract = Contract.From(Definition());

        contract.ShouldBe(new Contract(Instrument, "ESZ26", Raw, Root, false,
            new DateTimeOffset(2025, 9, 19, 14, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 18, 14, 30, 0, TimeSpan.Zero),
            0.25m));
        contract.Root.ShouldBe(Root);
    }

    [Fact]
    public void A_spread_is_marked_and_named_by_both_legs()
    {
        var contract = Contract.From(Definition("ESZ6-ESH7", DefinitionRecord.Spread));

        contract.IsSpread.ShouldBeTrue();
        contract.Symbol.ShouldBe("ESZ26-ESH27");
    }

    [Fact]
    public void Without_a_maturity_year_the_expiry_decides_it()
    {
        Contract.From(Definition(year: 0)).Symbol.ShouldBe("ESZ26");
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(DbnTime.Undefined)]
    public void An_unset_time_is_no_time(ulong unset)
    {
        var contract = Contract.From(Definition(activation: unset, expiration: unset));

        contract.FirstTrade.ShouldBeNull();
        contract.Expiration.ShouldBeNull();
    }

    [Fact]
    public void With_no_year_at_all_the_nearest_year_ending_in_its_digit_from_now_is_taken()
    {
        var now = DateTime.UtcNow.Year;

        var contract = Contract.From(Definition(year: 0, expiration: DbnTime.Undefined));

        var year = 2000 + int.Parse(contract.Symbol[^2..], System.Globalization.CultureInfo.InvariantCulture);
        year.ShouldBeInRange(now, now + 9);
        (year % 10).ShouldBe(6);
    }

    [Fact]
    public void No_tick_size_stays_unknown()
    {
        Contract.From(Definition(tick: null)).TickSize.ShouldBeNull();
    }
}
