# Known Issues

Living document for problems we've found but haven't fixed yet. Lower-friction than GitHub Issues — just edit this file when you discover something or finish investigating one.

**Status legend:**
- 🔴 Open — known broken, needs work
- 🟡 Investigating — partial info, not yet fixed
- 🟢 Mitigated — workaround in place, real fix deferred
- ✅ Fixed — resolved, kept here for posterity

---

## ✅ AI players run in OP2 god-mode for population/food (engine behavior, not a bug)

**Investigated**: 2026-05-27. Supersedes the earlier "slot-capacity sentinels" diagnosis, which was wrong - HFL's `GetKids/Workers/Scientists` are correct.

**The behavior**: When a player slot is `"IsHuman": false`, OP2's engine pegs that player's population to fixed defaults (`256 kids / 4096 workers / 4096 scientists` = 8448 total colonists) every tick and skips food simulation entirely (`food consumed = 0` regardless of building count). The .opm `Resources.Kids/Workers/Scientists` values are ignored for AI seats. Human seats use the .opm values normally.

**Why this is intentional**: The AI never starves, never runs out of workers, never has morale crashes. Bots can focus purely on base-building / military / starship and don't need population-management logic to be viable opponents. It's a design choice baked into OP2 since 1997.

**Verified end-to-end**:
1. With Player 0 = `IsHuman: true`, memory reader confirms `10 kids / 44 workers / 23 scientists = 77 total colonists` matching the .opm exactly, with food consumed = 77 (1/colonist).
2. With Player 0 = `IsHuman: false`, memory reader and HFL both show `256/4096/4096 = 8448` with food consumed = 0.
3. Calling `SetKids/SetWorkers/SetScientists` on an AI seat post-init **does** write the values (read-back on the same tick confirms - see `BotPlayer_<N>.txt` `Deferred resource re-apply attempt:` block), but OP2 reverts them within the same Mark. So a per-init write is futile; a per-tick write would have to fight OP2's reset every cycle.

**Design implications**:
- **AI vs AI**: fair - both sides have god-mode population, so the differentiator is ore acquisition, power, build speed, tile choices, and military strategy. Real gameplay still happens.
- **AI vs Human**: AI has a structural advantage on workforce. Compensate via .opm tweaks: lower starting resources for the AI, fewer convecs, lower tech cap.
- **Ore and power are still real constraints for AI** - no smelter, no factories; no Tokamak, no power. The AI is solving real economic problems, just immune to starvation.

**Workaround to FORCE real population dynamics on AI players** (verified 2026-05-27):
Set `"IsHuman": true` for the AI seat in the .opm. The SDK's `MissionLogic.StartMission` only checks `BotType != None` when deciding whether to construct a BotPlayer, NOT whether IsHuman is true - so the seat is still driven by AI code (TechCor / AIv2 / custom), but OP2 no longer applies the AI-only god-mode pop. Verified by setting all three cTest players to `IsHuman: true` with distinctive Workers values (44/54/64) and observing each player's Timeline.csv carry the exact .opm value. Trade-off: technically there are now multiple "human" seats which may break true multiplayer / network play - keep `IsHuman: false` for AI seats in shipped competitive missions, use the `IsHuman: true` trick only for local AI-vs-AI testing or single-player-with-AI-opponents missions.

**Diagnostic code kept in place** (harmless, useful for confirming this if someone re-opens the question):
- `MissionLogic.cs` captures `.opm Resources` for AI seats and at Mark 1 calls `SetKids/Workers/Scientists` with read-back logging
- Output appears once per bot in `BotPlayer_<N>.txt` as `Deferred resource re-apply attempt:` followed by `target / before / after` lines
- `target == after` confirms the Set wrote successfully; later Timeline.csv reverting to 4096 confirms OP2 resets

**Code references**:
- `DotNetMissionSDK/MissionSDK/MissionLogic.cs` deferred re-apply block (proof artifact, not a fix)
- `DotNetMissionSDK/HFL/PlayerEx.cs` `GetKids()/GetWorkers()/GetScientists()` - **correct**
- `NativeMissionSDK/NativeSDK/HFL/Source/PlayerEx.cpp` `OP2Player` struct offsets 148/152/156 - **correct**
- `DotNetMissionSDK/MissionSDK/MissionLogic.cs` seat-type logging in `BotLog.Get(i).Write(... seat=HUMAN/AI-only ...)`

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
