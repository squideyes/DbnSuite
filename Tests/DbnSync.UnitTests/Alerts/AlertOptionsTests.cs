using DbnSuite.Alerts;
using MailKit.Security;
using Shouldly;

namespace DbnSync.UnitTests.Alerts;

public sealed class AlertOptionsTests
{
    private const string Host = "smtp.example.com";
    private const string From = "DbnSuite alerts <alerts@example.com>";
    private const string To = "you@example.com, them@example.com";

    [Fact]
    public void No_host_means_off_and_valid()
    {
        var options = new AlertOptions { To = To };

        options.Enabled.ShouldBeFalse();
        AlertOptions.IsValid(options).ShouldBeTrue();
    }

    [Fact]
    public void A_full_setting_is_valid()
    {
        var options = new AlertOptions { Host = Host, From = From, To = To };

        options.Enabled.ShouldBeTrue();
        AlertOptions.IsValid(options).ShouldBeTrue();
    }

    [Theory]
    [InlineData("", To)]
    [InlineData("not an address", To)]
    [InlineData(From, "not an address")]
    public void A_bad_address_is_refused(string from, string to) =>
        AlertOptions.IsValid(new AlertOptions { Host = Host, From = from, To = to }).ShouldBeFalse();

    [Fact]
    public void A_bad_port_is_refused() =>
        AlertOptions.IsValid(new AlertOptions { Host = Host, From = From, To = To, Port = 0 }).ShouldBeFalse();

    [Theory]
    [InlineData(465, SecureSocketOptions.SslOnConnect)]
    [InlineData(2465, SecureSocketOptions.SslOnConnect)]
    [InlineData(587, SecureSocketOptions.StartTls)]
    [InlineData(2587, SecureSocketOptions.StartTls)]
    [InlineData(25, SecureSocketOptions.StartTls)]
    public void Every_port_is_encrypted(int port, SecureSocketOptions expected) =>
        SmtpAlertSender.SecurityFor(port).ShouldBe(expected);
}
