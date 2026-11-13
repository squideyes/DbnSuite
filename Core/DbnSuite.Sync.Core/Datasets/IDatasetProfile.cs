namespace DbnSuite.Sync.Core;

/// <summary>How far a dataset profile has been tested, and by whom.</summary>
public enum ProfileStatus
{
    /// <summary>The maintainer runs it against a real subscription to the dataset, every day.</summary>
    Verified,

    /// <summary>
    /// Written and run by someone else, against their own subscription. The
    /// maintainer has not run it and cannot: DbnSync starts on one only when
    /// <see cref="SyncOptions.AllowContributedProfile"/> is set.
    /// </summary>
    Contributed
}

/// <summary>
/// What DbnSync needs to know about one Databento dataset: which schemas it
/// keeps, how a group of instruments is asked for, and how far back the plan
/// includes each level. Everything else (planning, the spend policy, the
/// ledger, the archive) is the same for every dataset.
/// <para>
/// A profile is code in this repository, not a plugin: adding a dataset means
/// a class here, registered in <see cref="DatasetProfiles"/>, that passes the
/// conformance tests. Databento's files are never committed, since their
/// licence does not allow it; a profile is tested against its author's own
/// subscription.
/// </para>
/// </summary>
public interface IDatasetProfile
{
    /// <summary>Databento's dataset code, e.g. <c>GLBX.MDP3</c>.</summary>
    string Dataset { get; }

    ProfileStatus Status { get; }

    /// <summary>Who answers for the profile: the maintainer when verified, its contributor otherwise.</summary>
    string Maintainer { get; }

    /// <summary>
    /// What one group of instruments is called in this dataset, singular and
    /// lower case: <c>root</c> for futures. A group is the <c>{root}</c> part
    /// of every archive key.
    /// </summary>
    string GroupNoun { get; }

    /// <summary>The first day Databento has for the dataset.</summary>
    DateOnly Start { get; }

    /// <summary>The schemas DbnSync keeps for the dataset, by Databento's name.</summary>
    IReadOnlyDictionary<string, SchemaSpec> Schemas { get; }

    /// <summary>How far back the Standard plan includes each level.</summary>
    Entitlement Included { get; }

    /// <summary>Whether <paramref name="group"/>, upper case, is a well-formed group name.</summary>
    bool IsGroup(string group);

    /// <summary>The batch job that fetches one group and schema for the job's days.</summary>
    /// <param name="depth">Leading instruments to ask for, for <see cref="Selection.Leading"/> schemas.</param>
    BatchRequest Request(JobRequest job, int depth);
}
