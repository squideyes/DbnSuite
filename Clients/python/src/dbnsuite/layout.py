"""The raw archive's keys, version 1 of the layout: https://dbnsuite.squideyes.com/docs/layout

    {dataset}/{schema}/{root}/{yyyy}/{root}.{schema}.{yyyymmdd}.dbn.zst   one UTC day, Databento's bytes
    {dataset}/{schema}/{root}/{yyyy}/{root}.{schema}.{yyyymmdd}.empty     asked for, no data

Anything else, such as _dbnsync/, is skipped.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import date, datetime

DATA_SUFFIX = ".dbn.zst"
EMPTY_SUFFIX = ".empty"
LAYOUT_KEY = "_dbnsync/layout.json"

_KEY = re.compile(
    r"^(?P<dataset>[A-Z0-9.]+)/(?P<schema>[a-z0-9-]+)/(?P<root>[A-Z0-9]{1,6})/(?P<year>\d{4})/"
    r"(?P<root2>[A-Z0-9]{1,6})\.(?P<schema2>[a-z0-9-]+)\.(?P<day>\d{8})(?P<suffix>\.dbn\.zst|\.empty)$"
)


@dataclass(frozen=True)
class DayKey:
    """What a data or empty key says."""

    dataset: str
    schema: str
    root: str
    day: date
    empty: bool


def folder(dataset: str, schema: str, root: str) -> str:
    return f"{dataset}/{schema}/{root}/"


def data_key(dataset: str, schema: str, root: str, day: date) -> str:
    return f"{folder(dataset, schema, root)}{day:%Y}/{root}.{schema}.{day:%Y%m%d}{DATA_SUFFIX}"


def empty_key(dataset: str, schema: str, root: str, day: date) -> str:
    return f"{folder(dataset, schema, root)}{day:%Y}/{root}.{schema}.{day:%Y%m%d}{EMPTY_SUFFIX}"


def parse_key(key: str) -> DayKey | None:
    """The day a key holds, or None for any key that doesn't follow the grammar."""
    match = _KEY.match(key)
    if match is None:
        return None
    g = match.groupdict()
    if g["root"] != g["root2"] or g["schema"] != g["schema2"] or g["year"] != g["day"][:4]:
        return None
    try:
        day = datetime.strptime(g["day"], "%Y%m%d").date()
    except ValueError:
        return None
    return DayKey(g["dataset"], g["schema"], g["root"], day, g["suffix"] == EMPTY_SUFFIX)
