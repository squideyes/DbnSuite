# Changelog

What changed in each release of the **DbnClient** library and the **DbnFetch**
(`dbnf`) tool on NuGet, newest first. Breaking changes come first in each
release. The format follows [Keep a Changelog](https://keepachangelog.com), and
versions follow [Semantic Versioning](https://semver.org).

## 0.3.0 (2026-11-12)

### Breaking

- **Prices in ticks need a known tick size.** An export with `Prices = Ticks`
  (`--price ticks`) now stops with `Errors.NoTickSize` at the first price of an
  instrument whose definition is missing that day. It used to write that price
  as a decimal, mixing units in one column.
- **A trade date has one tape of each kind, for one contract:** the one that
  traded the most in the previous session (`ES.v.0`; it was open interest,
  `ES.n.0`). Tapes used to be made for every contract. Files are named by root,
  kind and date, as `ES.s1.20261029.tape`, and the header names the contract.
  `TapePaths.For` takes no symbol, `TapeBuilder.BuildAsync` takes no list of
  symbols, `dbnf tape build` has no `--symbol`, and `Catalog.HeldOn` gives a
  date's contract. Delete your tape folder and build again.
- **`TapeSeries`, `SeriesDay` and `SeriesBars` are gone.** Tapes are replayed
  as Monte Carlo runs of whole sessions in a random order, with `SessionSampler`
  and `SessionStream`, which stitches each run into one series as it streams.
  Nothing replays tapes in date order with catalog back-adjustment any more.
- **Tapes are made only for full ETH days:** a session that opened at 17:00
  Chicago time the evening before and closed at 16:00, each within five
  minutes; halts inside it are allowed. Holidays, early closes and late opens
  are left out whole, so every tape day is alike. `SessionCalendar.For` has no
  session for those dates, and `dbnf tape build`,
  `dbnf tape pull` and DbnSpool delete any tape an older version left for one.
- **.NET 11 is required.** DbnClient and DbnFetch target .NET 11 only. Projects
  still on .NET 10 can stay on 0.2.0. ([#45](https://github.com/squideyes/DbnSuite/pull/45))
- **`DbnRecord` is closed:** code outside DbnClient can no longer derive its own
  record types from it. Switches over the record types it decodes no longer need
  a catch-all arm. ([#45](https://github.com/squideyes/DbnSuite/pull/45))
- **Calendar roll days follow each root's convention.** `RollSchedule.BuildAsync`
  takes `int? rollDays` and `ExportRequest.RollDays` is `int?`; null means the
  root's own rule, and a number replaces the rule's days. `dbnf rolls` and
  `dbnf export --roll-days` no longer default to 8.
  ([#34](https://github.com/squideyes/DbnSuite/pull/34))
- **A date holds the same contract whatever span it is asked in.** A continuous
  series now looks back a week before the span to decide its first contract, so
  an export starting a few days after a roll can return a different contract
  than before. ([#33](https://github.com/squideyes/DbnSuite/pull/33))
- **`dbnf` reads dates as `yyyy-MM-dd` only,** whatever the computer's own date
  format, and refuses options that conflict or would be ignored, such as
  `--compress` with Parquet or NinjaTrader output, or `--session` with `--hours`.
  ([#24](https://github.com/squideyes/DbnSuite/pull/24))

### Added

- `BuildReport.Removed`: tapes an earlier build left of a kind this build
  didn't make, now deleted so a date never replays another contract.
  `dbnf tape build` lists them.
- `SessionStream` replays a Monte Carlo run's sessions, whole, in the order a
  `SessionSampler` deals them, loading the next tape while one replays. Each
  `StreamSession.Offset` stitches the run into one unbroken series in ticks,
  for indicators and stats that span sessions, while fills stay on as-traded
  prices. `SessionStream.Days` and `TapePaths.Dates` list the dates a tape
  folder holds.
- `SessionSampler` deals the trade dates of Monte Carlo runs: seeded warm-up
  sessions, then every date once in a random order (`Sampling.Permutation`,
  the default) or dates drawn with replacement (`Sampling.Bootstrap`). The
  same dates, seed and run give the same order on any machine and .NET
  version, and `Fingerprint` tells you when the dates behind a seed changed.
- `Catalog.FullDays`, `Catalog.IsFullDay`, `Catalog.Excluded` (why a date has
  no tapes, such as "its session closed Fri 12:15, not 16:00") and
  `TapePaths.RemoveExcluded`. The proof's new "Full ETH day" check fails any
  tape on a date that isn't one.
- `CalendarRule`: the months a root holds and what its roll counts back from.
  Gold (`GC`, `MGC`) holds February, April, June, August and December and rolls
  8 days before the delivery month begins; other roots roll 8 days before
  expiry as before. ([#34](https://github.com/squideyes/DbnSuite/pull/34))

### Fixed

- A continuous series above rank 0, such as `ES.v.1` or `ES.n.1`, held the
  same contract as rank 0 for about a week after each roll. Each rank now holds
  a contract that expires after the rank below's.
- `--as` exports of a root kept calendar spreads when told to leave them out.
- A tape build no longer leaves an older tape of a kind it didn't make, which
  could hold another contract after the catalog changed a date's contract.
- `SessionStream` stops loading the next tape when a run ends early or is
  cancelled, and always loads it on a background thread.
- A roll's gap takes both settlements from the same session. With no session
  both contracts settled, an adjusted export across that roll is refused with
  `NoSettlement`. ([#36](https://github.com/squideyes/DbnSuite/pull/36))
- The tape proof no longer fails three kinds of correct tape: a contract with
  resting orders but no activity in its session, a quote moved by the 00:00 UTC
  snapshot, and trades at the open or close within the receive clock's lead.
  ([#42](https://github.com/squideyes/DbnSuite/pull/42))
- `dbnf export definition` returned nothing; definitions are now filed by
  receive time. ([#26](https://github.com/squideyes/DbnSuite/pull/26))
- Evening hours, such as `--hours 18:00-23:00` or `Window.Hours(18:00, 23:00)`,
  matched nothing; they now mean the evening the session opens.
  ([#26](https://github.com/squideyes/DbnSuite/pull/26))
- A NinjaTrader export of a whole root, which mixed every contract into one
  file, is refused; a file for a one-digit year (`ESZ9` for 2019) is named for
  the right decade. ([#26](https://github.com/squideyes/DbnSuite/pull/26))
- A cache download that goes silent is given up after five minutes and resumes
  next time, instead of waiting for ever.
  ([#26](https://github.com/squideyes/DbnSuite/pull/26))
- A trade off the tick before the session opens no longer refuses a date's
  tapes. ([#26](https://github.com/squideyes/DbnSuite/pull/26))
- `dbnf tape pull` refuses a key that would write outside `--tapes`.
  ([#24](https://github.com/squideyes/DbnSuite/pull/24))
- `dbnf tape build` and `tape verify` fail on a span the catalog does not reach,
  and `--symbol` takes either contract form in any case; both used to do
  nothing and exit 0. ([#24](https://github.com/squideyes/DbnSuite/pull/24))
- `dbnf` reports ordinary mistakes, such as an unreadable saved login, a damaged
  tape or a skewed clock, as a message instead of a stack trace. Its secrets
  file is created private to its owner off Windows, and `logout` always removes
  the secret. ([#24](https://github.com/squideyes/DbnSuite/pull/24))

## 0.2.0 (2026-11-07)

### Added

- Compact tapes for backtesting: `DbnClient.Tape` and `dbnf tape`.
  ([#16](https://github.com/squideyes/DbnSuite/pull/16))

## 0.1.0 (2026-11-04)

The first release of DbnClient and DbnFetch.
