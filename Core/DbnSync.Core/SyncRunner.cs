using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DbnSync.Core;

/// <summary>
/// One pass: store whatever finished, then submit whatever is owed, within
/// the spend policy, which by default allows nothing that costs money.
/// <para>
/// The pass holds no state of its own. What is held is read from the store,
/// what is in flight from the job ledger beside it, and what is owed is
/// planned fresh from both, so a pass can die anywhere and the next one picks
/// up from the archive as it stands.
/// </para>
/// </summary>
public sealed class SyncRunner(
    IDatabento databento,
    IArchiveStore store,
    SyncOptions options,
    TimeProvider clock,
    ILogger<SyncRunner> logger)
{
    /// <summary>
    /// How many leading days a perishable job may shed to get a zero quote.
    /// A window edge costs one day; anything needing more is not an edge.
    /// </summary>
    private const int MaxTrims = 3;

    /// <summary>
    /// A job still failing this long after its first failure is abandoned and
    /// its days planned again: Databento keeps a job's files for a limited time, and a job
    /// whose files are gone can never succeed.
    /// </summary>
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// How far back the spend ledger is read for an earlier purchase of a paid
    /// job. Files last 30 days from when a job finishes, and a job can queue first.
    /// </summary>
    private const int LookBackDays = 60;

    public async Task<PassSummary> RunOnceAsync(CancellationToken cancel)
    {
        var tally = new PassTally(clock.GetUtcNow(), clock);

        var ends = await databento.GetAvailableEndsAsync(options.Dataset, cancel)
            .ConfigureAwait(false);

        if (!ends.IsSuccess)
        {
            logger.LogWarning("Nothing new is asked for this pass, dataset range unavailable: {Error}", ends.Error);
            tally.Issue(IssueLevel.Error, ends.Error!.Code, $"Dataset range unavailable: {ends.Error.Message}");

            // With Databento out of reach, or refusing the key, no other call would fare better, and each
            // pending job would only add an issue of its own.
            if (ends.Error.Code is not (Errors.Malformed or Errors.ServerError))
                return tally.Finish(clock.GetUtcNow(), ends.Error.ToString());
        }

        // Collecting needs no range, and a finished job's files do not wait for ever: when it is only
        // the range that cannot be read, they are still fetched, though nothing new can be planned.
        var pending = new List<PendingJob>();

        foreach (var job in await store.ListPendingAsync(cancel).ConfigureAwait(false))
        {
            if (await CollectAsync(job, tally, cancel).ConfigureAwait(false))
                tally.JobsCollected++;
            else
                pending.Add(job);
        }

        tally.JobsPending = pending.Count;

        if (!ends.IsSuccess)
            return tally.Finish(clock.GetUtcNow(), ends.Error!.ToString());

        await SubmitAsync(ends.Value, pending, tally, cancel).ConfigureAwait(false);

        tally.JobsPending = pending.Count;

        var summary = tally.Finish(clock.GetUtcNow());

        logger.LogInformation(
            "Pass done: {Collected} collected, {Submitted} submitted, {Pending} pending, " +
            "{Owed} owed, {Files} files ({Bytes} bytes), {Empty} empty days, " +
            "{Refused} quotes refused, {Issues} issues",
            summary.JobsCollected, summary.JobsSubmitted, summary.JobsPending,
            summary.JobsOwed, summary.FilesStored, summary.BytesStored,
            summary.EmptyDays, summary.QuotesRefused, summary.Issues.Count);

        return summary;
    }

    /// <summary>Stores a finished job's files. True once the job is off the ledger.</summary>
    private async Task<bool> CollectAsync(PendingJob job, PassTally tally,
        CancellationToken cancel)
    {
        var state = await databento.GetStateAsync(job.Id, job.SubmittedAt, cancel)
            .ConfigureAwait(false);

        if (!state.IsSuccess)
        {
            logger.LogWarning("Job {Job} state unavailable: {Error}", job.Id, state.Error);
            tally.Issue(IssueLevel.Warning, state.Error!.Code, $"State unavailable: {state.Error.Message}", job);

            return false;
        }

        if (state.Value == JobState.Working)
            return false;

        if (state.Value == JobState.Gone)
        {
            logger.LogWarning("Job {Job} ({Schema} {Root} {Range}) is gone; its days will be planned again",
                job.Id, job.Schema, job.Root, job.Range);
            tally.Issue(IssueLevel.Warning, IssueCodes.JobGone,
                "Expired or unknown to Databento; its days will be planned again.", job);

            await store.DeletePendingAsync(job.Id, cancel).ConfigureAwait(false);

            return true;
        }

        if (await StoreFilesAsync(job, tally, cancel).ConfigureAwait(false))
        {
            await store.DeletePendingAsync(job.Id, cancel).ConfigureAwait(false);

            return true;
        }

        var now = clock.GetUtcNow();

        if (job.FailingSince is not { } failingSince)
        {
            // The week starts now: a job left waiting while the worker was down has not been failing.
            await store.SavePendingAsync(job with { FailingSince = now }, cancel).ConfigureAwait(false);

            return false;
        }

        if (now - failingSince > GiveUpAfter)
        {
            logger.LogError("Job {Job} has failed to store since {FailingSince}; abandoning it",
                job.Id, failingSince);
            tally.Issue(IssueLevel.Error, IssueCodes.JobAbandoned,
                $"Failed to store since {failingSince:yyyy-MM-dd HH:mm}Z; its days will be planned again.", job);

            await store.DeletePendingAsync(job.Id, cancel).ConfigureAwait(false);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Downloads every file not already held, then marks the days that had
    /// none. Empty markers are written only once every file has landed, so a
    /// failure never leaves a day marked empty that actually had data.
    /// </summary>
    private async Task<bool> StoreFilesAsync(PendingJob job, PassTally tally,
        CancellationToken cancel)
    {
        var files = await databento.ListFilesAsync(job.Id, cancel).ConfigureAwait(false);

        if (!files.IsSuccess)
        {
            logger.LogWarning("Job {Job} files unavailable: {Error}", job.Id, files.Error);
            tally.Issue(IssueLevel.Warning, files.Error!.Code, $"Files unavailable: {files.Error.Message}", job);

            return false;
        }

        // A finished job always lists its manifest, so an answer with nothing at
        // all says nothing about the days, and a day marked empty is never asked for again.
        if (files.Value.Count == 0)
        {
            logger.LogWarning("Job {Job} ({Schema} {Root} {Range}) is done but lists no files",
                job.Id, job.Schema, job.Root, job.Range);
            tally.Issue(IssueLevel.Warning, IssueCodes.NoFiles, "Done, but Databento listed no files; no day was marked empty.", job);

            return false;
        }

        var byDay = new Dictionary<DateOnly, RemoteFile>();

        foreach (var file in files.Value.Where(f => !BatchFileName.IsSupport(f.Name)))
        {
            // The name becomes a path on the scratch disk, so it must be a bare file name.
            if (Path.GetFileName(file.Name) != file.Name || BatchFileName.DayOf(file.Name) is not { } day || !job.Range.Contains(day))
            {
                logger.LogError("Job {Job} produced {File}, which is not a day in {Range}",
                    job.Id, file.Name, job.Range);
                tally.Issue(IssueLevel.Error, IssueCodes.UnexpectedFile,
                    $"Produced {file.Name}, which is not a day in {job.Range}.", job);

                return false;
            }

            byDay[day] = file;
        }

        var held = await store.ListHeldAsync(job.Schema, job.Root, cancel)
            .ConfigureAwait(false);

        var scratch = Path.Combine(options.ScratchPath, job.Id);

        Directory.CreateDirectory(scratch);

        var failed = 0;

        try
        {
            await Parallel.ForEachAsync(
                byDay.Where(pair => !held.Contains(pair.Key)),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.DownloadConcurrency,
                    CancellationToken = cancel
                },
                async (pair, token) =>
                {
                    var path = Path.Combine(scratch, pair.Value.Name);

                    try
                    {
                        var bytes = await databento.DownloadAsync(pair.Value, path, token)
                            .ConfigureAwait(false);

                        if (!bytes.IsSuccess)
                        {
                            logger.LogWarning("Job {Job} {File} not downloaded: {Error}",
                                job.Id, pair.Value.Name, bytes.Error);
                            Interlocked.Increment(ref failed);
                            tally.Issue(bytes.Error!.Code == Errors.Corrupt ? IssueLevel.Error : IssueLevel.Warning,
                                bytes.Error.Code, bytes.Error.Message, job);

                            return;
                        }

                        await store.PutFileAsync(job.Schema, job.Root, pair.Key, path, token)
                            .ConfigureAwait(false);

                        tally.Stored(bytes.Value);
                    }
                    finally
                    {
                        File.Delete(path);
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        if (failed > 0)
            return false;

        foreach (var day in job.Range.Each())
        {
            if (byDay.ContainsKey(day) || held.Contains(day))
                continue;

            await store.PutEmptyAsync(job.Schema, job.Root, day, cancel).ConfigureAwait(false);

            tally.EmptyDays++;
        }

        logger.LogInformation("Job {Job} stored: {Schema} {Root} {Range}, {Files} files",
            job.Id, job.Schema, job.Root, job.Range, byDay.Count);

        return true;
    }


    private async Task SubmitAsync(IReadOnlyDictionary<string, DateTimeOffset> ends,
        List<PendingJob> pending, PassTally tally, CancellationToken cancel)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var coverage = new List<Coverage>();
        HashSet<string> unranged = [with(StringComparer.Ordinal)];

        foreach (var (root, schema) in options.Wanted())
        {
            if (!ends.TryGetValue(schema.Name, out var end))
            {
                logger.LogWarning("{Dataset} reports no range for {Schema}; skipped",
                    options.Dataset, schema.Name);

                // Said once per schema, not once per root: otherwise the schema would stop syncing in silence.
                if (unranged.Add(schema.Name))
                {
                    tally.Issue(IssueLevel.Error, Errors.Malformed,
                        $"Databento's range for {options.Dataset} does not name {schema.Name}, so nothing of it was asked for.");
                }

                continue;
            }

            var (first, included) = options.Window(schema.Level, today);
            var last = Entitlement.LastCompleteDay(end);

            if (first > last)
                continue;

            var held = await store.ListHeldAsync(schema.Name, root, cancel).ConfigureAwait(false);

            var inFlight = pending
                .Where(p => p.Root == root && p.Schema == schema.Name)
                .Select(p => p.Range)
                .ToList();

            if (included <= last)
                coverage.Add(new Coverage(root, schema, new DayRange(included, last), held, inFlight));

            if (first < included)
            {
                var beyondUpto = included.AddDays(-1) < last ? included.AddDays(-1) : last;

                coverage.Add(new Coverage(root, schema, new DayRange(first, beyondUpto), held, inFlight,
                    BeyondPlan: true));
            }
        }

        var owed = Planner.Plan(coverage);

        // Without a paid policy nothing may be spent, and the ledger is not read.
        IReadOnlyList<SpendEntry> thisMonth = options.Spend.Paid
            ? await store.ListSpendAsync(today, cancel).ConfigureAwait(false)
            : [];

        var (spentToday, spentMonth) = SpendLedger.Totals(thisMonth, today);
        var budget = new Budget(options.Spend, spentToday, spentMonth);
        var earlier = new EarlierPurchases(databento, store, today, thisMonth, pending);
        var submitted = 0;
        var adopted = 0;
        var settled = 0;
        var refusedBeyond = 0;
        var stalled = 0;

        foreach (var request in owed)
        {
            if (pending.Count >= options.MaxPendingJobs)
                break;

            // Days older than the plan includes are asked about until one is refused; the rest wait
            // for a later pass, rather than costing a quote each, every few minutes, for nothing.
            if (request.BeyondPlan && refusedBeyond > 0)
            {
                stalled++;

                continue;
            }

            switch (await SubmitOneAsync(request, pending, tally, budget, earlier, cancel).ConfigureAwait(false))
            {
                case Submission.Submitted:
                    submitted++;
                    break;
                case Submission.Adopted:
                    adopted++;
                    break;
                case Submission.NothingToFetch:
                    settled++;
                    break;
                case Submission.OverLimit when request.BeyondPlan:
                    refusedBeyond++;
                    stalled++;
                    break;
            }
        }

        if (refusedBeyond > 0)
        {
            logger.LogInformation("Days older than the plan includes quote above the spend policy ({Policy}); " +
                "{Stalled} jobs wait for a later pass", options.Spend.Describe(), stalled);
            tally.Issue(IssueLevel.Warning, Errors.OverLimit,
                $"Days older than the plan includes quote above what the spend policy allows ({options.Spend.Describe()}), " +
                $"so {stalled} such jobs wait for a later pass.");
        }

        tally.JobsSubmitted = submitted;
        tally.JobsOwed = owed.Count - submitted - adopted - settled - stalled;
    }

    /// <summary>
    /// Submits one job within the spend policy. A perishable job quoted above
    /// it is assumed to straddle the window's edge and sheds its oldest day,
    /// at most <see cref="MaxTrims"/> times; anything else quoted above it is
    /// refused outright. Days Databento has nothing for are marked empty only
    /// when asked for one at a time; given <paramref name="emptied"/>, such a
    /// day is added to it rather than warned about on its own.
    /// </summary>
    private async Task<Submission> SubmitOneAsync(JobRequest request, List<PendingJob> pending,
        PassTally tally, Budget budget, EarlierPurchases earlier, CancellationToken cancel,
        List<DateOnly>? emptied = null)
    {
        var trims = request.Schema.Level == Level.L0 || request.BeyondPlan ? 0 : MaxTrims;

        DayRange? range = request.Range;

        while (range is { } current)
        {
            var batch = options.Profile().Request(request with { Range = current }, options.Depth);

            var id = await SubmitWithinPolicyAsync(request, batch, current, budget, earlier, tally, cancel)
                .ConfigureAwait(false);

            if (id.IsSuccess)
            {
                var job = new PendingJob(id.Value.Id, request.Root, request.Schema.Name,
                    current, id.Value.At);

                await store.SavePendingAsync(job, cancel).ConfigureAwait(false);

                pending.Add(job);

                if (id.Value.Adopted)
                {
                    logger.LogWarning("Took up {Job}: {Schema} {Root} {Range} was already at Databento, " +
                        "unknown to the job ledger, so it was not bought again", job.Id, job.Schema, job.Root, job.Range);
                    tally.Issue(IssueLevel.Warning, IssueCodes.JobAdopted,
                        "Already at Databento and unknown to the job ledger, so taken up and not bought again.", job);

                    return Submission.Adopted;
                }

                logger.LogInformation("Submitted {Job}: {Schema} {Root} {Range}",
                    job.Id, job.Schema, job.Root, job.Range);

                return Submission.Submitted;
            }

            if (id.Error!.Code is Errors.NoSymbols or Errors.NoData)
            {
                // One answer is trusted to settle one day only: a wrong one must not empty a week.
                if (current.Days > 1)
                {
                    return await AskEachDayAsync(request with { Range = current }, id.Error.Code, pending, tally,
                        budget, earlier, cancel).ConfigureAwait(false);
                }

                await MarkEmptyAsync(request, current, tally, cancel).ConfigureAwait(false);

                if (emptied is not null)
                {
                    emptied.Add(current.From);

                    return Submission.NothingToFetch;
                }

                logger.LogWarning("{Schema} {Root} {Range}: Databento has nothing for it ({Code}), so its days hold no data",
                    request.Schema.Name, request.Root, current, id.Error.Code);
                tally.Issue(IssueLevel.Warning, id.Error.Code, id.Error.Code == Errors.NoSymbols
                        ? $"No {request.Root} instruments existed on these days, so they are marked as having no data."
                        : $"Databento has no {request.Schema.Name} records for {request.Root} on these days, so they are marked as having no data.",
                    request, current);

                return Submission.NothingToFetch;
            }

            if (id.Error.Code != Errors.OverLimit)
            {
                logger.LogWarning("{Schema} {Root} {Range} not submitted: {Error}",
                    request.Schema.Name, request.Root, current, id.Error);
                tally.Issue(id.Error.Code is Errors.Unreachable or Errors.ServerError ? IssueLevel.Warning : IssueLevel.Error,
                    id.Error.Code, $"Not submitted: {id.Error.Message}", request, current);

                return Submission.NotSubmitted;
            }

            tally.QuotesRefused++;

            if (request.BeyondPlan)
                return Submission.OverLimit;

            if (trims-- == 0)
            {
                logger.LogWarning("{Schema} {Root} {Range} quotes above the spend policy and was not submitted: {Error}",
                    request.Schema.Name, request.Root, current, id.Error);
                tally.Issue(IssueLevel.Warning, Errors.OverLimit,
                    $"Over the spend limit, so not submitted: {id.Error.Message}", request, current);

                return Submission.OverLimit;
            }

            range = current.WithoutFirstDay();
        }

        return Submission.NotSubmitted;
    }

    /// <summary>
    /// After Databento said it has nothing for several days, asks for each day
    /// alone, so only a day it says so for on its own is marked empty; a day it
    /// does have is submitted as a job of its own. The days marked empty are one
    /// warning. Days not reached, because enough jobs are pending, stay owed.
    /// </summary>
    private async Task<Submission> AskEachDayAsync(JobRequest request, string code, List<PendingJob> pending,
        PassTally tally, Budget budget, EarlierPurchases earlier, CancellationToken cancel)
    {
        var emptied = new List<DateOnly>();
        var outcomes = new List<Submission>();

        foreach (var day in request.Range.Each())
        {
            if (pending.Count >= options.MaxPendingJobs)
                break;

            outcomes.Add(await SubmitOneAsync(request with { Range = new DayRange(day, day) }, pending, tally,
                budget, earlier, cancel, emptied).ConfigureAwait(false));
        }

        if (emptied.Count > 0)
        {
            var range = new DayRange(emptied[0], emptied[^1]);

            logger.LogWarning("{Schema} {Root} {Range}: Databento has nothing for {Days} days asked for one at a time " +
                "({Code}), so they hold no data", request.Schema.Name, request.Root, range, emptied.Count, code);
            tally.Issue(IssueLevel.Warning, code, code == Errors.NoSymbols
                    ? $"No {request.Root} instruments existed on {emptied.Count} of these days, each asked for alone, so they are marked as having no data."
                    : $"Databento has no {request.Schema.Name} records for {request.Root} on {emptied.Count} of these days, each asked for alone, so they are marked as having no data.",
                request, range);
        }

        if (outcomes.Contains(Submission.Submitted))
            return Submission.Submitted;

        if (outcomes.Contains(Submission.Adopted))
            return Submission.Adopted;

        return outcomes.Count == request.Range.Days && outcomes.TrueForAll(o => o == Submission.NothingToFetch)
            ? Submission.NothingToFetch
            : Submission.NotSubmitted;
    }

    /// <summary>
    /// Submits at $0 unless the policy allows paying and the job is quoted above
    /// $0. If the spend ledger shows an earlier purchase of a paid job, the job is
    /// first looked for at Databento: if the process died after Databento accepted
    /// it, the job ledger has lost it, and it is taken up rather than bought twice.
    /// If Databento can't say, this job is not bought.
    /// Otherwise it is checked against what is left, and written to the spend
    /// ledger <em>before</em> it is submitted, with Databento asked to take it
    /// only at that quote or less. The entry is withdrawn only when Databento
    /// definitely refused the job; any other failure leaves it counted.
    /// </summary>
    private async Task<Result<Placed>> SubmitWithinPolicyAsync(JobRequest request, BatchRequest batch,
        DayRange range, Budget budget, EarlierPurchases earlier, PassTally tally, CancellationToken cancel)
    {
        if (!options.Spend.Paid)
            return await SubmitFreeAsync(batch, cancel).ConfigureAwait(false);

        var quote = await databento.QuoteAsync(batch, cancel).ConfigureAwait(false);

        if (!quote.IsSuccess)
            return quote.Error!;

        if (quote.Value == 0m)
            return await SubmitFreeAsync(batch, cancel).ConfigureAwait(false);

        var held = await earlier.FindAsync(request, range, cancel).ConfigureAwait(false);

        if (!held.IsSuccess)
            return held.Error!;

        var now = clock.GetUtcNow();

        // Before the limits: what was paid for is taken up even with nothing left to spend.
        if (held.Value.FirstOrDefault(j => j.Fills(batch) && !(j.ExpiresAt <= now)) is { } found)
            return new Placed(found.Id, found.ReceivedAt, Adopted: true);

        var allowance = budget.Allowance;

        if (quote.Value > allowance)
        {
            return new Error(Errors.OverLimit, string.Create(CultureInfo.InvariantCulture,
                $"{batch.Schema} {string.Join(',', batch.Symbols)} {range} quotes ${quote.Value}, over the ${allowance} the spend policy allows now."));
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{now.UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}")[..26];
        var entry = new SpendEntry(key, now, request.Root, request.Schema.Name, range, quote.Value);

        await store.SaveSpendAsync(entry, cancel).ConfigureAwait(false);

        var id = await databento.SubmitWithinAsync(batch, quote.Value, cancel).ConfigureAwait(false);

        if (!id.IsSuccess)
        {
            if (id.Error!.Code is Errors.OverLimit or Errors.Rejected or Errors.NoSymbols or Errors.NoData)
                await store.DeleteSpendAsync(entry, cancel).ConfigureAwait(false);
            else
                budget.Spend(entry.Cost);

            return id.Error;
        }

        await store.SaveSpendAsync(entry with { JobId = id.Value }, cancel).ConfigureAwait(false);

        budget.Spend(entry.Cost);
        tally.Spent += entry.Cost;

        logger.LogWarning("Paid job {Job}: {Schema} {Root} {Range} quoted ${Cost}; {Policy}",
            id.Value, request.Schema.Name, request.Root, range, entry.Cost, options.Spend.Describe());

        return new Placed(id.Value, clock.GetUtcNow());
    }

    private async Task<Result<Placed>> SubmitFreeAsync(BatchRequest batch, CancellationToken cancel)
    {
        var id = await databento.SubmitWithinAsync(batch, 0m, cancel).ConfigureAwait(false);

        return id.IsSuccess ? new Placed(id.Value, clock.GetUtcNow()) : id.Error!;
    }

    /// <summary>A job Databento has: submitted just now, or found there and taken up.</summary>
    private sealed record Placed(string Id, DateTimeOffset At, bool Adopted = false);

    /// <summary>
    /// What may be an earlier purchase of a paid job. Every purchase is in the
    /// spend ledger before it is submitted, and leaves only on a definite refusal,
    /// so a job with no entry cannot have been bought and Databento is not asked
    /// about it. An entry whose job is in the job ledger needs no asking either.
    /// The ledger is read once a pass, and each listing, failure or not, is kept
    /// for the pass, so a failure stops only the jobs that needed that listing.
    /// </summary>
    private sealed class EarlierPurchases(IDatabento databento, IArchiveStore store, DateOnly today,
        IReadOnlyList<SpendEntry> thisMonth, IReadOnlyList<PendingJob> ledger)
    {
        private static readonly Result<IReadOnlyList<RemoteJob>> None = (Result<IReadOnlyList<RemoteJob>>)Array.Empty<RemoteJob>();

        private readonly Dictionary<DateOnly, Result<IReadOnlyList<RemoteJob>>> listings = [];

        private List<SpendEntry>? entries;

        /// <summary>The jobs Databento holds that the job ledger doesn't, from the earliest entry for this job on; none if it has no entry.</summary>
        public async Task<Result<IReadOnlyList<RemoteJob>>> FindAsync(JobRequest request, DayRange range,
            CancellationToken cancel)
        {
            entries ??= await ReadAsync(cancel).ConfigureAwait(false);

            var known = ledger.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);

            var matching = entries
                .Where(e => e.Root == request.Root && e.Schema == request.Schema.Name && e.Range == range)
                .Where(e => e.JobId is null || !known.Contains(e.JobId))
                .ToList();

            if (matching.Count == 0)
                return None;

            // A day early: our clock may run ahead of Databento's across UTC midnight.
            var since = DateOnly.FromDateTime(matching.Min(e => e.At).UtcDateTime).AddDays(-1);

            if (!listings.TryGetValue(since, out var jobs))
                listings[since] = jobs = await databento.ListJobsAsync(since, known, cancel).ConfigureAwait(false);

            return jobs;
        }

        /// <summary>Every entry of the look-back; this month's were read for the budget.</summary>
        private async Task<List<SpendEntry>> ReadAsync(CancellationToken cancel)
        {
            var from = today.AddDays(-LookBackDays);
            var thisMonthStarts = new DateOnly(today.Year, today.Month, 1);
            var read = new List<SpendEntry>(thisMonth);

            for (var month = new DateOnly(from.Year, from.Month, 1); month < thisMonthStarts; month = month.AddMonths(1))
                read.AddRange(await store.ListSpendAsync(month, cancel).ConfigureAwait(false));

            return [.. read.Where(e => DateOnly.FromDateTime(e.At.UtcDateTime) >= from)];
        }
    }

    /// <summary>What the policy leaves to spend, as the pass goes.</summary>
    private sealed class Budget(SpendPolicy policy, decimal spentToday, decimal spentThisMonth)
    {
        private decimal today = spentToday;
        private decimal month = spentThisMonth;

        public decimal Allowance => policy.Allowance(today, month);

        public void Spend(decimal cost)
        {
            today += cost;
            month += cost;
        }
    }

    /// <summary>
    /// Records every day of a range as asked for and empty, exactly as a job
    /// that came back with no files would, so no later pass asks again.
    /// </summary>
    private async Task MarkEmptyAsync(JobRequest request, DayRange range, PassTally tally,
        CancellationToken cancel)
    {
        foreach (var day in range.Each())
        {
            await store.PutEmptyAsync(request.Schema.Name, request.Root, day, cancel).ConfigureAwait(false);

            tally.EmptyDays++;
        }
    }

    private enum Submission
    {
        Submitted,

        /// <summary>Already at Databento and unknown to the job ledger: taken up, not submitted.</summary>
        Adopted,

        /// <summary>Not submitted, and owed still: a later pass tries again.</summary>
        NotSubmitted,

        /// <summary>Not submitted because there is nothing to fetch: the days are settled.</summary>
        NothingToFetch,

        /// <summary>Quoted above the spend policy, even after shedding days.</summary>
        OverLimit
    }
}
