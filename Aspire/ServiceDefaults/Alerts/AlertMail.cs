using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbnSuite.Alerts;

/// <summary>A problem that needs a person. Problems with the same key are one problem.</summary>
public sealed record AlertItem(string Key, string Title, string Detail);

/// <summary>
/// Emails problems that need a person, each one at most once per <see cref="AlertOptions.Every"/>.
/// An email that can't be sent is logged and tried again next time; it never stops the caller.
/// </summary>
public sealed class AlertMail(
    IAlertSender sender,
    IOptions<AlertOptions> options,
    TimeProvider clock,
    ILogger<AlertMail> logger)
{
    private readonly Dictionary<string, DateTimeOffset> sentAt = [];
    private readonly Lock gate = new();

    /// <summary>For the startup log: where alerts go, or that they are off.</summary>
    public string Describe() =>
        options.Value.Enabled ? $"emailed to {options.Value.To} through {options.Value.Host}" : "off";

    /// <summary>Emails the items not already emailed recently, in one message. True when one was sent.</summary>
    public async Task<bool> RaiseAsync(string source, IEnumerable<AlertItem> items, CancellationToken cancel)
    {
        var settings = options.Value;

        if (!settings.Enabled)
            return false;

        var now = clock.GetUtcNow();
        AlertItem[] due;

        lock (gate)
        {
            due = [.. items.DistinctBy(i => i.Key)
                .Where(i => !sentAt.TryGetValue(i.Key, out var at) || now - at >= settings.Every)];
        }

        if (due.Length == 0)
            return false;

        var subject = due.Length == 1
            ? $"{source}: {due[0].Title}"
            : $"{source}: {due[0].Title}, and {due.Length - 1} more";

        try
        {
            await sender.SendAsync(subject, Body(source, due, settings.Every), cancel).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (!cancel.IsCancellationRequested)
                logger.LogError(e, "Could not email {Count} alerts from {Source}; trying again next time", due.Length, source);

            return false;
        }

        lock (gate)
        {
            foreach (var item in due)
                sentAt[item.Key] = now;
        }

        logger.LogInformation("Emailed {Count} alerts from {Source}: {Subject}", due.Length, source, subject);

        return true;
    }

    internal static string Body(string source, IReadOnlyList<AlertItem> items, TimeSpan every)
    {
        var text = new StringBuilder();

        text.Append(source).AppendLine(items.Count == 1 ? " met a problem that needs a look:" : " met problems that need a look:");
        text.AppendLine();

        foreach (var item in items)
        {
            text.Append("- ").AppendLine(item.Title);
            text.Append("  ").AppendLine(item.Detail);
            text.AppendLine();
        }

        text.Append("Each problem is emailed again after ").Append(Describe(every))
            .AppendLine(" if it is still there. The logs have the details.");

        return text.ToString();
    }

    private static string Describe(TimeSpan every) =>
        every.TotalHours >= 1 && every.TotalHours % 1 == 0
            ? every.TotalHours == 1 ? "an hour" : $"{every.TotalHours:0} hours"
            : every.TotalMinutes == 1 ? "a minute" : $"{every.TotalMinutes:0.#} minutes";
}
