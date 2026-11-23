"""What an archive holds, the days never fetched, and one day as a DataFrame.

Set DBNF_LOCATION to an S3 endpoint or a folder (and, for S3, DBNF_BUCKET, DBNF_ACCESS_KEY and
DBNF_SECRET_KEY), then run it from anywhere:

    pip install -e "Clients/python[dbn,pandas]"
    python Clients/python/examples/archive.py

The README and the docs quote this file; tests/test_examples.py runs it and checks they still do.
"""

from datetime import date
from dbnsuite import Archive

archive = Archive.from_env()  # DBNF_LOCATION, DBNF_BUCKET, DBNF_ACCESS_KEY, DBNF_SECRET_KEY
# or: Archive("https://nyc3.digitaloceanspaces.com", bucket="dbn", access_key=..., secret_key=...)
# or: Archive("/data/dbn"), a folder that dbnf pull --out filled

for s in archive.series("ES"):
    print(s.schema, s.days, s.empty_days, s.first, s.last)

archive.missing("mbo", "ES", date(2026, 10, 1), date(2026, 10, 31))  # never fetched

store = archive.open("mbo", "ES", date(2026, 10, 28))  # Databento's DBNStore; MissingDay if never fetched
if store is not None:  # None: a day with no data
    frame = store.to_df()
    print(frame.head())
