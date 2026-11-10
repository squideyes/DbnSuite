using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

namespace DbnFetch;

/// <summary>
/// Date options, read one way everywhere: yyyy-MM-dd. Left to itself the parser reads a date in the
/// computer's own format, so 10/11/2026 would be October on one PC and November on another.
/// </summary>
internal static class Dates
{
    private const string Format = "yyyy-MM-dd";

    public static Option<DateOnly?> Optional(string name, string description) =>
        new(name) { Description = description, CustomParser = result => Parse(result) };

    public static Option<DateOnly> Required(string name, string description) =>
        new(name) { Description = description, Required = true, CustomParser = result => Parse(result) ?? default };

    private static DateOnly? Parse(ArgumentResult result)
    {
        var text = result.Tokens.Count == 1 ? result.Tokens[0].Value : "";

        if (DateOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;

        result.AddError($"\"{text}\" is not a date. Write a date as yyyy-MM-dd, such as 2026-10-30.");

        return null;
    }
}
