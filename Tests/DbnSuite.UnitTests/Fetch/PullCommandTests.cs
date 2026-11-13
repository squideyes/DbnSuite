using DbnSuite.Client.Archive;
using DbnSuite.Fetch;
using DbnSuite.Fetch.Commands;
using Shouldly;
using static DbnSuite.UnitTests.FetchTests.SampleArchive;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class PullCommandTests : IDisposable
{
    private const string Range = "--from 2026-10-29 --to 2026-10-31";
    private const string TransferStopped = "The transfer stopped: ";
    private const string StackTrace = "   at ";
    private const string BadSchema = "tickles";
    private const string BadDate = "soon";
    private const string FolderAlready = "This archive is a folder already; give --out to copy from it.";
    private const string NoRange = "Give the days as one of --from (with --to), --last or --since.";
    private const string ToNeedsFrom = "--to needs --from.";
    private const string Backwards = "The range starts 2026-10-31, after it ends (2026-10-29).";
    private const string Refused = "The archive refused the key";
    private const string PartSuffix = ".part";
    private const string DataFile = "MNQ.mbo.20261029.dbn.zst";
    private const string NextDataFile = "MNQ.mbo.20261030.dbn.zst";
    private const string EmptyFile = "MNQ.mbo.20261031.empty";
    private const byte Damage = 0xFF;
    private const int PartLength = 20;

    private static readonly string DataKey = DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 29));
    private static readonly string NextKey = DbnSyncLayout.Data(Dataset, Mbo, Root, Day(10, 30));
    private static readonly string EmptyKey = DbnSyncLayout.Empty(Dataset, Mbo, Root, Day(10, 31));

    private readonly Dbnf dbnf = new();

    public void Dispose() => dbnf.Dispose();

    private static byte[] AnOrder(int day) => Order(FrontId, At(Day(10, day), 14), 1m, 1, 'A', 'B', 1);

    private void TwoDaysAndAnEmptyOne()
    {
        dbnf.Archive.Add(Mbo, Day(10, 29), AnOrder(29));
        dbnf.Archive.Add(Mbo, Day(10, 30), AnOrder(30), AnOrder(30));
        dbnf.Archive.AddEmpty(Mbo, Day(10, 31));
    }

    private string InOutput(string key) => Path.Combine(dbnf.Output, key.Replace('/', Path.DirectorySeparatorChar));

    private string InCache(string key) =>
        Path.Combine(dbnf.CacheFolder, Profiles.DefaultName, key.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Files_land_byte_for_byte_in_the_archives_own_layout()
    {
        TwoDaysAndAnEmptyOne();

        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption}");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.Written().ShouldBe([DataKey, NextKey]);
        (await File.ReadAllBytesAsync(InOutput(NextKey))).ShouldBe(await File.ReadAllBytesAsync(dbnf.Archive.PathOf(NextKey)));
    }

    [Fact]
    public async Task Pull_says_what_it_will_do_then_each_file_then_what_it_did()
    {
        TwoDaysAndAnEmptyOne();

        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --parallel 1");

        run.Lines[0].ShouldStartWith("2 files from 2026-10-29 to 2026-10-31: 0 already here, 2 to download (");
        run.Lines[0].ShouldEndWith("; 1 days had no data.");
        run.Lines[1].ShouldStartWith($"[1/2] 2026-10-29  {DataFile}");
        run.Lines[2].ShouldStartWith($"[2/2] 2026-10-30  {NextDataFile}");
        run.Lines[3].ShouldStartWith("Downloaded 2 files, ");
        run.Lines[3].ShouldEndWith($"into {dbnf.Output}");
    }

    [Fact]
    public async Task Dry_run_says_how_much_and_downloads_nothing()
    {
        TwoDaysAndAnEmptyOne();
        var size = new FileInfo(dbnf.Archive.PathOf(DataKey)).Length + new FileInfo(dbnf.Archive.PathOf(NextKey)).Length;

        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --dry-run");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldContain($"2 to download ({Text.Bytes(size)})");
        run.Lines[1].ShouldBe($"Files are in {dbnf.Output}");
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task Flat_puts_every_file_in_the_folder_itself()
    {
        TwoDaysAndAnEmptyOne();

        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --flat");

        dbnf.Written().ShouldBe([DataFile, NextDataFile]);
    }

    [Fact]
    public async Task Markers_also_writes_the_empty_files()
    {
        TwoDaysAndAnEmptyOne();

        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --markers");

        dbnf.Written().ShouldBe([DataKey, NextKey, EmptyKey]);
    }

    [Fact]
    public async Task Markers_are_written_even_when_no_file_needs_downloading()
    {
        TwoDaysAndAnEmptyOne();
        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption}");

        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --markers");

        dbnf.Written().ShouldContain(EmptyKey);
    }

    [Fact]
    public async Task Markers_are_written_for_a_range_of_only_empty_days()
    {
        dbnf.Archive.AddEmpty(Mbo, Day(10, 31));

        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --markers");

        dbnf.Written().ShouldBe([EmptyKey]);
    }

    [Fact]
    public async Task Dry_run_writes_no_markers()
    {
        TwoDaysAndAnEmptyOne();

        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --markers --dry-run");

        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task Markers_can_go_in_a_flat_folder()
    {
        TwoDaysAndAnEmptyOne();

        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --markers --flat");

        dbnf.Written().ShouldContain(EmptyFile);
    }

    [Fact]
    public async Task Files_already_there_are_skipped()
    {
        TwoDaysAndAnEmptyOne();
        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption}");

        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption}");

        run.Lines[0].ShouldContain("2 already here, 0 to download (0 B)");
        run.Lines[1].ShouldBe($"Files are in {dbnf.Output}");
    }

    [Fact]
    public async Task Force_downloads_a_file_that_is_already_there()
    {
        TwoDaysAndAnEmptyOne();
        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption}");
        var damaged = await File.ReadAllBytesAsync(InOutput(DataKey));
        damaged[^1] = Damage;
        await File.WriteAllBytesAsync(InOutput(DataKey), damaged);

        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --force");

        run.Lines[0].ShouldContain("0 already here, 2 to download");
        (await File.ReadAllBytesAsync(InOutput(DataKey))).ShouldBe(await File.ReadAllBytesAsync(dbnf.Archive.PathOf(DataKey)));
    }

    [Fact]
    public async Task Only_the_days_asked_for_come_down()
    {
        TwoDaysAndAnEmptyOne();

        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} --from 2026-10-30 {dbnf.OutOption}");

        dbnf.Written().ShouldBe([NextKey]);
    }

    [Theory]
    [InlineData("--last 5d")]
    [InlineData("--last 1w")]
    [InlineData("--last 1m")]
    [InlineData("--last 1y")]
    public async Task Last_counts_back_from_today(string range)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var recent = dbnf.Archive.Add(Mbo, today.AddDays(-2), AnOrder(30));
        dbnf.Archive.Add(Mbo, today.AddYears(-2), AnOrder(29));

        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {range} {dbnf.OutOption} --flat");

        dbnf.Written().ShouldBe([Path.GetFileName(recent)]);
    }

    [Fact]
    public async Task Since_runs_to_today()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var recent = dbnf.Archive.Add(Mbo, today.AddDays(-2), AnOrder(30));
        dbnf.Archive.Add(Mbo, today.AddDays(-20), AnOrder(29));

        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} --since {today.AddDays(-5):yyyy-MM-dd} {dbnf.OutOption} --flat");

        dbnf.Written().ShouldBe([Path.GetFileName(recent)]);
    }

    [Fact]
    public async Task Several_schemas_and_roots_are_comma_separated()
    {
        dbnf.Archive.Add(Mbo, Root, Day(10, 29), AnOrder(29));
        dbnf.Archive.Add(Definition, Root, Day(10, 29), FrontDefinition());
        dbnf.Archive.Add(Mbo, OtherRoot, Day(10, 29), AnOrder(29));
        dbnf.Archive.Add(Definition, OtherRoot, Day(10, 29), FrontDefinition(OtherRoot));

        var run = await dbnf.OnArchiveAsync($"pull {Mbo},{Definition} {Root},{OtherRoot} {Range} {dbnf.OutOption}");

        run.Lines[0].ShouldStartWith("4 files");
        dbnf.Written().Count.ShouldBe(4);
    }

    [Fact]
    public async Task Names_may_be_any_case_and_repeated()
    {
        TwoDaysAndAnEmptyOne();

        var run = await dbnf.OnArchiveAsync($"pull MBO,{Mbo} {Root.ToLowerInvariant()},{Root} {Range} {dbnf.OutOption} --dry-run");

        run.Lines[0].ShouldStartWith("2 files");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public async Task Any_parallel_setting_downloads_everything(int parallel)
    {
        TwoDaysAndAnEmptyOne();

        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --parallel {parallel}");

        run.ExitCode.ShouldBe(Session.Ok);
        dbnf.Written().Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_root_with_nothing_in_the_range_comes_down_as_nothing()
    {
        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[0].ShouldStartWith("0 files");
    }

    [Fact]
    public async Task A_folder_archive_has_no_cache_to_pull_into()
    {
        TwoDaysAndAnEmptyOne();

        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(FolderAlready);
    }

    [Fact]
    public async Task A_folder_filled_by_pull_is_an_archive()
    {
        TwoDaysAndAnEmptyOne();
        await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {Range} {dbnf.OutOption} --markers");

        var run = await dbnf.RunAsync($"ls --archive \"{dbnf.Output}\"");

        run.Lines[1].ShouldStartWith(Root);
        run.Lines[1].ShouldEndWith("2026-10-29  2026-10-30");
    }

    [Theory]
    [InlineData("", NoRange)]
    [InlineData("--to 2026-10-31 --last 5d", ToNeedsFrom)]
    [InlineData("--from 2026-10-31 --to 2026-10-29", Backwards)]
    [InlineData("--from 2026-10-29 --last 5d", NoRange)]
    public async Task A_bad_range_is_refused(string range, string message)
    {
        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} {range} {dbnf.OutOption}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(message);
    }

    [Fact]
    public async Task A_length_that_is_not_one_is_refused()
    {
        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} --last {BadDate} {dbnf.OutOption}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain($"\"{BadDate}\" is not a length");
    }

    [Fact]
    public async Task A_date_that_is_not_one_is_refused_when_parsing()
    {
        var run = await dbnf.OnArchiveAsync($"pull {Mbo} {Root} --from {BadDate} {dbnf.OutOption}");

        run.ExitCode.ShouldBe(Dbnf.ParseFailure);
        run.Error.ShouldContain(BadDate);
    }

    [Fact]
    public async Task One_bad_schema_stops_the_pull_before_anything_comes_down()
    {
        TwoDaysAndAnEmptyOne();

        var run = await dbnf.OnArchiveAsync($"pull {Mbo},{BadSchema} {Root} {Range} {dbnf.OutOption}");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain($"\"{BadSchema}\" is not a schema.");
        dbnf.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_out_files_go_to_the_cache_of_the_login()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        TwoDaysAndAnEmptyOne();
        await dbnf.LogInAsync(s3);

        var run = await dbnf.RunAsync($"pull {Mbo} {Root} {Range}");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines[^1].ShouldEndWith($"into {Path.Combine(dbnf.CacheFolder, Profiles.DefaultName)}");
        File.Exists(InCache(DataKey)).ShouldBeTrue();
    }

    [Fact]
    public async Task Markers_are_only_for_a_folder_of_your_own()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        TwoDaysAndAnEmptyOne();
        await dbnf.LogInAsync(s3);

        await dbnf.RunAsync($"pull {Mbo} {Root} {Range} --markers");

        File.Exists(InCache(EmptyKey)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_dropped_connection_fails_with_a_message_and_leaves_a_part_file()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        TwoDaysAndAnEmptyOne();
        await dbnf.LogInAsync(s3);
        s3.DropAfter = PartLength;

        var run = await dbnf.RunAsync($"pull {Mbo} {Root} --from 2026-10-29");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(TransferStopped);
        run.Error.ShouldNotContain(StackTrace);
        File.Exists(InCache(DataKey) + PartSuffix).ShouldBeTrue();
    }

    [Fact]
    public async Task An_interrupted_download_carries_on_where_it_stopped()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        TwoDaysAndAnEmptyOne();
        await dbnf.LogInAsync(s3);
        s3.DropAfter = PartLength;
        await dbnf.RunAsync($"pull {Mbo} {Root} --from 2026-10-29");
        s3.DropAfter = null;

        var run = await dbnf.RunAsync($"pull {Mbo} {Root} --from 2026-10-29");

        run.ExitCode.ShouldBe(Session.Ok);
        s3.Offsets[^1].ShouldBe(PartLength);
        (await File.ReadAllBytesAsync(InCache(DataKey))).ShouldBe(await File.ReadAllBytesAsync(dbnf.Archive.PathOf(DataKey)));
        File.Exists(InCache(DataKey) + PartSuffix).ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_key_fails_with_the_reason()
    {
        using var s3 = new FakeS3(dbnf.ArchiveFolder);
        TwoDaysAndAnEmptyOne();
        await dbnf.LogInAsync(s3);
        s3.Deny = true;

        var run = await dbnf.RunAsync($"pull {Mbo} {Root} {Range}");

        run.ExitCode.ShouldBe(Session.Failed);
        run.Error.ShouldContain(Refused);
    }

    [Fact]
    public void Split_trims_drops_blanks_and_repeats()
    {
        PullCommand.Split(" es, ,NQ,ES ", lower: false).ShouldBe(["ES", "NQ"]);
    }

    [Fact]
    public void Split_can_lower_case_for_schemas()
    {
        PullCommand.Split("MBO,Definition", lower: true).ShouldBe([Mbo, Definition]);
    }
}
