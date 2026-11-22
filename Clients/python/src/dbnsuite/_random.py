"""xoshiro256**, seeded by SplitMix64, exactly as DbnSuite.Client's SeededRandom.

A seed and run give the same numbers here as in C#, on any machine, so a Monte Carlo run
deals the same sessions in both.
"""

from __future__ import annotations

_MASK = (1 << 64) - 1
_GOLDEN = 0x9E3779B97F4A7C15


def _mix(z: int) -> int:
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & _MASK
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & _MASK
    return z ^ (z >> 31)


def _rotl(x: int, k: int) -> int:
    return ((x << k) | (x >> (64 - k))) & _MASK


class SeededRandom:
    """The generator for one run of a seed."""

    __slots__ = ("_s0", "_s1", "_s2", "_s3")

    def __init__(self, seed: int) -> None:
        state = seed & _MASK
        words = []
        for _ in range(4):
            state = (state + _GOLDEN) & _MASK
            words.append(_mix(state))
        self._s0, self._s1, self._s2, self._s3 = words

    @classmethod
    def for_run(cls, seed: int, run: int) -> "SeededRandom":
        """Each run of a seed is its own stream, and needs no other."""
        if run < 0:
            raise ValueError("A run is 0 or more.")
        return cls((seed & _MASK) ^ _mix(run & _MASK))

    def next(self) -> int:
        """The next 64-bit number."""
        s0, s1, s2, s3 = self._s0, self._s1, self._s2, self._s3
        result = (_rotl((s1 * 5) & _MASK, 7) * 9) & _MASK
        t = (s1 << 17) & _MASK

        s2 ^= s0
        s3 ^= s1
        s1 ^= s2
        s0 ^= s3
        s2 ^= t
        s3 = _rotl(s3, 45)

        self._s0, self._s1, self._s2, self._s3 = s0, s1, s2, s3
        return result

    def next_below(self, count: int) -> int:
        """A number from 0 to count - 1, each equally likely."""
        if count <= 0:
            raise ValueError("The count must be positive.")
        threshold = ((1 << 64) - count) % count
        while True:
            x = self.next()
            if x >= threshold:
                return x % count
