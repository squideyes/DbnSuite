using System.Reflection;
using DbnSuite.Sync.Core;
using Shouldly;

namespace DbnSuite.UnitTests.Core;

public sealed class IssueGuideTests
{
    private const string UnknownCode = "made.up";

    public static TheoryData<string> RaisedCodes()
    {
        var codes = new TheoryData<string>();

        foreach (var type in new[] { typeof(Errors), typeof(IssueCodes) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
                codes.Add((string)field.GetRawConstantValue()!);
        }

        return codes;
    }

    [Theory]
    [MemberData(nameof(RaisedCodes))]
    public void Every_code_DbnSync_raises_is_explained(string code)
    {
        var guide = IssueGuide.For(code);

        guide.Code.ShouldBe(code);
        guide.Title.ShouldNotBe(code);
        IssueGuide.All.ShouldContain(guide);
    }

    [Fact]
    public void An_unknown_code_asks_for_a_look()
    {
        var guide = IssueGuide.For(UnknownCode);

        guide.Title.ShouldBe(UnknownCode);
        guide.NeedsYou.ShouldBeTrue();
    }

    [Fact]
    public void Problems_that_resolve_themselves_do_not_ask_for_you()
    {
        IssueGuide.For(Errors.Unreachable).NeedsYou.ShouldBeFalse();
        IssueGuide.For(Errors.ServerError).NeedsYou.ShouldBeFalse();
        IssueGuide.For(IssueCodes.JobAdopted).NeedsYou.ShouldBeFalse();
        IssueGuide.For(IssueCodes.JobGone).NeedsYou.ShouldBeFalse();
        IssueGuide.For(IssueCodes.Crashed).NeedsYou.ShouldBeTrue();
    }
}
