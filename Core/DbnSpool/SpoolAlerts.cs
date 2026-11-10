using DbnSuite.Alerts;

namespace DbnSpool;

/// <summary>A root whose pass threw, with what it said.</summary>
public sealed record RootError(string Root, string Message);

/// <summary>What a pass over every root left undone, as alerts.</summary>
public static class SpoolAlerts
{
    public static IEnumerable<AlertItem> For(IEnumerable<RootResult> results, IEnumerable<RootError> errors)
    {
        foreach (var result in results)
        {
            if (result.Problem is { } problem)
            {
                yield return new AlertItem($"set-aside|{result.Root}", $"Dates set aside ({result.Root})",
                    $"{problem}. They get no tapes until DbnSpool's code changes, when they are tried again. " +
                    "What to do: find what stopped DbnSpool on those dates in the logs, such as running out of memory.");
            }

            if (result.Failed - result.SetAside is var failed and > 0)
            {
                yield return new AlertItem($"failed|{result.Root}", $"Tapes not made ({result.Root})",
                    $"{failed} trade dates or steps failed for {result.Root}, and nothing was uploaded for them. " +
                    "They are tried again when the archive next changes. What to do: read the logs for why, " +
                    "such as a proof that failed.");
            }
        }

        foreach (var error in errors)
        {
            yield return new AlertItem($"error|{error.Root}", $"Pass failed ({error.Root})",
                $"{error.Message} What to do: read the stack trace in the logs; the other roots kept running.");
        }
    }
}
