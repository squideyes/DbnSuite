namespace DbnClient.Tape;

/// <summary>
/// Counts the times a file's opening snapshot left a different top of book. Only a clear
/// with a replay behind it can do that: the first clear of a replay starts from nothing.
/// </summary>
public sealed class SnapshotWatch
{
    private (decimal?, long?, decimal?, long?) before;
    private bool inSnapshot;
    private bool comparing;
    private bool started;

    public int Changes { get; private set; }

    /// <summary>Sees one record, given the top of book as it stood before that record.</summary>
    /// <param name="clear">The record clears the book, as each file's snapshot opens.</param>
    /// <param name="snapshot">The record belongs to a snapshot.</param>
    /// <param name="afterOpen">The record falls at or after the session's open.</param>
    /// <param name="top">Best bid and size, then best ask and size.</param>
    public void See(bool clear, bool snapshot, bool afterOpen, (decimal?, long?, decimal?, long?) top)
    {
        if (clear)
        {
            before = top;
            comparing = started;
        }

        if (!snapshot && inSnapshot && comparing && afterOpen && top != before)
            Changes++;

        inSnapshot = snapshot;
        started = true;
    }
}
