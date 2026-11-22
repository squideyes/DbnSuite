"""DbnTape files, format version 1: a 192-byte header, then one body of variable-length events.

Decoding follows DbnSuite.Client's cursors exactly. The format is set out at
https://dbnsuite.squideyes.com/docs/dbntape#format. Times are microseconds since the UNIX
epoch, prices are whole ticks (multiply by ``header.tick_raw / 1e9`` for the price), and every
field keeps its last value until an event changes it, as a cursor does.

This decoder is plain Python: about a second per hundred thousand events.
"""

from __future__ import annotations

import calendar
import struct
import zlib
from dataclasses import dataclass
from datetime import date
from enum import IntEnum
from pathlib import Path

import numpy as np
import xxhash
import zstandard

HEADER_SIZE = 192
CURRENT_VERSION = 1
_MAGIC = b"TAPE"
_ZSTD_FLAG = 1
_RTH_FLAG = 2
_EVENT_CLOCK = 1
_SECOND_UNIT = 1_000_000_000
_MICROSECOND_UNIT = 1_000
_MICROS_PER_SECOND = 1_000_000
_INT32_MIN, _INT32_MAX = -(1 << 31), (1 << 31) - 1
_UINT32_MAX = (1 << 32) - 1
_MAX_LENGTH = 0x7FFFFFC7  # .NET's Array.MaxLength


class TapeKind(IntEnum):
    S1 = 1
    TRADE = 2
    L1 = 3


class TradeSide(IntEnum):
    NONE = 0
    BUYER = 1
    SELLER = 2


class L1Kind(IntEnum):
    QUOTE = 0
    TRADE = 1
    STATE = 2


KIND_NAMES = {TapeKind.S1: "s1", TapeKind.TRADE: "trade", TapeKind.L1: "l1"}


class TapeError(Exception):
    """A tape that can't be read, with the same codes as C#: tape.not_a_tape, tape.unsupported, tape.corrupt."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def _corrupt(message: str) -> TapeError:
    return TapeError("tape.corrupt", message)


@dataclass(frozen=True)
class RthMark:
    """Everything a cursor needs to begin where RTH does."""

    body_offset: int
    event_index: int
    time: int
    bid_price: int
    ask_price: int
    bid_size: int
    ask_size: int
    last_price: int
    market_state: int


@dataclass(frozen=True)
class TapeHeader:
    kind: TapeKind
    compressed: bool
    has_rth: bool
    time_unit: int
    trade_date: date
    tick_raw: int
    open_time: int
    close_time: int
    base_price: int
    instrument_id: int
    event_count: int
    body_length: int
    stored_length: int
    body_hash: int
    dataset: str
    symbol: str
    root: str
    rth_start: RthMark
    rth_end_offset: int
    rth_end_index: int

    @property
    def price_scale(self) -> float:
        """Multiply ticks by this for the price."""
        return self.tick_raw / 1e9


def _time_unit(kind: TapeKind) -> int:
    return _SECOND_UNIT if kind == TapeKind.S1 else _MICROSECOND_UNIT


def _read_text(field: bytes) -> str | None:
    end = field.find(b"\0")
    if end < 0:
        end = len(field)
    value = field[:end]
    if end == 0 or any(b < 0x21 or b > 0x7E for b in value) or any(field[end:]):
        return None
    return value.decode("ascii")


def _read_date(value: int) -> date | None:
    year, month, day = value // 10_000, value // 100 % 100, value % 100
    if not 1 <= year <= 9999 or not 1 <= month <= 12 or day < 1 or day > calendar.monthrange(year, month)[1]:
        return None
    return date(year, month, day)


def parse_header(source: bytes) -> TapeHeader:
    """Reads and checks a header, as C# does. Anything wrong is a TapeError."""
    if source[:4] != _MAGIC:
        raise TapeError("tape.not_a_tape", "Not a tape: the file does not start with TAPE.")
    if len(source) < HEADER_SIZE:
        raise _corrupt("The file is shorter than a tape header.")
    if source[4] != CURRENT_VERSION:
        raise TapeError("tape.unsupported", f"Tape version {source[4]} is not supported.")
    if zlib.crc32(source[:188]) != struct.unpack_from("<I", source, 188)[0]:
        raise _corrupt("The header's CRC does not match.")
    if source[5] not in (1, 2, 3):
        raise TapeError("tape.unsupported", f"Tape kind {source[5]} is not supported.")
    kind = TapeKind(source[5])
    if source[7] != _EVENT_CLOCK:
        raise TapeError("tape.unsupported", f"Tape clock {source[7]} is not supported.")
    flags = source[6]
    if flags & ~(_ZSTD_FLAG | _RTH_FLAG):
        raise _corrupt("The header sets an unknown flag.")
    if struct.unpack_from("<I", source, 8)[0] != _time_unit(kind):
        raise _corrupt("The header's time unit does not suit its kind.")
    trade_date = _read_date(struct.unpack_from("<I", source, 12)[0])
    if trade_date is None:
        raise _corrupt("The header's trade date is not a date.")
    dataset, symbol, root = _read_text(source[80:96]), _read_text(source[96:112]), _read_text(source[112:120])
    if dataset is None or symbol is None or root is None:
        raise _corrupt("The header's names are not zero-padded ASCII.")

    tick_raw, open_time, close_time = struct.unpack_from("<qqq", source, 16)
    base_price, instrument_id = struct.unpack_from("<iI", source, 40)
    event_count, body_length, stored_length = struct.unpack_from("<qqq", source, 48)
    (body_hash,) = struct.unpack_from("<Q", source, 72)
    mark = RthMark(*struct.unpack_from("<qqqiiIIiB", source, 120))
    rth_end_offset, rth_end_index = struct.unpack_from("<qq", source, 168)
    compressed, has_rth = bool(flags & _ZSTD_FLAG), bool(flags & _RTH_FLAG)

    if tick_raw <= 0 or open_time < 0 or close_time < open_time:
        raise _corrupt("The header's tick or session times make no sense.")
    if event_count <= 0 or body_length < event_count or stored_length <= 0:
        raise _corrupt("The header's lengths make no sense.")
    if body_length > _MAX_LENGTH or stored_length > _MAX_LENGTH:
        raise _corrupt("The header states a body too large to load.")
    if not compressed and stored_length != body_length:
        raise _corrupt("An uncompressed body must store exactly its own length.")
    if struct.unpack_from("<I", source, 184)[0] != 0 or any(source[165:168]):
        raise _corrupt("The header's reserved bytes are not zero.")
    if has_rth:
        fits = (
            mark.body_offset >= 0
            and mark.event_index >= 0
            and mark.body_offset <= rth_end_offset
            and mark.event_index <= rth_end_index
            and rth_end_offset <= body_length
            and rth_end_index <= event_count
        )
    else:
        fits = not any(source[120:184])
    if not fits:
        raise _corrupt("The header's RTH marks do not fit the body.")

    return TapeHeader(
        kind=kind,
        compressed=compressed,
        has_rth=has_rth,
        time_unit=_time_unit(kind),
        trade_date=trade_date,
        tick_raw=tick_raw,
        open_time=open_time,
        close_time=close_time,
        base_price=base_price,
        instrument_id=instrument_id,
        event_count=event_count,
        body_length=body_length,
        stored_length=stored_length,
        body_hash=body_hash,
        dataset=dataset,
        symbol=symbol,
        root=root,
        rth_start=mark,
        rth_end_offset=rth_end_offset,
        rth_end_index=rth_end_index,
    )


S1_DTYPE = np.dtype(
    [("time", "<i8"), ("open", "<i4"), ("high", "<i4"), ("low", "<i4"), ("close", "<i4"), ("volume", "<u4"), ("in_rth", "?")]
)
TRADE_DTYPE = np.dtype([("time", "<i8"), ("price", "<i4"), ("size", "<u4"), ("side", "u1"), ("in_rth", "?")])
L1_DTYPE = np.dtype(
    [
        ("kind", "u1"),
        ("time", "<i8"),
        ("bid_price", "<i4"),
        ("ask_price", "<i4"),
        ("bid_size", "<u4"),
        ("ask_size", "<u4"),
        ("trade_price", "<i4"),
        ("trade_size", "<u4"),
        ("side", "u1"),
        ("state", "u1"),
        ("in_rth", "?"),
    ]
)


class _Body:
    """The body and a position in it, with the reading rules C#'s TapeReading and Varint keep."""

    __slots__ = ("data", "pos", "end")

    def __init__(self, data: bytes) -> None:
        self.data = data
        self.pos = 0
        self.end = len(data)

    def byte(self) -> int:
        if self.pos >= self.end:
            raise _corrupt("The body ends before its last event.")
        value = self.data[self.pos]
        self.pos += 1
        return value

    def varint(self) -> int:
        data, pos = self.data, self.pos
        if pos < self.end and data[pos] < 0x80:
            self.pos = pos + 1
            return data[pos]
        value = 0
        for shift in range(0, 70, 7):
            if pos >= self.end:
                raise _corrupt("The body ends inside a number.")
            b = data[pos]
            pos += 1
            if shift == 63 and b > 1:
                raise _corrupt("A number is too large for 64 bits.")
            value |= (b & 0x7F) << shift
            if b < 0x80:
                self.pos = pos
                return value
        raise _corrupt("A number is longer than ten bytes.")

    def zigzag(self) -> int:
        v = self.varint()
        return (v >> 1) ^ -(v & 1)

    def add_price(self, price: int) -> int:
        return _price(price + self.zigzag())

    def size(self) -> int:
        value = self.varint()
        if value > _UINT32_MAX:
            raise _corrupt("A size is out of range.")
        return value

    def add_size(self, size: int) -> int:
        value = size + self.zigzag()
        if value < 0 or value > _UINT32_MAX:
            raise _corrupt("A size is out of range.")
        return value

    def gap(self, bias: int) -> int:
        value = self.varint()
        if value > (1 << 63) - 1 - bias:
            raise _corrupt("A time gap is too large.")
        return value + bias


def _price(value: int) -> int:
    if value < _INT32_MIN or value > _INT32_MAX:
        raise _corrupt("A price is out of range.")
    return value


def _side(bits: int) -> int:
    if bits == 3:
        raise _corrupt("A trade uses the reserved side.")
    return bits


def _add_time(time: int, gap: int) -> int:
    if gap > (1 << 63) - 1 - time:
        raise _corrupt("A time gap runs past the end of time.")
    return time + gap


class Tape:
    """A tape loaded whole. ``events()`` decodes every event, as a C# cursor over the whole session would."""

    def __init__(self, header: TapeHeader, body: bytes) -> None:
        self.header = header
        self._body = body
        self._events: np.ndarray | None = None

    @classmethod
    def load(cls, path: str | Path, verify_hash: bool = True) -> "Tape":
        return cls.from_bytes(Path(path).read_bytes(), verify_hash)

    @classmethod
    def from_bytes(cls, data: bytes, verify_hash: bool = True) -> "Tape":
        header = parse_header(data)
        stored = data[HEADER_SIZE:]
        if len(stored) != header.stored_length:
            raise _corrupt("The file's length does not match its header.")
        if verify_hash and xxhash.xxh3_64_intdigest(stored) != header.body_hash:
            raise _corrupt("The body's hash does not match its header.")
        if not header.compressed:
            return cls(header, stored)
        try:
            body = zstandard.ZstdDecompressor().decompress(stored, max_output_size=header.body_length)
        except zstandard.ZstdError as problem:
            raise _corrupt(f"The body cannot be unpacked: {problem}") from problem
        if len(body) != header.body_length:
            raise _corrupt("The body unpacks to the wrong length.")
        return cls(header, body)

    @property
    def kind(self) -> TapeKind:
        return self.header.kind

    def events(self) -> np.ndarray:
        """Every event, as a numpy structured array whose fields depend on the kind."""
        if self._events is None:
            decode = {TapeKind.S1: self._s1, TapeKind.TRADE: self._trades, TapeKind.L1: self._l1}[self.kind]
            self._events = decode()
        return self._events

    def to_pandas(self):
        """The events as a DataFrame, with a UTC time and the prices in dollars beside the ticks."""
        import pandas as pd

        frame = pd.DataFrame(self.events())
        frame.insert(0, "ts", pd.to_datetime(frame["time"], unit="us", utc=True))
        scale = self.header.price_scale
        for column in ("open", "high", "low", "close", "price", "bid_price", "ask_price", "trade_price"):
            if column in frame:
                frame[column + "_px"] = frame[column] * scale
        return frame

    def _rth_bounds(self) -> tuple[int, int]:
        h = self.header
        return (h.rth_start.event_index, h.rth_end_index) if h.has_rth else (0, 0)

    def _done(self, body: _Body) -> None:
        if body.pos != self.header.body_length:
            raise _corrupt("The events do not end where the body says they do.")

    def _s1(self) -> np.ndarray:
        h = self.header
        body = _Body(self._body)
        after, through = self._rth_bounds()
        rows = []
        seconds = h.open_time // h.time_unit - 1
        close = h.base_price
        for index in range(1, h.event_count + 1):
            control = body.byte()
            step = body.gap(2) if control & 1 else 1
            seconds = _add_time(seconds, step)
            open_ = body.add_price(close) if control & 2 else close
            close = body.add_price(open_) if control & 4 else open_
            top, bottom = max(open_, close), min(open_, close)
            if control & 8:
                distance = body.varint()
                if distance > _UINT32_MAX:
                    raise _corrupt("A price is out of range.")
                high = _price(top + distance + 1)
            else:
                high = top
            if control & 16:
                distance = body.varint()
                if distance > _UINT32_MAX:
                    raise _corrupt("A price is out of range.")
                low = _price(bottom - distance - 1)
            else:
                low = bottom
            volume = control >> 5 or body.size()
            rows.append((seconds * _MICROS_PER_SECOND, open_, high, low, close, volume, after < index <= through))
        self._done(body)
        return np.array(rows, dtype=S1_DTYPE)

    def _trades(self) -> np.ndarray:
        h = self.header
        body = _Body(self._body)
        after, through = self._rth_bounds()
        rows = []
        time = h.open_time // h.time_unit
        price = h.base_price
        for index in range(1, h.event_count + 1):
            control = body.byte()
            side = _side(control & 3)
            if control & 4:
                time = _add_time(time, body.gap(0))
            if control & 8:
                price = body.add_price(price)
            size = control >> 4 or body.size()
            rows.append((time, price, size, side, after < index <= through))
        self._done(body)
        return np.array(rows, dtype=TRADE_DTYPE)

    def _l1(self) -> np.ndarray:
        h = self.header
        body = _Body(self._body)
        after, through = self._rth_bounds()
        rows = []
        time = h.open_time // h.time_unit
        bid = ask = trade_price = h.base_price
        bid_size = ask_size = trade_size = side = state = 0
        kind = L1Kind.QUOTE
        for index in range(1, h.event_count + 1):
            control = body.byte()
            if control & 2:
                time = _add_time(time, body.gap(0))
            if control & 1:
                kind = L1Kind.TRADE
                side = _side((control >> 2) & 3)
                if not control & 16:
                    trade_price = body.add_price(trade_price)
                elif side == TradeSide.BUYER:
                    trade_price = ask
                elif side == TradeSide.SELLER:
                    trade_price = bid
                else:
                    raise _corrupt("An implied trade has no side.")
                trade_size = control >> 5 or body.size()
            elif control & 0xC0:
                raise _corrupt("A quote sets a reserved bit.")
            elif control & 0x3C:
                kind = L1Kind.QUOTE
                bid_moved, ask_moved = control & 4, control & 8
                if bid_moved:
                    bid = body.add_price(bid)
                if ask_moved:
                    ask = body.add_price(ask)
                if control & 16:
                    bid_size = body.size() if bid_moved else body.add_size(bid_size)
                if control & 32:
                    ask_size = body.size() if ask_moved else body.add_size(ask_size)
            else:
                kind = L1Kind.STATE
                action = body.byte()
                if action == 0:
                    raise _corrupt("A state event has no action.")
                state = action
            rows.append(
                (kind, time, bid, ask, bid_size, ask_size, trade_price, trade_size, side, state, after < index <= through)
            )
        self._done(body)
        return np.array(rows, dtype=L1_DTYPE)
