using DbnSuite.Alerts;
using DbnSync.Core;

namespace DbnSync.Worker;

/// <summary>
/// The issues of a pass that need a person, as alerts. Issues DbnSync deals with
/// by itself, such as Databento being unreachable for a while, are left to the dashboard.
/// </summary>
public static class PassAlerts
{
    public static IEnumerable<AlertItem> For(PassSummary pass)
    {
        foreach (var issue in pass.Issues)
        {
            var guide = IssueGuide.For(issue.Code);

            if (!guide.NeedsYou)
                continue;

            var where = string.Join(' ', new[] { issue.Root, issue.Schema }.Where(p => p is { Length: > 0 }));

            yield return new AlertItem(
                $"{issue.Code}|{issue.Root}|{issue.Schema}",
                where.Length == 0 ? guide.Title : $"{guide.Title} ({where})",
                $"{issue.Message} What to do: {guide.Action}");
        }
    }
}
