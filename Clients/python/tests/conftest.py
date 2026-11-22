import json
from pathlib import Path

import pytest

FIXTURES = Path(__file__).parent / "fixtures"
TAPES = FIXTURES / "tapes"
DATASET = "GLBX.MDP3"
ROOT = "ES"


def load_json(name: str):
    return json.loads((FIXTURES / name).read_text())


@pytest.fixture
def fixtures() -> Path:
    return FIXTURES
