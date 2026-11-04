# DbnSuite

<img src="Core/DbnSync.Worker/wwwroot/img/squidly.png" alt="Squidly" width="96" align="right" />

Three open-source tools for keeping and using Databento® market data, by
SquidEyes. Website: **[dbnsuite.squideyes.com](https://dbnsuite.squideyes.com)**. MIT licensed.

> **Independent project.** DbnSuite has no official connection with Databento;
> it is not made, endorsed or supported by Databento. Databento® is a registered
> trademark of Databento, Inc.
>
> **For licensed access only.** DbnSuite is meant to make better use of data
> you are licensed to receive from Databento, not to get around that licence.
> Use it only as your Databento agreements allow, including their terms on
> storing, sharing and redistributing data.
>
> **At your own risk.** Free software, provided as is, without warranty. See
> [NOTICE.md](NOTICE.md).

| Tool | What it does | Get it |
|---|---|---|
| **DbnSync** | Keeps a growing archive of Databento data in any S3-compatible store. By default it spends nothing at Databento | `ghcr.io/squideyes/dbnsync-worker` (Docker) |
| **DbnFetch** (`dbnf`) | Downloads raw files from the archive and exports bars, rebuilt order books and, for futures, sessions and back-adjusted continuous series, as Parquet, CSV or JSON Lines for research tools and trading platforms, DBN, or NinjaTrader 8's own import format | `dotnet tool install -g DbnFetch` |
| **DbnClient** | The engine under DbnFetch, as a C# library | `dotnet add package DbnClient` |

Each runs on your own machines with your own Databento account; nothing passes
through anyone else.

### Datasets

DbnSuite is built for Databento's whole corpus. What differs from one dataset
to the next is written down in a dataset profile
(`Core/DbnSync.Core/Datasets`), and a dataset without one is not supported.

| Dataset | Venue | Profile |
|---|---|---|
| `GLBX.MDP3` | CME Globex: CME, CBOT, NYMEX and COMEX futures | Verified |
| Databento's other 30 datasets | Equities, options, indices and other futures exchanges | None yet |

A **verified** profile is run by the maintainer against a real subscription
every day. A **contributed** profile is written and run by someone who
subscribes to that dataset; the maintainer has never run it, and DbnSync uses
it only when `DbnSync__AllowContributedProfile` is `true`, at your own risk.
DbnSuite never buys data to test with, and never commits Databento's files,
whose licence does not allow redistribution.

To add a dataset: implement `IDatasetProfile` with `Status` set to
`Contributed`, register it in `DatasetProfiles.All`, derive a test class from
`DatasetProfileConformance`, run it against your own subscription for at least
14 days with the spend policy at $0, and add its rules to the data layout. The
[Datasets docs](https://dbnsuite.squideyes.com/docs/datasets#adding) give every step.

Features named for futures (continuous series, rolls, CME trade dates) apply
to futures datasets only. The examples below use `GLBX.MDP3`, the one dataset
supported so far.

## DbnSync

DbnSync fetches the data your Databento plan includes and keeps it in an
S3 store you own, exactly as Databento delivers it. By default it submits only
jobs that Databento quotes at $0. On Standard and similar plans, whose included history
runs in rolling windows (16+ years of L0, 12 months of L1, one month of
L2/L3), it works through whatever leaves the plan first, so the archive keeps
every day long after the plan stops including it. Full documentation is at
[dbnsuite.squideyes.com/docs](https://dbnsuite.squideyes.com/docs).

- [Spending: $0 by default](#spending-0-by-default)
- [What it keeps](#what-it-keeps)
- [Working with the data](#working-with-the-data)
- [Dashboard](#dashboard)
- [Run locally](#run-locally)
- [Deploy on a NAS](#deploy-on-a-nas)
- [Keys](#keys)
- [Reaching it from the internet](#reaching-it-from-the-internet)
- [Configuration](#configuration)
- [Running DbnSync on DigitalOcean](#running-dbnsync-on-digitalocean)
- [Layout of the code](#layout-of-the-code)
- [Contributing](#contributing)
- [Licence](#licence)

## Spending: $0 by default

Every batch job is quoted with `metadata.get_cost` immediately before it is
submitted. Out of the box it is submitted only if the quote is **exactly
zero**. The quote is the streaming price, which batch delivery never exceeds.
A book or L1 job that straddles the edge of its window sheds its oldest day
and is quoted again, up to three times; anything else that quotes above the
limit is logged and skipped.

> **Paid jobs are at your own risk.** They are billed by Databento to your
> account. DbnSync checks each quote against your limits and its own ledger,
> but it is provided as is, with no warranty: a bug, a price change, a quote
> that differs from the bill or a mistaken setting can cost real money, and
> that risk is yours. Watch your Databento billing page, and set no limit
> higher than you can afford to lose.

A spend policy can allow paid jobs. It needs all four of these in `.env`, and
DbnSync refuses to start on a policy that is half set or whose limits don't
nest (per job ≤ per day ≤ per month):

| `.env` setting | Default | |
|---|---|---|
| `DBNSYNC_ALLOW_PAID_JOBS` | `false` | off, every limit counts as $0 |
| `DBNSYNC_MAX_PER_JOB` | `0` | the most one job may be quoted at, in US dollars |
| `DBNSYNC_MAX_PER_DAY` | `0` | all jobs submitted in one UTC day, together |
| `DBNSYNC_MAX_PER_MONTH` | `0` | all jobs submitted in one UTC month, together |

- Days the plan includes are planned first; paid days only after every
  included day is stored or in flight.
- A paid job is written to the ledger under `_dbnsync/spend/` with its quote
  *before* it is submitted, and Databento is asked to take it only at that
  quote or less. A crash in between over-counts, never under-counts.
- A paid job with an earlier entry in the ledger may already have been bought:
  a crash can follow Databento's acceptance. DbnSync lists Databento's jobs
  from that entry's date, which is free, and takes up an identical job the job
  ledger doesn't know instead of buying it again. If the list can't be read,
  that job waits. A job with no such entry needs no listing. Only an exact
  match counts: a job for other days is left alone, even if some days overlap.
- `DbnSync__ReachBackTo` (under the worker's `environment:` in the compose
  file) plans L1 and book days older than the Standard windows. Without a paid
  policy they are fetched only if your plan quotes them at $0.
- The dashboard shows the policy, and what the ledger says was spent today and
  this month; the worker logs a warning with the policy when it starts. The
  ledger is DbnSync's count of Databento's quotes, not Databento's bill.

## What it keeps

Defaults, all configurable:

| Schema | Level | Free window | Instruments | Days per job |
|---|---|---|---|---|
| `mbo` | L3 | 1 month | the 2 leading outrights by open interest (`MNQ.n.0`, `MNQ.n.1`) | 5 |
| `ohlcv-1s` | L0 | back to 2020 | every instrument (`MNQ.FUT`) | 92 |
| `definition`, `statistics`, `status` | L0 | back to 2020 | every instrument | 366 |

Roots: ES/MES, NQ/MNQ, YM/MYM, RTY/M2K, CL/MCL, GC/MGC. That is twelve roots,
but six markets, since each micro is the same market as its mini at a tenth the
size. They are the roots that came out best in the author's August 2026
measurement of trading cost against movement. NG, SI and the treasuries were
dropped as too thin; copper (HG/MHG) measured last with crude and was left out
here. Choose your own with `DBNSYNC_ROOTS`.

- **`mbo` takes leading outrights, not the parent.** Spreads dominate the
  bytes: CL's parent `mbo` was 51 GB in October 2026 against 5 GB for its
  front month. Two outrights cover both sides of a roll.
- **Off by default:**
  - `mbp-10`: about 5× the size of `mbo` and can be rebuilt from it.
  - `trades`, `tbbo`, `bbo-*`: can be rebuilt from `mbo`.
  - `mbp-1`: the one L1 schema that holds something `mbo` lacks, namely
    implied liquidity. At about 500 GB a year it is the biggest thing left out.
  - `ohlcv-1m/1h/1d`: exact merges of `ohlcv-1s`.

  Any of these can be switched on in `Schemas`.

The work is ordered by what expires first: the book, then L0, and the oldest
days first within each.

### Sizes

Measured with `metadata.get_billable_size` for October 2026, front contract,
**uncompressed** `mbo`, in GB per month:

| MNQ | NQ | ES | MES | MGC | GC | MYM | M2K | RTY | CL | YM | MCL |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 67.4 | 30.6 | 17.2 | 16.7 | 8.8 | 8.3 | 6.8 | 5.5 | 5.4 | 5.0 | 4.6 | 3.9 |

Files are stored zstd-compressed exactly as Databento ships them, about 3.1×
smaller. At that ratio the defaults come to:

| | On day one | Growth per year |
|---|---|---|
| `mbo`, one month | ~65 GB | ~750 GB |
| L0 since 2020 | ~25 GB | ~5 GB |
| **Total** | **~90 GB** | **~750 GB** |

To keep the book for fewer roots, narrow one schema without touching the rest:

```
DbnSync__RootsBySchema__mbo=MNQ,MES,MGC,MCL
```

### How it is stored

```
dbn/                                              bucket
  GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261028.dbn.zst  one UTC day, Databento's bytes unchanged
  GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261031.empty    asked for, no data (a Saturday)
  _dbnsync/jobs/GLBX-20261102-WT7SQW3ED5.json      submitted, not yet stored
  _dbnsync/passes/2026-11-02/051433120.json        a pass that changed something or met a problem
```

A day counts as held when either its data file or its `.empty` marker exists.
Every pass plans from what the store actually holds, day by day, so a hole
left by any failure is asked for again. Each file is checked against
Databento's size and SHA-256 before it is uploaded, and the `.empty` markers
for a job are written only after all of its files have landed.

## Working with the data

The archive is plain S3: any S3 client lists and downloads it, and any DBN
reader opens the files. Nothing needs DbnSync to be running.

### Connecting

| Setting | Value |
|---|---|
| Endpoint | `http://<nas>:9000` on the LAN, or your HTTPS name from outside |
| Bucket | `dbn` |
| Region | `us-east-1` (any value works; RustFS ignores it) |
| Addressing | path-style |
| Key | a read-only key for the `dbn` bucket (see [Keys](#keys)) |

Object keys follow `GLBX.MDP3/{schema}/{root}/{yyyy}/{root}.{schema}.{yyyyMMdd}.dbn.zst`,
one per UTC day. `.empty` objects mark days with no data; skip them.

### What is in a file

Each `.dbn.zst` is one UTC day of one schema for one root, as Databento
delivered it: zstd-compressed [DBN](https://github.com/databento/dbn),
version 3.

- **The metadata block** says what was requested (`MNQ.n.0`, `MNQ.n.1` for
  `mbo`; `MNQ.FUT` for the rest) and maps each instrument id in the file back
  to it. Records name instruments only by id.
- **`mbo`** holds only the two leading outrights. Which contract `MNQ.n.0` is
  changes at each roll; the mapping in each day's file says which it was that
  day, and that day's `definition` file names it (`MNQZ6`).
- **Prices** are integers in units of 1e-9; times are nanoseconds since the
  UNIX epoch, UTC. DBN readers convert both.

### DbnFetch (`dbnf`), the command-line tool

`dbnf` lists the archive, prints a day's records, downloads raw files and
exports processed ones. It is a .NET tool built as a native binary for Windows
(x64 and ARM), macOS and Linux. The DbnClient library underneath it does the same in your own C#.
Until the packages are on nuget.org, run it from the repo with
`dotnet run --project Core/DbnFetch --`, followed by the command.

**Log in once** with your read-only key. It asks for the secret, which Windows
then keeps encrypted for your user:

```bash
dbnf login --location https://s3.example.com --access-key dbn-reader
```

`dbnf doctor` checks the login, the archive, your clock and the cache, and
`dbnf logout` forgets the login. `--profile` keeps more than one login.
Scripts can set `DBNF_LOCATION`, `DBNF_ACCESS_KEY` and
`DBNF_SECRET_KEY` instead.

**What the archive holds:**

```bash
dbnf ls --root ES
```

```
Root  Schema       Days  No data      Size  First       Last
ES    definition  2,141      357    4.1 MB  2020-01-01  2026-11-02
ES    mbo            26        5    5.2 GB  2026-10-02  2026-11-01
ES    ohlcv-1s    2,130      368  871.1 MB  2020-01-01  2026-11-02
```

`dbnf coverage ES` draws a root day by day, a month to a line, and
`dbnf contracts ES` lists its contracts with their expiry and tick size.
Contracts are shown with two-digit years (`ESZ26`), and CME's own `ESZ6` is
accepted too.

**A day's records**, with times in Chicago unless `--tz` says otherwise:

```bash
dbnf show mbo MESZ26 2026-10-30 --limit 3
```

```
2026-10-25T07:00:06.845623025-05:00  MESZ26   clear  -      0 @ -     order 0
2026-10-25T07:00:06.845623025-05:00  MESZ26   add    ask    1 @ 7820  order 6883391723797
2026-10-25T07:00:06.845623025-05:00  MESZ26   add    ask    2 @ 7820  order 6883401784619
```

Each day's `mbo` opens with a snapshot of the book: a clear, then every resting
order. The symbol can be a root (`MES`), a contract (`MESZ26`) or, for `mbo`,
`MES.n.0` or `MES.n.1`.

**Download raw files** exactly as stored:

```bash
dbnf pull mbo,definition MNQ --from 2026-10-01 --to 2026-10-31 --out D:\dbn
```

Files land in a cache (`%LOCALAPPDATA%\DbnFetch\cache`) unless `--out` names a
folder. A file already there is skipped, an interrupted download carries on
where it stopped, and `--dry-run` shows what would come down and how many GB.
A folder filled by `pull --out` is an archive in its own right:
`dbnf --archive D:\dbn ls`. `dbnf cache` shows what the cache holds, and
`dbnf cache clear` frees it.


**Export processed files** for CME trade dates. Tuesday's session runs from
Monday 17:00 to Tuesday 16:00 Chicago time, with records stamped 16:00 to 17:00
counted to it too, since settlement and status messages land there. Times are
written in Chicago unless `--tz` says otherwise:

```bash
dbnf export ohlcv-1s ES.v.0 --bars 5m --session rth --since 2026-01-01 --adjust difference --format parquet
```

- **Symbols:** a root (`ES`), a contract (`ESZ26`), or a continuous series.
  `ES.c.0` rolls by the calendar (`--roll-days`, 8 by default), `ES.v.0` by
  the previous session's volume, `ES.n.0` by open interest. A roll is decided
  from the last complete session and takes effect at the next open.
  `dbnf rolls ES.v.0 --since 2025-01-01` lists the rolls with their gaps.
  `--adjust difference` or `ratio` back-adjusts earlier prices, using the
  settlement gap at each roll; a roll with no settlement stops an adjusted
  export. `mbo` holds `n.0` and `n.1` only, and keeps raw prices, so `--adjust`
  on an `mbo` series is refused.
- **Hours:** `--session eth` (default) or `rth`, or your own with
  `--hours 09:30-16:00 --hours-tz America/New_York`.
- **Bars:** `--bars` 5s, 1m, 15m, 1h, 1d, 1w or 1mo from `ohlcv-1s`, or
  500t (trades) and 1000v (volume) from `mbo`. Bars count from the session's
  open.
- **From `mbo`:** `--as trades`, `tbbo`, `mbp-1`, `mbp-10`, `bbo-1s` or
  `bbo-1m` rebuilds the order book and writes the schema Databento sells
  separately. The `mbo` trades of a day add up to the archive's own
  `ohlcv-1s` volume, and the rebuilt book is never crossed.
- **Formats:** `--format csv` (default), `parquet`, `jsonl`, `dbn` (a filtered
  copy of the records) or `nt8` (NinjaTrader 8 import text for bars, and
  `--as trades` ticks). `--compress gzip` or `zstd` for the text formats.
- **Columns:** Databento's field names; `symbol` names the contract
  (`ESZ26`) and `series` the continuous name. `--columns ts_event,close`
  keeps some, `--price ticks` or `raw` changes the units, and
  `--time unix-ns`, `unix-ms` or `excel` (with `--split-time`) changes the times.
- **Files:** one per trade date; `--single` for one file, `--partition` for
  `series=`/`date=` folders, `--name` for names of your own.

### Other clients

**AWS CLI:**

```bash
aws configure set default.s3.addressing_style path
```

```bash
aws s3 ls s3://dbn/GLBX.MDP3/mbo/MNQ/2026/ --endpoint-url http://<nas>:9000
```

```bash
aws s3 sync s3://dbn/GLBX.MDP3/ohlcv-1s/ES/ ./ES-ohlcv-1s --exclude "*.empty" --endpoint-url http://<nas>:9000
```

**Python** with `boto3` and Databento's own `databento` package, which reads
DBN into a pandas DataFrame:

```python
import boto3, databento as db
from botocore.config import Config

s3 = boto3.client(
    "s3",
    endpoint_url="http://<nas>:9000",
    aws_access_key_id="<read-only key>",
    aws_secret_access_key="<its secret>",
    config=Config(s3={"addressing_style": "path"}),
)

key = "GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261028.dbn.zst"
store = db.DBNStore.from_bytes(s3.get_object(Bucket="dbn", Key=key)["Body"].read())
df = store.to_df()   # one row per event, prices in dollars, symbols mapped
```

**The `dbn` command-line tool** (`cargo install dbn-cli`) turns a downloaded
file into CSV or JSON:

```bash
dbn MNQ.mbo.20261028.dbn.zst --csv --output MNQ.mbo.20261028.csv
```

**rclone** mounts or syncs the bucket: use provider `Other`, the endpoint
above, and `force_path_style = true`.

## Dashboard

The worker serves a Blazor dashboard on port 8080, and through Caddy, behind a
password, at your dashboard name (see [Reaching it from the internet](#reaching-it-from-the-internet)):

- **Corpus** (`/`): totals, an ETA for the backfill, plus a coverage table of every root and schema with
  each series' days as a proportional bar, its size, and its state in words.
  Each cell opens the series.
- **Series** (`/series/mbo/MNQ`): every day as a cell, one row per month, newest
  first, with each month's stored size. Hover a cell for its date, state and
  size. Underneath, a table of every gap.
- **Activity** (`/activity`): every warning and error from recorded passes,
  filterable by level, each linked to its series and job, plus the passes
  themselves. Covers the last 24 hours, 7 days or 30 days. Click an issue to
  open it: what its code means, what DbnSync does about it by itself, whether
  it needs you, the full message, and a link to the logs.
- **Job** (`/job/GLBX-20261102-WT7SQW3ED5`): where one Databento job stands
  (in flight, with Databento's own view of it, or stored), its days as the
  archive holds them now, and every issue it has met, grouped by code so a
  retry every five minutes reads as one problem.

The **ETA** is when every owed and pending day should be stored: the days left
divided by how many days the passes of the last 24 hours stored or found
empty. Lost days don't count, since no pass can fetch them. With nothing
settled in 24 hours it reads **Unknown**, and once nothing is left, **Caught up**.

Log links open Seq filtered to the job, or to the minutes around an issue
that has no job. They need `Dashboard__LogsUrl`, Seq's address as your browser
reaches it, so they work only on the LAN. A pass that crashes keeps its stack
trace only in the logs.

A day is in one of five states:

| State | Meaning |
|---|---|
| Stored | a data file is in the archive |
| No data | asked for; Databento had none (weekends, holidays) |
| Pending | in a submitted job, not yet stored |
| Owed | still free, not yet asked for; a later pass will take it |
| Lost | aged out of the free window without being stored; only money brings it back |

**Lost** is the state to watch. For `mbo`, a day goes from Owed to Lost if the
worker is down for more than a month. Every other state resolves itself.

The archive is rescanned after every pass, or on demand with **Rescan**. A pass
that stored, submitted or collected anything, or met a problem, is recorded
under `_dbnsync/passes/`; quiet passes are not. `/status` returns the last pass
as JSON.

## Run locally

Requires the .NET 10 SDK and Docker.

```bash
dotnet user-secrets set "Parameters:databento-api-key" "<key>" --project Aspire/AppHost
```

```bash
dotnet run --project Aspire/AppHost
```

The AppHost starts a RustFS S3 server and Seq, and links the dashboard to Seq.
In Development, DbnSync only fetches MCL `mbo` and `definition` from
20 October 2026 (`appsettings.Development.json`), so the local volume stays
small.

```bash
dotnet test DbnSuite.slnx
```

The S3 tests start RustFS containers through Testcontainers, so Docker must be running.

## Deploy with Claude Code

The [`dbnsync-deploy`](.claude/skills/dbnsync-deploy/SKILL.md) skill follows the
steps below for you, locally or over SSH, and later updates and troubleshoots
the service. Clone this repository, run `claude` in it, and type
`/dbnsync-deploy`. You type your Databento key into `.env` yourself, and the
skill never turns on spending. It is instructions for an AI model, not a
guarantee: read each command before you approve it.

## Deploy on a NAS

`Deploy/docker-compose.yml` runs three containers: RustFS holding the archive
on a NAS folder, Seq for searchable logs, and the DbnSync worker. CI publishes
the worker image to `ghcr.io/squideyes/dbnsync-worker` on every push to `main`. This
is how it went onto a UGREEN NAS running UGOS; other NAS systems differ only in
where SSH and Docker are switched on.

**Commands marked _NAS_ run in an SSH session on the NAS** (`ssh <user>@<nas>`,
where the prompt reads `user@NAS:~$`). **Commands marked _PC_ run on your own
machine.** UGOS turns SSH off again after a set time; switch it back on in
**Control Panel → Terminal** if a session drops.

1. In UGOS: **Control Panel → Terminal**, tick **SSH**. Install **Docker** from
   the App Center.
2. _NAS_: make the archive folder and hand it to RustFS, which runs as uid 10001:

   ```text
   sudo mkdir -p /volume1/docker/dbnsync/s3
   sudo chown -R 10001:10001 /volume1/docker/dbnsync/s3
   sudo chown <user> /volume1/docker/dbnsync
   ```

3. _PC_: copy `Deploy/.env.example` to `Deploy/.env` and fill it in: the
   Databento key, an S3 root secret (`openssl rand -base64 30`), the archive
   folder, a Seq admin password, and Seq's address for the dashboard's log
   links. `.env` is ignored by git.
4. _PC_: copy the files across. `-O` makes `scp` use the classic protocol;
   UGOS's SFTP service does not see `/volume1`.

   ```text
   scp -O Deploy/docker-compose.yml Deploy/Caddyfile Deploy/.env <user>@<nas>:/volume1/docker/dbnsync/
   ```

5. _NAS_: lock `.env` to your own user, and start:

   ```text
   cd /volume1/docker/dbnsync
   chmod 600 .env
   sudo docker compose pull
   sudo docker compose up -d
   ```

6. Open the dashboard at `http://<nas>:8080`. The first backfill is about
   670 jobs, at most 30 in flight at once, and takes a day or two.

**Updating** to a newly published image, _NAS_:

```text
cd /volume1/docker/dbnsync
sudo docker compose pull
sudo docker compose up -d
```

| Port | What |
|---|---|
| 9000 | S3 API: point any S3 client here, path-style |
| 9001 | S3 console: users, keys and policies |
| 8080 | DbnSync dashboard, `/status` and `/health` |
| 5341 | Seq: the worker's logs. Set a retention policy in its Settings; it keeps everything otherwise |

### About MinIO

MinIO no longer publishes public images: `minio/minio` on Docker Hub returns
404, and quay.io requires a login. The compose file uses **RustFS**, an
Apache-2.0 server with MinIO's layout, ports and console. DbnSync speaks only
the S3 API, so MinIO (if you have an image), Garage, SeaweedFS or DigitalOcean
Spaces work the same way. Change `Store__ServiceUrl` and the keys.

Whichever server you use, read and write the archive **through S3**. None of
them treats a file copied into its data folder as an object.

## Keys

The root key in `.env` can do anything, including delete the archive. Keep it
on the NAS, and give everything else its own key:

| Who | Can | Policy |
|---|---|---|
| The worker | read and write the `dbn` bucket | `dbn-readwrite` |
| Every consumer (the demo client, notebooks, other machines) | read the `dbn` bucket | `dbn-readonly` |

In the RustFS console (`http://<nas>:9001`, signed in with the root key),
create the two policies, then a user for each with its policy attached, and
give each user an access key.

`dbn-readonly`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["s3:GetBucketLocation", "s3:ListBucket"], "Resource": ["arn:aws:s3:::dbn"] },
    { "Effect": "Allow", "Action": ["s3:GetObject"], "Resource": ["arn:aws:s3:::dbn/*"] }
  ]
}
```

`dbn-readwrite`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["s3:GetBucketLocation", "s3:ListBucket", "s3:ListBucketMultipartUploads"], "Resource": ["arn:aws:s3:::dbn"] },
    { "Effect": "Allow", "Action": ["s3:GetObject", "s3:PutObject", "s3:DeleteObject", "s3:AbortMultipartUpload", "s3:ListMultipartUploadParts"], "Resource": ["arn:aws:s3:::dbn/*"] }
  ]
}
```

Put the worker's key in `.env` as `DBNSYNC_ACCESS_KEY` and
`DBNSYNC_SECRET_KEY`, then `sudo docker compose up -d` to restart it with them.

## Reaching it from the internet

Two ways, depending on who needs to reach it.

**Only your own devices: Tailscale.** Install Tailscale on the NAS (a UGOS app
or a container) and on each device. They then reach `http://<nas's tailscale
name>:9000` from anywhere, encrypted, with no port opened on the router and
nothing visible to the internet. This is the safer choice, and needs no
domain or certificate.

**Anything on the internet, including cloud services and partners: HTTPS on
your own domain.** The compose file's optional `https` service puts Caddy in
front of the S3 API and the dashboard (see `Deploy/Caddyfile`), each under its
own name, and gets and renews a Let's Encrypt certificate for each by itself.
UGOS keeps ports 80 and 443 for its own web page, so Caddy listens on 8443 and
the router forwards the internet's 443 there.

1. **Give the NAS a fixed address** on the router, so the forwarding rule
   keeps pointing at it. On a Verizon Fios CR1000B: open `http://192.168.1.1`
   and sign in with the admin password printed on the router's label. Under
   **Advanced → Network Settings → IPv4 Address Distribution**, give the NAS a
   static lease.
2. **Point two names at your home.** Find your public IP on the router's
   **System → System Status** page, and add DNS `A` records such as
   `s3.example.com` and `dbnsync.example.com` pointing at it, wherever your
   domain's DNS is hosted. Fios addresses rarely change, but they can; if
   yours does, update the records.
3. **Forward the port.** On the CR1000B: **Advanced → Security & Firewall →
   Port Forwarding**. Create a rule: original port **443**, protocol **TCP**,
   forward to the NAS's address, port **8443**, schedule **Always**. Then click
   **Apply Changes** at the top of the page: until you do, no rule takes
   effect, though the list shows it. Forward nothing else: not 80, 9000, 9001,
   8080 or 5341.
4. _NAS_: in `.env`, set `S3_DOMAIN`, `DASHBOARD_DOMAIN`, `DASHBOARD_USER` and
   `DASHBOARD_PASSWORD_HASH` (`.env.example` has the command that makes the
   hash). Copy `Caddyfile` beside the compose file, then start Caddy:

   ```text
   cd /volume1/docker/dbnsync
   sudo docker compose --profile internet up -d
   ```

5. From outside your network, `https://s3.example.com` is now the S3 endpoint
   (use only read-only keys out there), and `https://dbnsync.example.com` the
   dashboard, which asks for the user name and password.

Exposed this way, the S3 API is reachable by anyone who finds it; what keeps
them out is the keys. Use long generated secrets, give consumers read-only
keys, and keep RustFS updated. Scanners find a newly opened port within
seconds.

The router's port forwarding list also shows rules marked **UPnP IGD**, which
devices opened for themselves. A UGREEN NAS with UGREENlink on opens its own
web page (9999, 9443) and SSH (22) this way. Turn on two-factor sign-in for
every UGOS account (**Control Panel → Security → Account security**), and
keep SSH off when you are not using it.

## Configuration

`DbnSync` section, or `DbnSync__*` environment variables:

| Setting | Default | |
|---|---|---|
| `Dataset` | `GLBX.MDP3` | must have a profile; see [Datasets](#datasets) |
| `AllowContributedProfile` | `false` | starts on a contributed profile, at your own risk |
| `Roots` | `ES,MES,NQ,MNQ,YM,MYM,RTY,M2K,CL,MCL,GC,MGC` | comma-separated |
| `Schemas` | `mbo,ohlcv-1s,definition,statistics,status` | comma-separated |
| `RootsBySchema__<schema>` | none | replaces `Roots` for that schema |
| `HistoryStart` | `2020-01-01` | oldest day ever asked for |
| `ReachBackTo` | none | oldest day asked for at L1 and above, when older than the Standard windows |
| `Spend__AllowPaidJobs` | `false` | see [Spending](#spending-0-by-default); read its caveats first |
| `Spend__MaxPerJob`, `Spend__MaxPerDay`, `Spend__MaxPerMonth` | `0` | spend limits in US dollars |
| `Interval` | `06:00:00` | between passes once caught up |
| `BusyInterval` | `00:05:00` | between passes while work is pending |
| `MaxPendingJobs` | `30` | jobs in flight |
| `DownloadConcurrency` | `2` | files downloaded at once per job |
| `Depth` | `2` | leading outrights for book and L1 schemas |
| `ScratchPath` | `/scratch` in the image | where a file sits between download and upload |

`Store` section: `ServiceUrl`, `Bucket` (`dbn`), `Prefix`, `AccessKey`,
`SecretKey`, `Region` (`us-east-1`).

The Databento key is `Databento__ApiKey` or `DATABENTO_API_KEY`.

`Dashboard__LogsUrl`: Seq's address as a browser reaches it, for the log links.
Logs go to Seq when `ConnectionStrings__seq` is set.

## Running DbnSync on DigitalOcean

Without a NAS, a Droplet does the same job, with DigitalOcean Spaces as the S3
store instead of RustFS:

1. Create a Space (for example `dbn` in `nyc3`) and a Spaces access key for it.
2. Create a Droplet (Docker on Ubuntu, 1 GB is enough) and copy
   `docker-compose.yml` and `.env.example` to it, as for a NAS.
3. In `.env`, point the worker at the Space instead of the local S3 server:
   `S3_SERVICE_URL=https://nyc3.digitaloceanspaces.com`, the Space's name as
   `S3_BUCKET`, and the Spaces key as `DBNSYNC_ACCESS_KEY` and
   `DBNSYNC_SECRET_KEY`. The `S3_ROOT_*` and `S3_DATA_PATH` settings are
   unused, but compose still wants a value in each.
4. Start the worker and Seq without the local S3 server:
   `docker compose up -d --no-deps dbnsync seq`.

Spaces costs $5 a month for 250 GB, then $0.02 per GB.

## Layout of the code

```
Core/DbnSync.Core       planning, windows, keys, the sync pass, issue explanations; no I/O
Core/DbnSync.Data       Databento HTTP client, S3 store
Core/DbnSync.Worker     host: background loop, Blazor dashboard, /status, /health
Core/DbnClient          library: the archive, DBN files, contracts, rolls, sessions, bars, export, Parquet
Core/DbnFetch         the dbnf tool, a Native AOT .NET tool
Core/DbnSuite.Site      The website: Blazor WebAssembly, published to GitHub Pages
Tests/DbnSync.UnitTests xUnit, Shouldly, NSubstitute, bUnit; RustFS through Testcontainers
Aspire/AppHost          local orchestration: worker, RustFS, Seq
Aspire/ServiceDefaults
Deploy/                 NAS compose file, Caddyfile and .env.example
```

Package versions live in `Directory.Packages.props`, shared build settings in
`Directory.Build.props`, and `nuget.config` restores from nuget.org only.

## Contributing

Issues and pull requests are welcome at
[github.com/squideyes/DbnSuite](https://github.com/squideyes/DbnSuite). See
[CONTRIBUTING.md](CONTRIBUTING.md) for the rules, how to build and test, and
suggested contributions, such as a DbnClient in a language other than C#.

## Licence

MIT. See [LICENSE](LICENSE). Databento® is a registered trademark of Databento,
Inc.; other names are trademarks of their owners. DbnSuite uses Databento's open
DBN format and its public API, and has no official connection with Databento,
Inc. Use it only as your Databento agreements allow. Provided as is, without
warranty, at your own risk: see [NOTICE.md](NOTICE.md).
