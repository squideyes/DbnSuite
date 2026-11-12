using DbnClient;
using DbnClient.Tape;
using DbnClient.Time;
using DbnFetch.Commands;
using DbnSync.UnitTests.TapeFormatTests;
using Shouldly;

namespace DbnSync.UnitTests.FetchTests;

public sealed class TapeDatesTests
{
    private const string Dataset = "GLBX.MDP3";
    private const string Root = "ES";
    private const string December = "ESZ6";
    private const string March = "ESH7";
    private const string BadRange = "input.bad_range";

    private static readonly DateOnly Thursday = new(2026, 10, 29);
    private static readonly DateOnly Friday = new(2026, 10, 30);
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static CatalogContract Contract(uint id, string symbol, int year) => new()
    {
        InstrumentId = id,
        Symbol = symbol,
        TickRaw = 250_000_000,
        TickValue = 12.5m,
        Year = year,
        Month = 12,
        FirstTrade = 0,
        LastTrade = long.MaxValue
    };

    private static Catalog CatalogOf(params DateOnly[] dates) => new(Dataset, Root,
        [Contract(1, December, 2026), Contract(2, March, 2027)],
        [.. dates.Select(d => TapeFixture.FullDay(d))]);

    private static readonly Catalog ThreeDays = CatalogOf(Thursday, Friday, Monday);

    [Fact]
    public void A_span_inside_the_catalog_is_kept()
    {
        TapeDates.Span(Thursday, Friday, ThreeDays).Value.ShouldBe(new DateSpan(Thursday, Friday));
    }

    [Fact]
    public void From_alone_is_that_one_date()
    {
        TapeDates.Span(Friday, null, ThreeDays).Value.ShouldBe(new DateSpan(Friday, Friday));
    }

    [Fact]
    public void A_span_is_cut_to_what_the_catalog_covers()
    {
        TapeDates.Span(Thursday.AddDays(-30), Monday.AddDays(30), ThreeDays).Value.ShouldBe(new DateSpan(Thursday, Monday));
    }

    [Fact]
    public void A_span_back_to_front_is_bad_usage()
    {
        TapeDates.Span(Friday, Thursday, ThreeDays).Error!.Code.ShouldBe(BadRange);
    }

    [Fact]
    public void A_span_after_the_catalog_ends_is_an_error_naming_what_it_covers()
    {
        var span = TapeDates.Span(Monday.AddDays(1), null, ThreeDays);

        span.Error!.Code.ShouldBe(TapeDates.OutsideCatalog);
        span.Error.Message.ShouldContain("2026-10-29 to 2026-11-02");
        span.Error.Message.ShouldContain($"dbnf tape catalog {Root}");
    }

    [Fact]
    public void A_span_before_the_catalog_starts_is_an_error()
    {
        TapeDates.Span(Thursday.AddDays(-9), Thursday.AddDays(-1), ThreeDays).Error!.Code.ShouldBe(TapeDates.OutsideCatalog);
    }

    [Fact]
    public void A_catalog_with_no_dates_is_an_error()
    {
        TapeDates.Span(Friday, null, CatalogOf()).Error!.Code.ShouldBe(TapeDates.OutsideCatalog);
    }

    [Fact]
    public void Only_dates_with_a_session_are_built()
    {
        TapeDates.In(new DateSpan(Friday, Monday), ThreeDays).ShouldBe([Friday, Monday]);
    }

    [Fact]
    public void A_weekend_inside_the_catalog_has_nothing_to_build()
    {
        var weekend = TapeDates.Span(Friday.AddDays(1), Friday.AddDays(2), ThreeDays);

        weekend.IsSuccess.ShouldBeTrue();
        TapeDates.In(weekend.Value, ThreeDays).ShouldBeEmpty();
    }

    [Fact]
    public void Every_date_of_a_span_is_proved()
    {
        TapeDates.Between(new DateSpan(Friday, Monday)).Count().ShouldBe(4);
    }

    [Fact]
    public void No_symbol_named_means_every_contract()
    {
        TapeDates.Symbols(null, ThreeDays).Value.ShouldBeNull();
        TapeDates.Symbols([], ThreeDays).Value.ShouldBeNull();
    }

    [Theory]
    [InlineData("ESZ6")]
    [InlineData("ESZ26")]
    [InlineData("esz26")]
    [InlineData(" esz6 ")]
    public void A_contract_is_named_in_either_form_and_any_case(string name)
    {
        TapeDates.Symbols([name], ThreeDays).Value.ShouldBe([December]);
    }

    [Fact]
    public void Several_contracts_can_be_named()
    {
        TapeDates.Symbols(["ESZ26", "ESH7"], ThreeDays).Value.ShouldBe([December, March], ignoreOrder: true);
    }

    [Theory]
    [InlineData("ESZ16")]
    [InlineData("NQZ6")]
    public void A_contract_the_catalog_lacks_is_an_error(string name)
    {
        var symbols = TapeDates.Symbols([December, name], ThreeDays);

        symbols.Error!.Code.ShouldBe(Errors.BadSymbol);
        symbols.Error.Message.ShouldContain(name);
    }
}
