# DbnSuite.Fetch (`dbnf`)

A command-line tool that reads a [DbnSync](https://dbnsuite.squideyes.com/docs/dbnsync) archive of
Databento® files, in S3 or a folder, and writes the data your research tools and trading platforms
want.

> **Independent project.** DbnSuite has no official connection with Databento; it is not made,
> endorsed or supported by Databento. Databento is a registered trademark of Databento, Inc.
>
> **For licensed access only.** Use it only as your Databento agreements allow, including their
> terms on storing, sharing and redistributing data.
>
> **At your own risk.** Free software under the MIT licence, provided as is, without warranty. See
> [NOTICE.md](https://github.com/squideyes/DbnSuite/blob/main/NOTICE.md).

```bash
dotnet tool install -g DbnSuite.Fetch
```

It needs the .NET 11 SDK and installs a native build on Windows, macOS and Linux, each on x64 and
ARM64. Standalone binaries that need no .NET are attached to each
[release](https://github.com/squideyes/DbnSuite/releases).

## What it does

- **See what's there:** every series with its days and size, a root's coverage day by day,
  instruments, and a day's records at the terminal.
- **Raw files, fast:** parallel downloads that resume, a local cache, and a dry run that says how
  many GB would come down.
- **Research-ready exports:** CME trade dates and sessions, time, tick and volume bars, continuous
  futures rolled and back-adjusted, and order books rebuilt from MBO, as CSV, Parquet, JSON Lines,
  DBN or NinjaTrader 8.
- **DbnTape:** build, prove, pull and replay compact tapes for backtests.
- **An MCP server:** `dbnf mcp` lets Claude or any other agent that speaks MCP answer questions about
  your archive. Market data never leaves your machine unless you start it with `--allow-data`.
- **Secrets kept safe:** your key's secret is encrypted on Windows, in the Keychain on macOS and in
  your keyring on Linux.

## A first look

```bash
dbnf login --location https://nyc3.digitaloceanspaces.com --access-key <read-only key>
dbnf ls
dbnf export ohlcv-1s ES.v.0 --bars 5m --adjust difference --last 30d --format parquet --single
```

## Learn more

- [DbnSuite.Fetch docs](https://dbnsuite.squideyes.com/docs/dbnfetch): every command and option.
- [Changelog](https://github.com/squideyes/DbnSuite/blob/main/CHANGELOG.md) and
  [source](https://github.com/squideyes/DbnSuite).
