namespace DbnClient.Tape;

/// <summary>Reads each symbol's real session on each date from a root's catalog.</summary>
public sealed class SessionCalendar(Catalog catalog) : ISessionCalendar
{
    public SessionDay? For(string symbol, DateOnly tradeDate)
    {
        if (catalog.Contract(symbol, tradeDate) is not { } contract || catalog.Day(tradeDate) is not { } day)
            return null;

        var entry = day.Contracts.FirstOrDefault(c => c.InstrumentId == contract.InstrumentId);
        var session = day.Sessions[entry?.Session ?? 0];

        if (entry is null && (contract.FirstTrade > session.Open || contract.LastTrade < session.Open))
            return null;

        var (rthOpen, rthClose) = SessionRules.Rth(catalog.Root, tradeDate, session.Open, session.Close);

        return new SessionDay(tradeDate, session.Open, session.Close, session.Breaks, rthOpen, rthClose);
    }
}
