namespace DbnClient.Tape;

/// <summary>A stop inside a session. Times are nanoseconds since the UNIX epoch.</summary>
public sealed record SessionBreak(long Start, long End);

/// <summary>
/// One symbol's real session on one trade date, as its status records tell it.
/// Times are nanoseconds since the UNIX epoch.
/// </summary>
/// <param name="RthOpen">Null when that date has no regular session.</param>
/// <param name="RthClose">Null when that date has no regular session.</param>
public sealed record SessionDay(
    DateOnly TradeDate,
    long Open,
    long Close,
    IReadOnlyList<SessionBreak> Breaks,
    long? RthOpen,
    long? RthClose);

/// <summary>Answers when a symbol traded. This is the seam a non-CME calendar plugs into.</summary>
public interface ISessionCalendar
{
    /// <summary>The symbol's session on a trade date, or null when it has none.</summary>
    SessionDay? For(string symbol, DateOnly tradeDate);
}
