"""Deals the sessions of Monte Carlo runs, as DbnSuite.Client's SessionSampler does."""

from __future__ import annotations

import struct
from dataclasses import dataclass
from datetime import date
from typing import Iterable, Literal

import xxhash

from ._random import SeededRandom

Sampling = Literal["permutation", "bootstrap"]


@dataclass(frozen=True)
class SessionDraw:
    """One session of a run, in the order it is replayed."""

    trade_date: date
    is_warmup: bool


def _day_number(day: date) -> int:
    """.NET's DateOnly.DayNumber: days since 0001-01-01."""
    return day.toordinal() - 1


class SessionSampler:
    """A run is its warm-up sessions, drawn at random with replacement, then its trading
    sessions: every day once in a random order (permutation), or drawn with replacement
    (bootstrap). The same days, seed and run always give the same sessions, here and in C#.
    """

    def __init__(
        self,
        days: Iterable[date],
        seed: int,
        sampling: Sampling = "permutation",
        warmup: int = 0,
        sessions: int | None = None,
    ) -> None:
        if warmup < 0:
            raise ValueError("Warm-up can't be negative.")
        if sampling not in ("permutation", "bootstrap"):
            raise ValueError(f"Unknown sampling {sampling!r}.")
        self.days: list[date] = sorted(set(days))
        if not self.days:
            raise ValueError("There are no days to deal from.")
        if sessions is not None and sampling == "permutation":
            raise ValueError("A permutation uses every day once, so it takes no count of sessions.")
        if sessions is not None and sessions <= 0:
            raise ValueError("A bootstrap run needs at least one session.")

        self.seed = seed
        self.sampling = sampling
        self.warmup = warmup
        self.sessions = sessions if sessions is not None else len(self.days)
        packed = struct.pack(f"<{len(self.days)}i", *(_day_number(d) for d in self.days))
        self.fingerprint = f"{xxhash.xxh3_64_intdigest(packed):016x}"
        """A hash of the days. Keep it with a seed: the same seed deals differently once the days change."""

    def run(self, run: int) -> list[SessionDraw]:
        """Run ``run``'s sessions: the warm-up ones, then the trading ones."""
        random = SeededRandom.for_run(self.seed, run)
        days = self.days
        draws = [SessionDraw(days[random.next_below(len(days))], True) for _ in range(self.warmup)]

        if self.sampling == "bootstrap":
            draws += [SessionDraw(days[random.next_below(len(days))], False) for _ in range(self.sessions)]
            return draws

        deck = list(days)
        for i in range(len(deck) - 1, 0, -1):
            j = random.next_below(i + 1)
            deck[i], deck[j] = deck[j], deck[i]

        return draws + [SessionDraw(d, False) for d in deck]
