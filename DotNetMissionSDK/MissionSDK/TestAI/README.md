# TestAI

A from-scratch reference bot that demonstrates the pluggable AI architecture without leaning on TechCor's full goal/task tree, AsyncPump, or weighted-priority system. Single file, runs entirely on the main thread out of `IBotPlayer.Update`. Selected by setting `"AIImpl": "TestAI"` on a player in the mission `.opm`.

## Status (2026-05-27)

✅ **Working end-to-end on cTest / on6_01.map** with a 3-player AI vs AI vs AI setup.

The bot reliably deploys a complete starting colony and starts ore production within ~20 Marks (200 seconds game time):

| Mark | Event |
|---|---|
| 1 | CommandCenter deployed (anchor for everything else) |
| 2 | Tokamak placed with 8-tile buffer from cluster (meltdown-safe) |
| 5 | CommonOreMine deployed by RoboMiner on nearest common beacon |
| 6 | CommonOreSmelter placed inside CC tube range |
| 12 | Agridome placed inside CC tube range |
| 17 | StructureFactory placed inside CC tube range |
| ~20+ | Cargo trucks shuttling CommonMetal to smelter, ore accumulating ~1000/Mark |

## What it does

1. **Deploy starting convecs in priority order**: CC first, then Smelter, Tokamak, Agridome, StructureFactory. Each build anchors off the CC's position once the CC exists.
2. **Deploy RoboMiner**: finds the nearest unmined common-ore beacon and calls `DoDeployMiner`.
3. **Drive cargo trucks to the smelter**: one new dock dispatch per cycle (no traffic jams), already-docked trucks get unload + post-unload nudge so they free the dock for the next truck.
4. **Lay tubes (mostly unused)**: if any building lands disconnected from the CC tube network the earthworker bridges the gap one tile per cycle. In practice the placement logic keeps everything connected so this rarely fires.

## What it deliberately does NOT do

- Train new colonists / workers / vehicles
- Build more convecs / earthworkers / cargo trucks (uses only the starting kits)
- Research, military, or repair
- Anything past the initial 6-building setup

TestAI plateaus once the starting kits are deployed. That's by design — it's a clean reference implementation, not a tournament competitor. When the user's colony stalls because it has no more kits and no factory production loop, the bot has done its job.

## Where to extend

- **Cargo truck pickup**: after unloading, drive trucks back to the mine to load more ore. Currently they sit idle once empty.
- **Structure factory output**: queue more convecs / earthworkers / trucks from the factory so the bot doesn't starve on starting kits.
- **Build queueing**: spread newly-produced convecs across follow-on builds (more residences, labs, second smelter, etc.).
- **Defense / military**: GuardPosts when threat detected, train Lynx etc. from VehicleFactory.

Each of these is a 30-100 LOC addition to this same file. The architecture supports it without touching the rest of the SDK.

## Implementation notes

### Throttling

- `ACTION_INTERVAL_TICKS = 30` — global throttle for command dispatch
- `PER_UNIT_COOLDOWN_TICKS = 100` — per-convec / per-miner cooldown (don't reissue while in flight)
- `PER_TILE_BUILD_CLAIM_TICKS = 500` — prevents two convecs being sent to overlapping tiles
- `PER_TILE_TUBE_COOLDOWN_TICKS = 500` — earthworker skips tiles it just tried and failed

### Placement rules in `IsValidTile`

- Every footprint tile must pass `tileMap.IsTilePassable` (rejects impassable terrain)
- No existing buildings in the area
- No enemy vehicles in the area (own vehicles are fine — `ClearDeployArea` moves them out before `DoBuild`)
- Tokamak / MHDGenerator get an **8-tile separation buffer** so a meltdown doesn't take the colony with it
- Tube-requiring buildings (Smelter, Factory, Lab, Residence, etc.) must be inside `commandMap.ConnectsTo(playerID, area)` — i.e. inside the CC's tube reach

### Deploy sequence

```
TryDeployOneConvec
├── Pass 1: iterate s_DeployPriority [CC, Smelter, Tokamak, Agridome, Factory]
│   └── For each kit, find an idle convec with that cargo. Deploy first match.
└── Pass 2: deploy any remaining idle convec (non-priority kits), anchored to CC
```

Pre-deploy: `ClearDeployArea` issues `DoMove` on any friendly vehicles inside the proposed footprint, pushing them one tile away from the deploy center.

### Truck dispatch

```
For each truck with CommonMetal cargo:
├── If on smelter dock: smelter.DoUnloadCargo() + nudge truck off if idle >8 ticks
└── If NOT on dock and we haven't dispatched a new docker this cycle:
        truck.DoDock(smelter), set flag
```

Only one truck per cycle gets a fresh dock command; trucks already in motion are left alone; trucks on the dock always get pumped.

## File layout

```
MissionSDK/TestAI/
├── BotPlayer.cs   (~400 LOC, the whole bot)
└── README.md      (this file)
```

Class: `DotNetMissionSDK.TestAI.BotPlayer : DotNetMissionSDK.AI.IBotPlayer`
