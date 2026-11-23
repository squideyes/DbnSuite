using DbnSuite.Client;
using DbnSuite.Client.Archive;
using DbnSuite.Fetch;
using DbnSuite.Fetch.Mcp;
using Shouldly;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class ToolHostTests : IDisposable
{
    private const long Limit = 1_000;

    private readonly Dbnf dbnf = new();
    private readonly ToolHost host;
    private readonly ToolHost closed;

    public ToolHostTests()
    {
        var session = new Session { Profiles = dbnf.Profiles };
        var parse = App.Create(session).Parse(["mcp"]);

        host = new ToolHost(session, parse) { Out = dbnf.Output, MaxBytes = Limit };
        closed = new ToolHost(session, parse);
    }

    public void Dispose()
    {
        host.Dispose();
        closed.Dispose();
        dbnf.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    public void No_folder_means_the_out_folder_itself(string? folder)
    {
        host.Inside(folder).Value.ShouldBe(dbnf.Output);
    }

    [Theory]
    [InlineData("bars")]
    [InlineData("bars/es")]
    [InlineData("bars/./es")]
    public void A_relative_folder_lands_inside(string folder)
    {
        host.Inside(folder).Value.ShouldBe(Path.Combine(dbnf.Output, folder.Replace("./", "").Replace('/', Path.DirectorySeparatorChar)));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../out-and-about")]
    [InlineData("bars/../../out-and-about")]
    [InlineData("bars\\..\\..\\out-and-about")]
    [InlineData("C:out-and-about")]
    [InlineData("/etc")]
    public void A_folder_that_could_leave_is_refused(string folder)
    {
        host.Inside(folder).Error!.Code.ShouldBe(ToolHost.OutsideOut);
    }

    [Fact]
    public void A_rooted_folder_is_refused_even_inside_out()
    {
        host.Inside(Path.Combine(dbnf.Output, "bars")).Error!.Code.ShouldBe(ToolHost.OutsideOut);
    }

    [Fact]
    public void Without_out_nothing_is_inside()
    {
        closed.Inside("bars").Error!.Code.ShouldBe(ToolHost.OutsideOut);
    }

    [Fact]
    public void The_limit_allows_up_to_it_and_more_only_when_confirmed()
    {
        host.Limit(Limit, confirm: false, "pull").ShouldBeNull();
        host.Limit(Limit + 1, confirm: true, "pull").ShouldBeNull();
        host.Limit(Limit + 1, confirm: false, "pull")!.Code.ShouldBe(ToolHost.TooLarge);
    }

    [Fact]
    public void Keys_are_hidden_from_any_message()
    {
        host.Remember(new ArchiveOptions { Location = "https://s3.example.com", AccessKey = TestKey.Access, SecretKey = TestKey.Secret });

        var refused = host.Refuse(new Error(Errors.Denied, $"Refused {TestKey.Access} signed with {TestKey.Secret}."));

        refused.Message.ShouldStartWith(Errors.Denied);
        refused.Message.ShouldNotContain(TestKey.Access);
        refused.Message.ShouldNotContain(TestKey.Secret);
    }
}
