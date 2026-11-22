using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DbnSuite.Fetch;

/// <summary>Where a saved login's secret is kept.</summary>
public enum SecretPlace
{
    /// <summary>In dbnf's secrets file, encrypted for the signed-in Windows user (DPAPI).</summary>
    Encrypted,

    /// <summary>In the user's macOS Keychain.</summary>
    Keychain,

    /// <summary>In the Linux Secret Service: GNOME Keyring, KWallet or another keyring.</summary>
    SecretService,

    /// <summary>In dbnf's secrets file as it is, readable by the user alone.</summary>
    File
}

/// <summary>
/// Something that keeps login secrets better than a plain file. What it gives back from
/// <see cref="Protect"/> is all that dbnf's secrets file holds for the login.
/// </summary>
public interface ISecretVault
{
    SecretPlace Place { get; }

    /// <summary>Keeps a secret; null when this vault can't, so the caller keeps it in the file.</summary>
    string? Protect(string account, string secret);

    /// <summary>The secret back, from what <see cref="Protect"/> gave; null when it can't be had here.</summary>
    string? Unprotect(string account, string stored);

    /// <summary>Lets go of a secret <see cref="Protect"/> kept. Anything it didn't keep is left alone.</summary>
    void Forget(string account, string stored);
}

/// <summary>A command's exit code and standard output, or null when it couldn't run or took too long.</summary>
public delegate CommandResult? RunCommand(string file, IReadOnlyList<string> arguments, string? input);

public sealed record CommandResult(int ExitCode, string Output);

public static class SecretVaults
{
    /// <summary>Long enough for someone to unlock a keyring when asked; a headless machine fails at once.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>The vault for this operating system: DPAPI, the Keychain or the Secret Service.</summary>
    public static ISecretVault? ForThisMachine(string configFolder)
    {
        if (OperatingSystem.IsWindows())
            return new DpapiVault();

        var service = ServiceName(configFolder);

        if (OperatingSystem.IsMacOS())
            return new MacKeychain(Run, service);

        if (OperatingSystem.IsLinux())
            return new SecretService(Run, service);

        return null;
    }

    /// <summary>
    /// One name per config folder, so two folders never share a secret: "dbnf" and the
    /// first eight hex digits of the folder's SHA-256.
    /// </summary>
    public static string ServiceName(string configFolder) =>
        "dbnf-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configFolder))))[..8];

    /// <summary>Runs a command with its arguments passed as they are, never through a shell.</summary>
    public static CommandResult? Run(string file, IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);

            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (input is not null)
                process.StandardInput.Write(input);

            process.StandardInput.Close();

            if (!process.WaitForExit(Patience))
            {
                process.Kill(entireProcessTree: true);

                return null;
            }

            return new CommandResult(process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (Win32Exception)
        {
            // Not installed.
            return null;
        }
    }
}

/// <summary>Windows: the secret stays in dbnf's file, encrypted for the signed-in user.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiVault : ISecretVault
{
    private const string Mark = "dpapi:";

    public SecretPlace Place => SecretPlace.Encrypted;

    public string? Protect(string account, string secret) =>
        Mark + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public string? Unprotect(string account, string stored)
    {
        if (!stored.StartsWith(Mark, StringComparison.Ordinal))
            return null;

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(stored[Mark.Length..]), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Saved by another Windows user or machine: log in again.
            return null;
        }
    }

    public void Forget(string account, string stored)
    {
    }
}

/// <summary>
/// A keyring reached through its own command-line tool. The secret goes in on standard input,
/// never as an argument, and is read back before dbnf relies on it.
/// </summary>
public abstract class KeyringVault(RunCommand run, string service) : ISecretVault
{
    protected RunCommand Run { get; } = run;

    protected string Service { get; } = service;

    public abstract SecretPlace Place { get; }

    /// <summary>What the secrets file holds for a secret kept here.</summary>
    protected abstract string Mark { get; }

    public string? Protect(string account, string secret)
    {
        if (secret.Length == 0 || !Store(account, secret))
            return null;

        return Find(account) == secret ? Mark : null;
    }

    public string? Unprotect(string account, string stored) =>
        stored == Mark ? Find(account) : null;

    public void Forget(string account, string stored)
    {
        if (stored == Mark)
            Delete(account);
    }

    protected abstract bool Store(string account, string secret);

    protected abstract string? Find(string account);

    protected abstract void Delete(string account);

    protected static string? Output(CommandResult? result) =>
        result is { ExitCode: 0, Output: var output } && output.TrimEnd('\r', '\n') is { Length: > 0 } secret ? secret : null;
}

/// <summary>macOS: the user's login Keychain, through Apple's <c>security</c> tool.</summary>
public sealed class MacKeychain(RunCommand run, string service, string? keychain = null) : KeyringVault(run, service)
{
    private const string Tool = "/usr/bin/security";

    public override SecretPlace Place => SecretPlace.Keychain;

    protected override string Mark => "keychain:";

    /// <summary>
    /// <c>security -i</c> reads its command from standard input, so the secret is never an
    /// argument anyone can see. Its words are split on spaces, so anything with a space or a
    /// quote in it stays in the file instead.
    /// </summary>
    protected override bool Store(string account, string secret)
    {
        if (!Plain(account) || !Plain(secret) || !Plain(Service) || keychain is not null && !Plain(keychain))
            return false;

        var command = $"add-generic-password -U -s {Service} -a {account} -w {secret}{(keychain is null ? "" : " " + keychain)}\n";

        return Run(Tool, ["-i"], command) is { ExitCode: 0 };
    }

    protected override string? Find(string account) =>
        Output(Run(Tool, With(["find-generic-password", "-s", Service, "-a", account, "-w"]), null));

    protected override void Delete(string account) =>
        Run(Tool, With(["delete-generic-password", "-s", Service, "-a", account]), null);

    private string[] With(string[] arguments) => keychain is null ? arguments : [.. arguments, keychain];

    private static bool Plain(string word) =>
        word.Length > 0 && !word.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\');
}

/// <summary>Linux: the Secret Service, through libsecret's <c>secret-tool</c>.</summary>
public sealed class SecretService(RunCommand run, string service) : KeyringVault(run, service)
{
    private const string Tool = "secret-tool";
    private const string ServiceAttribute = "service";
    private const string AccountAttribute = "account";

    public override SecretPlace Place => SecretPlace.SecretService;

    protected override string Mark => "secret-service:";

    protected override bool Store(string account, string secret) =>
        Run(Tool, ["store", "--label", $"dbnf login {account}", ServiceAttribute, Service, AccountAttribute, account], secret)
            is { ExitCode: 0 };

    protected override string? Find(string account) =>
        Output(Run(Tool, ["lookup", ServiceAttribute, Service, AccountAttribute, account], null));

    protected override void Delete(string account) =>
        Run(Tool, ["clear", ServiceAttribute, Service, AccountAttribute, account], null);
}
