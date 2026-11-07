using DbnSpool;
using Shouldly;

namespace DbnSync.UnitTests.SpoolTests;

public sealed class SpoolLedgerTests
{
    private const string Seen = "20261029/20";
    private const string Build = "build-1";
    private const string NewBuild = "build-2";
    private const int MostAttempts = 2;

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
    public void Attempts_survive_the_shelf()
    {
        var ledger = new SpoolLedger();

        ledger.Begin(Date.Date, Build);

        SpoolLedger.From(ledger.ToBytes()).Attempt(Date.Date).ShouldBe(new SpoolAttempt(Date.Date, 1, Build));
    }

    [Fact]
    public void Starts_under_one_build_add_up_and_a_new_build_starts_afresh()
    {
        var ledger = new SpoolLedger();

        ledger.Begin(Date.Date, Build);
        ledger.Begin(Date.Date, Build);

        ledger.IsSetAside(Date.Date, Build, MostAttempts).ShouldBeTrue();
        ledger.IsSetAside(Date.Date, NewBuild, MostAttempts).ShouldBeFalse();

        ledger.Begin(Date.Date, NewBuild);

        ledger.Attempt(Date.Date).ShouldBe(new SpoolAttempt(Date.Date, 1, NewBuild));
    }

    [Fact]
    public void A_finished_date_has_no_attempt()
    {
        var ledger = new SpoolLedger();

        ledger.Begin(Date.Date, Build);
        ledger.End(Date.Date);

        ledger.Attempts.ShouldBeEmpty();
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
