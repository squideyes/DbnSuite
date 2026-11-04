using DbnSync.Core;

namespace DbnSync.UnitTests;

internal static class TestData
{
    public const string Dataset = "GLBX.MDP3";
    public const string Root = "MNQ";
    public const string OtherRoot = "MCL";
    public const string Mbo = "mbo";
    public const string Trades = "trades";
    public const string Definition = "definition";
    public const string JobId = "GLBX-20261028-UBTJS4DLA4";
    public const string OtherJobId = "GLBX-20261029-ABCDEFGHIJ";
    public const string ApiKey = "db-test-key";

    public static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public static DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    public static DateOnly Day(int month, int day) => new(2026, month, day);

    public static SchemaSpec Spec(string name) => GlbxMdp3.Instance.Schemas[name];

    /// <summary>Databento's daily-split file name for a day.</summary>
    public static string FileName(DateOnly day, string schema) =>
        $"glbx-mdp3-{day:yyyyMMdd}.{schema}.dbn.zst";
}
