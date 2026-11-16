# Executable model examples

These are maintainer consumers of A19's Python model tool. They create no native
runtime, C# application API or NuGet package. Python 3.9 or newer is sufficient.

From the repository root:

```sh
python3 samples/api/verification-models/minimal.py
python3 samples/api/verification-models/advanced.py
```

The minimal example checks the corrected mailbox and prints its finite progress
bound. The advanced example reproduces the deliberately unsafe stale-unschedule
race, round-trips and replays its JSON trace, and checks that a state-budget limit
is **inconclusive**, never a pass.

Sample evidence state: `product-executed` **for the maintainer model tool only**,
Linux x64. Exact source identities, commands, stdout/stderr and results are
recorded by `tests/Verification.Models/run.py` in its selected evidence directory.
No C# consumer, actual Engine, Native AOT, Windows or package-consumption outcome
is established by these examples. See
[the model guide](../../../docs/guides/verification-models.md) for assumptions,
full-suite acceptance and later implementation refinement.
