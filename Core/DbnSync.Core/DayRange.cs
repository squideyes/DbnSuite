using System.Globalization;
using System.Text.Json.Serialization;

namespace DbnSync.Core;

/// <summary>A run of whole UTC days, both ends inclusive.</summary>
public readonly record struct DayRange
{
    /// <remarks>
    /// Named for the serializer: a struct is otherwise built from its default
    /// constructor, and a job ledger read back with every range at 0001-01-01.
    /// </remarks>
    [JsonConstructor]
    public DayRange(DateOnly from, DateOnly upto)
    {
        if (upto < from)
        {
            throw new ArgumentOutOfRangeException(nameof(upto),
                $"{upto:yyyy-MM-dd} is before {from:yyyy-MM-dd}.");
        }

        From = from;
        Upto = upto;
    }

    public DateOnly From { get; }

    public DateOnly Upto { get; }

    public int Days => Upto.DayNumber - From.DayNumber + 1;

    public bool Contains(DateOnly day) => day >= From && day <= Upto;

    public IEnumerable<DateOnly> Each()
    {
        for (var day = From; day <= Upto; day = day.AddDays(1))
            yield return day;
    }

    /// <summary>The same run less its oldest day, or null when nothing would be left.</summary>
    public DayRange? WithoutFirstDay() =>
        Days > 1 ? new DayRange(From.AddDays(1), Upto) : null;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{From:yyyy-MM-dd}..{Upto:yyyy-MM-dd}");
}
