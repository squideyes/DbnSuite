"""Tape folders, and runs of whole sessions stitched end to end, as DbnSuite.Client's SessionStream does."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date, datetime
from pathlib import Path
from typing import Iterator

from .sampler import SessionSampler
from .tape import KIND_NAMES, L1Kind, Tape, TapeKind


def _kind_name(kind: TapeKind | str) -> str:
    return kind if isinstance(kind, str) else KIND_NAMES[TapeKind(kind)]


def tape_path(tape_root: str | Path, dataset: str, kind: TapeKind | str, root: str, trade_date: date) -> Path:
    """Where a tape lives under a tape folder: {dataset}/{kind}/{root}/{yyyy}/{root}.{kind}.{yyyymmdd}.tape."""
    name = _kind_name(kind)
    return Path(tape_root, dataset, name, root, f"{trade_date:%Y}", f"{root}.{name}.{trade_date:%Y%m%d}.tape")


def _trade_date_of(name: str) -> date | None:
    parts = name.split(".")
    if len(parts) != 4 or parts[3] != "tape":
        return None
    try:
        return datetime.strptime(parts[2], "%Y%m%d").date()
    except ValueError:
        return None


def tape_dates(tape_root: str | Path, dataset: str, kind: TapeKind | str, root: str) -> list[date]:
    """The dates a root has tapes of one kind for, oldest first."""
    name = _kind_name(kind)
    folder = Path(tape_root, dataset, name, root)
    if not folder.is_dir():
        return []
    found = {_trade_date_of(p.name) for p in folder.rglob(f"{root}.{name}.*.tape")}
    return sorted(d for d in found if d is not None)


@dataclass(frozen=True)
class StreamSession:
    """One session of a run. Add ``offset`` ticks to its prices to stitch it onto the session before it."""

    index: int
    trade_date: date
    is_warmup: bool
    tape: Tape
    offset: int


def _ends(tape: Tape) -> tuple[int, int] | None:
    """The first and last trade price, in ticks: what sessions are stitched by."""
    events = tape.events()
    if tape.kind == TapeKind.S1:
        prices_first, prices_last = events["open"], events["close"]
    elif tape.kind == TapeKind.TRADE:
        prices_first = prices_last = events["price"]
    else:
        trades = events[events["kind"] == L1Kind.TRADE]
        prices_first = prices_last = trades["trade_price"]
    if len(prices_first) == 0:
        return None
    return int(prices_first[0]), int(prices_last[-1])


class SessionStream:
    """Whole sessions of a root's tapes, dealt by a sampler and stitched so each opens where the last closed."""

    def __init__(self, tape_root: str | Path, dataset: str, root: str, kind: TapeKind | str) -> None:
        self.tape_root = Path(tape_root)
        self.dataset = dataset
        self.root = root
        self.kind = kind if isinstance(kind, TapeKind) else {v: k for k, v in KIND_NAMES.items()}[kind]
        self._ends: dict[date, tuple[int, int]] = {}

    def days(self) -> list[date]:
        return tape_dates(self.tape_root, self.dataset, self.kind, self.root)

    def run(self, sampler: SessionSampler, run: int) -> Iterator[StreamSession]:
        offset = 0
        previous: tuple[int, int] | None = None
        tick_raw: int | None = None

        for i, draw in enumerate(sampler.run(run)):
            path = tape_path(self.tape_root, self.dataset, self.kind, self.root, draw.trade_date)
            tape = Tape.load(path)
            ends = self._ends.get(draw.trade_date)
            if ends is None:
                ends = _ends(tape)
                if ends is None:
                    raise ValueError(f"{path} has no trades, so its session can't be stitched.")
                self._ends[draw.trade_date] = ends
            if tick_raw is not None and tick_raw != tape.header.tick_raw:
                raise ValueError(
                    f"The {draw.trade_date:%Y-%m-%d} tape's tick is {tape.header.tick_raw}, and the run's is "
                    f"{tick_raw}, so its sessions can't be stitched in ticks."
                )
            if previous is not None:
                offset += previous[1] - ends[0]
            previous = ends
            tick_raw = tape.header.tick_raw
            yield StreamSession(i, draw.trade_date, draw.is_warmup, tape, offset)
