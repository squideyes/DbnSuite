using DbnSuite.Client.Tape;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

public sealed class SessionPresenceTests
{
    private const int Bid = 30_842;
    private const int Ask = 30_843;
    private const long Micro = 1_000;

    private static readonly long Open = ContractRunTests.Open;
    private static readonly long Close = ContractRunTests.Close;

    [Fact]
    public void Resting_orders_seen_only_in_snapshots_do_not_open_the_session()
    {
        byte[][] records =
        [
            ContractRunTests.Record(Open - Micro, 'R', 0, 0, 0, 'N', ContractRunTests.Snapshot),
            ContractRunTests.Record(Open - Micro, 'A', 1, Bid, 40, 'B', ContractRunTests.Snapshot),
            ContractRunTests.Record(Open + Micro, 'R', 0, 0, 0, 'N', ContractRunTests.Snapshot),
            ContractRunTests.Record(Open + Micro, 'A', 1, Bid, 40, 'B', ContractRunTests.Snapshot)
        ];

        var (opened, tape) = Both(records);

        opened.ShouldBeFalse();
        tape.ShouldBeFalse();
    }

    [Fact]
    public void An_event_in_the_session_opens_it()
    {
        byte[][] records =
        [
            ContractRunTests.Record(Open - Micro, 'A', 1, Bid, 40, 'B', ContractRunTests.Last),
            ContractRunTests.Record(Open + Micro, 'A', 2, Ask, 25, 'A', ContractRunTests.Last)
        ];

        var (opened, tape) = Both(records);

        opened.ShouldBeTrue();
        tape.ShouldBeTrue();
    }

    [Fact]
    public void Events_only_before_the_open_do_not_open_it()
    {
        byte[][] records = [ContractRunTests.Record(Open - Micro, 'A', 1, Bid, 40, 'B', ContractRunTests.Last)];

        var (opened, tape) = Both(records);

        opened.ShouldBeFalse();
        tape.ShouldBeFalse();
    }

    [Fact]
    public void An_event_at_the_close_ends_it_before_it_opened()
    {
        byte[][] records =
        [
            ContractRunTests.Record(Close, 'A', 1, Bid, 40, 'B', ContractRunTests.Last),
            ContractRunTests.Record(Open + Micro, 'A', 2, Ask, 25, 'A', ContractRunTests.Last)
        ];

        var (opened, tape) = Both(records);

        opened.ShouldBeFalse();
        tape.ShouldBeFalse();
    }

    /// <summary>What the proof decides, and whether the builder made an L1 tape, from the same records.</summary>
    private static (bool Opened, bool Tape) Both(byte[][] records)
    {
        var run = ContractRunTests.Run();
        var presence = new SessionPresence(new SessionDay(TapeFixture.TradeDate, Open, Close, [], null, null));

        foreach (var record in records)
        {
            run.Feed(record);
            presence.Feed(record);
        }

        run.Finish();

        return (presence.Opened, run.L1 is not null);
    }
}
