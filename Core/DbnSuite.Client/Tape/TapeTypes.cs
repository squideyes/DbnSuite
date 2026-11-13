namespace DbnSuite.Client.Tape;

/// <summary>What a tape holds. The value is the header's Kind byte.</summary>
public enum TapeKind : byte
{
    S1 = 1,
    Trade = 2,
    L1 = 3
}

/// <summary>Who crossed the spread. The value is the event's side bits.</summary>
public enum TradeSide : byte
{
    None = 0,
    Buyer = 1,
    Seller = 2
}

/// <summary>The three events an L1 tape can hold.</summary>
public enum L1Kind : byte
{
    Quote,
    Trade,
    State
}

/// <summary>Error codes for tapes that cannot be read or written.</summary>
public static class TapeErrors
{
    /// <summary>The bytes do not start with the TAPE magic number.</summary>
    public const string NotATape = "tape.not_a_tape";

    /// <summary>A real tape, of a version, kind or clock this code does not read.</summary>
    public const string Unsupported = "tape.unsupported";

    /// <summary>The tape is truncated or damaged.</summary>
    public const string Corrupt = "tape.corrupt";

    /// <summary>The writer was handed something a tape cannot hold.</summary>
    public const string Refused = "tape.refused";

    /// <summary>mbo does not hold the contract for the whole trade date, so it gets no tick tapes.</summary>
    public const string NotHeld = "tape.not_held";

    /// <summary>The tape has no events, so no file is written.</summary>
    public const string Empty = "tape.empty";

    /// <summary>The file could not be read or written.</summary>
    public const string Io = "tape.io";
}
