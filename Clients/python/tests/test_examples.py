"""The examples run, on synthetic fixtures, and the README still quotes them word for word."""

import re
import runpy
from datetime import date
from pathlib import Path

import pytest

from dbnsuite import Archive, layout

from conftest import FIXTURES

EXAMPLES = Path(__file__).parent.parent / "examples"
README = Path(__file__).parent.parent / "README.md"


def run(name: str) -> None:
    runpy.run_path(str(EXAMPLES / name), run_name="__main__")


def lines(text: str) -> list[str]:
    """Non-blank lines with runs of spaces closed up, so alignment doesn't count as drift."""
    return [re.sub(r"\s+", " ", line).strip() for line in text.splitlines() if line.strip()]


def contains(haystack: list[str], needle: list[str]) -> bool:
    return any(haystack[i : i + len(needle)] == needle for i in range(len(haystack) - len(needle) + 1))


def test_tapes_loads_a_tape_into_numpy_and_pandas(monkeypatch, capsys):
    monkeypatch.chdir(FIXTURES)

    run("tapes.py")

    assert "events, a tick is" in capsys.readouterr().out


def test_sessions_deals_and_stitches_a_run(monkeypatch, capsys):
    monkeypatch.chdir(FIXTURES)

    run("sessions.py")

    out = capsys.readouterr().out
    assert "warm-up" in out and "trading" in out and "fingerprint" in out


def test_archive_lists_series_and_finds_a_day(monkeypatch, tmp_path, capsys):
    store = tmp_path / "archive"
    for day, key in [(date(2026, 10, 28), layout.data_key), (date(2026, 10, 31), layout.empty_key)]:
        path = store / key("GLBX.MDP3", "mbo", "ES", day)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"synthetic, not DBN")
    monkeypatch.setenv("DBNF_LOCATION", str(store))
    monkeypatch.setenv("DBNSUITE_CACHE", str(tmp_path / "cache"))
    opened = []
    # Opening a day is Databento's own package, which these bytes aren't for; the rest is ours.
    monkeypatch.setattr(Archive, "open", lambda self, schema, root, day: opened.append((schema, root, day)))

    run("archive.py")

    assert capsys.readouterr().out.startswith("mbo 1 1 2026-10-28 2026-10-28")
    assert opened == [("mbo", "ES", date(2026, 10, 28))]


@pytest.mark.parametrize("block", re.findall(r"```python\n(.*?)```", README.read_text(), re.S))
def test_the_readme_quotes_an_example(block):
    examples = [lines(path.read_text()) for path in EXAMPLES.glob("*.py")]

    assert any(contains(example, lines(block)) for example in examples), block
