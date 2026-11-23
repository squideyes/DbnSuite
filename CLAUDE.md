# DbnSuite: notes for Claude Code

These hold on every machine and in cloud sessions. This repository is public:
nothing here, or in anything committed, may hold a key, a server address,
an account detail or Databento data.

[CONTRIBUTING.md](CONTRIBUTING.md) comes first. Its rules have no exceptions: no
Databento data, no keys, nothing that spends by default, nothing that needs a paid
licence, and the notices on every surface. It also covers branches, commits, tests
and the changelog.

## The pieces

| Folder | What it is | Ships as |
|---|---|---|
| `Core/DbnSuite.Client` | The C# library | NuGet `DbnSuite.Client` |
| `Core/DbnSuite.Fetch` | `dbnf`, the command-line tool | NuGet `DbnSuite.Fetch`, plus native binaries |
| `Core/DbnSuite.Sync.*` | DbnSync, the archiving worker | `ghcr.io/squideyes/dbnsync-worker` |
| `Core/DbnSuite.Spool` | DbnSpool, the tape worker | `ghcr.io/squideyes/dbnspool-worker` |
| `Core/DbnSuite.Alerts` | Alert emails for both workers | Inside both worker images |
| `Core/DbnSuite.Site` | Blazor WebAssembly site | GitHub Pages, dbnsuite.squideyes.com |
| `Aspire/AppHost` | Local orchestration | Not shipped |
| `Deploy` | Self-hosting: RustFS, Seq, Caddy | Compose files |
| `Deploy/digitalocean` | A Droplet with a Space as the store | Compose files |
| `Clients/python` | A contributed Python client, largely untested | Not published yet |

The Python client reads the archive and tapes, and deals sessions, as C# does. C#'s
`PythonFixturesTests` writes its fixtures and checks C# still matches them. When the tape format
or the sampler changes, run that test with `DBNSUITE_WRITE_FIXTURES=1`, then
`pytest Clients/python`. The client never rebuilds books, bars or back-adjustment: those stay in
C#, behind `dbnf export`.

## Docs, website and code never drift apart

The website, dbnsuite.squideyes.com, is `Core/DbnSuite.Site`. `pages.yml` deploys it whenever a
change to that folder reaches `main`. The docs are a promise about the code, so they change in the
same pull request as the code, never in a later one.

- **Every user-facing change updates its docs in the same PR.** That means a command, option,
  default, setting, environment variable, port, path, file format, error code, public type or
  MCP tool. Update every place that describes it:

  | What changed | Where it's described |
  |---|---|
  | `dbnf` commands, options, MCP tools | `Pages/Docs/DbnFetch.razor`, the README's DbnSuite.Fetch sections |
  | DbnSuite.Client's public API | `Pages/Docs/DbnClient.razor`: samples, errors table, namespaces |
  | Tapes, the catalog, sampling | `Pages/Docs/Tapes.razor`, and the Python client if the format changes |
  | The archive's keys and layout | `Pages/Docs/Layout.razor`, `Pages/Docs/Archive.razor` |
  | DbnSync settings, `.env`, Compose | `Pages/Docs/DbnSync.razor`, the README, `Deploy/**/.env.example`, the `dbnsync-deploy` skill |
  | DbnSpool | `Pages/Docs/DbnSpool.razor`, `Deploy/digitalocean` |
  | The Python client | `Clients/python/README.md`, `Pages/Docs/Python.razor` |
  | A new piece, platform or headline feature | `Pages/Home.razor`, `Pages/Docs/Overview.razor`, `Layout/DocsLayout.razor`, the README's table |

- **Check claims against the code, not memory.** Before writing a default, a range or a name,
  read it in the code. Sample output comes from a real run, and C# samples must compile against
  the current API.
- **Renames sweep everything.** After renaming anything user-facing, search the whole repo,
  including `Core/DbnSuite.Site`, `README.md`, `CONTRIBUTING.md`, `Deploy/` and
  `Clients/python`, for the old name.
- **The packages ship the README.** NuGet shows the root `README.md` for both packages, so it
  must be right on the day of a release.
- **Audit before a release.** Before tagging, compare each docs page against the code, one
  page or area at a time, and fix what has drifted in the release PR. After `pages.yml` runs,
  open the live site and check the pages that changed.

## Dependencies: always upgrade, never pin back

- **Upgrade, don't hold back.** Never pin, hold back or roll back a package, SDK,
  tool or image, and never add a Dependabot ignore rule.
  - When an upgrade breaks something, migrate to the new API using the vendor's
    migration guide.
  - If that still doesn't work, stop and work out a workaround with the
    maintainer. Don't offer a rollback as an option.
- **Releases are different.** A deploy pinned to a tested build
  (`DBNSUITE_VERSION`) is release control, not holding a dependency back.
- **Dependabot sees image tags only in `Deploy/` and the root Dockerfiles.** Image tags written in C#, such as RustFS in
  the AppHost and the tests, and Seq in the AppHost, need bumping by hand. Bump
  them in the same PR as the matching tag in `Deploy/`.

## Building and testing

- **Build everything first.** Build the whole solution, then test, as CI does:
  `dotnet test` alone never builds the AppHost or the site.

  ```bash
  dotnet build DbnSuite.slnx -c Release
  ```

  ```bash
  nice -n 10 dotnet test DbnSuite.slnx -c Release --no-build -- xUnit.MaxParallelThreads=1
  ```

- **Run tests gently on the maintainer's PCs:** at low priority, on one thread,
  as above. A CPU stress test once crashed one of them. Anything heavy, such as
  load, soak, benchmarks or wide parallel runs, belongs in CI.
- **Docker must be running.** The S3 tests start RustFS with Testcontainers.
- **Set every S3 region in code.**
  - Every S3 client passes the store's configured `Region`, both for signing and
    when creating a bucket.
  - Left unset, the AWS SDK reads `AWS_REGION`. RustFS 1.0.5 and later refuses a
    bucket whose region doesn't match its own.
  - Tests that create buckets set the region too.
- **Run it locally with the Aspire CLI.** Aspire 17 needs it, and
  `aspire update --self` keeps it current.

  ```bash
  aspire run --apphost Aspire/AppHost/DbnSuite.AppHost.csproj
  ```

  - In Development, DbnSync fetches only a small slice. It's set in
    `Core/DbnSuite.Sync.Worker/appsettings.Development.json`.
  - Put those settings back after changing them for a one-off run.

## Merging and releasing

- **Changes reach `main` only through pull requests.**
  - Squash-merge them and delete the branch.
  - CI takes about ten minutes, and auto-merge is off.
- **Push as the `squideyes` GitHub account.**
- **To release a version:**
  1. In a release PR, rename `## Unreleased` in `CHANGELOG.md` to
     `## x.y.z (yyyy-mm-dd)`.
  2. Merge the PR.
  3. Tag the merge commit `vx.y.z` and push the tag.
  4. `release.yml` builds the native `dbnf` binaries and publishes both packages
     to nuget.org.
- **nuget.org takes a while to show a release.** The packages may show on
  nuget.org before `dotnet tool update` finds them, and the per-platform `dbnf`
  packages appear last.

## Production

- **What runs where:**
  - DbnSync, DbnSpool and Seq run on a DigitalOcean Droplet from
    `Deploy/digitalocean`.
  - The archive is in the Space `dbn`, and tapes are in `dbn-tapes`.
  - How to reach the Droplet is kept outside this repository.
  - The `dbnsync-deploy` skill in `.claude/skills` covers installing, updating and
    repairing.
- **Deploy a build** by setting `DBNSUITE_VERSION` in the Droplet's `.env` to the
  full commit id of a build whose CI passed. Then pull and `up -d`, with
  `--profile spool` for DbnSpool.
- **Back up first.** Copy Seq's volume before a major Seq upgrade. Copy the archive
  through S3, never from a store's data folder.
- **Keep secrets off the screen.**
  - Never print, echo or type a secret: `.env` values, Spaces keys, the Databento
    key.
  - The maintainer types credentials themself, for example `dbnf login`.
  - On the Droplet, read a `.env` value with `sed` straight into the command that
    needs it. Use S3 from an `rclone` container there, so no key leaves the
    Droplet.
- **Spending stays off.** Never turn on paid Databento jobs or raise the spend
  limits. If jobs are refused, change the dates, roots or schemas, never the
  limit.

## DbnTape: settled design

Check every change to tapes against these rules first. Don't change code the
maintainer has called settled without asking. Propose briefly, then iterate.

- **One tape a trade date, of each kind,** for that date's single most tradable
  contract: the one with the most volume in the previous session. Never one for
  each listed contract.
- **Full ETH sessions only.** Holidays, early closes and late opens are left out
  whole.
- **Shuffled replay.** Consumers replay whole sessions in shuffled order for
  Monte Carlo backtests, so nothing may rely on one day following another. A
  given seed must always give the same session order, including the warm-up.
- **Back-adjustment happens while streaming, never in the tapes.** Each session
  is stitched onto the one before it in the shuffled stream. Multi-day warm-up
  means the previous sessions in that stream, not in calendar order.

## Working with the maintainer

- **Act on blanket permission.** Once the maintainer gives it ("do all of X"),
  carry it out and batch the steps, rather than asking about each one.
- **Commit and push only when asked.** "Ship it" means build, test, PR, CI and
  squash-merge.
- **Leave top-level folders alone.** The maintainer deletes them themself.
