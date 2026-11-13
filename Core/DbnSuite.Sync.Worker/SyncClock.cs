namespace DbnSuite.Sync.Worker;

/// <summary>
/// The key of the <see cref="TimeProvider"/> the sync itself reads and waits
/// on: the worker, its passes, the corpus scan and the pages that show them.
/// The framework keeps the unkeyed one. Tests move the sync's clock a day or
/// a week at a time, and that must not fire the timers the framework sets,
/// such as service discovery's 10-second cleanup, beside a pass.
/// </summary>
public static class SyncClock
{
    public const string Key = "DbnSuite.Sync";
}
