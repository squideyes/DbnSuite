using System.Text.RegularExpressions;
using Shouldly;

namespace DbnSuite.Tests.Samples;

/// <summary>
/// The docs pages quote the samples word for word, so a sample that no longer compiles, or a page
/// edited on its own, fails here. Spacing doesn't count: a page may align code differently.
/// </summary>
public sealed partial class DocsQuoteTheSamplesTests
{
    private const string Docs = "Core/DbnSuite.Site/Pages/Docs";
    private const string CSharp = "Samples/DbnSuite.Samples";
    private const string Python = "Clients/python/examples";

    public static TheoryData<string, string, string, string> Quotes => new()
    {
        { "DbnClient.razor", "Open", CSharp, "*.cs" },
        { "DbnClient.razor", "List", CSharp, "*.cs" },
        { "DbnClient.razor", "Read", CSharp, "*.cs" },
        { "DbnClient.razor", "Export", CSharp, "*.cs" },
        { "DbnClient.razor", "Rolls", CSharp, "*.cs" },
        { "DbnClient.razor", "Errors", CSharp, "*.cs" },
        { "Tapes.razor", "Replay", CSharp, "*.cs" },
        { "Tapes.razor", "BarStream", CSharp, "*.cs" },
        { "Tapes.razor", "MonteCarlo", CSharp, "*.cs" },
        { "Python.razor", "Archive", Python, "*.py" },
        { "Python.razor", "Tapes", Python, "*.py" },
        { "Python.razor", "Sessions", Python, "*.py" },
    };

    [Theory]
    [MemberData(nameof(Quotes))]
    public void The_page_quotes_a_sample(string page, string snippet, string samples, string pattern)
    {
        var quoted = Lines(Snippet(page, snippet));
        var directives = quoted.Where(IsDirective).ToList();
        var code = quoted.Where(l => !IsDirective(l)).ToList();

        var files = Directory.EnumerateFiles(Path.Combine(Repository.Root, samples), pattern).Select(f => Lines(File.ReadAllText(f)));

        files.Any(f => Contains(f, code) && directives.All(f.Contains))
            .ShouldBeTrue($"{page}'s {snippet} is no longer in {samples}. Change the sample and the page together.");
    }

    private static string Snippet(string page, string name)
    {
        var text = File.ReadAllText(Path.Combine(Repository.Root, Docs, page));
        var match = Regex.Match(text, $"""private const string {name} = \"\"\"\r?\n(.*?)\r?\n\s*\"\"\";""", RegexOptions.Singleline);

        match.Success.ShouldBeTrue($"{page} has no snippet named {name}.");

        return match.Groups[1].Value;
    }

    /// <summary>A file's lines, with spacing closed up and blank lines left out.</summary>
    private static List<string> Lines(string text) =>
        [.. text.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0)];

    /// <summary>A using directive or a Python import, which belongs at the top of a file, not in the code quoted.</summary>
    private static bool IsDirective(string line) => Directive().IsMatch(line);

    private static bool Contains(List<string> file, List<string> code) =>
        Enumerable.Range(0, Math.Max(0, file.Count - code.Count + 1)).Any(i => file.Skip(i).Take(code.Count).SequenceEqual(code));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(using (static )?[\w.]+;|from [\w.]+ import [\w, ]+|import [\w., ]+)$")]
    private static partial Regex Directive();
}
