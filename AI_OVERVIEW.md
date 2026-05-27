# BotPlayer — AI Overview & Status

Living document tracking what the OP2DotNetMissionSDK AI does today, how it's put together, and what's left to do. Update as work lands.

**Status legend:**
- ✅ Working as designed
- ⚠️ Partial / has known issues
- 🚧 In progress
- ❌ Not implemented / stubbed

**Last reviewed:** 2026-05-27

---

## Architecture

Each AI player is a [`BotPlayer`](DotNetMissionSDK/MissionSDK/AI/BotPlayer.cs) instance owning three managers. The instance ticks once per game cycle.

```
BotPlayer (per AI player)
├── BaseManager       — economy, base, research, starship
├── LaborManager      — worker assignment to structures
└── CombatManager     — military groups, threat zones, attacks
```

### Three architectural strengths

| | Status | Notes |
|---|---|---|
| **Deterministic-async planning** | ✅ | `AsyncPump.Run(planFn, completionFn, timeToAdd=6)` runs planning off-thread, applies effect on main thread at a fixed `TethysGame.Time()`. Critical for lockstep multiplayer. |
| **Immutable `StateSnapshot`** | ✅ | Frozen world view per tick. Worker thread reads from it safely; pool-managed via `Retain`/`Release`. |
| **Weighted goal system** | ⚠️ | Goals compute 0–1 importance; top goal executes. **All 10 `BotType` personality weight tables are stubbed** — every personality currently behaves identically. |

---

## What the AI understands (the StateSnapshot model)

| Snapshot field | Contains | Status |
|---|---|---|
| `players[]` | All 8 player slots: tech, resources, morale, population, units | ✅ |
| `gaia` | Mining beacons (yield, type), fumaroles, magma vents | ✅ |
| `tileMap` | Per-tile passability, wrap-aware | ✅ |
| `unitMap` | Per-tile occupancy (collision + pathfinding) | ✅ |
| `commandMap` | Per-player CC tube connectivity | ⚠️ Rebuilt every frame (perf risk on large maps) |
| `strengthMap` | Military strength heatmap | ✅ |
| `structureInfo` / `vehicleInfo` / `weaponInfo` | Sizes, costs, build times, weapons, research prereqs | ✅ |

The AI can answer: "Where's the nearest unmined common beacon I have CC coverage to?", "Which of my structures are disconnected from a CC?", "What's the closest enemy CC?"

---

## What the AI can do (the 15 top-level goals)

Defined in [`BaseManager.cs:69-86`](DotNetMissionSDK/MissionSDK/AI/Managers/BaseManager.cs). Each cycle they compete; top goal executes.

| Goal | Weight | What it does | Status |
|---|---|---|---|
| `ExpandCommonMiningGoal` | 1.0 | Survey + deploy CommonOreMine + smelter + truck routes | ✅ |
| `MaintainPowerGoal` | 0.99 | Build Tokamak when demand > supply | ✅ |
| `UnloadCommonMetalGoal` | 1.0 | Truck routes: smelter → structure factory | ✅ |
| `UnloadRareMetalGoal` | 1.0 | Same for rare metal | ✅ |
| `UnloadFoodGoal` | 1.0 | Truck routes: agridome → residences | ✅ |
| `RepairStructuresGoal` | 1.0 | Send RepairVehicle/ConVec to damaged buildings; critical-first | ✅ |
| `MaintainFoodGoal` | 0.99 | Build agridomes when production < consumption | ✅ |
| `MaintainPopulationGoal` | 1.0 | Build Residences, Nurseries, Universities, Medical Centers | ✅ |
| `MaintainResearchGoal` | 1.0 | Assign scientists, build Standard / Advanced Labs | ✅ |
| `MaintainDefenseGoal` | 0.75 | GuardPosts near each base, MeteorDefense (Eden only) | ⚠️ Build but unit-type choice is crude |
| `MaintainWallsGoal` | 0.1 | Wall construction (rare priority) | ⚠️ Rarely fires |
| `ExpandRareMiningGoal` | 0.97 | Same pattern as common but for rare ore | ✅ |
| `MaintainArmyGoal` | 0.98 | Combat vehicle production toward desired strength | ⚠️ First-fit unit selection |
| `LaunchStarshipGoal` | 1.0 | Build SpacePort + 8 starship modules + launch evac | ✅ |
| `MaintainStructureFactoryGoal` | 1.0 | Build StructureFactory + supply convecs | ✅ |

---

## How decisions get made (per-cycle loop)

```
1. Main thread: build StateSnapshot.Create() — frozen view of the world
2. Main thread: BaseManager hands snapshot to AsyncPump.Run(...)
3. Worker thread (off main):
   a. Run UpdateImportance() on every goal — 0-1 score
   b. Sort goals by importance descending
   c. For top goal: PerformTaskTree() — walk prerequisites, execute action
   d. Collect BotCommands (DoBuild, DoMove, etc.)
   e. Collect StructureLaborOrder for LaborManager
4. AsyncPump scheduler waits until target tick reached
5. Main thread: completion fires — BotCommands.Execute() applies all atomically
```

Result: AI thinks for ~6 ticks (≈1 second of game time), then in one frame applies all its decisions. Deterministic across clients.

---

## Combat system

[`CombatManager`](DotNetMissionSDK/MissionSDK/AI/Managers/CombatManager.cs) divides the world into **Threat Zones**:

| Zone type | Trigger | Status |
|---|---|---|
| Proximity | Enemy units near our base | ✅ |
| Defense | Structures need guarding | ✅ |
| Vulnerable structure | Critical structure exposed | ✅ |
| Enemy base | Known enemy CC location | ✅ |
| Mining | Mining outposts that need protection | ✅ |

Each zone gets a `VehicleGroup` (Assault / Capture / Bomber / Harass). Groups have `UnitSlot[]` with "desired strength":
- Every 10th slot: repair unit
- Every 5th slot: EMP unit
- Else: standard combat (Lynx → Panther → Tiger by build order)

**Currently first-fit by build order** — AI doesn't score "this Lynx vs that Tiger" for the zone's actual threat. Most visible gap in combat behavior.

---

## Labor allocation

After BaseManager picks goals, it builds a `StructureLaborOrder` list (which structures should be active, priority order). [`LaborManager`](DotNetMissionSDK/MissionSDK/AI/Managers/LaborManager.cs) takes that list, looks at current Workers/Scientists, assigns:

```
Smelters → Agridomes → Nurseries → Labs → Factories → others
```

If you have 10 workers and 12 active structures, the 10 most-important get them, the rest go idle.

⚠️ **Known issue from forum**: LaborManager occasionally fights BaseManager — base wants smelter active, labor wants workers on morale instead. Unresolved arbitration design.

---

## What works visibly today

A typical AI in cTest demonstrates this full loop:

- ✅ Builds CC, expands to multiple structures
- ✅ Connects via tubes (Earthworker dispatched, tubes laid)
- ✅ Multiple Common Ore mines + smelters operational
- ✅ Power generation running (Tokamaks)
- ✅ Agridome producing food
- ✅ Standard Lab researching tech
- ✅ Convoy / truck routes from mine to smelter to factory
- ✅ Vehicle Factory producing convecs + combat units
- ✅ Combat units visible (Panthers grouping)
- ✅ Defense structures (GuardPosts with weapon cargo)
- ✅ Morale management
- ✅ Repair vehicles dispatched to damaged structures

That's a fully functional economy + military + research loop in around 5-10 minutes of play.

---

## Improvements landed this session (2026-05-27)

| Change | Location | Impact |
|---|---|---|
| DoBuild throttle | [`BuildStructureTask.cs`](DotNetMissionSDK/MissionSDK/AI/Tasks/Base/Structure/BuildStructureTask.cs) | 30-tick per-convec cooldown — kills "Building command not successful" spam |
| State-message log dedup | `BuildStructureTask.cs`, `BaseManager.cs` | "Top goals", "convec busy", "NO VALID TILE" only logged on change |
| Debug-marker toggle | [`CustomLogic.cs`](DotNetMissionSDK/CustomLogic.cs) | `showDebugVehicleMarkers` const flips DNA-helix vehicle destination markers on/off |

---

## Known gaps (the roadmap)

### High-leverage, small effort

| Gap | Evidence | Effort | Priority |
|---|---|---|---|
| ❌ BotType personality weights stubbed | [`BaseManager.cs:88`](DotNetMissionSDK/MissionSDK/AI/Managers/BaseManager.cs) — all 10 switch cases empty | Small per personality (~15 LOC × 10) | **High** — differentiates all bot personalities |
| ❌ `IsTaskComplete` always false | [`BuildStructureTask.cs:46`](DotNetMissionSDK/MissionSDK/AI/Tasks/Base/Structure/BuildStructureTask.cs) — comment says "Task is never complete" | Small (~20 LOC) but risky — may break MaintainStructure logic | Medium |
| ⚠️ Combat unit selection is first-fit | `CombatManager.PopulateCombatGroups()` — picks first matching by build order | Medium — needs scoring function (weapon vs zone threat) | High — visible AI quality jump |

### Larger features

| Gap | Effort | Priority |
|---|---|---|
| ❌ Disaster awareness (meteors, quakes, storms) | Medium — new goal class + reaction tree | Medium |
| ❌ Captured-unit handling (Spider hacking) | Medium — needs unit-acquisition lifecycle | Low |
| ❌ Retreat behavior | Medium — group state machine | High |
| ❌ Scout deployment | Medium — exploration goal + scout AI | Medium |
| ❌ Light tower deployment (night ops) | Small — single placement task | Low |
| ❌ Evac transport management | Medium — population evacuation logic | Low |
| ❌ Garage management | Medium — vehicle parking + repair queue | Low |
| ⚠️ Sync pathfinding in build tasks | [`BuildStructureTask.cs:115`](DotNetMissionSDK/MissionSDK/AI/Tasks/Base/Structure/BuildStructureTask.cs) TODO | Medium — async refactor | Medium |

### Architectural cleanup

| Item | Why |
|---|---|
| `PlayerCommandMap` rebuilt every frame | Potentially expensive on large maps. Could be incremental on structure/tube events. |
| LaborManager vs BaseManager arbitration | Forum-noted issue: they fight over worker assignment. Needs explicit priority handshake. |
| `canBuildXxx` flags in [`BotPlayer.cs`](DotNetMissionSDK/MissionSDK/AI/BotPlayer.cs) commented out | TechCor planned per-bot capability toggles but never wired them. Could enable Scout/LightTower/Evac/etc. as a config layer above BotType. |

---

## BotType personalities (the missing dimension)

From [`BotPlayer.cs`](DotNetMissionSDK/MissionSDK/AI/BotPlayer.cs), 10 personality flavors:

| BotType | Intended behavior | Weight table status |
|---|---|---|
| `None` | Bot does nothing (not actually constructed) | n/a |
| `PopulationGrowth` | Grow population; build optional structures; minimal defense | ❌ Stubbed |
| `LaunchStarship` | Race for starship victory; minimal defense | ❌ Stubbed |
| `EconomicGrowth` | Resource acquisition focused; minimal defense | ❌ Stubbed |
| `Passive` | No new structures; minimal defense | ❌ Stubbed |
| `Defender` | Strong defense + allied protection; no offense | ❌ Stubbed |
| `Balanced` | Military + offense with best available strategy | ❌ Stubbed |
| `Aggressive` | Military focus; offensive; minimal self-defense | ❌ Stubbed |
| `Harassment` | Strikes trucks, power, weakly defended utility | ❌ Stubbed |
| `Wreckless` | Attacks regardless of odds | ❌ Stubbed |

**Filling these in is the single highest-leverage improvement available.** Currently all 10 produce identical play because the weight tables are empty switch cases. Each one is a ~15-line weight matrix that biases the 15 goals appropriately.

---

## File map (where the AI lives)

| Concern | Path |
|---|---|
| Top-level AI controller | `DotNetMissionSDK/MissionSDK/AI/BotPlayer.cs` |
| Base / economy / research | `DotNetMissionSDK/MissionSDK/AI/Managers/BaseManager.cs` |
| Worker assignment | `DotNetMissionSDK/MissionSDK/AI/Managers/LaborManager.cs` |
| Combat | `DotNetMissionSDK/MissionSDK/AI/Managers/CombatManager.cs` |
| Goals (15 files) | `DotNetMissionSDK/MissionSDK/AI/Tasks/Base/Goals/*.cs` |
| Tasks (build, mine, repair, etc.) | `DotNetMissionSDK/MissionSDK/AI/Tasks/Base/*/*.cs` |
| Threat zones | `DotNetMissionSDK/MissionSDK/AI/Combat/CombatZone.cs` |
| Vehicle groups | `DotNetMissionSDK/MissionSDK/AI/Groups/*.cs` |
| Per-AI diagnostic log | `DotNetMissionSDK/MissionSDK/AI/BotLog.cs` |
| Mission-author toggles | `DotNetMissionSDK/CustomLogic.cs` |

---

## How to read the bot logs

Each AI run produces `OPU/logs/BotPlayer_N.txt`. Line format:

```
[HH:mm:ss.fff t=N] <message>
```

- `HH:mm:ss.fff` — wall-clock time (handy for matching to in-game events / your watch)
- `t=N` — game tick (handy for matching to `TethysGame.Time()` references in code)

Three message types:

1. **`Top goals: A=0.99, B=0.95, C=0.90`** — top 3 goals at this tick. Logged only when the line changes vs the previous one. So gaps in time between two `Top goals` lines = the goal selection was stable.
2. **`BuildStructureTask(X): <state>`** — build attempts. State messages (`no convec`, `convec busy`, `NO VALID TILE`) are deduped — only logged when state changes. **Events** (`issuing DoBuild at (x,y) convec=N`) always log because they're actions, not states.
3. **`Mission Ended`** — written across all bot logs simultaneously when MissionLogic disposes.

Useful greps:
- `grep 'issuing DoBuild' BotPlayer_*.txt` — every build actually issued
- `grep 'NO VALID TILE' BotPlayer_*.txt` — placement failures
- `grep 'Top goals' BotPlayer_*.txt` — every distinct goal-state the bot was in

---

## Goal-by-goal expected behavior

For mission authors building maps that interact with the AI, here's what each goal will do at high importance:

### Economy goals (always 1.0 when conditions met)
- **ExpandCommonMining**: Look for closest unmined common beacon with CC coverage → deploy survey → deploy mine → connect tube → build smelter → start truck route
- **ExpandRareMining**: Same pattern for rare
- **UnloadCommonMetal/RareMetal/Food**: Route cargo trucks between producers and consumers

### Maintenance goals (0.99 baseline)
- **MaintainPower**: When `currentPower < projectedDemand * 1.2`, build a Tokamak (or solar/geothermal if appropriate)
- **MaintainFood**: When `foodProduction < foodConsumption * 1.1`, build an Agridome
- **MaintainPopulation**: Build Residences when crowded; Nurseries for kid production; Medical when injuries
- **MaintainResearch**: Assign scientists to highest-priority unresearched tech in the tree
- **MaintainStructureFactory**: Keep at least one StructureFactory connected and supplied with convecs

### Defense goals (lower priority)
- **MaintainDefense** (0.75): GuardPosts placed near each CC; MeteorDefense for Eden bases
- **MaintainWalls** (0.1): Rare; lava walls or concrete walls in high-threat directions

### Combat goal
- **MaintainArmy** (0.98): Maintain force composition based on threat zones; fills VehicleGroup slots

### Victory goal
- **LaunchStarship** (1.0): Build SpacePort → research starship techs → build all 8 modules → load + launch

---

## Notes on debug visualization

When `CustomLogic.showDebugVehicleMarkers = true`:
- **White DNA-helix icons** appear at the **destination tile** of every AI vehicle that receives a `DoMove(path)` command
- They update whenever the AI re-plans a vehicle's route
- They only appear for the local player (so you only see your own AI's intentions, not the opponent's)
- They're harmless — placed as standard OP2 markers, removed when the vehicle reaches destination or gets new orders

Toggle the flag in [`CustomLogic.cs`](DotNetMissionSDK/CustomLogic.cs) for clean gameplay screenshots / shipped missions.

---

## Quick-reference: AI strengths and weaknesses

**Strengths**:
- Full economy loop works (mine → smelt → manufacture)
- Research progression works
- Truck logistics works
- Repair behavior works (critical-first)
- Threat zones for combat awareness
- Deterministic-async planning
- Per-tick immutable state snapshots
- 15-goal weighted priority system

**Weaknesses**:
- All 10 personalities behave identically (weight tables empty)
- Combat unit selection is build-order first-fit (not threat-scored)
- No disaster preparation
- No retreat / fallback behavior
- No captured-unit logic
- Sometimes deadlocks between BaseManager and LaborManager on worker priorities
- A few completion-tracking bugs cause repeated build attempts

**Bottom line**: TechCor built a production-quality RTS AI **framework** that's then under-populated with content. The architecture is solid. Filling in the missing personality weights + smarter combat selection would dramatically improve the visible AI variety.
