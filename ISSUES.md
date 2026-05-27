# Known Issues

Living document for problems we've found but haven't fixed yet. Lower-friction than GitHub Issues — just edit this file when you discover something or finish investigating one.

**Status legend:**
- 🔴 Open — known broken, needs work
- 🟡 Investigating — partial info, not yet fixed
- 🟢 Mitigated — workaround in place, real fix deferred
- ✅ Fixed — resolved, kept here for posterity

---

## 🟡 Population accessor returns slot-capacity, not current count

**Discovered**: 2026-05-27 while building the per-bot status writer.

**Symptom**: `_Player::Kids()`, `_Player::Workers()`, `_Player::Scientists()` (and equivalently HFL's `OP2Player` struct fields at offsets 148/152/156) return constant values `256 / 4096 / 4096` regardless of game state:
- Both AI bots return identical numbers (would diverge if real)
- Numbers don't change across hours of play
- Math always works out: `numAvailable + numRequired == 4096` exactly — a partition of a fixed cap
- 256, 4096 are powers of 2 — classic bit-field limit signatures

**Conclusion**: Those fields contain **per-player workforce slot capacity** (the max workers a player slot can theoretically hold), not the colony's actual population. OP2 must store real population elsewhere.

**What works instead** (in the same `OP2Player` struct):
- `numWorkersRequired` — workers currently assigned to buildings (real)
- `numScientistsRequired` — scientists currently assigned to buildings (real)
- `numScientistsAssignedToResearch` — researching count (real)
- `numScientistsAsWorkers` — scientists working as workers (real)

**Current workaround**: `BotPlayer_<N>_Status.txt` shows a `WORKFORCE` section with only the trusted fields. The misleading `Kids / Workers / Scientists / total` lines are gone.

**Path to a real fix**:
1. Set up cTest in OP2 1.3.6 (clean debug environment, no OPULauncher patching)
2. Wait until populations diverge between Eden and Plymouth (around 10–20 min play time)
3. Locate `playerArray` base address via debugger
4. Scan `playerArray[0]` for Eden's kid/worker/scientist counts and `playerArray[1]` for Plymouth's
5. Matching offsets are the real fields
6. Add them to HFL's `OP2Player` struct in `NativeMissionSDK/NativeSDK/HFL/Source/PlayerEx.cpp`
7. Add accessor methods to PlayerEx and corresponding `PlayerEx_GetX` exports in DotNetInterop
8. Hook them into `PlayerState` and the status writer

The real fields are likely buried in HFL's `unk1[5]`, `unk2[17]`, `unk3[47]`, or the giant `unk4[662]` arrays. A targeted memory scan with known divergent values should pinpoint them quickly.

**Note on prior attempts**: The user has a memory-reader for OP2 that found population values but only worked for player 0. That was probably looking at the UI-display singleton (which OP2 only computes for the local player), not the per-player playerArray. The playerArray approach should work for all players.

**Code references**:
- `NativeMissionSDK/NativeSDK/HFL/Source/PlayerEx.cpp` lines 4–63: `OP2Player` struct with documented offsets
- `DotNetMissionSDK/HFL/PlayerEx.cs`: `GetKids()/GetWorkers()/GetScientists()` accessors (currently work but return wrong field)
- `DotNetMissionSDK/MissionSDK/AI/BotPlayer.cs:128`: comment in `WriteStatus` explaining the current limitation

---

## 🟡 BuildStructureTask retries same blocked location

**Discovered**: 2026-05-27 while watching Plymouth try to build a Tokamak.

**Symptom**: When the chosen build location turns out to be unbuildable (terrain, blocking unit, etc.), `BuildStructureTask` keeps reissuing `DoBuild` at the same coordinates every ~90 ticks until something clears. Bot can stall indefinitely if the obstacle is persistent.

**Example from log**:
```
[14:07:29 t=3324] BuildStructureTask(Tokamak): issuing DoBuild at (145,84) convec=31
[14:07:37 t=3414] BuildStructureTask(Tokamak): issuing DoBuild at (145,84) convec=31
[14:07:45 t=3504] BuildStructureTask(Tokamak): issuing DoBuild at (145,84) convec=31
```

Same convec, same target, every ~90 ticks (the 30-tick DoBuild throttle correctly allows re-attempts spaced out, but the bot doesn't pick a new tile).

**Path to fix**: in `BuildStructureTask.PerformTask` (or `IsValidTile` callback), track recently-failed locations and exclude them from the next `Pathfinder.GetClosestValidTile` search for some cooldown window. Probably ~10 LOC.

**Code references**:
- `DotNetMissionSDK/MissionSDK/AI/Tasks/Base/Structure/BuildStructureTask.cs` around the `Pathfinder.GetClosestValidTile` call

---

## 🟡 BotType weight tables stubbed — all 10 personalities behave identically

**Discovered**: 2026-05-26 during AI code review.

**Symptom**: `BaseManager.cs:88` has a switch case for each `BotType` (`PopulationGrowth`, `LaunchStarship`, `EconomicGrowth`, etc.) but every case is empty. So no matter which BotType the .opm assigns, all bots use the same default goal weights and behave identically.

**Path to fix**: fill in each switch case with a weight table that biases the 15 top-level goals appropriately for that personality. Per-personality is ~15 LOC of weight assignments.

**Code references**:
- `DotNetMissionSDK/MissionSDK/AI/Managers/BaseManager.cs:88` — the `switch (botPlayer.botType)` block

---

## 🟡 Combat unit selection is first-fit by build order

**Discovered**: 2026-05-26 during AI code review.

**Symptom**: `CombatManager.PopulateCombatGroups` fills `VehicleGroup` slots by picking the first matching unit type in build order (Lynx → Panther → Tiger → Spider → Scorpion). Doesn't score weapon-vs-threat or unit-strength-vs-zone-need.

**Path to fix**: replace first-fit with a scoring function that weighs weapon type, hp, speed, target zone composition.

**Code references**:
- `DotNetMissionSDK/MissionSDK/AI/Managers/CombatManager.cs` — `PopulateCombatGroups` method

---

## 🟡 IsTaskComplete in BuildStructureTask always returns false

**Discovered**: 2026-05-27 during diagnostic log analysis.

**Symptom**: The `BuildStructureTask.IsTaskComplete(StateSnapshot)` method has a long comment block then returns `false` unconditionally. Means the task is never marked complete, so consuming code (MaintainStructure tasks etc.) always re-evaluates. Contributes to the DoBuild spam pattern we throttled.

**Code reference**:
- `DotNetMissionSDK/MissionSDK/AI/Tasks/Base/Structure/BuildStructureTask.cs:46` — `// Task is never complete. Always try to build another one.`

**Path to fix**: make `IsTaskComplete` return `true` once an instance of `m_KitToBuild` has been deployed at the target area. Risk: may break the goal that genuinely wants to build MULTIPLE copies — needs careful testing against MaintainStructureTask behavior.

---

## ✅ UnitEx P/Invoke crash (fixed in 06b40a1)

**Symptom**: OP2 hard-crashed at ~25 min runtime with `System.EntryPointNotFoundException` for `UnitEx_GetUnknownValue`.

**Cause**: C# `UnitEx.cs` declared P/Invoke for `UnitEx_GetUnknownValue` and `UnitEx_SetUnknownValue` but `DotNetInterop.dll` never exported them. C# call → missing entry point → exception → escaped C++/CLI shim → OP2 process termination.

**Fix**: Added stub exports in `NativeMissionSDK/DotNetInterop/HFL/UnitEx.cpp`. Get returns 0, Set is a no-op. Mission survives; the AI's launchpad cargo transfer logic is functionally a no-op (so starship modules don't actually load on the pad), but everything else works.

**Proper fix deferred**: requires knowing what offset 46 of the OP2 unit struct actually represents. Probably `UnitEx_GetLaunchPadCargo` is the right delegate target, but uncertain.

---

## How to add a new issue

Copy the structure of an existing entry. Keep it short — what's broken, what we know, where the code is, what'd it take to fix. Don't worry about polish; this is a living dev notebook, not customer-facing.
