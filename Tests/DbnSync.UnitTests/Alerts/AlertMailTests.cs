using DbnSuite.Alerts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace DbnSync.UnitTests.Alerts;

public sealed class AlertMailTests
{
    private const string Source = "DbnSync";
    private const string Host = "smtp.example.com";
    private const string From = "Alerts <alerts@example.com>";
    private const string To = "you@example.com";

    private static readonly AlertItem Refused = new("databento.rejected|ES|mbo", "Databento refused (ES mbo)", "401: bad key.");
    private static readonly AlertItem Crashed = new("pass.crashed||", "Pass crashed", "S3 is down.");

    private readonly IAlertSender sender = Substitute.For<IAlertSender>();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 10, 6, 0, 0, TimeSpan.Zero));

    private AlertMail Mail(string host = Host) =>
        new(sender, Options.Create(new AlertOptions { Host = host, From = From, To = To }), clock, NullLogger<AlertMail>.Instance);

    [Fact]
    public async Task A_problem_is_emailed_with_its_title_in_the_subject()
    {
        var sent = await Mail().RaiseAsync(Source, [Refused], CancellationToken.None);

        sent.ShouldBeTrue();
        await sender.Received(1).SendAsync("DbnSync: Databento refused (ES mbo)",
            Arg.Is<string>(b => b.Contains("401: bad key.") && b.Contains("after 24 hours")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Several_problems_go_in_one_email()
    {
        await Mail().RaiseAsync(Source, [Refused, Crashed, Refused], CancellationToken.None);

        await sender.Received(1).SendAsync("DbnSync: Databento refused (ES mbo), and 1 more",
            Arg.Is<string>(b => b.Contains("S3 is down.")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_same_problem_is_not_emailed_again_within_a_day()
    {
        var mail = Mail();

        await mail.RaiseAsync(Source, [Refused], CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(23));
        var again = await mail.RaiseAsync(Source, [Refused], CancellationToken.None);

        again.ShouldBeFalse();
        await sender.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_same_problem_is_emailed_again_after_a_day()
    {
        var mail = Mail();

        await mail.RaiseAsync(Source, [Refused], CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(24));
        var again = await mail.RaiseAsync(Source, [Refused], CancellationToken.None);

        again.ShouldBeTrue();
        await sender.Received(2).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_problem_is_emailed_at_once_without_repeating_the_old_one()
    {
        var mail = Mail();

        await mail.RaiseAsync(Source, [Refused], CancellationToken.None);
        await mail.RaiseAsync(Source, [Refused, Crashed], CancellationToken.None);

        await sender.Received(1).SendAsync("DbnSync: Pass crashed",
            Arg.Is<string>(b => !b.Contains("401: bad key.")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_email_that_fails_is_tried_again_next_time()
    {
        var mail = Mail();
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("no route")), Task.CompletedTask);

        var first = await mail.RaiseAsync(Source, [Refused], CancellationToken.None);
        var second = await mail.RaiseAsync(Source, [Refused], CancellationToken.None);

        first.ShouldBeFalse();
        second.ShouldBeTrue();
    }

    [Fact]
    public async Task Nothing_is_sent_when_alerts_are_off()
    {
        var mail = Mail(host: "");

        var sent = await mail.RaiseAsync(Source, [Refused], CancellationToken.None);

        sent.ShouldBeFalse();
        mail.Describe().ShouldBe("off");
        await sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default);
    }

    [Fact]
    public async Task No_problems_send_nothing()
    {
        var sent = await Mail().RaiseAsync(Source, [], CancellationToken.None);

        sent.ShouldBeFalse();
        await sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default);
    }
}
