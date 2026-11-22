using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbnSuite.Client;
using DbnSuite.Client.Archive;

namespace DbnSuite.Fetch;

/// <summary>One saved login: an archive and the key that reads it. The secret is kept apart.</summary>
public sealed record Profile
{
    public required string Location { get; init; }
    public string Bucket { get; init; } = "dbn";
    public string Prefix { get; init; } = "";
    public string AccessKey { get; init; } = "";
    public string Region { get; init; } = "us-east-1";
    public string Dataset { get; init; } = "GLBX.MDP3";

    public ArchiveOptions ToOptions(string secret) => new()
    {
        Location = Location,
        Bucket = Bucket,
        Prefix = Prefix,
        AccessKey = AccessKey,
        SecretKey = secret,
        Region = Region,
        Dataset = Dataset
    };
}

/// <summary>
/// Saved logins, in the user's own config folder. Secrets never sit in the
/// profiles file: Windows encrypts them for the signed-in user (DPAPI), macOS
/// keeps them in the Keychain and Linux in the Secret Service. Without a
/// keyring they go in a file only the user can read. A damaged file is an
/// error naming it, and logging in again replaces it.
/// </summary>
public sealed class Profiles
{
    public const string DefaultName = "default";

    /// <summary>A file of saved logins cannot be read.</summary>
    public const string Damaged = "profile.damaged";

    private const string ProfilesFile = "profiles.json";
    private const string SecretsFile = "secrets.json";
    private const string DamagedSuffix = ".damaged";

    private const string PlainMark = "plain:";

    private readonly string folder;
    private readonly ISecretVault? vault;

    /// <summary>Saved logins with no keyring: DPAPI on Windows, the file elsewhere.</summary>
    public Profiles(string folder)
        : this(folder, OperatingSystem.IsWindows() ? new DpapiVault() : null)
    {
    }

    public Profiles(string folder, ISecretVault? vault)
    {
        this.folder = folder;
        this.vault = vault;

        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(folder);
        else
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// The config folder: %APPDATA%\DbnFetch on Windows, ~/.config/DbnFetch on Linux and
    /// ~/Library/Application Support/DbnFetch on macOS.
    /// </summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "DbnFetch");

    /// <summary>
    /// The cache folder: %LOCALAPPDATA%\DbnFetch\cache on Windows, $XDG_CACHE_HOME/DbnFetch on
    /// Linux when that is set, and ~/.cache/DbnFetch otherwise.
    /// </summary>
    public static string DefaultCache => CacheFolder(Environment.GetEnvironmentVariable("XDG_CACHE_HOME"));

    /// <summary>The cache folder, given XDG_CACHE_HOME. The XDG spec ignores a relative path, and so does dbnf.</summary>
    internal static string CacheFolder(string? xdgCacheHome)
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbnFetch", "cache");

        if (OperatingSystem.IsLinux() && xdgCacheHome is { Length: > 0 } && Path.IsPathRooted(xdgCacheHome))
            return Path.Combine(xdgCacheHome, "DbnFetch");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "DbnFetch");
    }

    public Result<IReadOnlyDictionary<string, Profile>> All()
    {
        var file = Load();

        return file.IsSuccess ? file.Value.Profiles : file.Error!;
    }

    public Result<Profile?> Find(string name)
    {
        var file = Load();

        return file.IsSuccess ? file.Value.Profiles.GetValueOrDefault(name) : file.Error!;
    }

    /// <summary>
    /// Saves a login, and says where its secret went. It is how a damaged file is replaced,
    /// so what can be read of it is set aside, not lost.
    /// </summary>
    public SecretPlace Save(string name, Profile profile, string secret)
    {
        var file = OrFresh(Load(), ProfilesFile, new ProfilesFile());
        file.Profiles[name] = profile;
        Write(ProfilesFile, JsonSerializer.Serialize(file, ProfilesJson.Default.ProfilesFile));

        var secrets = OrFresh(LoadSecrets(), SecretsFile, []);

        // Whatever kept the old secret lets go of it first, so a keyring is never left holding one.
        if (secrets.GetValueOrDefault(name) is { } old)
            vault?.Forget(name, old);

        var (stored, place) = vault?.Protect(name, secret) is { } kept
            ? (kept, vault.Place)
            : (PlainMark + secret, SecretPlace.File);

        secrets[name] = stored;
        WriteSecrets(secrets);

        return place;
    }

    /// <summary>Where the secrets file is, for saying so.</summary>
    public string SecretsPath => Path.Combine(folder, SecretsFile);

    public Result<bool> Remove(string name)
    {
        var file = Load();

        if (!file.IsSuccess)
            return file.Error!;

        var secrets = LoadSecrets();

        if (!secrets.IsSuccess)
            return secrets.Error!;

        // A secret can outlive its login, when the profiles file was replaced; it goes either way.
        if (secrets.Value.Remove(name, out var stored))
        {
            vault?.Forget(name, stored);
            WriteSecrets(secrets.Value);
        }

        if (!file.Value.Profiles.Remove(name))
            return false;

        Write(ProfilesFile, JsonSerializer.Serialize(file.Value, ProfilesJson.Default.ProfilesFile));

        return true;
    }

    public Result<string?> Secret(string name)
    {
        var secrets = LoadSecrets();

        if (!secrets.IsSuccess)
            return secrets.Error!;

        return secrets.Value.GetValueOrDefault(name) is { } stored ? Unprotect(name, stored) : (string?)null;
    }

    /// <summary>
    /// The archive to use, or null when there is none: the named profile, with
    /// any of DBNF_LOCATION, DBNF_BUCKET, DBNF_ACCESS_KEY and DBNF_SECRET_KEY
    /// taking precedence, so scripts and CI need no saved login.
    /// </summary>
    public Result<ArchiveOptions?> Resolve(string name)
    {
        var profile = Find(name);

        if (!profile.IsSuccess)
            return profile.Error!;

        var location = Environment.GetEnvironmentVariable("DBNF_LOCATION") ?? profile.Value?.Location;

        if (location is null)
            return (ArchiveOptions?)null;

        var secret = Secret(name);

        if (!secret.IsSuccess)
            return secret.Error!;

        var options = (profile.Value ?? new Profile { Location = location }).ToOptions(secret.Value ?? "");

        return options with
        {
            Location = location,
            Bucket = Environment.GetEnvironmentVariable("DBNF_BUCKET") ?? options.Bucket,
            AccessKey = Environment.GetEnvironmentVariable("DBNF_ACCESS_KEY") ?? options.AccessKey,
            SecretKey = Environment.GetEnvironmentVariable("DBNF_SECRET_KEY") ?? options.SecretKey
        };
    }

    private Result<ProfilesFile> Load() =>
        Read(ProfilesFile, json => JsonSerializer.Deserialize(json, ProfilesJson.Default.ProfilesFile), () => new ProfilesFile());

    private Result<Dictionary<string, string>> LoadSecrets() =>
        Read(SecretsFile, json => JsonSerializer.Deserialize(json, ProfilesJson.Default.DictionaryStringString), () => []);

    private Result<T> Read<T>(string name, Func<string, T?> parse, Func<T> nothing)
        where T : class
    {
        var path = Path.Combine(folder, name);

        if (!File.Exists(path))
            return nothing();

        try
        {
            return parse(File.ReadAllText(path)) ?? nothing();
        }
        catch (JsonException e)
        {
            return new Error(Damaged, $"{path} is damaged ({e.Message}), so the saved logins cannot be read. Run dbnf login again to replace it.");
        }
    }

    /// <summary>What was read, or, from a damaged file, <paramref name="fresh"/>, with the file moved aside.</summary>
    private T OrFresh<T>(Result<T> loaded, string name, T fresh)
    {
        if (loaded.IsSuccess)
            return loaded.Value;

        var path = Path.Combine(folder, name);

        File.Move(path, path + DamagedSuffix, overwrite: true);

        return fresh;
    }

    private void WriteSecrets(Dictionary<string, string> secrets) =>
        Write(SecretsFile, JsonSerializer.Serialize(secrets, ProfilesJson.Default.DictionaryStringString));

    /// <summary>Replaces a file whole. Off Windows it is made readable by its owner alone, before anything is in it.</summary>
    private void Write(string name, string json)
    {
        var path = Path.Combine(folder, name);
        var temp = path + ".tmp";

        File.Delete(temp);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var writer = new StreamWriter(temp, new UTF8Encoding(false), options))
            writer.Write(json);

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A secret from the file as it is, or from whatever kept it; null when it can't be had here.</summary>
    private string? Unprotect(string name, string stored) =>
        stored.StartsWith(PlainMark, StringComparison.Ordinal)
            ? stored[PlainMark.Length..]
            : vault?.Unprotect(name, stored);
}

public sealed class ProfilesFile
{
    public Dictionary<string, Profile> Profiles { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ProfilesFile))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ProfilesJson : JsonSerializerContext;
