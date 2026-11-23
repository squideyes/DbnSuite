"""Load a tape into numpy and pandas.

Run it from the folder that holds your tapes folder, such as one dbnf tape pull filled:

    pip install -e "Clients/python[pandas]"
    python path/to/Clients/python/examples/tapes.py

The README and the docs quote this file; tests/test_examples.py runs it and checks they still do.
"""

from dbnsuite import Tape

tape = Tape.load("tapes/GLBX.MDP3/l1/ES/2026/ES.l1.20261029.tape")
events = tape.events()  # numpy structured array: kind, time, bid_price, ask_price, ...
frame = tape.to_pandas()  # with a UTC timestamp and prices in dollars beside the ticks

print(f"{len(events):,} events, a tick is {tape.header.price_scale}")
print(frame.head())
