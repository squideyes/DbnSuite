using DbnSuite.Spool;
using Shouldly;

namespace DbnSuite.UnitTests.SpoolTests;

public sealed class SpoolStatusTests
{
    private const string Problem = "MES: 2026-11-05 set aside after stopping DbnSpool 2 times";

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 15, 0, 0, TimeSpan.Zero);
    private static readonly RootResult Done = new("ES", 1, 0, false);
    private static readonly RootResult Troubled = new("MES", 0, 1, false, Problem);

    [Fact]
    public void A_running_pass_shows_each_root_as_it_finishes()
    {
        var status = new SpoolStatus();

        status.Started();
        status.RootDone(Done);

        status.Snapshot.Running.ShouldBeTrue();
        status.Snapshot.ThisPass.ShouldBe([Done]);
        status.Snapshot.LastProblem.ShouldBeNull();
    }

    [Fact]
    public void A_problem_shows_before_the_pass_ends()
    {
        var status = new SpoolStatus();

        status.Started();
        status.RootDone(Troubled);
        status.RootDone(Done);

        status.Snapshot.LastProblem.ShouldBe(Problem);
    }

    [Fact]
    public void A_finished_pass_moves_to_the_last_pass()
    {
        var status = new SpoolStatus();

        status.Started();
        status.RootDone(Done);
        status.Finished(Now, [Done], null);

        status.Snapshot.ShouldSatisfyAllConditions(
            s => s.Running.ShouldBeFalse(),
            s => s.LastPassAt.ShouldBe(Now),
            s => s.LastPass.ShouldBe([Done]),
            s => s.ThisPass.ShouldBeEmpty());
    }
}
