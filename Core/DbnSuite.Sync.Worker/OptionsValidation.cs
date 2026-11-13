using DbnSuite.Sync.Core;
using DbnSuite.Sync.Data;
using Microsoft.Extensions.Options;

namespace DbnSuite.Sync.Worker;

/// <summary>Refuses to start on a configuration that could not do what it says.</summary>
public sealed class SyncOptionsValidator : IValidateOptions<SyncOptions>
{
    private static readonly TimeSpan LongestInterval = TimeSpan.FromDays(30);

    public ValidateOptionsResult Validate(string? name, SyncOptions options) =>
        Validate(options, DatasetProfiles.Find(options.Dataset));

    /// <summary>Validates against <paramref name="profile"/>, the dataset's profile or null for none.</summary>
    public static ValidateOptionsResult Validate(SyncOptions options, IDatasetProfile? profile)
    {
        if (profile is null)
            return ValidateOptionsResult.Fail(
                $"DbnSync:Dataset '{options.Dataset}' has no profile. Supported: {string.Join(", ", DatasetProfiles.All.Keys)}.");

        var failures = new List<string>();

        if (profile.Status == ProfileStatus.Contributed && !options.AllowContributedProfile)
            failures.Add($"The {profile.Dataset} profile was contributed by {profile.Maintainer} and has never been run " +
                "by DbnSuite's maintainer. Set DbnSync:AllowContributedProfile to true to use it, at your own risk.");

        var noun = profile.GroupNoun;

        if (options.RootList.Count == 0)
            failures.Add($"DbnSync:Roots names no {noun}s.");

        if (options.SchemaList.Count == 0)
            failures.Add("DbnSync:Schemas names no schemas.");

        var roots = options.RootList
            .Concat(options.RootsBySchema.Values.SelectMany(v => v.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

        failures.AddRange(roots.Where(r => !profile.IsGroup(r.ToUpperInvariant()))
            .Select(r => $"'{r}' is not a {profile.Dataset} {noun}."));

        failures.AddRange(options.SchemaList.Concat(options.RootsBySchema.Keys)
            .Where(s => !profile.Schemas.ContainsKey(s.ToLowerInvariant()))
            .Select(s => $"'{s}' is not a schema DbnSync keeps for {profile.Dataset}: {string.Join(", ", profile.Schemas.Keys)}."));

        if (options.HistoryStart < profile.Start)
            failures.Add($"DbnSync:HistoryStart is before {profile.Dataset} begins ({profile.Start:yyyy-MM-dd}).");

        if (options.Interval < TimeSpan.FromMinutes(5) || options.BusyInterval < TimeSpan.FromMinutes(1))
            failures.Add("DbnSync:Interval must be at least 5 minutes and BusyInterval at least 1.");

        // A wait cannot be longer than about 49 days, and a month between looks would lose book days anyway.
        if (options.Interval > LongestInterval || options.BusyInterval > LongestInterval)
            failures.Add("DbnSync:Interval and BusyInterval must be at most 30 days.");

        if (string.IsNullOrWhiteSpace(options.ScratchPath))
            failures.Add("DbnSync:ScratchPath is empty; it names the folder downloads land in.");

        if (options.MaxPendingJobs is < 1 or > 500)
            failures.Add("DbnSync:MaxPendingJobs must be 1-500.");

        if (options.DownloadConcurrency is < 1 or > 16)
            failures.Add("DbnSync:DownloadConcurrency must be 1-16.");

        if (options.Depth is < 1 or > 4)
            failures.Add("DbnSync:Depth must be 1-4.");

        if (options.ReachBackTo is { } reach && reach < profile.Start)
            failures.Add($"DbnSync:ReachBackTo is before {profile.Dataset} begins ({profile.Start:yyyy-MM-dd}).");

        failures.AddRange(SpendFailures(options.Spend));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// A spend policy that is half set is refused rather than guessed at: paid
    /// jobs need every limit, and the limits must nest.
    /// </summary>
    private static IEnumerable<string> SpendFailures(SpendPolicy spend)
    {
        if (spend.MaxPerJob < 0 || spend.MaxPerDay < 0 || spend.MaxPerMonth < 0)
            yield return "DbnSync:Spend limits cannot be negative.";

        if (!spend.AllowPaidJobs)
            yield break;

        if (spend.MaxPerJob <= 0 || spend.MaxPerDay <= 0 || spend.MaxPerMonth <= 0)
            yield return "DbnSync:Spend:AllowPaidJobs is on, so MaxPerJob, MaxPerDay and MaxPerMonth must all be set above 0.";
        else if (spend.MaxPerJob > spend.MaxPerDay || spend.MaxPerDay > spend.MaxPerMonth)
            yield return "DbnSync:Spend limits must nest: MaxPerJob at most MaxPerDay, and MaxPerDay at most MaxPerMonth.";
    }

}

public sealed class StoreOptionsValidator : IValidateOptions<StoreOptions>
{
    public ValidateOptionsResult Validate(string? name, StoreOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            failures.Add("Store:ServiceUrl must be an absolute URL, e.g. http://minio:9000.");

        if (string.IsNullOrWhiteSpace(options.Bucket))
            failures.Add("Store:Bucket is required.");

        if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            failures.Add("Store:AccessKey and Store:SecretKey are required.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
