using DbnSuite.Client.Contracts;
using Shouldly;

namespace DbnSuite.UnitTests.ClientTests;

public sealed class ContractSymbolTests
{
    private const int Year2026 = 2026;
    private const int Year2029 = 2029;
    private const int Year2030 = 2030;

    [Theory]
    [InlineData("ESZ6", Year2026, "ESZ26")]
    [InlineData("M2KH7", 2027, "M2KH27")]
    [InlineData("ESH0", Year2030, "ESH30")]
    public void An_outright_gets_its_maturity_years_two_digits(string raw, int year, string expected)
    {
        ContractSymbol.Expand(raw, year).ShouldBe(expected);
    }

    [Theory]
    [InlineData("ESZ6-ESH7", Year2026, "ESZ26-ESH27")]
    [InlineData("CLZ9-CLF0", Year2029, "CLZ29-CLF30")]
    public void Each_leg_of_a_spread_counts_on_from_the_one_before(string raw, int year, string expected)
    {
        ContractSymbol.Expand(raw, year).ShouldBe(expected);
    }

    [Fact]
    public void A_name_that_is_not_a_contract_is_left_alone()
    {
        const string NotAContract = "UD:1V: VT 1029467648";

        ContractSymbol.Expand(NotAContract, Year2026).ShouldBe(NotAContract);
    }

    [Theory]
    [InlineData("ESZ26")]
    [InlineData("ESZ6")]
    [InlineData("esz26")]
    public void Either_form_names_the_contract(string typed)
    {
        ContractSymbol.Matches(typed, "ESZ6", Year2026).ShouldBeTrue();
    }

    [Fact]
    public void Another_decade_is_another_contract()
    {
        ContractSymbol.Matches("ESZ16", "ESZ6", Year2026).ShouldBeFalse();
    }
}
