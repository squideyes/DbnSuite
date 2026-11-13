using DbnSuite.Client.Tape;
using Shouldly;

namespace DbnSuite.UnitTests.TapeFormatTests;

public sealed class TradeSecondsTests
{
    private const long Second = 1_000_000_000;
    private const long Lead = 400_000;
    private const long Price = 6_000_250_000_000;
    private const uint Size = 3;

    private static readonly long Open = ContractRunTests.Open;
    private static readonly long Close = ContractRunTests.Close;
    private static readonly SessionDay Session = new(TapeFixture.TradeDate, Open, Close, [], null, null);

    [Fact]
    public void Trades_that_match_their_bars_agree()
    {
        var seconds = new TradeSeconds(Session, 0);

        seconds.Trade(At(Open + 10 * Second), At(Open + 10 * Second), Price, Size);
        seconds.Trade(At(Open + 20 * Second), At(Open + 20 * Second), Price + 1, Size);

        seconds.Against([Bar(Open + 10 * Second, Price, Size), Bar(Open + 20 * Second, Price + 1, Size)]).ShouldBeNull();
        seconds.SessionVolume.ShouldBe(2UL * Size);
    }

    [Fact]
    public void A_trade_received_before_the_open_by_the_clocks_lead_is_compared_with_its_bar()
    {
        var seconds = new TradeSeconds(Session, Lead);
        var received = Open - Lead / 2;

        seconds.Trade(At(Open), At(received), Price, Size);

        seconds.Against([Bar(received, Price, Size)]).ShouldBeNull();
        seconds.SessionVolume.ShouldBe(Size);
    }

    [Fact]
    public void A_trade_before_the_open_received_in_its_first_second_is_in_that_seconds_bar()
    {
        var seconds = new TradeSeconds(Session, 0);

        seconds.Trade(At(Open - 1), At(Open + 1), Price, Size);
        seconds.Trade(At(Open + 2), At(Open + 2), Price, Size);

        seconds.Against([Bar(Open, Price, 2 * Size)]).ShouldBeNull();
        seconds.SessionVolume.ShouldBe(Size);
    }

    [Fact]
    public void A_bar_with_no_trades_fails()
    {
        var seconds = new TradeSeconds(Session, 0);

        seconds.Against([Bar(Open + 5 * Second, Price, Size)]).ShouldNotBeNull().ShouldStartWith("the source has a bar at");
    }

    [Fact]
    public void A_bar_of_another_volume_fails()
    {
        var seconds = new TradeSeconds(Session, 0);

        seconds.Trade(At(Open + 5 * Second), At(Open + 5 * Second), Price, Size);

        seconds.Against([Bar(Open + 5 * Second, Price, Size + 1)]).ShouldNotBeNull().ShouldStartWith("the second");
    }

    [Fact]
    public void Trades_with_no_bar_fail()
    {
        var seconds = new TradeSeconds(Session, 0);

        seconds.Trade(At(Open + 5 * Second), At(Open + 5 * Second), Price, Size);

        seconds.Against([]).ShouldBe("trades fill 1 seconds, bars 0");
    }

    private static ulong At(long nanos) => (ulong)nanos;

    private static SecondBar Bar(long nanos, long price, ulong volume) => new(nanos / Second, price, price, price, price, volume);
}
