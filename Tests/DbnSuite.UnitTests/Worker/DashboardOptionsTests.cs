using DbnSuite.Sync.Worker;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class DashboardOptionsTests
{
    private const string SeqUrl = "http://nas.local:5341/";

    [Fact]
    public void Without_a_logs_address_there_are_no_links()
    {
        var options = new DashboardOptions();

        options.LogsForJob(JobId).ShouldBeNull();
        options.LogsAround(Now).ShouldBeNull();
    }

    [Fact]
    public void A_job_link_filters_Seq_to_that_job()
    {
        var options = new DashboardOptions { LogsUrl = SeqUrl };

        var link = options.LogsForJob(JobId)!;

        link.ShouldStartWith("http://nas.local:5341/#/events?filter=");
        Uri.UnescapeDataString(link).ShouldEndWith($"filter=Job = '{JobId}'");
    }

    [Fact]
    public void A_link_without_a_job_covers_the_minutes_before_the_issue()
    {
        var options = new DashboardOptions { LogsUrl = SeqUrl };

        var filter = Uri.UnescapeDataString(options.LogsAround(Now)!);

        filter.ShouldContain("@Timestamp >= DateTime('2026-11-01T11:55:00.0000000Z')");
        filter.ShouldContain("@Timestamp <= DateTime('2026-11-01T12:01:00.0000000Z')");
    }
}
