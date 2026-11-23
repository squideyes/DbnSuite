"""Deal one Monte Carlo run of whole sessions, stitched end to end, as C# deals it.

Run it from the folder that holds your tapes folder:

    pip install -e Clients/python
    python path/to/Clients/python/examples/sessions.py

The README and the docs quote this file; tests/test_examples.py runs it and checks they still do.
"""

from dbnsuite import SessionSampler, SessionStream

stream = SessionStream("tapes", "GLBX.MDP3", "ES", "l1")
sampler = SessionSampler(stream.days(), seed=42, warmup=5)

for session in stream.run(sampler, run=0):
    events = session.tape.events()
    # add session.offset ticks to this session's prices to stitch it onto the one before
    kind = "warm-up" if session.is_warmup else "trading"
    print(f"{session.index:2} {session.trade_date} {kind:8} {len(events):6,} events, offset {session.offset:+}")

print("fingerprint", sampler.fingerprint)
