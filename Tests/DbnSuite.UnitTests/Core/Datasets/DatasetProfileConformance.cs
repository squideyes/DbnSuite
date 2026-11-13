using DbnSuite.Sync.Core;
using Shouldly;

namespace DbnSuite.UnitTests.Core.Datasets;

/// <summary>
/// What every dataset profile must pass. A new profile gets a test class that
/// derives from this one and names a few real groups of its dataset; these
/// tests then hold it to Databento's schemas and levels, the archive's key
/// grammar and the batch API's limits. They prove the profile is well formed,
/// not that it fetches the right data: that takes a run against a real
/// subscription, which only its maintainer can do.
/// </summary>
public abstract class DatasetProfileConformance
{
    /// <summary>The DbnSuite maintainer: the only one who may mark a profile verified.</summary>
    public const string Maintainer = "SquidEyes";

    /// <summary>Databento's schemas and their licensing levels.</summary>
    public static readonly IReadOnlyDictionary<string, Level> DatabentoSchemas = new Dictionary<string, Level>
    {
        ["mbo"] = Level.L3,
        ["mbp-10"] = Level.L2,
        ["mbp-1"] = Level.L1,
        ["tbbo"] = Level.L1,
        ["trades"] = Level.L1,
        ["bbo-1s"] = Level.L1,
        ["bbo-1m"] = Level.L1,
        ["cmbp-1"] = Level.L1,
        ["cbbo-1s"] = Level.L1,
        ["cbbo-1m"] = Level.L1,
        ["tcbbo"] = Level.L1,
        ["ohlcv-1s"] = Level.L0,
        ["ohlcv-1m"] = Level.L0,
        ["ohlcv-1h"] = Level.L0,
        ["ohlcv-1d"] = Level.L0,
        ["definition"] = Level.L0,
        ["statistics"] = Level.L0,
        ["status"] = Level.L0,
        ["imbalance"] = Level.L0
    };

    /// <summary>The <c>stype_in</c> values a batch job may use.</summary>
    public static readonly IReadOnlySet<string> SymbolTypes =
        new HashSet<string>(StringComparer.Ordinal) { "raw_symbol", "parent", "continuous", "instrument_id" };

    /// <summary>The most symbols one batch job may name.</summary>
    public const int MaxSymbols = 2000;

    private static readonly DayRange Days = new(new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 30));

    protected abstract IDatasetProfile Profile { get; }

    /// <summary>A few real groups of the dataset, as a user would configure them.</summary>
    protected abstract IReadOnlyList<string> Groups { get; }

    /// <summary>Strings that must never be accepted as a group, beyond the ones every profile refuses.</summary>
    protected virtual IReadOnlyList<string> NotGroups => [];

    public static TheoryData<int> Depths => [1, 2, 3, 4];

    [Fact]
    public void Is_registered_under_its_dataset_code()
    {
        DatasetProfiles.Find(Profile.Dataset).ShouldBeSameAs(Profile);
    }

    [Fact]
    public void Names_a_dataset_the_way_Databento_does()
    {
        Profile.Dataset.ShouldMatch(@"^[A-Z0-9]{4}\.[A-Z0-9]+$");
    }

    [Fact]
    public void Names_a_maintainer()
    {
        Profile.Maintainer.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Is_verified_only_by_the_DbnSuite_maintainer()
    {
        if (Profile.Status == ProfileStatus.Verified)
            Profile.Maintainer.ShouldBe(Maintainer);
    }

    [Fact]
    public void Calls_a_group_by_a_lower_case_word()
    {
        Profile.GroupNoun.ShouldMatch("^[a-z]+$");
    }

    [Fact]
    public void Starts_on_a_plausible_day()
    {
        Profile.Start.ShouldBeInRange(new DateOnly(2000, 1, 1), DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public void Keeps_at_least_one_schema()
    {
        Profile.Schemas.ShouldNotBeEmpty();
    }

    [Fact]
    public void Keys_each_schema_by_its_own_name()
    {
        foreach (var (name, spec) in Profile.Schemas)
            spec.Name.ShouldBe(name);
    }

    [Fact]
    public void Keeps_only_Databento_schemas_at_their_own_level()
    {
        foreach (var spec in Profile.Schemas.Values)
        {
            DatabentoSchemas.ShouldContainKey(spec.Name);
            spec.Level.ShouldBe(DatabentoSchemas[spec.Name], spec.Name);
        }
    }

    [Fact]
    public void Sizes_every_job_between_a_day_and_a_year()
    {
        foreach (var spec in Profile.Schemas.Values)
            spec.ChunkDays.ShouldBeInRange(1, 366, spec.Name);
    }

    [Fact]
    public void Includes_no_more_book_than_L1()
    {
        Profile.Included.BookDays.ShouldBeInRange(1, Profile.Included.L1Days);
        Profile.Included.L1Days.ShouldBeLessThanOrEqualTo(3660);
    }

    [Fact]
    public void Names_some_groups_to_test_with()
    {
        Groups.ShouldNotBeEmpty();
    }

    [Fact]
    public void Accepts_its_own_groups()
    {
        foreach (var group in Groups)
            Profile.IsGroup(group).ShouldBeTrue(group);
    }

    [Fact]
    public void Refuses_a_group_that_would_break_an_archive_key()
    {
        string[] unsafeGroups = ["", " ", "A B", "A/B", "A.B", "A\\B", "..", "A\tB"];

        foreach (var group in unsafeGroups.Concat(NotGroups))
            Profile.IsGroup(group).ShouldBeFalse($"'{group}'");
    }

    [Fact]
    public void Stores_every_group_and_schema_under_a_key_that_reads_back()
    {
        var day = Days.From;

        foreach (var group in Groups)
        foreach (var schema in Profile.Schemas.Keys)
        {
            ArchiveKeys.DayOf(ArchiveKeys.Data(Profile.Dataset, schema, group, day)).ShouldBe(day);
            ArchiveKeys.DayOf(ArchiveKeys.Empty(Profile.Dataset, schema, group, day)).ShouldBe(day);
        }
    }

    [Theory]
    [MemberData(nameof(Depths))]
    public void Asks_for_exactly_the_job_it_was_given(int depth)
    {
        foreach (var group in Groups)
        foreach (var spec in Profile.Schemas.Values)
        {
            var request = Profile.Request(new JobRequest(group, spec, Days), depth);

            request.Dataset.ShouldBe(Profile.Dataset);
            request.Schema.ShouldBe(spec.Name);
            request.Range.ShouldBe(Days);
        }
    }

    [Theory]
    [MemberData(nameof(Depths))]
    public void Names_symbols_the_batch_API_accepts(int depth)
    {
        foreach (var group in Groups)
        foreach (var spec in Profile.Schemas.Values)
        {
            var request = Profile.Request(new JobRequest(group, spec, Days), depth);

            SymbolTypes.ShouldContain(request.StypeIn);
            request.Symbols.ShouldNotBeEmpty();
            request.Symbols.Count.ShouldBeLessThanOrEqualTo(MaxSymbols);
            request.Symbols.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s) && !s.Contains(','));
            request.Symbols.ShouldBeUnique();
        }
    }
}
