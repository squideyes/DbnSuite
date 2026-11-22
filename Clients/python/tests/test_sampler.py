"""The generator and sampler against vectors C# wrote (PythonFixturesTests)."""

from datetime import date

import pytest
from conftest import load_json

from dbnsuite import SessionSampler
from dbnsuite._random import SeededRandom

VECTORS = load_json("sampler.json")


@pytest.mark.parametrize("vector", VECTORS["random"], ids=lambda v: f"seed{v['seed']}-run{v['run']}")
def test_the_generator_gives_the_numbers_csharp_does(vector):
    random = SeededRandom.for_run(int(vector["seed"]), vector["run"])

    numbers = [str(random.next()) for _ in vector["next"]]
    below = [random.next_below(n) for n in range(1, 13)]

    assert numbers == vector["next"]
    assert below == vector["below_1_to_12"]


@pytest.mark.parametrize("case", VECTORS["cases"], ids=lambda c: f"{c['sampling']}-seed{c['seed']}")
def test_a_sampler_deals_the_sessions_csharp_does(case):
    days = [date.fromisoformat(d) for d in case["days"]]

    sampler = SessionSampler(days, int(case["seed"]), case["sampling"], case["warmup"], case["sessions"])

    assert sampler.fingerprint == case["fingerprint"]
    for run, expected in case["runs"].items():
        dealt = [[d.trade_date.isoformat(), d.is_warmup] for d in sampler.run(int(run))]
        assert dealt == expected


def test_the_order_and_repeats_of_days_dont_matter():
    days = [date(2026, 10, 2), date(2026, 10, 1), date(2026, 10, 2)]

    assert SessionSampler(days, 1).days == [date(2026, 10, 1), date(2026, 10, 2)]


def test_a_permutation_takes_no_count_of_sessions():
    with pytest.raises(ValueError):
        SessionSampler([date(2026, 10, 1)], 1, "permutation", sessions=3)


def test_there_must_be_days_to_deal():
    with pytest.raises(ValueError):
        SessionSampler([], 1)
