using DbnClient;
using DbnClient.Contracts;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

public sealed class SymbolSpecTests
{
    [Theory]
    [InlineData("ES", "ES")]
    [InlineData("m2k", "M2K")]
    [InlineData("MNQ", "MNQ")]
    public void A_bare_root_asks_for_every_contract(string text, string root)
    {
        var spec = SymbolSpec.Parse(text).Value;

        spec.Kind.ShouldBe(SymbolKind.Root);
        spec.Root.ShouldBe(root);
    }

    [Theory]
    [InlineData("ESZ26", "ES")]
    [InlineData("ESZ6", "ES")]
    [InlineData("M2KZ26", "M2K")]
    [InlineData("ESZ26-ESH27", "ES")]
    public void A_contract_or_spread_names_its_root(string text, string root)
    {
        var spec = SymbolSpec.Parse(text).Value;

        spec.Kind.ShouldBe(SymbolKind.Contract);
        spec.Root.ShouldBe(root);
    }

    [Fact]
    public void A_continuous_name_carries_its_rule_and_rank()
    {
        var spec = SymbolSpec.Parse("es.V.1").Value;

        spec.Kind.ShouldBe(SymbolKind.Continuous);
        spec.Text.ShouldBe("ES.v.1");
        spec.Root.ShouldBe("ES");
        spec.Rule.ShouldBe('v');
        spec.Rank.ShouldBe(1);
    }

    [Theory]
    [InlineData("ES.x.0")]
    [InlineData("E S")]
    [InlineData("")]
    public void Anything_else_is_refused(string text)
    {
        SymbolSpec.Parse(text).Error!.Code.ShouldBe(Errors.BadSymbol);
    }
}
