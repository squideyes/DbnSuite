using System.Globalization;
using DbnSuite.Client.Time;

namespace DbnSuite.Client.Export;

/// <summary>How cells become text: times in a zone and a style, prices as plain decimals.</summary>
public sealed class CellFormat(TimeZoneInfo zone, TimeMode time, bool splitTime)
{
    /// <summary>Excel counts days from 1899-12-30.</summary>
    private static readonly DateTime ExcelEpoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

    public TimeZoneInfo Zone { get; } = zone;

    public TimeMode Time { get; } = time;

    /// <summary>A time is two columns, date and time of day, as NinjaTrader and some spreadsheets want.</summary>
    public bool SplitTime { get; } = splitTime && time == TimeMode.Iso;

    /// <summary>The header names, with a split time as <c>name_date</c> and <c>name_time</c>.</summary>
    public IEnumerable<string> Names(IReadOnlyList<Column> columns) =>
        columns.SelectMany(c => c.Kind == CellKind.Time && SplitTime
            ? new[] { c.Name + "_date", c.Name + "_time" }
            : [c.Name]);

    /// <summary>The text of a cell; a split time gives two values.</summary>
    public void Write(Column column, Cell cell, List<string> values)
    {
        if (cell.IsEmpty)
        {
            values.Add("");

            if (column.Kind == CellKind.Time && SplitTime)
                values.Add("");

            return;
        }

        switch (column.Kind)
        {
            case CellKind.Time when SplitTime:
            {
                var iso = Zones.Format((ulong)cell.Number, Zone);
                values.Add(iso[..10]);
                values.Add(iso[11..29]);
                break;
            }

            case CellKind.Time:
                values.Add(TimeText((ulong)cell.Number));
                break;

            case CellKind.Price:
                values.Add(Price(cell.Price));
                break;

            case CellKind.Integer:
                values.Add(cell.Number.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                values.Add(cell.Text ?? "");
                break;
        }
    }

    public string TimeText(ulong nanos) => Time switch
    {
        TimeMode.UnixNanos => nanos.ToString(CultureInfo.InvariantCulture),
        TimeMode.UnixMillis => (nanos / 1_000_000).ToString(CultureInfo.InvariantCulture),
        TimeMode.Excel => Excel(nanos).ToString("0.###########", CultureInfo.InvariantCulture),
        _ => Zones.Format(nanos, Zone)
    };

    public static string Price(decimal? price) =>
        price is { } p ? p.ToString("0.#########", CultureInfo.InvariantCulture) : "";

    /// <summary>Whether a time is a number in this style, for formats that type their values.</summary>
    public bool TimeIsNumber => Time is TimeMode.UnixNanos or TimeMode.UnixMillis or TimeMode.Excel;

    private double Excel(ulong nanos)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddTicks((long)(nanos / 100)), Zone).DateTime;

        return (local - ExcelEpoch).TotalDays;
    }
}
