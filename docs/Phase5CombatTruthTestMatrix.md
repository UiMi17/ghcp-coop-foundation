# Phase 5 Combat Truth Test Matrix

This checklist verifies host-authoritative combat truth after GHC extensions (`DamageState`, `UnitState`, `CrewState`, `HitResolved`, `CompartmentState`).

Wire overview and architecture: [CoopReplication.md](CoopReplication.md) (розділ **§6 GHC** — типи подій, дві черги на клієнті, prefs).

## Global Logging Setup

- `LogCombatReplication = true`
- `LogDamageState = true`
- `CombatApplyMaxPerFrame = 32`
- `CombatApplyMaxMsPerFrame = 4.0`

## T1 - Single hit, no kill

- Scenario: one AP hit to tracked vehicle, target remains operational.
- Expected host logs:
  - `GHC send Fired`
  - `GHC send Struck`
  - `GHC send DamageState`
  - `GHC send UnitState`
  - `GHC send CrewState` (if changed)
- Expected client logs:
  - `GHC recv DamageState ... destroyed=False`
  - `GHC recv UnitState ... flags=...`
  - `GHC recv CrewState ...`
- Pass criteria:
  - Client summary contains `damage-state recv > 0` and `damage-state applied > 0`.

## T2 - Kill path variants

- Scenario A: hard destroy (burn/detonation).
- Scenario B: incapacitation path (`CannotMove` + `CannotShoot` or crew incap).
- Scenario C: abandonment path (evacuation).
- Expected:
  - Host sends `UnitState` with relevant flags set.
  - Client applies matching `UnitState`.
  - `destroyParityMismatch = 0`.

## T3 - Burst pressure

- Scenario: repeated high-RoF impacts and spall-rich exchange for at least 60s.
- Expected:
  - No `apply failed` lines.
  - `maxPending` remains bounded and recovers.
  - `budgetHits` can be non-zero, but queue drains by session end.

## T4 - Ownership transition in combat

- Scenario: swap controlled unit while active engagements continue.
- Expected:
  - No regressions in `GHC recv` processing.
  - New controlled unit does not get remote state forced by stale packets.
  - No persistent `victim netId not found` spam.

## T5 - Long session drift (20 minutes)

- Scenario: mixed driving and combat for at least 20 minutes.
- Expected:
  - `seqGaps` limited and non-growing catastrophically.
  - Damage/neutralization outcomes remain consistent between host and client.
  - End summary shows non-zero recv/apply for `DamageState`, `UnitState`, `CrewState`.

## Closure Criteria

- `damage-state recv > 0` and `damage-state applied > 0`
- `unit-state recv > 0` and `unit-state applied > 0`
- `crew-state recv > 0` and `crew-state applied > 0`
- `destroyParityMismatch == 0`
- `GHC apply-fail struck=0 impactFx=0 damageState=0`
