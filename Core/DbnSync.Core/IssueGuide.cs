namespace DbnSync.Core;

/// <summary>Issue codes the sync pass raises itself; Databento's are in <see cref="Errors"/>.</summary>
public static class IssueCodes
{
    public const string JobGone = "job.gone";
    public const string NoFiles = "job.no_files";
    public const string JobAbandoned = "job.abandoned";
    public const string JobAdopted = "job.adopted";
    public const string UnexpectedFile = "job.unexpected_file";
    public const string Crashed = "pass.crashed";
}

/// <summary>
/// What an issue code means for the archive, in plain words: what happened,
/// what DbnSync does about it by itself, and what, if anything, needs a person.
/// </summary>
public sealed record IssueGuide(string Code, string Title, string Meaning, string Next, string Action, bool NeedsYou)
{
    private static readonly Dictionary<string, IssueGuide> Known = new IssueGuide[]
    {
        new(Errors.OverLimit, "Over the spend limit",
            "Databento quoted more for this job than the spend policy allows, so it was not submitted. By default the policy allows nothing but $0. A book job at the edge of its one-month window sheds its oldest day and is quoted again, up to three times, before it is skipped.",
            "The next pass plans the same days again. Days that leave the plan's window unstored become Lost. Days older than the plan includes wait, and are asked about again on a later pass.",
            "Nothing, if you mean to spend nothing. Otherwise see the spend policy in the documentation, and read its caveats first: paid jobs are billed by Databento, at your own risk.",
            NeedsYou: false),
        new(Errors.NotFree, "Not free",
            "Databento quoted more than $0 for this job, so it was not submitted. A book job at the edge of its one-month window sheds its oldest day and is quoted again, up to three times, before it is skipped.",
            "The next pass plans the same days again. Days that have aged out of the free window become Lost.",
            "Nothing, unless the same days are refused pass after pass. Then they are outside what the plan makes free: check HistoryStart and the plan's windows.",
            NeedsYou: false),
        new(IssueCodes.NoFiles, "No files listed",
            "Databento said this job was done but listed no files at all. Even a job with no data lists its manifest, so the answer can't be trusted, and no day was marked as having no data.",
            "The job stays pending and is asked about again on the next pass. If it never lists its files, it is abandoned and its days are planned again.",
            "Nothing, unless it repeats for days.",
            NeedsYou: false),
        new(Errors.Unreachable, "Databento unreachable",
            "The request got no answer at all: a network failure, a DNS failure or a timeout.",
            "Nothing is lost. The next pass asks again, and owed days wait; a book day has a month before it ages out.",
            "Nothing, unless it repeats for hours. Then check the NAS's internet connection.",
            NeedsYou: false),
        new(Errors.ServerError, "Databento had a server error",
            "Databento answered with a 5xx: a failure on its side. The request may still have taken effect, so a paid job stays counted in the spend ledger.",
            "Nothing is lost. The next pass asks again, and owed days wait; a book day has a month before it ages out.",
            "Nothing, unless it repeats for hours. Then check Databento's status page.",
            NeedsYou: false),
        new(Errors.Rejected, "Databento refused",
            "Databento answered with an error. The message starts with its HTTP status and quotes its reply.",
            "The next pass tries again.",
            "401 or 403: the API key is wrong or the subscription has lapsed, so fix the key. 400 or 422: DbnSync sent a request Databento will not take, which needs a code fix. 429: Databento's rate limit, and it passes.",
            NeedsYou: true),
        new(Errors.NoSymbols, "Not listed yet",
            "None of the instruments asked for existed on these days: the root had not been listed yet. MCL, for one, began trading in July 2021.",
            "When several days were asked for at once, each is asked for again on its own. Only a day Databento has nothing for when asked alone is marked as having no data, like weekends, and not asked for again; a day it does have is fetched.",
            "Nothing.",
            NeedsYou: false),
        new(Errors.NoData, "No records",
            "The instruments existed, but Databento holds no records of this schema for them on these days, usually the weeks between a root's listing and its first trade.",
            "When several days were asked for at once, each is asked for again on its own. Only a day Databento has nothing for when asked alone is marked as having no data, like weekends, and not asked for again; a day it does have is fetched.",
            "Nothing.",
            NeedsYou: false),
        new(Errors.Malformed, "Unexpected answer",
            "Databento answered, but not in the shape DbnSync expects.",
            "The next pass tries again.",
            "If it repeats, Databento has changed its API and DbnSync needs an update.",
            NeedsYou: true),
        new(Errors.Corrupt, "Download failed its check",
            "A downloaded file's size or SHA-256 did not match what Databento listed, so it was thrown away and nothing was stored.",
            "The next pass downloads the file again.",
            "Nothing, unless the same file fails every time. Then suspect the scratch disk or something on the network rewriting downloads.",
            NeedsYou: false),
        new(IssueCodes.JobGone, "Job gone",
            "Databento no longer knows this job, usually because its files expired before they were collected.",
            "The job's days are planned again. Days still inside the free window are asked for anew; any that have aged out become Lost.",
            "Nothing.",
            NeedsYou: false),
        new(IssueCodes.JobAbandoned, "Job abandoned",
            "The job could not be stored for seven days, so DbnSync stopped trying it.",
            "Its days are planned again as a new job.",
            "Look at this job's earlier issues to see what kept failing.",
            NeedsYou: true),
        new(IssueCodes.JobAdopted, "Job taken up",
            "Before paying for a job, DbnSync found the very same job at Databento that its job ledger did not know, most likely because it stopped just after Databento accepted it. It took that job up instead of buying it again.",
            "The job is collected as usual. Nothing new was spent; what the spend ledger already counts for it stays counted.",
            "Nothing.",
            NeedsYou: false),
        new(IssueCodes.UnexpectedFile, "Unexpected file",
            "The job delivered a file for a day outside the days it asked for, so nothing from it was stored.",
            "The next pass tries again; after seven days the job is abandoned and its days planned again.",
            "This is a mismatch between DbnSync and Databento's file naming, and needs a code fix.",
            NeedsYou: true),
        new(IssueCodes.Crashed, "Pass crashed",
            "Something unexpected stopped the whole pass. Most often the S3 server is down, full, or refusing the keys.",
            "A new pass starts after the busy interval.",
            "Read the stack trace in the logs. If it names S3, check the S3 container, its disk and the keys.",
            NeedsYou: true),
    }.ToDictionary(g => g.Code);

    public static IReadOnlyCollection<IssueGuide> All => Known.Values;

    public static IssueGuide For(string code) =>
        Known.TryGetValue(code, out var guide)
            ? guide
            : new(code, code, "A code this version of DbnSync has no description for.",
                "Unknown.", "Read the message and the logs.", NeedsYou: true);
}
