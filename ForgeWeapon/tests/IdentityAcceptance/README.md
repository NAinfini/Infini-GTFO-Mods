# Independent W1 identity acceptance

This suite consumes the **compiled** ForgeWeapon module and its shared Runtime SDK.
It does not source-link a second index/kernel or install GTFO hooks. Adapter inputs
and live-object predicates are explicitly synthetic; passing is not game verification.

## Run from the mod repository root

```powershell
python ForgeWeapon/tools/verify-identity-acceptance.py
python ForgeWeapon/tools/test-identity-mutations.py
```

The first command builds the actual module and executes the independent consumer.
The second builds an unmodified isolated copy first, then intentionally breaks six
identity checks in separate `.artifacts` copies. A compilation error is not counted
as successful mutation detection. Each mutant must fail its named regression test.
Neither command changes production C#, user game files, profile settings or Git state.

Every run uses a new output directory. `receipt.json` retains source hashes and
command exit codes; `tests.json` contains individual results. Input changes during
a run invalidate its stable acceptance claim. Existing evidence is not overwritten.

## Coverage and limits

The suite covers shared-provider registration, repeated observations, instance versus
resource identity, A-B-A ownership, owner life changes, wield/loading states, atomic
slot/definition rejection, live-predicate failure, epoch/authority/stop/disposal,
foreign tickets, callback reentrancy, retired-life replay protection, and bounded
active/history capacity. It does not implement or test actual ammo transactions,
spawn/deploy side effects, attack execution, multiplayer traffic or checkpoints.

`EquipmentUseTicket` is a process-local precondition, not a reservation, network
identity or permission grant. Native mapping, command-stage gates, ownership
protocols and the shared R5 ledger remain integration prerequisites. The original
20-case `tests/fixtures/w1-runtime-acceptance.json` is not marked game-verified by
this suite and its unexecuted cost/deployment cases remain pending.

Production identity code and `tests/Identity` have a concurrent owner. Results for this suite and the merged mutation set are recorded in
[the Weapon validation record](../../VALIDATION.md).
