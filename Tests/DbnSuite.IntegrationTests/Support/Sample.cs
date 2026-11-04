namespace DbnSuite.IntegrationTests.Support;

/// <summary>The values scenarios share; every record they produce is generated.</summary>
public static class Sample
{
    public const string Dataset = "GLBX.MDP3";
    public const string Mnq = "MNQ";
    public const string Es = "ES";
    public const string Nq = "NQ";
    public const string Mbo = "mbo";
    public const string Ohlcv = "ohlcv-1s";
    public const string Definition = "definition";

    /// <summary>A made-up key, which only the fake gateway accepts.</summary>
    public const string ApiKey = "db-integration-tests-not-a-real-key";

    /// <summary>A Wednesday; what the plan includes runs to yesterday.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Where the dataset ends, so Tuesday 2026-11-03 is the last complete day.</summary>
    public static readonly DateTimeOffset AvailableEnd = new(2026, 11, 4, 3, 30, 0, TimeSpan.Zero);

    /// <summary>Friday: with the weekend, five days to yesterday, which is one mbo job.</summary>
    public static readonly DateOnly FirstDay = new(2026, 10, 30);

    public static readonly DateOnly Saturday = new(2026, 10, 31);
    public static readonly DateOnly Sunday = new(2026, 11, 1);
    public static readonly DateOnly Monday = new(2026, 11, 2);
    public static readonly DateOnly LastDay = new(2026, 11, 3);

    public static readonly DateOnly[] TradingDays = [FirstDay, Monday, LastDay];
}
