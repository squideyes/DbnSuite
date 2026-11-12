# Changelog

What changed in each release of the **DbnClient** library and the **DbnFetch**
(`dbnf`) tool on NuGet, newest first. Breaking changes come first in each
release. The format follows [Keep a Changelog](https://keepachangelog.com), and
versions follow [Semantic Versioning](https://semver.org).

## Unreleased

### Breaking

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

- `CalendarRule`: the months a root holds and what its roll counts back from.
  Gold (`GC`, `MGC`) holds February, April, June, August and December and rolls
  8 days before the delivery month begins; other roots roll 8 days before
  expiry as before. ([#34](https://github.com/squideyes/DbnSuite/pull/34))

### Fixed

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
