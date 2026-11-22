# dbnsuite for Python

Read a [DbnSync](https://dbnsuite.squideyes.com) archive of Databento® files, and DbnTape tapes,
from Python.

> **Contributed and largely untested.** This client is new. Its tests check it against the C#
> code on synthetic tapes and a stand-in archive, but nobody has yet used it for real research.
> It needs people who work in Python to adopt it, review it, try it on their own archives and
> tapes, and say what's wrong or missing: open an issue with the
> [`python` label](https://github.com/squideyes/DbnSuite/issues?q=label%3Apython), or send a pull
> request. Expect its API to change. Use it at your own risk.

DbnSuite is an independent project with no official connection with Databento, Inc. It is for
data you are licensed to receive; your Databento agreements govern storing, sharing and
redistributing it.

## What it does

- **Archive access.** It lists what an archive holds and finds the days that were never fetched.
  It downloads a day into a local cache and opens it with Databento's own `databento` package.
  The archive can be on S3 or in a folder, and the client follows the
  [data layout](https://dbnsuite.squideyes.com/docs/layout)'s rules: a missing day is never
  treated as a day without trading.
- **Tapes.** It reads S1, Trade and L1 tapes into numpy arrays or pandas DataFrames.
- **Monte Carlo runs.** It deals sessions with the same seeded shuffle as C#, so a seed gives the
  same sessions in both languages, and stitches them end to end with the same offsets.

It does **not** rebuild order books, make bars, back-adjust or write NinjaTrader files. For those,
run [`dbnf export`](https://dbnsuite.squideyes.com/docs/dbnfetch#export) and read its Parquet. One
engine, in C#, keeps every language's numbers the same.

## Install

It isn't on PyPI yet. From a clone of the repository:

```bash
pip install -e "Clients/python[dbn,pandas]"
```

`[dbn]` adds Databento's `databento` package, for opening raw days, and `[pandas]` adds pandas.

## Archive

```python
from datetime import date
from dbnsuite import Archive

archive = Archive.from_env()  # DBNF_LOCATION, DBNF_BUCKET, DBNF_ACCESS_KEY, DBNF_SECRET_KEY
# or: Archive("https://nyc3.digitaloceanspaces.com", bucket="dbn", access_key=..., secret_key=...)
# or: Archive("/data/dbn"), a folder that dbnf pull --out filled

for s in archive.series("ES"):
    print(s.schema, s.days, s.empty_days, s.first, s.last)

archive.missing("mbo", "ES", date(2026, 10, 1), date(2026, 10, 31))  # never fetched

store = archive.open("mbo", "ES", date(2026, 10, 28))  # Databento's DBNStore, or None for no data
frame = store.to_df()
```

## Tapes

```python
from dbnsuite import Tape

tape = Tape.load("tapes/GLBX.MDP3/l1/ES/2026/ES.l1.20261029.tape")
events = tape.events()  # numpy structured array: kind, time, bid_price, ask_price, ...
frame = tape.to_pandas()  # with a UTC timestamp and prices in dollars beside the ticks
```

Times are microseconds since the UNIX epoch. Prices are whole ticks; multiply by
`tape.header.price_scale` for the price. Every field keeps its last value until an event changes
it, as a C# cursor does. The decoder is plain Python, at about a second per hundred thousand
events, so an L1 day can take a while.

## Monte Carlo sessions

```python
from dbnsuite import SessionSampler, SessionStream

stream = SessionStream("tapes", "GLBX.MDP3", "ES", "l1")
sampler = SessionSampler(stream.days(), seed=42, warmup=5)

for session in stream.run(sampler, run=0):
    events = session.tape.events()
    # add session.offset ticks to this session's prices to stitch it onto the one before
```

`sampler.fingerprint` is a hash of the days. Keep it with a seed, because the same seed deals
differently once new tapes arrive.

## Tests

```bash
pip install -e "Clients/python[test]"
pytest Clients/python
```

The fixtures in `tests/fixtures` are synthetic: generated prices, not market data. C# writes them
and checks them in `PythonFixturesTests`. If the tape format or the sampler changes, run that test
with `DBNSUITE_WRITE_FIXTURES=1` and these tests must still pass.
