"""Sessions stitched as C#'s SessionStream stitches the same tapes (PythonFixturesTests)."""

import pytest
from conftest import DATASET, ROOT, TAPES, load_json

from dbnsuite import SessionSampler, SessionStream, tape_dates

VECTORS = load_json("stream.json")


@pytest.mark.parametrize("kind", ["s1", "trade", "l1"])
def test_a_run_stitches_with_the_offsets_csharp_gives(kind):
    want = VECTORS[kind]
    stream = SessionStream(TAPES, DATASET, ROOT, kind)
    sampler = SessionSampler(stream.days(), int(want["seed"]), warmup=want["warmup"])

    for run, expected in want["runs"].items():
        got = [[s.index, s.trade_date.isoformat(), s.is_warmup, s.offset] for s in stream.run(sampler, int(run))]
        assert got == expected


def test_the_tape_dates_are_found_oldest_first():
    dates = tape_dates(TAPES, DATASET, "l1", ROOT)

    assert [d.isoformat() for d in dates] == ["2026-10-27", "2026-10-28", "2026-10-29"]


def test_a_root_with_no_tapes_has_no_dates():
    assert tape_dates(TAPES, DATASET, "l1", "NQ") == []
