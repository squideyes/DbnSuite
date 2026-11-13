using System.CommandLine;
using System.Text;
using DbnSuite.Client.Archive;

namespace DbnSuite.Fetch.Commands;

/// <summary><c>dbnf coverage ES</c>: one root, day by day, as a grid per month.</summary>
public static class CoverageCommand
{
    private const char Stored = '#';
    private const char NoData = '.';
    private const char Missing = '-';

    public static Command Create(Session session)
    {
        var root = new Argument<string>("root") { Description = "A root, such as ES." };
        var schema = new Option<string?>("--schema") { Description = "Only this schema; every schema of the root by default." };
        var months = new Option<int>("--months") { Description = "How many recent months to draw.", DefaultValueFactory = _ => 12 };
        var all = new Option<bool>("--all") { Description = "Draw every month the archive holds." };

        var command = new Command("coverage", "Show one root day by day: stored, no data, or missing.")
        {
            root, schema, months, all
        };

        command.SetAction((parse, cancel) => session.RunAsync(parse, async archive =>
        {
            var name = parse.GetValue(root)!.ToUpperInvariant();

            if (parse.GetValue(months) < 1)
                return await session.FailAsync(new DbnSuite.Client.Error("input.bad_months", "--months is how many months to draw, so it must be 1 or more.")).ConfigureAwait(false);

            var listed = await archive.ListSeriesAsync(cancel).ConfigureAwait(false);

            if (!listed.IsSuccess)
                return await session.FailAsync(listed.Error!).ConfigureAwait(false);

            var schemas = listed.Value
                .Where(s => s.Root == name)
                .Where(s => parse.GetValue(schema) is not { } wanted || string.Equals(s.Schema, wanted, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Schema)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (schemas.Count == 0)
            {
                await session.Out.WriteLineAsync($"The archive holds nothing for {name}.").ConfigureAwait(false);

                return Session.Ok;
            }

            foreach (var s in schemas)
            {
                var days = await archive.ListDaysAsync(s, name, cancel).ConfigureAwait(false);

                if (!days.IsSuccess)
                    return await session.FailAsync(days.Error!).ConfigureAwait(false);

                await session.WriteAsync(Draw(name, s, days.Value, parse.GetValue(all) ? int.MaxValue : parse.GetValue(months))).ConfigureAwait(false);
                await session.Out.WriteLineAsync().ConfigureAwait(false);
            }

            await session.Out.WriteLineAsync($"{Stored} stored   {NoData} no data (weekend, holiday)   {Missing} not in the archive").ConfigureAwait(false);

            return Session.Ok;
        }, cancel));

        return command;
    }

    /// <summary>A header line, then one line per month from the first stored day to the last.</summary>
    internal static IEnumerable<string> Draw(string root, string schema, IReadOnlyList<StoredDay> days, int months)
    {
        if (days.Count == 0)
        {
            yield return $"{root} {schema}: nothing stored.";
            yield break;
        }

        var byDay = days.ToDictionary(d => d.Day);
        var first = days[0].Day;
        var last = days[^1].Day;
        var missing = last.DayNumber - first.DayNumber + 1 - days.Count;
        var stored = days.Count(d => !d.Empty);

        yield return Text.Invariant(
            $"{root} {schema}: {Text.Count(stored)} days stored, {Text.Count(days.Count - stored)} no data, {Text.Count(missing)} missing, {Text.Day(first)} to {Text.Day(last)}");
        yield return "         1        10        20         31";

        var start = new DateOnly(last.Year, last.Month, 1).AddMonths(1 - Math.Min(months, 12 * (last.Year - first.Year + 1)));

        if (start < new DateOnly(first.Year, first.Month, 1))
            start = new DateOnly(first.Year, first.Month, 1);

        for (var month = start; month <= last; month = month.AddMonths(1))
        {
            var line = new StringBuilder(Text.Invariant($"{month:yyyy-MM}  "));

            for (var day = month; day.Month == month.Month; day = day.AddDays(1))
            {
                line.Append(day < first || day > last
                    ? ' '
                    : byDay.TryGetValue(day, out var d) ? d.Empty ? NoData : Stored : Missing);
            }

            yield return line.ToString().TrimEnd();
        }
    }
}
