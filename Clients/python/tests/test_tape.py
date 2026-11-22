"""Tapes against what C# made of them (PythonFixturesTests): the header and every event."""

import struct
import zlib
from datetime import date

import pytest
from conftest import DATASET, FIXTURES, ROOT, TAPES, load_json

from dbnsuite import Tape, TapeError, TapeKind, tape_path

DATES = [date(2026, 10, 27), date(2026, 10, 28), date(2026, 10, 29)]
KINDS = ["s1", "trade", "l1"]
CASES = [(kind, day) for kind in KINDS for day in DATES]


def expected(kind: str, day: date) -> dict:
    return load_json(f"expected/{ROOT}.{kind}.{day:%Y%m%d}.json")


@pytest.mark.parametrize("kind,day", CASES, ids=lambda c: str(c))
def test_a_tape_decodes_to_the_events_csharp_gives(kind, day):
    want = expected(kind, day)

    tape = Tape.load(tape_path(TAPES, DATASET, kind, ROOT, day))
    events = tape.events()

    rows = [[_plain(v) for v in row] for row in events.tolist()]
    assert rows == want["events"]


@pytest.mark.parametrize("kind,day", CASES, ids=lambda c: str(c))
def test_a_header_reads_as_csharp_reads_it(kind, day):
    want = expected(kind, day)["header"]

    h = Tape.load(tape_path(TAPES, DATASET, kind, ROOT, day)).header

    mark = want.pop("rth_start")
    assert int(h.kind) == want.pop("kind")
    assert h.trade_date.isoformat() == want.pop("trade_date")
    assert f"{h.body_hash:016x}" == want.pop("body_hash")
    assert {k: getattr(h, k) for k in want} == want
    assert {k: getattr(h.rth_start, k) for k in mark} == mark


def test_a_compressed_tape_is_unpacked():
    tape = Tape.load(tape_path(TAPES, DATASET, "l1", ROOT, date(2026, 10, 28)))

    assert tape.header.compressed
    assert len(tape.events()) == tape.header.event_count


def test_events_come_as_a_dataframe_with_prices():
    tape = Tape.load(tape_path(TAPES, DATASET, "trade", ROOT, date(2026, 10, 29)))

    frame = tape.to_pandas()

    assert len(frame) == tape.header.event_count
    assert frame["price_px"].iloc[0] == frame["price"].iloc[0] * 0.25
    assert str(frame["ts"].dt.tz) == "UTC"


def _bytes(kind="trade", day=date(2026, 10, 29)) -> bytearray:
    return bytearray(tape_path(TAPES, DATASET, kind, ROOT, day).read_bytes())


def _recrc(data: bytearray) -> bytearray:
    struct.pack_into("<I", data, 188, zlib.crc32(bytes(data[:188])))
    return data


def test_a_file_that_isnt_a_tape_is_refused():
    with pytest.raises(TapeError) as error:
        Tape.from_bytes(b"NOPE" + bytes(300))

    assert error.value.code == "tape.not_a_tape"


def test_a_damaged_header_fails_its_crc():
    data = _bytes()
    data[20] ^= 0xFF

    with pytest.raises(TapeError, match="CRC") as error:
        Tape.from_bytes(bytes(data))

    assert error.value.code == "tape.corrupt"


def test_a_damaged_body_fails_its_hash():
    data = _bytes()
    data[-1] ^= 0xFF

    with pytest.raises(TapeError, match="hash"):
        Tape.from_bytes(bytes(data))


def test_a_newer_version_is_not_supported():
    data = _bytes()
    data[4] = 2

    with pytest.raises(TapeError) as error:
        Tape.from_bytes(bytes(_recrc(data)))

    assert error.value.code == "tape.unsupported"


def test_a_truncated_body_is_refused():
    with pytest.raises(TapeError, match="length"):
        Tape.from_bytes(bytes(_bytes()[:-1]))


def test_an_unknown_flag_is_refused():
    data = _bytes()
    data[6] |= 0x80

    with pytest.raises(TapeError, match="flag"):
        Tape.from_bytes(bytes(_recrc(data)))


def test_events_that_overrun_the_body_are_refused():
    data = _bytes()
    count = struct.unpack_from("<q", data, 48)[0]
    struct.pack_into("<q", data, 48, count + 1)

    tape = Tape.from_bytes(bytes(_recrc(data)), verify_hash=False)

    with pytest.raises(TapeError, match="ends"):
        tape.events()


def test_the_kind_comes_from_the_header():
    assert Tape.load(tape_path(TAPES, DATASET, "s1", ROOT, DATES[0])).kind == TapeKind.S1


def _plain(value):
    return bool(value) if isinstance(value, bool) else int(value)
