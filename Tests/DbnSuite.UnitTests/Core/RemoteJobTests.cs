using DbnSuite.Sync.Core;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Core;

public sealed class RemoteJobTests
{
    private const string Continuous = "continuous";
    private const string Parent = "parent";
    private const string Front = "MNQ.n.0";
    private const string Next = "MNQ.n.1";
    private const string Csv = "csv";
    private const string OtherDataset = "XNAS.ITCH";

    private static readonly DayRange Range = new(Day(10, 28), Day(10, 30));

    private static readonly BatchRequest Request = new(Dataset, Mbo, [Front, Next], Continuous, Range);

    private static readonly RemoteJob Same = new(JobId, Now, null, Dataset, Mbo, [Front, Next], Continuous, Range, BatchRequest.Encoding);

    public static TheoryData<RemoteJob> Different() => new()
    {
        Same with { Dataset = OtherDataset },
        Same with { Schema = Trades },
        Same with { Symbols = [Front] },
        Same with { Symbols = [Front, Next, Root] },
        Same with { StypeIn = Parent },
        Same with { Range = new DayRange(Day(10, 29), Day(10, 30)) },
        Same with { Range = new DayRange(Day(10, 28), Day(10, 29)) },
        Same with { Range = null },
        Same with { Encoding = Csv }
    };

    [Fact]
    public void The_same_job_fills_the_request()
    {
        Same.Fills(Request).ShouldBeTrue();
    }

    [Fact]
    public void The_order_of_the_symbols_does_not_matter()
    {
        (Same with { Symbols = [Next, Front] }).Fills(Request).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Different))]
    public void A_job_that_differs_in_anything_asked_for_does_not_fill_the_request(RemoteJob job)
    {
        job.Fills(Request).ShouldBeFalse();
    }
}
