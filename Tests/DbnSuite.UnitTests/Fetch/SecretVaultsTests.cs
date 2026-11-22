using DbnSuite.Fetch;
using Shouldly;

namespace DbnSuite.UnitTests.FetchTests;

/// <summary>A test that needs macOS's own <c>security</c> tool and a throwaway keychain.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "Runs on macOS only.";
    }
}

public sealed class SecretVaultsTests
{
    private const string Account = "default";
    private const string Secret = "a-forty-character-secret-for-the-tests-1";
    private const string Service = "dbnf-0123abcd";
    private const string KeychainMark = "keychain:";
    private const string SecretServiceMark = "secret-service:";
    private const string PlainMark = "plain:";
    private const string Security = "/usr/bin/security";
    private const string SecretTool = "secret-tool";
    private const string KeychainFile = "/tmp/dbnf-tests.keychain-db";
    private const string Missing = "no-such-tool-for-dbnf-tests";

    /// <summary>A keyring in memory, reached the way the real tools are, that remembers every call.</summary>
    private sealed class FakeTools
    {
        private readonly Dictionary<string, string> kept = [];

        public List<(string File, IReadOnlyList<string> Arguments, string? Input)> Calls { get; } = [];

        public bool Broken { get; set; }

        public string? ReadsBack { get; set; }

        public CommandResult? Run(string file, IReadOnlyList<string> arguments, string? input)
        {
            Calls.Add((file, arguments, input));

            if (Broken)
                return null;

            if (file == Security)
                return Mac(arguments, input);

            return Linux(arguments, input);
        }

        private CommandResult Mac(IReadOnlyList<string> arguments, string? input)
        {
            if (arguments is ["-i"])
            {
                var words = input!.Trim().Split(' ');
                kept[words[Array.IndexOf(words, "-a") + 1]] = words[Array.IndexOf(words, "-w") + 1];

                return new CommandResult(0, "");
            }

            var account = arguments[arguments.ToList().IndexOf("-a") + 1];

            return arguments[0] switch
            {
                "find-generic-password" => Found(account),
                _ => new CommandResult(kept.Remove(account) ? 0 : 44, "")
            };
        }

        private CommandResult Linux(IReadOnlyList<string> arguments, string? input)
        {
            var account = arguments[^1];

            switch (arguments[0])
            {
                case "store":
                    kept[account] = input!;
                    return new CommandResult(0, "");
                case "lookup":
                    return Found(account);
                default:
                    kept.Remove(account);
                    return new CommandResult(0, "");
            }
        }

        private CommandResult Found(string account) =>
            kept.TryGetValue(account, out var secret)
                ? new CommandResult(0, (ReadsBack ?? secret) + "\n")
                : new CommandResult(44, "");
    }

    private readonly FakeTools tools = new();

    private MacKeychain Keychain(string? file = null) => new(tools.Run, Service, file);

    private SecretService Linux() => new(tools.Run, Service);

    [Fact]
    public void The_keychain_keeps_a_secret_and_gives_it_back()
    {
        var keychain = Keychain();

        var stored = keychain.Protect(Account, Secret);

        stored.ShouldBe(KeychainMark);
        keychain.Unprotect(Account, stored!).ShouldBe(Secret);
    }

    [Fact]
    public void The_keychain_gets_the_secret_on_standard_input_never_as_an_argument()
    {
        Keychain().Protect(Account, Secret);

        tools.Calls.ShouldAllBe(c => !c.Arguments.Contains(Secret));
        tools.Calls[0].Input.ShouldBe($"add-generic-password -U -s {Service} -a {Account} -w {Secret}\n");
    }

    [Fact]
    public void A_word_with_a_space_or_quote_stays_out_of_the_keychain()
    {
        var stored = Keychain().Protect("my work", Secret);

        stored.ShouldBeNull();
        tools.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void A_named_keychain_is_used_for_every_command()
    {
        var keychain = Keychain(KeychainFile);

        keychain.Forget(Account, keychain.Protect(Account, Secret)!);

        tools.Calls[0].Input!.TrimEnd().ShouldEndWith(KeychainFile);
        tools.Calls.Skip(1).Select(c => c.Arguments.Last()).ShouldAllBe(last => last == KeychainFile);
    }

    [Fact]
    public void The_secret_service_keeps_a_secret_and_gives_it_back()
    {
        var linux = Linux();

        var stored = linux.Protect(Account, Secret);

        stored.ShouldBe(SecretServiceMark);
        linux.Unprotect(Account, stored!).ShouldBe(Secret);
        tools.Calls[0].File.ShouldBe(SecretTool);
        tools.Calls[0].Input.ShouldBe(Secret);
        tools.Calls.ShouldAllBe(c => !c.Arguments.Contains(Secret));
    }

    [Fact]
    public void Without_the_tool_the_secret_is_left_for_the_file()
    {
        tools.Broken = true;

        Linux().Protect(Account, Secret).ShouldBeNull();
        Keychain().Protect(Account, Secret).ShouldBeNull();
    }

    [Fact]
    public void A_secret_that_doesnt_read_back_the_same_is_not_relied_on()
    {
        tools.ReadsBack = "something-else";

        Linux().Protect(Account, Secret).ShouldBeNull();
    }

    [Fact]
    public void An_empty_secret_goes_nowhere_near_a_keyring()
    {
        Linux().Protect(Account, "").ShouldBeNull();

        tools.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Forgetting_lets_go_of_the_secret()
    {
        var linux = Linux();
        var stored = linux.Protect(Account, Secret)!;

        linux.Forget(Account, stored);

        linux.Unprotect(Account, stored).ShouldBeNull();
    }

    [Fact]
    public void A_vault_leaves_alone_what_it_did_not_keep()
    {
        var keychain = Keychain();

        keychain.Forget(Account, PlainMark + Secret);

        keychain.Unprotect(Account, PlainMark + Secret).ShouldBeNull();
        tools.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Each_config_folder_has_its_own_service_name()
    {
        var one = SecretVaults.ServiceName(Path.Combine(Path.GetTempPath(), "one"));
        var two = SecretVaults.ServiceName(Path.Combine(Path.GetTempPath(), "two"));

        one.ShouldNotBe(two);
        one.ShouldBe(SecretVaults.ServiceName(Path.Combine(Path.GetTempPath(), "one")));
        one.ShouldMatch("^dbnf-[0-9a-f]{8}$");
    }

    [Fact]
    public void Each_platform_has_its_vault()
    {
        var vault = SecretVaults.ForThisMachine(Path.GetTempPath());

        var expected = OperatingSystem.IsWindows() ? SecretPlace.Encrypted
            : OperatingSystem.IsMacOS() ? SecretPlace.Keychain
            : SecretPlace.SecretService;
        vault!.Place.ShouldBe(expected);
    }

    [Fact]
    public void A_command_runs_with_its_output_and_exit_code()
    {
        var result = SecretVaults.Run("dotnet", ["--version"], null);

        result!.ExitCode.ShouldBe(0);
        result.Output.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_command_that_isnt_installed_gives_nothing()
    {
        SecretVaults.Run(Missing, [], null).ShouldBeNull();
    }

    [MacFact]
    public void The_real_keychain_keeps_and_forgets_a_secret()
    {
        const string Password = "dbnf-tests";
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".keychain-db");
        SecretVaults.Run(Security, ["create-keychain", "-p", Password, file], null)!.ExitCode.ShouldBe(0);
        SecretVaults.Run(Security, ["unlock-keychain", "-p", Password, file], null)!.ExitCode.ShouldBe(0);
        var keychain = new MacKeychain(SecretVaults.Run, Service, file);

        try
        {
            var stored = keychain.Protect(Account, Secret);
            var back = keychain.Unprotect(Account, stored!);
            keychain.Forget(Account, stored!);

            stored.ShouldBe(KeychainMark);
            back.ShouldBe(Secret);
            keychain.Unprotect(Account, stored!).ShouldBeNull();
        }
        finally
        {
            SecretVaults.Run(Security, ["delete-keychain", file], null);
        }
    }
}
