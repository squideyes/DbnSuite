"""Read a DbnSync archive of Databento files, and DbnTape tapes, from Python.

Contributed and largely untested: see the README. DbnSuite is an independent project with no
official connection with Databento, Inc., for data you are licensed to receive.
"""

from .archive import Archive, FolderStore, LayoutError, MissingDay, S3Store, Series, StoredDay
from .sampler import SessionDraw, SessionSampler
from .stream import SessionStream, StreamSession, tape_dates, tape_path
from .tape import L1Kind, Tape, TapeError, TapeHeader, TapeKind, TradeSide, parse_header

__version__ = "0.1.0"

__all__ = [
    "Archive",
    "FolderStore",
    "L1Kind",
    "LayoutError",
    "MissingDay",
    "S3Store",
    "Series",
    "SessionDraw",
    "SessionSampler",
    "SessionStream",
    "StoredDay",
    "StreamSession",
    "Tape",
    "TapeError",
    "TapeHeader",
    "TapeKind",
    "TradeSide",
    "parse_header",
    "tape_dates",
    "tape_path",
]
