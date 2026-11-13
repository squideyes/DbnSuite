using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DbnSuite.UnitTests.Deploy;

/// <summary>
/// The deployment skill repeats names from Deploy/. These tests keep it from
/// drifting, and hold it to the rules that protect someone's money and keys.
/// </summary>
public sealed partial class DeploySkillTests
{
    private const string SolutionFile = "DbnSuite.slnx";
    private const string SkillFolder = ".claude/skills/dbnsync-deploy";
    private const string SkillFile = "SKILL.md";
    private const string EnvExample = "Deploy/.env.example";
    private const string PaidSwitch = "DBNSYNC_ALLOW_PAID_JOBS";

    private static readonly DirectoryInfo Root = FindRoot();

    private static DirectoryInfo FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);

        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, SolutionFile)))
            folder = folder.Parent;

        return folder!;
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root.FullName, path));

    private static IEnumerable<string> SkillTexts() =>
        Directory.EnumerateFiles(Path.Combine(Root.FullName, SkillFolder), "*.md", SearchOption.AllDirectories)
            .Select(File.ReadAllText);

    [GeneratedRegex(@"\b(?:DBNSYNC|S3|SEQ|DATABENTO|DASHBOARD)_[A-Z0-9_]*[A-Z0-9]\b")]
    private static partial Regex Setting();

    [GeneratedRegex(@"`((?:references|scripts)/[\w.-]+)`")]
    private static partial Regex BundledFile();

    [Fact]
    public void Every_setting_the_skill_names_is_in_the_example_env()
    {
        var example = Read(EnvExample);

        var missing = SkillTexts()
            .SelectMany(text => Setting().Matches(text).Select(match => match.Value))
            .Distinct()
            .Where(name => !example.Contains(name, StringComparison.Ordinal));

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Every_file_the_skill_points_to_exists()
    {
        var folder = Path.Combine(Root.FullName, SkillFolder);

        var missing = SkillTexts()
            .SelectMany(text => BundledFile().Matches(text).Select(match => match.Groups[1].Value))
            .Distinct()
            .Where(path => !File.Exists(Path.Combine(folder, path)));

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void The_skill_keeps_the_notice()
    {
        var skill = Read($"{SkillFolder}/{SkillFile}");

        skill.ShouldContain("no official connection with Databento, Inc.");
        skill.ShouldContain("licensed to receive");
        skill.ShouldContain("at their own risk");
    }

    [Fact]
    public void The_skill_leaves_spending_off()
    {
        var skill = Read($"{SkillFolder}/{SkillFile}");

        skill.ShouldContain("Never turn on spending");
        skill.ShouldNotContain($"{PaidSwitch}=true");
    }

    [Fact]
    public void The_example_env_leaves_spending_off()
    {
        var example = Read(EnvExample);

        example.ShouldContain($"# {PaidSwitch}=false");
        example.ShouldNotContain($"\n{PaidSwitch}=true");
    }
}
