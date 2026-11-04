# Contributing to DbnSuite

Pull requests are welcome. DbnSuite is built by SquidEyes for its own trading
research, in C#, and is deliberately prescriptive: it would rather do a few
things one way, and do them properly, than try to be everything to everyone.
If a change is large, or adds a new way of doing something, open an issue first
so nobody writes code that won't be merged.

DbnSuite is an independent project with no official connection with Databento,
Inc. Databento® is a registered trademark of Databento, Inc. By contributing you
agree that your work is licensed under the [MIT licence](LICENSE).

## Rules that have no exceptions

- **No Databento data.** Never commit, attach or paste Databento files or
  records, including in tests, examples and issues. They are licensed to you,
  not to this project. Tests use generated records.
- **No keys.** Never commit an API key or S3 secret, or paste one into an issue.
  Tests must not read a real key or call Databento.
- **Nothing that spends by default.** Spending stays off unless the person running
  DbnSync turns it on, and every paid path needs tests.
- **Nothing that needs a paid licence.** Check a package's licence before you add
  it. Packages such as FluentAssertions 8+, AutoMapper 15+ and MediatR 13+ are out.
- **No Databento logo,** and no wording that suggests Databento made, endorses or
  supports DbnSuite. Keep the notices in [NOTICE.md](NOTICE.md) on every surface.

## Building and testing

You need the .NET 10 SDK, and Docker running for the tests that use real S3.

```bash
dotnet build DbnSuite.slnx
```

```bash
dotnet test DbnSuite.slnx
```

Warnings are errors. `Tests/DbnSync.UnitTests` holds the unit tests.
`Tests/DbnSuite.IntegrationTests` runs the real worker against a real S3 server
and a fake Databento gateway. The site is `Core/DbnSuite.Site`, a Blazor
WebAssembly app.

## What a good pull request looks like

- **Small and focused,** on a branch such as `feature/short-description` or
  `fix/issue-description`, with [conventional commits](https://www.conventionalcommits.org)
  (`feat:`, `fix:`, `docs:`, `chore:`).
- **Tested.** Coverage is close to 100%, and it stays there. Test behaviour, not
  implementation, and work out expected values by hand from the documented rules
  rather than copying what the code produced. A bug fix starts with a test that
  fails. Use xUnit, Shouldly and NSubstitute; `Foo.cs` gets `FooTests.cs`.
- **Documented.** If behaviour changes, change the docs in the same pull request:
  the site, the README and, for deployment, the `dbnsync-deploy` skill. The data
  layout is a versioned promise, so changing it needs an issue first.
- **In the style around it.** File-scoped namespaces, nullable reference types,
  async with a `CancellationToken` for all I/O, and expected failures returned as
  results with stable codes, not thrown.

## Suggested contributions

These are things SquidEyes won't build, because it can't use or test them, but
would gladly take from someone who can.

### DbnClient in other languages

DbnClient is C# only, and SquidEyes has no plans to port it. If you want one in
Python, Rust, C++, Go or anything else, write it and send a pull request. Anything
that speaks S3 and reads DBN can already use an archive, so
[The archive](https://dbnsuite.squideyes.com/docs/archive) is the place to start.

A client in another language should:

- live under `Clients/<language>/`, with its own README, build and tests;
- follow the [data layout](https://dbnsuite.squideyes.com/docs/layout) exactly, so that it
  reads the same archive and writes the same files as `dbnf`;
- use Databento's own open-source DBN libraries rather than a new decoder;
- pass its tests in CI, using generated data only;
- carry the notices, and say plainly that it is contributed: its contributor
  maintains it, SquidEyes doesn't test or support it, and people use it at their
  own risk.

A contributed client whose maintainer stops responding for six months is removed.

### Dataset profiles

Each Databento dataset needs a profile, and SquidEyes subscribes only to CME. If
you subscribe to another, [Datasets](https://dbnsuite.squideyes.com/docs/datasets) sets out
the six steps, including 14 days of real runs at $0 on your own subscription.

### Export formats for trading platforms

`dbnf` writes NinjaTrader 8's import format natively. Native formats for
platforms such as MetaTrader and TradeStation would be welcome from someone who
uses them and can prove the files import cleanly.

### Deployment

Tested notes for other NAS systems and S3 stores, and improvements to the
`dbnsync-deploy` Claude Code skill. Say what you ran it on. Every setting the
skill names must stay in `Deploy/.env.example`; the tests check this.

## Reporting a bug

Open an issue with the version, what you ran, what you expected and what happened.
For DbnSync, include the issue code from the dashboard. Check that nothing you
paste contains a key or Databento data.
