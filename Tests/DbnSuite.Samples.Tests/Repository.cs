using Shouldly;

namespace DbnSuite.Tests.Samples;

/// <summary>Where the repository is, found by walking up to DbnSuite.slnx.</summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    /// <summary>The synthetic tapes the Python client's tests use, which C# writes.</summary>
    public static string Fixtures => Path.Combine(Root, "Clients", "python", "tests", "fixtures");

    private static string FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);

        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "DbnSuite.slnx")))
            folder = folder.Parent;

        folder.ShouldNotBeNull("The repository root, with DbnSuite.slnx, was not found.");

        return folder.FullName;
    }
}

/// <summary>Tests that take over the console or the current folder, which run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}
