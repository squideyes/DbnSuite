using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbnSuite.Client.Calendars;

// The calendar file as written by hand. Every member is nullable so a missing one is reported with the
// rest of the file's problems, rather than failing the read.

internal sealed record CalendarFile
{
    public string? Id { get; init; }

    public string? TimeZone { get; init; }

    public List<string>? Roots { get; init; }

    public TemplateFile? Session { get; init; }

    public List<HolidayFile>? Holidays { get; init; }

    public List<ChangeFile>? Changes { get; init; }
}

internal sealed record TemplateFile
{
    public TimeOnly? Open { get; init; }

    public int OpenDayOffset { get; init; }

    public TimeOnly? Close { get; init; }

    public List<DayOfWeek>? TradingDays { get; init; }

    public List<DailyHaltFile>? DailyHalts { get; init; }
}

internal sealed record DailyHaltFile
{
    public TimeOnly? From { get; init; }

    public TimeOnly? To { get; init; }

    public DateOnly? Since { get; init; }

    public DateOnly? Until { get; init; }

    public string? Note { get; init; }
}

internal sealed record AnnouncementsFile
{
    public List<ChangeFile>? Changes { get; init; }
}

internal sealed record HolidayFile
{
    public string? Name { get; init; }

    /// <summary>A fixed date, <c>MM-dd</c>.</summary>
    public string? Fixed { get; init; }

    public Observance? Observed { get; init; }

    public int? Nth { get; init; }

    public DayOfWeek? Weekday { get; init; }

    public int? Month { get; init; }

    public bool? Easter { get; init; }

    public int Offset { get; init; }

    public CalendarChangeKind? Kind { get; init; }

    public TimeOnly? At { get; init; }

    public List<DayOfWeek>? Weekdays { get; init; }

    public List<int>? DayOfMonth { get; init; }

    public int? Since { get; init; }

    public int? Until { get; init; }
}

internal sealed record ChangeFile
{
    public DateOnly? TradeDate { get; init; }

    public CalendarChangeKind? Kind { get; init; }

    public DateTime? At { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public string? Note { get; init; }

    public string? Source { get; init; }

    public bool Verified { get; init; }
}

/// <summary>camelCase members and enum names, comments and trailing commas allowed, unknown members refused.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CalendarFile))]
[JsonSerializable(typeof(AnnouncementsFile))]
internal sealed partial class CalendarJson : JsonSerializerContext
{
    // Lazy, because the generated Default is set after this file's static fields.
    private static readonly Lazy<JsonSerializerOptions> strict = new(CreateStrict);

    /// <summary>The context's options, with enums read only by name.</summary>
    public static JsonSerializerOptions Strict => strict.Value;

    private static JsonSerializerOptions CreateStrict()
    {
        var options = new JsonSerializerOptions(Default.Options);

        options.Converters.Add(new JsonStringEnumConverter<DayOfWeek>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<Observance>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<CalendarChangeKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();

        return options;
    }
}
