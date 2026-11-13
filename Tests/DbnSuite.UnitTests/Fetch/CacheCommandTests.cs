using DbnSuite.Client.Archive;
using DbnSuite.Fetch;
using Shouldly;
using static DbnSuite.UnitTests.FetchTests.SampleArchive;
using static DbnSuite.UnitTests.TestData;

namespace DbnSuite.UnitTests.FetchTests;

[Collection(DbnfCollection.Name)]
public sealed class CacheCommandTests : IDisposable
{
    private const string Range = "--from 2026-10-29 --to 2026-10-30";
    private const string NothingCached = "This archive is a folder; nothing is cached for it.";
    private const string NotLoggedIn = "Not logged in. Run dbnf login first.";
    private const string NoBytes = "Freed 0 B.";
    private const string Work = "work";

    private static readonly DateOnly Traded = Day(10, 29);
    private static readonly string MboKey = DbnSyncLayout.Data(Dataset, Mbo, Root, Traded);
    private static readonly string OtherKey = DbnSyncLayout.Data(Dataset, Mbo, OtherRoot, Traded);
    private static readonly string DefinitionKey = DbnSyncLayout.Data(Dataset, Definition, Root, Traded);

    private readonly Dbnf dbnf = new();
    private readonly FakeS3 s3;

    public CacheCommandTests()
    {
        s3 = new FakeS3(dbnf.ArchiveFolder);

        var order = Order(FrontId, At(Traded, 14), 1m, 1, 'A', 'B', 1);

        dbnf.Archive.Add(Mbo, Root, Traded, order);
        dbnf.Archive.Add(Mbo, OtherRoot, Traded, order);
        dbnf.Archive.Add(Definition, Root, Traded, FrontDefinition());
    }

    public void Dispose()
    {
        s3.Dispose();
        dbnf.Dispose();
    }

    private string Cached(string key, string profile = Profiles.DefaultName) =>
        Path.Combine(dbnf.CacheFolder, profile, key.Replace('/', Path.DirectorySeparatorChar));

    private async Task PullEverythingAsync()
    {
        await dbnf.LogInAsync(s3);
        await dbnf.RunAsync($"pull {Mbo},{Definition} {Root},{OtherRoot} {Range}");
    }

    [Fact]
    public async Task A_folder_archive_has_nothing_cached()
    {
        var run = await dbnf.OnArchiveAsync("cache");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([NothingCached]);
    }

    [Fact]
    public async Task A_folder_archive_has_nothing_to_clear()
    {
        var run = await dbnf.OnArchiveAsync("cache clear");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([NothingCached]);
    }

    [Fact]
    public async Task Without_a_login_there_is_no_cache_to_show()
    {
        var run = await dbnf.RunAsync("cache");

        run.ExitCode.ShouldBe(Session.Usage);
        run.Error.ShouldContain(NotLoggedIn);
    }

    [Fact]
    public async Task An_empty_cache_is_one_line()
    {
        await dbnf.LogInAsync(s3);

        var run = await dbnf.RunAsync("cache");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([$"{Path.Combine(dbnf.CacheFolder, Profiles.DefaultName)}: 0 files, 0 B"]);
    }

    [Fact]
    public async Task The_cache_is_shown_by_root_and_schema()
    {
        await PullEverythingAsync();

        var run = await dbnf.RunAsync("cache");

        run.Lines[0].ShouldStartWith($"{Path.Combine(dbnf.CacheFolder, Profiles.DefaultName)}: 3 files, ");
        run.Lines[2].ShouldStartWith("Root");
        run.Lines[3].ShouldStartWith($"{OtherRoot}   {Mbo}");
        run.Lines[4].ShouldStartWith($"{Root}   {Definition}");
        run.Lines[5].ShouldStartWith($"{Root}   {Mbo}");
    }

    [Fact]
    public async Task Each_profile_has_its_own_cache()
    {
        await dbnf.LogInAsync(s3, $"--profile {Work}");

        await dbnf.RunAsync($"pull {Mbo} {Root} {Range} --profile {Work}");

        File.Exists(Cached(MboKey, Work)).ShouldBeTrue();
        File.Exists(Cached(MboKey)).ShouldBeFalse();
    }

    [Fact]
    public async Task Clear_deletes_everything()
    {
        await PullEverythingAsync();
        var size = new[] { MboKey, OtherKey, DefinitionKey }.Sum(k => new FileInfo(Cached(k)).Length);

        var run = await dbnf.RunAsync("cache clear");

        run.ExitCode.ShouldBe(Session.Ok);
        run.Lines.ShouldBe([$"Freed {Text.Bytes(size)}."]);
        File.Exists(Cached(MboKey)).ShouldBeFalse();
        File.Exists(Cached(DefinitionKey)).ShouldBeFalse();
    }

    [Fact]
    public async Task Clearing_an_empty_cache_frees_nothing()
    {
        await dbnf.LogInAsync(s3);

        var run = await dbnf.RunAsync("cache clear");

        run.Lines.ShouldBe([NoBytes]);
    }

    [Theory]
    [InlineData("cache clear MBO")]
    [InlineData("cache clear mbo")]
    public async Task Clear_can_delete_one_schema(string line)
    {
        await PullEverythingAsync();

        var run = await dbnf.RunAsync(line);

        run.ExitCode.ShouldBe(Session.Ok);
        File.Exists(Cached(MboKey)).ShouldBeFalse();
        File.Exists(Cached(OtherKey)).ShouldBeFalse();
        File.Exists(Cached(DefinitionKey)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("cache clear mbo mnq")]
    [InlineData("cache clear mbo MNQ")]
    public async Task Clear_can_delete_one_schema_of_one_root(string line)
    {
        await PullEverythingAsync();

        var run = await dbnf.RunAsync(line);

        run.ExitCode.ShouldBe(Session.Ok);
        File.Exists(Cached(MboKey)).ShouldBeFalse();
        File.Exists(Cached(OtherKey)).ShouldBeTrue();
        File.Exists(Cached(DefinitionKey)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("cache clear --schema mbo")]
    [InlineData("cache clear --root MNQ")]
    [InlineData("cache clear --schema mbo --root MNQ")]
    [InlineData("cache clear tickles")]
    [InlineData("cache clear tickles MNQ")]
    public async Task What_is_not_a_schema_is_refused_and_nothing_is_deleted(string line)
    {
        await PullEverythingAsync();

        var run = await dbnf.RunAsync(line);

        run.ExitCode.ShouldBe(Session.Usage);
        File.Exists(Cached(MboKey)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_cleared_file_comes_down_again()
    {
        await PullEverythingAsync();
        await dbnf.RunAsync("cache clear");

        var run = await dbnf.RunAsync($"pull {Mbo} {Root} {Range}");

        run.Lines[0].ShouldContain("0 already here, 1 to download");
    }

    [Fact]
    public async Task A_second_read_of_the_same_day_downloads_nothing()
    {
        await dbnf.LogInAsync(s3);
        await dbnf.RunAsync($"show {Mbo} {Root} 2026-10-29");
        var downloads = s3.Downloads.Count;

        var run = await dbnf.RunAsync($"show {Mbo} {Root} 2026-10-29");

        run.ExitCode.ShouldBe(Session.Ok);
        downloads.ShouldBeGreaterThan(0);
        s3.Downloads.Count.ShouldBe(downloads);
    }

    [Fact]
    public async Task A_file_that_changes_in_the_archive_is_downloaded_again()
    {
        await dbnf.LogInAsync(s3);
        await dbnf.RunAsync($"show {Mbo} {Root} 2026-10-29");
        var downloads = s3.Downloads.Count;
        s3.Version = "2";

        await dbnf.RunAsync($"show {Mbo} {Root} 2026-10-29");

        s3.Downloads.Count.ShouldBeGreaterThan(downloads);
    }

    [Fact]
    public async Task Pull_sees_a_changed_file_as_one_to_download()
    {
        await PullEverythingAsync();
        s3.Version = "2";

        var run = await dbnf.RunAsync($"pull {Mbo} {Root} {Range} --dry-run");

        run.Lines[0].ShouldContain("0 already here, 1 to download");
    }

    [Fact]
    public async Task A_damaged_cached_file_says_how_to_fetch_it_again_and_that_mends_it()
    {
        await PullEverythingAsync();
        var path = Cached(MboKey);
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, new byte[bytes.Length]);

        var broken = await dbnf.RunAsync($"show {Mbo} {Root} {Date}");
        var pulled = await dbnf.RunAsync($"pull {Mbo} {Root} --from {Date} --force");
        var shown = await dbnf.RunAsync($"show {Mbo} {Root} {Date}");

        broken.ExitCode.ShouldBe(Session.Failed);
        broken.Error.ShouldContain(path);
        broken.Error.ShouldContain($"dbnf pull {Mbo} {Root} --from {Date} --force");
        broken.Error.ShouldNotContain("   at ");
        pulled.ExitCode.ShouldBe(Session.Ok);
        shown.ExitCode.ShouldBe(Session.Ok);
    }

    private const string Date = "2026-10-29";
}
