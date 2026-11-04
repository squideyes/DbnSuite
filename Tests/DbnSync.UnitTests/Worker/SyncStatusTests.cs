using DbnSync.Core;
using DbnSync.Worker;
using Shouldly;
using static DbnSync.UnitTests.TestData;

namespace DbnSync.UnitTests.Worker;

public sealed class SyncStatusTests
{
    private static readonly DateTimeOffset NextPass = Now.AddMinutes(5);

    private static readonly PassSummary Pass = new(Now, Now, 1, 0, 0, 0, 0, 0, 0, 0, 0, null, []);

    private readonly SyncOptions options = new() { Roots = $"{Root}, {OtherRoot}", Schemas = $"{Mbo},{Definition}" };

    [Fact]
    public void Before_any_pass_it_shows_what_is_kept_and_the_policy()
    {
        var status = new SyncStatus(options);

        var snapshot = status.Snapshot;

        snapshot.Running.ShouldBeFalse();
        snapshot.NextPassAt.ShouldBeNull();
        snapshot.LastPass.ShouldBeNull();
        snapshot.Roots.ShouldBe([Root, OtherRoot]);
        snapshot.Schemas.ShouldBe([Mbo, Definition]);
        snapshot.SpendPolicy.ShouldBe(options.Spend.Describe());
    }

    [Fact]
    public void A_pass_that_starts_clears_the_next_pass()
    {
        var status = new SyncStatus(options);
        status.Finished(Pass, NextPass);

        status.Started();

        status.Snapshot.Running.ShouldBeTrue();
        status.Snapshot.NextPassAt.ShouldBeNull();
        status.Snapshot.LastPass.ShouldBe(Pass);
    }

    [Fact]
    public void A_pass_that_finishes_is_kept_with_when_the_next_one_starts()
    {
        var status = new SyncStatus(options);
        status.Started();

        status.Finished(Pass, NextPass);

        status.Snapshot.Running.ShouldBeFalse();
        status.Snapshot.LastPass.ShouldBe(Pass);
        status.Snapshot.NextPassAt.ShouldBe(NextPass);
    }

    [Fact]
    public void Finishing_with_no_pass_keeps_the_last_one()
    {
        var status = new SyncStatus(options);
        status.Finished(Pass, NextPass);

        status.Finished(null, NextPass.AddMinutes(5));

        status.Snapshot.LastPass.ShouldBe(Pass);
    }

    [Fact]
    public void Listeners_hear_each_start_and_finish()
    {
        var status = new SyncStatus(options);
        var heard = 0;
        status.Changed += () => heard++;

        status.Started();
        status.Finished(Pass, NextPass);

        heard.ShouldBe(2);
    }
}
