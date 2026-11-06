using DbnSpool;
using Shouldly;

namespace DbnSync.UnitTests.SpoolTests;

public sealed class SpoolLedgerTests
{
    private const string Seen = "20261029/20";

    private static readonly SpooledDate Date = new(new DateOnly(2026, 10, 29), 6, true);

    [Fact]
    public void A_ledger_survives_the_shelf()
    {
        var ledger = new SpoolLedger { Seen = Seen };

        ledger.Add(Date);

        var read = SpoolLedger.From(ledger.ToBytes());

        read.Seen.ShouldBe(Seen);
        read.Spooled(Date.Date).ShouldBe(Date);
    }

    [Fact]
    public void No_object_means_an_empty_ledger()
    {
        SpoolLedger.From(null).Dates.ShouldBeEmpty();
    }

    [Fact]
    public void Each_root_has_its_own_key()
    {
        SpoolLedger.KeyOf("GLBX.MDP3", "ES").ShouldBe("GLBX.MDP3/spool/ES.json");
    }
}
