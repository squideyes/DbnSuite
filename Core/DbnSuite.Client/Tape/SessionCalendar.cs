namespace DbnSuite.Client.Tape;

/// <summary>
/// Reads each symbol's real session on each date from a root's catalog. Only full ETH days have
/// one here (see <see cref="Catalog.Excluded"/>), so nothing that asks makes or expects a tape
/// for a holiday or an early close.
/// </summary>
public sealed class SessionCalendar(Catalog catalog) : ISessionCalendar
{
    public SessionDay? For(string symbol, DateOnly tradeDate)
    {
        if (!catalog.IsFullDay(tradeDate))
            return null;

        if (catalog.Contract(symbol, tradeDate) is not { } contract || catalog.Day(tradeDate) is not { } day)
            return null;

        var entry = day.Contracts.FirstOrDefault(c => c.InstrumentId == contract.InstrumentId);
        var session = day.Sessions[entry?.Session ?? 0];

        if (entry is null && (contract.FirstTrade > session.Open || contract.LastTrade < session.Open))
            return null;

        // A contract can trade its own session; it too must be a whole one.
        if (SessionRules.NotFullEth(tradeDate, session.Open, session.Close) is not null)
            return null;

        var (rthOpen, rthClose) = SessionRules.Rth(catalog.Root, tradeDate, session.Open, session.Close);

        return new SessionDay(tradeDate, session.Open, session.Close, session.Breaks, rthOpen, rthClose);
    }
}
