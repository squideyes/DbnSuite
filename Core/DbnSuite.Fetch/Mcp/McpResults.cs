using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbnSuite.Fetch.Mcp;

/// <summary>Which archive the server reads and how healthy it is, as <c>dbnf doctor</c> checks it. It never holds a key.</summary>
internal sealed record ArchiveStatus
{
    /// <summary>The saved login in use; null when <c>--archive</c> names a folder.</summary>
    public string? Profile { get; init; }

    /// <summary>folder or s3; null when there is no login to say.</summary>
    public string? Kind { get; init; }

    public string? Location { get; init; }
    public string? Bucket { get; init; }
    public string? Prefix { get; init; }
    public string? Region { get; init; }
    public string? Dataset { get; init; }

    /// <summary>For an S3 login: whether an access key and its secret are saved. The key itself is never shown.</summary>
    public bool? KeySaved { get; init; }

    /// <summary>True when the archive was read: the login reaches it and its layout is one dbnf knows.</summary>
    public required bool Reachable { get; init; }

    public string? Layout { get; init; }

    /// <summary>What is wrong, when something is.</summary>
    public string? Problem { get; init; }

    /// <summary>How far this computer's clock is ahead of the archive's, in seconds; negative when behind.</summary>
    public double? ClockSkewSeconds { get; init; }

    public bool? ClockOk { get; init; }
    public string? Cache { get; init; }
    public long? CacheBytes { get; init; }
    public long? CacheFreeBytes { get; init; }
    public bool? CacheOk { get; init; }

    /// <summary>True when started with --allow-data, so show_records and bars are offered.</summary>
    public required bool DataTools { get; init; }

    /// <summary>The --out folder that export, pull and tape_pull write into; null when they are not offered.</summary>
    public string? Out { get; init; }

    public required double MaxGb { get; init; }
    public string? Tapes { get; init; }
}

internal sealed record SeriesEntry(string Root, string Schema, int Days, int NoDataDays, long Bytes, DateOnly? First, DateOnly? Last);

internal sealed record SeriesList(IReadOnlyList<SeriesEntry> Series, long TotalBytes);

/// <param name="Stored">How many days hold a file.</param>
/// <param name="NoData">Days the archive marks as having had no data: weekends, holidays and the like.</param>
/// <param name="Missing">Days in the range the archive holds nothing for: never fetched, or not yet.</param>
internal sealed record CoverageResult(
    string Root,
    string Schema,
    DateOnly From,
    DateOnly To,
    DateOnly? FirstHeld,
    DateOnly? LastHeld,
    int Stored,
    IReadOnlyList<DateOnly> NoData,
    IReadOnlyList<DateOnly> Missing);

internal sealed record ContractEntry(
    string Symbol,
    string RawSymbol,
    uint InstrumentId,
    bool Spread,
    DateTimeOffset? Listed,
    DateTimeOffset? Expires,
    decimal? TickSize);

internal sealed record ContractsResult(string Root, IReadOnlyList<ContractEntry> Contracts);

/// <param name="TradeDate">The first trade date the new contract is held.</param>
internal sealed record RollEntry(DateOnly TradeDate, string From, string To, decimal? FromSettle, decimal? ToSettle, decimal? Gap);

internal sealed record RollsResult(string Series, DateOnly From, DateOnly To, string? HeldOnFrom, IReadOnlyList<RollEntry> Rolls);

internal sealed record CalendarHalt(DateTimeOffset From, DateTimeOffset To);

/// <param name="Kind">closed, early_close, late_open, halt or regular.</param>
internal sealed record CalendarNote(string Kind, string Note, string Source);

/// <param name="Open">False on a holiday, or another weekday with no session.</param>
/// <param name="Reason">Why there is no session, when there is none.</param>
internal sealed record CalendarDay(
    DateOnly TradeDate,
    bool Open,
    string? Reason,
    DateTimeOffset? OpenTime,
    DateTimeOffset? CloseTime,
    bool EarlyClose,
    bool LateOpen,
    IReadOnlyList<CalendarHalt> Halts,
    DateTimeOffset? RthOpen,
    DateTimeOffset? RthClose,
    IReadOnlyList<CalendarNote> Notes);

internal sealed record CalendarResult(string Root, string Calendar, string Zone, IReadOnlyList<CalendarDay> Days);

/// <param name="Held">The contract the date's tapes hold: the one that traded the most the session before.</param>
/// <param name="Excluded">Why the date has no tapes, when it has none: only full ETH days do.</param>
/// <param name="HeldClearedVolume">The held contract's cleared volume that day, from the statistics.</param>
/// <param name="Tapes">The kinds of tape there are for the date: s1, trade or l1.</param>
internal sealed record CatalogDayEntry(
    DateOnly TradeDate,
    string? Held,
    bool FullDay,
    string? Excluded,
    long? HeldClearedVolume,
    IReadOnlyList<string> Tapes);

/// <param name="Source">The catalog file read, or the store key it came from.</param>
internal sealed record TapeCatalogResult(
    string Root,
    string Dataset,
    string Source,
    DateOnly? First,
    DateOnly? Last,
    int Contracts,
    IReadOnlyList<CatalogDayEntry> Days);

internal sealed record TapeInfoResult(
    string Source,
    string Symbol,
    string Root,
    string Kind,
    string Dataset,
    DateOnly TradeDate,
    uint InstrumentId,
    decimal Tick,
    DateTimeOffset SessionOpen,
    DateTimeOffset SessionClose,
    bool HasRth,
    long? RthFirstEvent,
    long? RthLastEvent,
    long Events,
    long BodyBytes,
    long StoredBytes,
    bool Compressed,
    IReadOnlyList<string> Summary);

/// <param name="Volume">Every outright contract's volume that trade date, summed.</param>
/// <param name="Leader">The outright contract that traded the most.</param>
internal sealed record VolumeDay(DateOnly TradeDate, long Volume, string Leader, long LeaderVolume);

/// <param name="Source">Where the numbers come from.</param>
/// <param name="MissingDays">UTC days whose ohlcv-1s file the archive lacks, so their volume is not counted.</param>
internal sealed record VolumeResult(string Root, DateOnly From, DateOnly To, string Source, IReadOnlyList<VolumeDay> Days,
    IReadOnlyList<DateOnly> MissingDays);

internal sealed record RecordsResult(
    string Schema,
    string Symbol,
    DateOnly Day,
    string Heading,
    IReadOnlyList<string> Records,
    long Shown,
    long Matched,
    long Total);

/// <param name="Rows">The bars, each an object of the export's columns.</param>
/// <param name="Total">How many bars the day has, of which <see cref="Rows"/> holds the first.</param>
internal sealed record BarsResult(
    string Symbol,
    DateOnly TradeDate,
    string Bars,
    string Schema,
    string Session,
    IReadOnlyList<JsonElement> Rows,
    int Total,
    bool Truncated,
    IReadOnlyList<DateOnly> MissingDays);

internal sealed record WrittenFile(string Path, long Bytes);

/// <param name="SourceBytes">The raw files the export reads, which is what the size limit weighs.</param>
/// <param name="Rows">Rows written; null on a dry run.</param>
internal sealed record ExportResult(
    string Folder,
    bool DryRun,
    long SourceBytes,
    int SourceFiles,
    long? Rows,
    long Bytes,
    IReadOnlyList<WrittenFile> Files,
    IReadOnlyList<DateOnly> MissingDays);

/// <param name="Files">Up to the first hundred files, as paths.</param>
internal sealed record PullResult(
    string Folder,
    bool DryRun,
    int Wanted,
    int AlreadyHere,
    int Downloaded,
    int NoDataDays,
    long Bytes,
    IReadOnlyList<string> Files);

/// <param name="Files">Up to the first hundred tapes copied, as paths.</param>
internal sealed record TapePullResult(
    string Folder,
    bool DryRun,
    bool CatalogCopied,
    int Copied,
    int AlreadyHere,
    long Bytes,
    IReadOnlyList<string> Files);

/// <summary>Every type the tools take or return, for Native AOT: no reflection at run time.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(DateOnly?))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ArchiveStatus))]
[JsonSerializable(typeof(SeriesList))]
[JsonSerializable(typeof(CoverageResult))]
[JsonSerializable(typeof(ContractsResult))]
[JsonSerializable(typeof(RollsResult))]
[JsonSerializable(typeof(CalendarResult))]
[JsonSerializable(typeof(TapeCatalogResult))]
[JsonSerializable(typeof(TapeInfoResult))]
[JsonSerializable(typeof(VolumeResult))]
[JsonSerializable(typeof(RecordsResult))]
[JsonSerializable(typeof(BarsResult))]
[JsonSerializable(typeof(ExportResult))]
[JsonSerializable(typeof(PullResult))]
[JsonSerializable(typeof(TapePullResult))]
internal sealed partial class McpJson : JsonSerializerContext;
