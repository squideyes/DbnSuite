using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DbnSuite.Alerts;

/// <summary>Sends one plain-text email.</summary>
public interface IAlertSender
{
    Task SendAsync(string subject, string body, CancellationToken cancel);
}

/// <summary>Sends over SMTP, always encrypted, signing in when a user name is set.</summary>
public sealed class SmtpAlertSender(IOptions<AlertOptions> options) : IAlertSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(string subject, string body, CancellationToken cancel)
    {
        var settings = options.Value;

        using var message = new MimeMessage();

        message.From.Add(MailboxAddress.Parse(settings.From));
        message.To.AddRange(InternetAddressList.Parse(settings.To));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };

        await client.ConnectAsync(settings.Host, settings.Port, SecurityFor(settings.Port), cancel).ConfigureAwait(false);

        if (settings.User.Length > 0)
            await client.AuthenticateAsync(settings.User, settings.Password, cancel).ConfigureAwait(false);

        await client.SendAsync(message, cancel).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// 465, and 2465 for hosts such as DigitalOcean that block the usual mail ports,
    /// take TLS from the start. Every other port must upgrade with STARTTLS: never plain text.
    /// </summary>
    public static SecureSocketOptions SecurityFor(int port) =>
        port is 465 or 2465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
}
