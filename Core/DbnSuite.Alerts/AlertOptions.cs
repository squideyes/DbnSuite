using MimeKit;

namespace DbnSuite.Alerts;

/// <summary>
/// Where alert emails go and the SMTP server that sends them. Alerts are off
/// unless both <see cref="Host"/> and <see cref="To"/> are set.
/// </summary>
public sealed class AlertOptions
{
    public const string Section = "Alerts";

    /// <summary>The SMTP server, such as smtp.resend.com.</summary>
    public string Host { get; set; } = "";

    /// <summary>465 or 2465 use TLS from the start; any other port must offer STARTTLS.</summary>
    public int Port { get; set; } = 465;

    /// <summary>The SMTP user name; empty to send without signing in.</summary>
    public string User { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>The sender, such as <c>DbnSuite alerts &lt;alerts@example.com&gt;</c>.</summary>
    public string From { get; set; } = "";

    /// <summary>One address or several, separated by commas.</summary>
    public string To { get; set; } = "";

    /// <summary>How long before the same problem is emailed again, if it is still there.</summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromHours(24);

    public bool Enabled => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(To);

    /// <summary>Off is valid; on needs a sender, at least one recipient, a port and a positive interval.</summary>
    public static bool IsValid(AlertOptions options) =>
        !options.Enabled
        || options.Port is > 0 and <= 65535
            && options.Every > TimeSpan.Zero
            && MailboxAddress.TryParse(options.From, out _)
            && InternetAddressList.TryParse(options.To, out var to)
            && to.Mailboxes.Any();
}
