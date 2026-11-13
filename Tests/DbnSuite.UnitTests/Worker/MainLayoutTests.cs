using Bunit;
using DbnSuite.Sync.Worker.Components.Layout;
using Shouldly;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.Worker;

public sealed class MainLayoutTests : DashboardContext
{
    private const string Inside = "the page";

    private IRenderedComponent<MainLayout> Layout() =>
        Render<MainLayout>(p => p.Add(l => l.Body, Inside));

    [Fact]
    public void Wraps_the_page_with_the_navigation_and_the_notice()
    {
        var layout = Layout();

        layout.Find("main").TextContent.ShouldContain(Inside);
        layout.FindAll("nav a").Select(a => a.TextContent).ShouldBe(["Corpus", "Activity"]);
        layout.Find("footer").TextContent.ShouldContain("no official connection with Databento");
    }

    [Fact]
    public void Before_any_pass_it_says_starting()
    {
        var layout = Layout();

        layout.Find(".pass").TextContent.ShouldContain("Starting");
    }

    [Fact]
    public void Follows_a_pass_as_it_starts_and_finishes()
    {
        var layout = Layout();

        Status.Started();
        layout.WaitForAssertion(() => layout.Find(".pass").TextContent.ShouldContain("Pass running"));
        Status.Finished(null, Now.AddMinutes(5));

        layout.WaitForAssertion(() => layout.Find(".pass").TextContent.ShouldContain("Next pass in 5 min"));
    }

    [Fact]
    public void A_layout_that_has_gone_stops_listening()
    {
        var layout = Layout();

        layout.Instance.Dispose();
        Status.Started();

        layout.Find(".pass").TextContent.ShouldContain("Starting");
    }
}
