"""A DbnSync archive, over S3 or in a folder, read by the layout's rules.

- A day is held when its ``.dbn.zst`` or its ``.empty`` marker exists. A missing day was never
  fetched: it is *not* a day without trading. Only ``.empty`` says that.
- Data objects never change once stored, so the cache keeps each by key and ETag indefinitely.
- Every S3 call names the region, so ``AWS_REGION`` on the machine can't change what is sent.
"""

from __future__ import annotations

import os
import shutil
import sys
from dataclasses import dataclass
from datetime import date
from hashlib import sha256
from pathlib import Path
from typing import Any, Iterable, Iterator, Protocol

from . import layout

DEFAULT_DATASET = "GLBX.MDP3"
DEFAULT_BUCKET = "dbn"
DEFAULT_REGION = "us-east-1"


class LayoutError(ValueError):
    """The archive breaks a layout rule, so its days can't be trusted."""


class MissingDay(LookupError):
    """The day was never fetched into the archive, so nothing is known about it."""


@dataclass(frozen=True)
class StoredObject:
    key: str
    size: int
    etag: str | None


@dataclass(frozen=True)
class StoredDay:
    """One UTC day of a schema and root: its data, or its empty marker."""

    day: date
    key: str
    size: int
    etag: str | None
    empty: bool


@dataclass(frozen=True)
class Series:
    """A schema of a root, as ``dbnf ls`` shows it."""

    schema: str
    root: str
    days: int
    empty_days: int
    size: int
    first: date | None
    last: date | None


class Store(Protocol):
    def list(self, prefix: str) -> Iterator[StoredObject]: ...

    def folders(self, prefix: str) -> list[str]: ...

    def download(self, key: str, target: Path) -> None: ...


class S3Store:
    """An S3-compatible bucket, path-style, as DbnSync writes it."""

    def __init__(
        self,
        location: str,
        bucket: str,
        access_key: str | None,
        secret_key: str | None,
        region: str = DEFAULT_REGION,
        prefix: str = "",
        client: Any = None,
    ) -> None:
        self.bucket = bucket
        self.prefix = prefix.strip("/") + "/" if prefix.strip("/") else ""
        if client is None:
            import boto3
            from botocore.config import Config

            client = boto3.client(
                "s3",
                endpoint_url=location,
                aws_access_key_id=access_key,
                aws_secret_access_key=secret_key,
                region_name=region,
                config=Config(signature_version="s3v4", s3={"addressing_style": "path"}),
            )
        self.client = client

    def list(self, prefix: str) -> Iterator[StoredObject]:
        pages = self.client.get_paginator("list_objects_v2").paginate(Bucket=self.bucket, Prefix=self.prefix + prefix)
        for page in pages:
            for item in page.get("Contents", []):
                yield StoredObject(item["Key"][len(self.prefix):], item["Size"], item.get("ETag", "").strip('"') or None)

    def folders(self, prefix: str) -> list[str]:
        pages = self.client.get_paginator("list_objects_v2").paginate(
            Bucket=self.bucket, Prefix=self.prefix + prefix, Delimiter="/"
        )
        found = []
        for page in pages:
            for common in page.get("CommonPrefixes", []):
                found.append(common["Prefix"][len(self.prefix) + len(prefix):].rstrip("/"))
        return found

    def download(self, key: str, target: Path) -> None:
        self.client.download_file(self.bucket, self.prefix + key, str(target))


class FolderStore:
    """A folder in the archive's layout, such as one ``dbnf pull --out`` filled."""

    def __init__(self, root: str | Path) -> None:
        self.root = Path(root)

    def list(self, prefix: str) -> Iterator[StoredObject]:
        base = self.root / prefix
        if not base.is_dir():
            return
        for path in sorted(base.rglob("*")):
            if path.is_file():
                yield StoredObject(path.relative_to(self.root).as_posix(), path.stat().st_size, None)

    def folders(self, prefix: str) -> list[str]:
        base = self.root / prefix
        return sorted(p.name for p in base.iterdir() if p.is_dir()) if base.is_dir() else []

    def download(self, key: str, target: Path) -> None:
        shutil.copyfile(self.root / key, target)


def default_cache() -> Path:
    """DBNSUITE_CACHE, else the platform's cache folder, in a dbnsuite folder of its own."""
    if os.environ.get("DBNSUITE_CACHE"):
        return Path(os.environ["DBNSUITE_CACHE"])
    if sys.platform == "win32":
        return Path(os.environ.get("LOCALAPPDATA", Path.home())) / "dbnsuite" / "cache"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Caches" / "dbnsuite"
    xdg = os.environ.get("XDG_CACHE_HOME")
    return (Path(xdg) if xdg and Path(xdg).is_absolute() else Path.home() / ".cache") / "dbnsuite"


class Archive:
    """A DbnSync archive. ``location`` is an S3 endpoint (``https://...``) or a folder."""

    def __init__(
        self,
        location: str | Path,
        *,
        bucket: str = DEFAULT_BUCKET,
        access_key: str | None = None,
        secret_key: str | None = None,
        region: str = DEFAULT_REGION,
        prefix: str = "",
        dataset: str = DEFAULT_DATASET,
        cache: str | Path | None = None,
        store: Store | None = None,
    ) -> None:
        text = str(location)
        is_s3 = text.startswith(("http://", "https://"))
        self.dataset = dataset
        self.store: Store = store or (
            S3Store(text, bucket, access_key, secret_key, region, prefix) if is_s3 else FolderStore(text)
        )
        name = f"{text}|{bucket}|{prefix}" if is_s3 else str(Path(text).resolve())
        self.cache = Path(cache) if cache else default_cache() / sha256(name.encode()).hexdigest()[:12]

    @classmethod
    def from_env(cls, **overrides: Any) -> "Archive":
        """From DBNF_LOCATION, DBNF_BUCKET, DBNF_ACCESS_KEY and DBNF_SECRET_KEY, as dbnf reads them."""
        location = overrides.pop("location", None) or os.environ.get("DBNF_LOCATION")
        if not location:
            raise ValueError("Set DBNF_LOCATION, or pass location.")
        settings = {
            "bucket": os.environ.get("DBNF_BUCKET", DEFAULT_BUCKET),
            "access_key": os.environ.get("DBNF_ACCESS_KEY"),
            "secret_key": os.environ.get("DBNF_SECRET_KEY"),
        }
        settings.update(overrides)
        return cls(location, **settings)

    def schemas(self) -> list[str]:
        return self.store.folders(f"{self.dataset}/")

    def roots(self, schema: str) -> list[str]:
        return self.store.folders(f"{self.dataset}/{schema}/")

    def days(self, schema: str, root: str, start: date | None = None, end: date | None = None) -> list[StoredDay]:
        """The days held, oldest first, from ``start`` to ``end`` inclusive."""
        found: dict[date, StoredDay] = {}
        for item in self.store.list(layout.folder(self.dataset, schema, root)):
            parsed = layout.parse_key(item.key)
            if parsed is None or parsed.schema != schema or parsed.root != root or parsed.dataset != self.dataset:
                continue
            if (start and parsed.day < start) or (end and parsed.day > end):
                continue
            if parsed.day in found:
                raise LayoutError(f"{schema} {root} {parsed.day} has both a data object and an empty marker.")
            found[parsed.day] = StoredDay(parsed.day, item.key, item.size, item.etag, parsed.empty)
        return [found[d] for d in sorted(found)]

    def missing(self, schema: str, root: str, start: date, end: date) -> list[date]:
        """Days from ``start`` to ``end`` that were never fetched: neither data nor an empty marker."""
        held = {d.day for d in self.days(schema, root, start, end)}
        span = (end - start).days + 1
        return [d for d in (date.fromordinal(start.toordinal() + i) for i in range(span)) if d not in held]

    def series(self, root: str | None = None) -> list[Series]:
        """What the archive holds, a line for each schema and root."""
        lines = []
        for schema in self.schemas():
            for r in self.roots(schema):
                if root and r != root:
                    continue
                days = self.days(schema, r)
                data = [d for d in days if not d.empty]
                lines.append(
                    Series(
                        schema,
                        r,
                        len(data),
                        len(days) - len(data),
                        sum(d.size for d in data),
                        data[0].day if data else None,
                        data[-1].day if data else None,
                    )
                )
        return lines

    def path(self, schema: str, root: str, day: date) -> Path | None:
        """A day's file in the local cache, downloading it if need be; None for a day with no data."""
        stored = self.days(schema, root, day, day)
        if not stored:
            raise MissingDay(f"{schema} {root} {day} was never fetched into the archive.")
        held = stored[0]
        if held.empty:
            return None
        target = self.cache / held.key
        tag = target.with_name(target.name + ".etag")
        if target.exists() and target.stat().st_size == held.size and _read(tag) == (held.etag or ""):
            return target
        target.parent.mkdir(parents=True, exist_ok=True)
        partial = target.with_name(target.name + ".part")
        self.store.download(held.key, partial)
        os.replace(partial, target)
        tag.write_text(held.etag or "")
        return target

    def paths(self, schema: str, root: str, start: date, end: date) -> Iterable[tuple[date, Path | None]]:
        """Each held day from ``start`` to ``end``, with its cached file or None for no data."""
        for held in self.days(schema, root, start, end):
            yield held.day, self.path(schema, root, held.day)

    def open(self, schema: str, root: str, day: date):
        """A day as Databento's own DBNStore (``pip install dbnsuite[dbn]``), or None for no data."""
        import databento

        path = self.path(schema, root, day)
        return None if path is None else databento.DBNStore.from_file(path)


def _read(path: Path) -> str | None:
    try:
        return path.read_text()
    except OSError:
        return None
