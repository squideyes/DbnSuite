"""The archive's rules, over a folder and over a stand-in for S3. The files are synthetic bytes, not DBN."""

from datetime import date
from pathlib import Path

import pytest

from dbnsuite import Archive, LayoutError, MissingDay, S3Store
from dbnsuite import layout

DATASET = "GLBX.MDP3"
SCHEMA = "ohlcv-1s"
ROOT = "ES"
BYTES = b"not really dbn"


def put(folder: Path, key: str, body: bytes = BYTES) -> None:
    path = folder / key
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(body)


@pytest.fixture
def archive(tmp_path: Path) -> Archive:
    store = tmp_path / "archive"
    put(store, layout.data_key(DATASET, SCHEMA, ROOT, date(2026, 10, 29)))
    put(store, layout.empty_key(DATASET, SCHEMA, ROOT, date(2026, 10, 31)), b"")
    put(store, layout.data_key(DATASET, SCHEMA, ROOT, date(2026, 11, 2)))
    put(store, "_dbnsync/passes/2026-11-02/051433120.json", b"{}")
    put(store, f"{DATASET}/{SCHEMA}/{ROOT}/2026/notes.txt", b"ignored")
    return Archive(store, cache=tmp_path / "cache")


def test_keys_follow_the_grammar():
    key = layout.data_key(DATASET, "mbo", "MNQ", date(2026, 10, 28))

    parsed = layout.parse_key(key)

    assert key == "GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261028.dbn.zst"
    assert parsed == layout.DayKey(DATASET, "mbo", "MNQ", date(2026, 10, 28), False)


@pytest.mark.parametrize(
    "key",
    [
        "GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261028.dbn",
        "GLBX.MDP3/mbo/MNQ/2025/MNQ.mbo.20261028.dbn.zst",
        "GLBX.MDP3/mbo/MNQ/2026/MES.mbo.20261028.dbn.zst",
        "GLBX.MDP3/mbo/MNQ/2026/MNQ.mbo.20261332.dbn.zst",
        "_dbnsync/layout.json",
    ],
)
def test_keys_off_the_grammar_are_skipped(key):
    assert layout.parse_key(key) is None


def test_held_days_come_oldest_first_with_their_markers(archive):
    days = archive.days(SCHEMA, ROOT)

    assert [(d.day, d.empty) for d in days] == [
        (date(2026, 10, 29), False),
        (date(2026, 10, 31), True),
        (date(2026, 11, 2), False),
    ]


def test_a_missing_day_is_not_a_day_without_trading(archive):
    missing = archive.missing(SCHEMA, ROOT, date(2026, 10, 29), date(2026, 11, 2))

    assert missing == [date(2026, 10, 30), date(2026, 11, 1)]


def test_a_day_never_fetched_is_an_error_not_an_empty_day(archive):
    with pytest.raises(MissingDay):
        archive.path(SCHEMA, ROOT, date(2026, 10, 30))


def test_an_empty_day_has_no_file(archive):
    assert archive.path(SCHEMA, ROOT, date(2026, 10, 31)) is None


def test_a_cached_day_is_not_downloaded_again(archive, monkeypatch):
    first = archive.path(SCHEMA, ROOT, date(2026, 10, 29))
    monkeypatch.setattr(archive.store, "download", lambda *_: pytest.fail("downloaded again"))

    again = archive.path(SCHEMA, ROOT, date(2026, 10, 29))

    assert again == first
    assert again.read_bytes() == BYTES
    assert again.is_relative_to(archive.cache)


def test_a_cached_file_that_no_longer_matches_is_downloaded_again(archive):
    first = archive.path(SCHEMA, ROOT, date(2026, 10, 29))
    first.write_bytes(b"damaged")

    again = archive.path(SCHEMA, ROOT, date(2026, 10, 29))

    assert again.read_bytes() == BYTES


def test_both_data_and_a_marker_for_one_day_breaks_the_layout(archive, tmp_path):
    put(tmp_path / "archive", layout.empty_key(DATASET, SCHEMA, ROOT, date(2026, 10, 29)), b"")

    with pytest.raises(LayoutError):
        archive.days(SCHEMA, ROOT)


def test_the_series_say_what_the_archive_holds(archive):
    (series,) = archive.series()

    assert (series.schema, series.root, series.days, series.empty_days) == (SCHEMA, ROOT, 2, 1)
    assert (series.first, series.last) == (date(2026, 10, 29), date(2026, 11, 2))
    assert series.size == 2 * len(BYTES)


class FakeS3:
    """Just enough of a boto3 S3 client: a paginated listing and a download."""

    def __init__(self, objects: dict[str, bytes]) -> None:
        self.objects = objects
        self.calls = []

    def get_paginator(self, name):
        assert name == "list_objects_v2"
        return self

    def paginate(self, **kwargs):
        self.calls.append(kwargs)
        prefix, delimiter = kwargs["Prefix"], kwargs.get("Delimiter")
        keys = sorted(k for k in self.objects if k.startswith(prefix))
        if delimiter:
            folders = sorted({prefix + k[len(prefix):].split("/")[0] + "/" for k in keys if "/" in k[len(prefix):]})
            return [{"CommonPrefixes": [{"Prefix": f} for f in folders]}]
        contents = [{"Key": k, "Size": len(self.objects[k]), "ETag": '"abc"'} for k in keys]
        return [{"Contents": contents[:1]}, {"Contents": contents[1:]}]

    def download_file(self, bucket, key, target):
        Path(target).write_bytes(self.objects[key])


def test_an_s3_archive_reads_under_its_prefix_across_pages(tmp_path):
    key = "archive/" + layout.data_key(DATASET, SCHEMA, ROOT, date(2026, 10, 29))
    marker = "archive/" + layout.empty_key(DATASET, SCHEMA, ROOT, date(2026, 10, 31))
    client = FakeS3({key: BYTES, marker: b""})
    store = S3Store("https://s3.example.com", "dbn", "key", "secret", prefix="archive", client=client)
    archive = Archive("https://s3.example.com", store=store, cache=tmp_path / "cache")

    days = archive.days(SCHEMA, ROOT)
    path = archive.path(SCHEMA, ROOT, date(2026, 10, 29))

    assert [d.day for d in days] == [date(2026, 10, 29), date(2026, 10, 31)]
    assert days[0].etag == "abc"
    assert path.read_bytes() == BYTES
    assert archive.schemas() == [SCHEMA]
    assert archive.roots(SCHEMA) == [ROOT]
    assert client.calls[0]["Bucket"] == "dbn"


def test_an_archive_comes_from_dbnf_variables(monkeypatch, tmp_path):
    monkeypatch.setenv("DBNF_LOCATION", str(tmp_path))
    monkeypatch.setenv("DBNF_BUCKET", "market-data")

    archive = Archive.from_env(cache=tmp_path / "cache")

    assert archive.store.root == tmp_path


def test_without_a_location_there_is_no_archive(monkeypatch):
    monkeypatch.delenv("DBNF_LOCATION", raising=False)

    with pytest.raises(ValueError):
        Archive.from_env()


def test_the_region_is_always_named_so_aws_region_cant_change_it(monkeypatch):
    monkeypatch.setenv("AWS_REGION", "us-east-2")

    store = S3Store("https://s3.example.com", "dbn", "key", "secret")

    assert store.client.meta.region_name == "us-east-1"
