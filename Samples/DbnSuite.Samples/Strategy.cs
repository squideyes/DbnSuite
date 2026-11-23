using DbnSuite.Client.Tape;

namespace DbnSuite.Samples;

/// <summary>A stand-in for your own strategy: it only counts what the samples hand it.</summary>
public sealed class Strategy
{
    private int quotes;
    private int trades;
    private int states;
    private int bars;
    private int tradable;

    public void OnQuote(long time, int bidPrice, uint bidSize, int askPrice, uint askSize) => quotes++;

    public void OnTrade(long time, int price, uint size, TradeSide side) => trades++;

    /// <summary>A trade in a Monte Carlo run: its own price, and its price stitched onto the run so far.</summary>
    public void OnTrade(int price, long stitched, bool canTrade)
    {
        trades++;

        if (canTrade)
            tradable++;
    }

    public void OnState(long time, byte state) => states++;

    public void OnBar(long end, int open, int high, int low, int close, long volume) => bars++;

    /// <summary>The end of a Monte Carlo run, where a real strategy would record its result. This one prints the first.</summary>
    public void OnRunEnd(int run)
    {
        if (run == 0)
            Console.WriteLine($"monte carlo, run 0: {this}");
    }

    public override string ToString() =>
        $"{quotes:N0} quotes, {trades:N0} trades ({tradable:N0} tradable), {states:N0} state changes, {bars:N0} bars";
}
