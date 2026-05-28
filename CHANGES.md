# Changes — Sessions 2026-05-26 / 2026-05-27

Brought OP2DotNetMissionSDK from "won't load" to "human vs AI on `on6_01.map`, with full diagnostic logs". This document records every change, why it was needed, and what it unblocks.

## Versioning

TechCor's last upstream release was tagged **DotNetMissionSDK 2.0** on 2019-08-18 (commit `cff3f7e` on `TechCor8/OP2DotNetMissionSDK`, with the note *"AI has regressed. Fixes required."*). All work in this community fork (`leviathan400/OP2DotNetMissionSDK`) is **DotNetMissionSDK 3.0** — the in-game startup banner and the `CustomLogic.SDK_VERSION` constant both read `"DotNetMissionSDK 3.0"`. Bump that constant per release.

## Validation

End-to-end test: cTest mission (Colony Game, Eden, 2 players) launches via OPULauncher, both AI bots are created (BotType=LaunchStarship for each), each builds a CommandCenter, top-level goal selection runs, and AI events fire ("Wreckage Discovered", etc.). Confirmed visually and via per-bot logs (`BotPlayer_0.txt`, `BotPlayer_1.txt`) showing top goals per cycle and DoBuild commands.

## Build environment

### MSVC toolset override
- **Problem**: `NativeSDK/HFL/Source/HFL.vcxproj` and `NativeSDK/Outpost2DLL/Outpost2DLL.vcxproj` target the `v141` (VS 2017) toolset. Modern Visual Studio installs ship newer toolsets; `v141` may not be present.
- **Fix**: build with `-p:PlatformToolset=v143` to override at MSBuild time. No source change required.
- **Command** (from sln dir):
  ```
  MSBuild.exe DotNetMissionSDK.sln -t:Restore -p:Configuration=Release        # first time
  MSBuild.exe DotNetMissionSDK.sln -p:Configuration=Release -p:PlatformToolset=v143 -m
  ```

### HFL submodule sync
- **Problem**: The bundled `NativeMissionSDK/NativeSDK/HFL` submodule was older than what `DotNetInterop/HFL/UnitEx.cpp` expects. Build failed with ~50 C2039 errors (missing methods like `SetLaunchPadCargo`, `HasPower`, `HasWorkers`, `GetBarYield`, `GetOreType`, `GetSurveyedBy`, `GetLabCurrentTopic`, `GetLabScientistCount`, `IsInfected`, `GetNumTruckLoadsSoFar`).
- **Fix**: Copy `OP2MissionSDK/HFL/Source/*` (the newer upstream) over `NativeMissionSDK/NativeSDK/HFL/Source/`. Adds `Research.cpp`/`Research.h`, refreshes `UnitEx.h`.
- **Caveat**: `OP2Helper` and `Outpost2DLL` submodules were already aligned; only HFL needed updating.

## Native plugin

### `NativeMissionSDK/NativePlugin/LevelMain.cpp`
- **Problem**: shipped with hex-edit placeholder X-strings for level metadata. A fresh build produces a DLL with literal `"LevelDescXXXXX..."` and `"DotNetMissionSDKXXXXXXX..."` strings, so OP2 can't load it and the C# DLL name resolution fails.
- **Fix**: Replaced `ExportLevelDetails(...)` with `ExportLevelDetailsEx("Colony Game, Eden", "on6_01.map", "MULTITEK.TXT", Colony, 2, 12, false)` and set `SdkPath = "DotNetMissionSDK_v0.dll"` (matching the versioned C# DLL name).
- **Caveat**: Emits deprecation warning ("`ExportLevelDetailsEx` has been deprecated. Please use `ExportLevelDetailsFull` instead"). Non-blocking; switch to `ExportLevelDetailsFull` when convenient.

## C# JSON deserialization

`DataContractJsonSerializer` does **not** call constructors. Reference-type fields default to `null` after deserialization unless `[OnDeserializing]` initializes them. The repo only had partial OnDeserializing coverage. Without these, the code crashes with `NullReferenceException` during mission initialization on otherwise-valid JSON.

### `DotNetMissionSDK/MissionReader/Json/MissionRoot.cs`
- Extended existing `OnDeserializing` to default `LevelDetails`, `MasterVariant`, `MissionVariants`, `Disasters`, `Triggers` (in addition to `SdkVersion`, `Regions` it already defaulted).

### `DotNetMissionSDK/MissionReader/Json/MissionVariant.cs`
- **Added** `[OnDeserializing]` method defaulting `Name`, `TethysGame`, `TethysDifficulties`, `Players`, `Layouts`.

### `DotNetMissionSDK/MissionReader/Json/PlayerData.cs`
- **Added** `[OnDeserializing]` method defaulting `BotType`, `Color`, `Allies`, `Resources`, `Difficulties`.

## Mission data schema migration — `D:\Outpost 2\cTest.opm`

The shipped `cTest.opm` is in an older schema that the current `MissionRoot` doesn't recognize. Migrated to the new schema:

1. **Wrapped top-level `TethysGame`/`Players`/`AutoLayouts` inside `MasterVariant`**. The new schema expects `MasterVariant.{TethysGame,Players,AutoLayouts}`, not these as top-level keys. `Disasters`/`Triggers` stay at top level.
2. **Nested each player's resource fields into a `Resources` sub-object**. Previously: `Player.{TechLevel,MoraleLevel,Kids,Workers,CommonOre,Units,...}` flat. New: `Player.Resources.{TechLevel,...}`. Top-level player keeps `ID,IsEden,IsHuman,BotType,Color,Allies,Resources`.
3. **Trimmed Players from 6 to 2** to match `LevelDetails.NumPlayers=2`. Extra player blocks caused `ArgumentOutOfRangeException` in BotPlayer construction (see defensive fix below).
4. **Saved without UTF-8 BOM**. `DataContractJsonSerializer` rejects the BOM with "Encountered unexpected character 'ï'". PowerShell's `Set-Content -Encoding UTF8` writes the BOM; use `[System.IO.File]::WriteAllText(path, content, [System.Text.UTF8Encoding]::new($false))` instead.
5. Note: `Triggers2` field remains (old name). Current SDK reads `Triggers`. Migration of the trigger payload to the new event-driven schema is a follow-up.

## Defensive runtime fixes

### `DotNetMissionSDK/MissionSDK/MissionLogic.cs` — StartMission player loop
- **Problem**: `StartMission` iterates JSON `missionVariant.Players` with loop index `i`, creating a `BotPlayer(botType, i)` for each. If the JSON has more players than `TethysGame.PlayerCount()`, the BotPlayer's downstream code (`MaintainDefenseTask.GeneratePrerequisites` → `GameState.players[ownerID]`) crashes with `ArgumentOutOfRangeException`.
- **Fix**: Loop condition now also bounds by active player count:
  ```csharp
  int activePlayerCount = TethysGame.PlayerCount();
  for (int i=0; i < missionVariant.Players.Count && i < activePlayerCount; ++i)
  ```

### `DotNetMissionSDK/Main.cs` — Initialize exception visibility
- **Problem**: C# exceptions thrown inside `Initialize()` propagated to native OP2.exe and crashed the process without any diagnostic.
- **Fix**: Wrapped `InitializeSystems()` + `m_MissionLogic.InitializeNewMission()` in try/catch that writes `ex.ToString()` to `DotNetLog.txt` and returns `false` (which surfaces as OP2's "Could not initialize game" rather than a hard crash).

## Diagnostics — per-bot event log

New diagnostic infrastructure for AI behavior analysis. One log file per AI player in the OP2 working directory.

### `DotNetMissionSDK/MissionSDK/AI/BotLog.cs` (new file)
- Thread-safe static accessor: `BotLog.Get(ownerID)` returns the per-bot logger
- Writes to `BotPlayer_{ownerID}.txt` (FileMode.Create — overwrites each mission start)
- `Write(int tick, string message)` — usable from main thread AND AsyncPump worker threads
- Pass `stateSnapshot.time` for the tick when called from inside a task (worker thread)

### Wired into:
- **`BotPlayer.cs`** — logs construction with botType
- **`BaseManager.cs`** — captures `TethysGame.Time()` on main thread before scheduling async work; the async worker logs the top 3 goals by importance each cycle
- **`BuildStructureTask.cs`** — logs every PerformTask invocation: "no convec", "convec busy action=X", "NO VALID TILE near (x,y)", or "issuing DoBuild at (x,y) convec=N"

## What the diagnostics revealed (not yet addressed)

The first run with logging exposed several AI behaviors worth examining later:

- **`IsTaskComplete()` always returns `false`** in `BuildStructureTask` ([line 46](DotNetMissionSDK/MissionSDK/AI/Tasks/Base/Structure/BuildStructureTask.cs)). After a structure is built, the same `DoBuild` is re-issued repeatedly (e.g. StructureFactory at (53,35) was issued at t=342, t=348, t=474, t=480 — same convec, same location). This is what produces the in-game "Building command not successful" messages.
- **Multiple goals share a single convec via redundant prereqs.** At each cycle ~5 different goals (MaintainPower, MaintainArmy, MaintainResearch, etc.) each independently create a `BuildStructureTask(StructureFactory)` prereq. All five find the same convec, see it's busy, log a failure, repeat. Out of 3,702 `BuildStructureTask(StructureFactory)` invocations across both bots, only 272 issued a real `DoBuild`.
- **`BaseManager.cs:88` BotType weight tables are still stubbed** — all 10 BotType personalities currently behave identically because the switch cases are empty.

## Deployment

Build → deploy paths:
- Build output: `Release/` (native) and `DotNetMissionSDK/bin/Release/netstandard2.0/` (C#)
- Post-build copy: `Outpost2/` (staging dir under repo root)
- **Recommended runtime layout** (matches OPU 1.4.1+ convention):
  - `DotNetInterop.dll` — at OP2 install root (shared across all missions)
  - `OPU/maps/<MissionName>/cTest.dll`, `cTest.opm`, `DotNetMissionSDK_v0.dll` — per-mission folder

## Additional fixes — 2026-05-27

### `.opm` path resolution (DotNetMissionSDK/Main.cs)
- **Problem**: `Attach()` looked for `<MissionName>.opm` via a relative path, which resolved to OP2's current working directory. OPULauncher sets CWD to its own `OPU/` subdir, so the JSON had to be duplicated there.
- **Fix**: Resolve the `.opm` path relative to the mission DLL's directory first; fall back to CWD for legacy installs. Eliminates the dual-deploy requirement.

### Mission metadata documentation (NativeMissionSDK/NativePlugin/LevelMain.cpp)
- Added a block comment documenting every parameter of `ExportLevelDetailsEx` so future mission authors can see what to change for their own mission.

### Wall-clock-timestamped logs (3 new files + wiring)
- **`DotNetMissionSDK/MissionSdkLog.cs`** (new) — append-mode `MissionSDK.log` for SDK lifecycle events (DLL load, attach, init, detach). Wall-clock-stamped. Persists history across mission runs.
- **`DotNetMissionSDK/TimestampedTextWriter.cs`** (new) — wraps Console.Out/Error so every `Console.WriteLine` in the C# SDK automatically gets `[yyyy-MM-dd HH:mm:ss.fff]` prefix in `DotNetLog.txt`. Handles multi-line strings (e.g. exception traces) correctly — one timestamp per line.
- **`BotLog`** got a wall-clock header (`# BotLog opened at ...`) and a wall-clock footer on close. New `BotLog.WriteAll(tick, msg)` static broadcasts to every open bot log — used to record "Mission Ended" in every bot log simultaneously.
- **`MissionLogic.Dispose()`** now broadcasts "Mission Ended" to all logs so events align across files.
- **`DotNetMissionEntry.Detach()`** calls `BotLog.CloseAll()` to release file handles cleanly (was previously leaked).

### Bundled `Outpost2/cTest.opm` migrated to new schema
- The shipped `.opm` was in the old (pre-MasterVariant) schema, causing fresh clones to crash on init. Replaced with the migrated version (MasterVariant wrapper, Resources sub-object per player, 2 players: 1 human + 1 AI, UTF-8 without BOM).

### `LevelMain.cpp` `SdkPath` matches versioned DLL name
- `Export const char SdkPath[] = "DotNetMissionSDK_v0.dll"` — matches the C# build output. Without this, the native side wouldn't find the C# DLL.

### README rewrite
- Complete rewrite covering: what the SDK is, layer architecture, build prerequisites with explicit toolset notes, OPU `maps/` folder convention for installation, JSON-only mission creation, C# mission creation, the four diagnostic log files, differences from native SDK, maintenance status.

## De-submoduling — 2026-05-27 (post-initial-release)

**Problem**: `NativeMissionSDK/NativeSDK` was a git submodule pointing at `OutpostUniverse/OP2MissionSDK @ 441ef49` (V4.1.0 tag). That repo itself has further nested submodules for HFL, OP2Helper, Outpost2DLL. The nested-submodule structure caused multiple practical issues:
- Stale pinning: V4.1.0 (2019-era) pinned an older HFL than what `DotNetInterop/HFL/UnitEx.cpp` needs. Builds failed with ~50 C2039 errors until the HFL submodule was manually advanced.
- Cloning required `git clone --recurse-submodules` plus an awareness of the manual HFL update — friction for anyone trying the SDK.
- GitHub Desktop awkwardness: navigating into a submodule treats it as a separate repo, and accidental commits there land in detached HEAD with no way to push (unless you fork the submodule too).
- Confirmation from BlackBox (HFL author) that **HFL is no longer maintained upstream**, so tracking submodule updates buys nothing.

**Fix**: Convert the NativeSDK submodule to vendored regular files. Two-commit operation:

1. **`7408ab1 Remove NativeSDK submodule`** — `git submodule deinit -f NativeMissionSDK/NativeSDK`, `git rm -rf NativeMissionSDK/NativeSDK`, remove `.gitmodules`, clean `.git/modules/NativeMissionSDK/NativeSDK`.
2. **`c06328b Vendor NativeSDK source files`** — restore 277 source files (HFL, OP2Helper, Outpost2DLL, odasl, Readme.md) as regular tracked files. HFL Source matches `OutpostUniverse/HFL` master (which has the methods we need despite being 6 years old). OP2Helper/Outpost2DLL/odasl are as-shipped in OP2MissionSDK V4.1.0.

**Result**: repo is now fully self-contained.
- `git clone https://github.com/leviathan400/OP2DotNetMissionSDK` works without `--recurse-submodules`
- No more manual HFL sync step needed
- Build succeeds straight off a fresh clone (with the `-p:PlatformToolset=v143` override still required for the v141-targeted vcxprojs inside the vendored OP2MissionSDK)
- Trade-off: lost automatic upstream tracking (which we weren't really getting via the pinned V4.1.0 anyway). If we want to sync from upstream HFL/OP2MissionSDK in the future, that becomes a deliberate manual sync, not an "init the submodule" step.

**Files changed**: 209 new tracked files in commit 2 (+11,569 lines), 2 deletions in commit 1.

## Stability & diagnostics — 2026-05-27 (later session)

### `UnitEx_GetUnknownValue` P/Invoke crash — fixed
- **Problem**: OP2 hard-crashed at ~25 min runtime with `System.EntryPointNotFoundException` for `UnitEx_GetUnknownValue`. C# `UnitEx.cs` declared P/Invokes for `UnitEx_GetUnknownValue` and `UnitEx_SetUnknownValue` but `DotNetInterop.dll` never exported them. C# call → missing entry point → exception → escaped the C++/CLI shim → OP2 process termination.
- **Fix**: Added stub exports in `NativeMissionSDK/DotNetInterop/HFL/UnitEx.cpp`. Get returns 0, Set is a no-op. The AI's launchpad cargo transfer logic becomes a no-op (starship modules don't actually load on the pad via that path) but everything else works and missions now run for hours. Verified with a 95-min session.
- **Proper fix deferred**: requires knowing what offset 46 of the OP2 unit struct actually represents. Probably `UnitEx_GetLaunchPadCargo` is the right delegate target. Tracked in `ISSUES.md`.

### Logs moved to `OPU/logs/` subfolder
- All SDK log files (`MissionSDK.log`, `DotNetLog.txt`, `BotPlayer_<N>.txt`) now write into a `logs/` subdirectory of the OP2 working directory instead of dumping into the OPU root. Subfolder is auto-created on mission start.

### Per-bot log dedup
- `BotLog` now suppresses consecutive identical messages with a single `(last message repeated Nx)` summary line on the next distinct event. Catches the rapid-fire bursts where multiple goals' BuildStructureTask instances log the same "convec busy" line within milliseconds. Cuts typical bot-log size by ~10x.

### `BotPlayer_<N>_Status.txt` — per-bot state snapshot
- New `BotPlayer.WriteStatus(stateSnapshot)` writes a human-readable state file every 100 ticks (1 Mark) covering: RESOURCES (ore caps, food production/consumption/status), WORKFORCE (workers/scientists assigned, researching, doing worker jobs, morale), POWER (generated/consumed/available, inactive capacity, unpowered structures), BUILDINGS (total/active/idle plus a counted breakdown of every type), VEHICLES (civilian and military counts), COMBAT (total offensive strength), STARSHIP MODULES (modules in spaceport storage).
- Overwrite-on-write so you can `tail`/poll it during play to see what each bot is currently doing.

### `BotPlayer_<N>_Research.txt` — per-bot research snapshot
- New `BotPlayer.WriteResearchStatus(stateSnapshot)` writes alongside Status on the same 100-tick cadence. Reports lab counts (basic / standard / advanced from `PlayerUnitState`), scientists currently researching (from `numScientistsAssignedToResearch`), and every completed tech grouped by `TechCategory` (Basic, Defense, Power, Vehicles, Food, Metals, Weapons, Space, Morale, Disaster, Population, Spaceship). Each tech line shows level + lab type (B/S/A) + name + ID.
- Uses `Player.HasTechnology(techID)` to identify completed techs by iterating `Research.GetTechCount()`.

### HFL population accessors added
- **`NativeMissionSDK/NativeSDK/HFL/Source/PlayerEx.{cpp,h}`** — added `GetKids()`, `GetWorkers()`, `GetScientists()` reading directly from the `OP2Player` struct (offsets 148/152/156).
- **`NativeMissionSDK/DotNetInterop/HFL/PlayerEx.cpp`** — exported `PlayerEx_GetKids` / `PlayerEx_GetWorkers` / `PlayerEx_GetScientists`.
- **`DotNetMissionSDK/HFL/PlayerEx.cs`** — added `GetKids()` / `GetWorkers()` / `GetScientists()` wrappers with matching `DllImport` declarations.
- **`DotNetMissionSDK/MissionSDK/State/Snapshot/PlayerState.cs`** — exposes them on the immutable per-tick snapshot.
- **Caveat**: these fields turned out to return slot-capacity sentinels (256 / 4096 / 4096) for both Eden and Plymouth at all times, not the colony's actual population. The status writer therefore shows only the `numWorkersRequired` / `numScientistsRequired` / `numScientistsAssignedToResearch` / `numScientistsAsWorkers` fields, which are real. The real population fields live elsewhere in the `OP2Player` struct (likely buried in `unk1[5]` / `unk2[17]` / `unk3[47]` / `unk4[662]`) and need a memory-scan reverse-engineering pass to locate per-player. Tracked in `ISSUES.md`.

### `ISSUES.md` — local issue tracker
- New living document for problems we've found but haven't fixed yet, with a status legend (🔴 Open / 🟡 Investigating / 🟢 Mitigated / ✅ Fixed). Replaces GitHub Issues for solo-dev pace. Currently lists: the population accessor caveat above, `BuildStructureTask` retrying the same blocked location, stubbed `BotType` weight tables (all 10 personalities behave identically), first-fit combat unit selection by build order, `IsTaskComplete` in `BuildStructureTask` always returning false, and the resolved UnitEx crash (kept for posterity).

### `AI_OVERVIEW.md` — architectural tour
- New companion doc describing how `BotPlayer` works end-to-end: per-tick lifecycle, the three managers (Base / Labor / Combat), the weighted-goal system, how StateSnapshot + AsyncPump keep AI work deterministic and off the main thread, and a roadmap of improvements (real per-personality weight tables, smarter combat scoring, blocked-tile cooldown for `BuildStructureTask`).

### README install path corrections
- README now correctly describes the **two shared DLLs in `OPU\`** model (both `DotNetInterop.dll` and `DotNetMissionSDK_v0.dll` live in the OPU folder, shared across all missions) rather than the old "DotNetMissionSDK_v0 ships per-mission" wording. Mission folders are just `<MissionName>.dll` + `<MissionName>.opm`.

## Pluggable AI architecture — 2026-05-27 (later session)

The SDK previously had exactly one AI implementation, `BotPlayer`, hard-wired into `MissionLogic.StartMission`. Extracted a minimal plugin contract so multiple AI implementations can coexist and be selected per player slot in the mission `.opm`. Goal is to enable a community AI tournament where different bots fight in the same game.

### `MissionSDK/AI/IBotPlayer.cs` — new shared contract
- 5-member interface: `int playerID { get; }`, `bool isActive { get; }`, `Start()`, `Stop()`, `Update(StateSnapshot)`. Everything else (managers, goal trees, threat zones, vehicle groups, build-tile cooldowns, etc.) is implementation-private.
- Lives in `DotNetMissionSDK.AI` namespace so all bot folders can reference it without duplication.

### TechCor's `BotPlayer` now implements `IBotPlayer`
- Single change: `public class BotPlayer` → `public class BotPlayer : IBotPlayer`. Zero behavioral change; the existing public surface already matched the contract.

### `MissionLogic.cs` factory
- `private BotPlayer[] m_BotPlayer` → `private IBotPlayer[] m_BotPlayer`.
- `StartMission` now dispatches on a new `"AIImpl"` string field from the player's JSON:
  - `""` or `"TechCor"` → `AI.BotPlayer` (default, backward-compatible)
  - `"AIv2"` → `AIv2.BotPlayer` (active-development AI)
  - `"AI_Blank"` → `AI_Blank.BotPlayer` (no-op stub / template)
  - unknown → logs warning and falls back to TechCor
- Selection is logged to each bot's `BotPlayer_<N>.txt` at construct time so you can confirm which implementation actually loaded.

### Trigger system: `BotPlayer[]` → `IBotPlayer[]`
- Updated `EventSystem.StartMission`, `EventTrigger.m_BotPlayers` + `StartMission`, and `EventTriggerAction.Execute` signatures to use the interface.
- One trigger (`TriggerActionType.SetPlayerBotType`) reads/writes `botType` which is TechCor-specific, so it now does `if (botPlayers[id] is BotPlayer tc) tc.botType = …` and silently no-ops for other AI implementations.

### `MissionReader/Json/PlayerData.cs` — new `AIImpl` field
- Optional string, defaults to empty. Threaded through `OnDeserializing`, copy constructor, and `Concat` so it survives schema merges.

### `MissionSDK/AIv2/` — first alternative AI folder
- Created by **bulk-copying** the entire `MissionSDK/AI/` tree (70 files: managers, goal tree, task tree, vehicle groups, combat zones, BotLog) into `MissionSDK/AIv2/` and rewriting:
  - All `namespace DotNetMissionSDK.AI[.Sub]` declarations → `namespace DotNetMissionSDK.AIv2[.Sub]`
  - All `using DotNetMissionSDK.AI.Sub` directives → `using DotNetMissionSDK.AIv2.Sub` (intra-tree references stay inside AIv2)
- `AIv2/BotPlayer.cs` has `using IBotPlayer = DotNetMissionSDK.AI.IBotPlayer;` alias because the shared interface lives in the AI namespace — direct `using DotNetMissionSDK.AI;` would create ambiguous references for the dozen class names that now exist in both namespaces.
- AIv2 has its **own** copy of `BotType` enum, `BotLog`, `Goal`/`Task` base classes, all 15 goals, all tasks, all managers. The AI namespace and AIv2 namespace are fully independent — improving AIv2 cannot break TechCor, and vice versa.
- AIv2 currently plays identically to TechCor since it's a fresh clone; behavioral improvements will go here from now on.

### `MissionSDK/AI_Blank/` — minimal template
- Single file: `AI_Blank/BotPlayer.cs`. Implements `IBotPlayer`, has the same constructor signature as the other implementations, but does nothing in `Update` except log a heartbeat once per Mark.
- Useful as (a) a fast "spectator" slot in tests, (b) a copy-and-fill starting point for new AI authors.

### Docs
- New top-level "Pluggable AI architecture" section in `AI_OVERVIEW.md` explaining the contract, the three folders (AI / AIv2 / AI_Blank), the `AIImpl` JSON field, and how to add a new AI.
- File map in `AI_OVERVIEW.md` reorganized to call out which paths apply to each AI folder.
- README maintenance bullets list the pluggable AI architecture as a key feature of this fork.

### Verified
- Clean build (only the pre-existing CS0162 warning in Vehicle.cs).
- End-to-end test: cTest mission with player 0 = TechCor, player 1 = AIv2-stub (before the bulk copy). `BotPlayer_0.txt` showed TechCor goal evaluation and DoBuild commands; `BotPlayer_1.txt` showed `AI impl selected: AIv2` + construct + Start + heartbeats. The factory dispatch and per-tick polymorphism work as designed.

## AI population god-mode investigation - 2026-05-27 (later session)

Spent a chunk of the session investigating apparent "wrong" population values (`256 kids / 4096 workers / 4096 scientists` returned by HFL accessors for AI bots, regardless of what the `.opm` requested). Earlier session's diagnosis ("HFL accessors return slot-capacity sentinels") turned out to be wrong - the accessors are correct.

### Actual finding
OP2's engine **pegs population to fixed defaults and skips food simulation for any player slot with `IsHuman: false`**, every tick. The `.opm Resources.Kids/Workers/Scientists` are ignored for AI seats; food consumed stays at 0. This is intentional 1997-era engine behavior, not a bug - it lets AI bots focus on base-building / military without starving themselves.

### Verification chain
1. Flipped player 0 to `IsHuman: true` in cTest.opm → memory reader showed exactly the .opm values (10/44/23 = 77 colonists, food consumed = 77). Same DLL, same map.
2. Flipped player 0 back to `IsHuman: false` → 256/4096/4096 = 8448, food consumed = 0.
3. Tried a deferred `SetKids/Workers/Scientists` re-apply at Mark 1 with read-back logging:
   - **before**: 256/4096/4096 (OP2 defaults)
   - **after** (same tick, immediately after Set): exactly the target (10/44/23 for player 1, 10/100/23 for player 2 with custom workers=100)
   - **Timeline at Mark 2 and onward**: back to 256/4096/4096
   - Conclusion: Set DOES write, but OP2's per-tick simulation immediately reverts AI seats to defaults.

### Resolution
- Don't fight it. AI vs AI is fair (both god-moded). AI vs Human means AI has a structural workforce advantage - design around it with lower starting resources, fewer convecs, or tech caps in the .opm.
- Ore and power remain real constraints for AI - no smelter means no factories, no Tokamak means no power. The bot still solves a meaningful economic problem.

### IsHuman=true workaround for AI bots (discovered after the main investigation)
`MissionLogic.StartMission` constructs a `BotPlayer` for any slot where `BotType != None`, independent of the `IsHuman` flag. So setting `"IsHuman": true` on a seat does **not** disable the AI - the seat is still driven by the configured AI implementation (TechCor / AIv2 / custom). It does, however, switch OP2 out of god-mode for that player's population/food simulation, so the `.opm Resources.Kids/Workers/Scientists` values are honored, colonists consume food, and morale matters.

Verified 2026-05-27 by setting all three cTest players to `IsHuman: true` with distinctive Workers values (`44 / 54 / 64`). Each player's `Timeline.csv` carried the exact .opm value through every Mark. OP2 launches fine with multiple human seats locally; this may not survive true network multiplayer but is fine for local AI-vs-AI testing.

This means:
- **All-AI tournament with realistic population**: mark every AI seat as `IsHuman: true` in the .opm
- **All-AI quick-test mode (default)**: leave seats as `IsHuman: false` for unlimited workforce, faster iteration
- **Human vs AI**: AI seats `IsHuman: false` for fairness compensation (AI gets god-mode pop, balance via lower starting resources)
- **Shipped multiplayer**: keep AI seats `IsHuman: false`

Documented in `AI_OVERVIEW.md` (new "Population mode: AI seat vs Human seat" section) and `ISSUES.md` (workaround paragraph appended to the closed god-mode entry).

### Plumbed in this round (kept as future-proofing)
- **`BotPlayer_<N>_Status.txt` POPULATION block restored** - now shows `Kids / Workers / Scientists / Morale` from HFL accessors. Separate `WORKFORCE ASSIGNMENT` block keeps the trusted `numWorkersRequired` / etc. for "actually assigned to active buildings" counts. Both blocks updated in `AI/BotPlayer.cs` and `AIv2/BotPlayer.cs`.
- **`BotPlayer_<N>_Timeline.csv` got 4 new columns**: `kids,workers,scientists,totalColonists`. Useful for trend analysis once you have a human seat where these values are real.
- **`BotLog` header now shows seat type per player**: `AI impl selected: <impl> | seat=HUMAN` or `seat=AI-only (OP2 may override .opm population - see ISSUES.md)`. Lets you tell at a glance whether to trust population values for that slot.
- **Deferred resource re-apply diagnostic kept in `MissionLogic.cs`** - fires once at Mark 1, logs `target / before / after` to each AI bot's log. Proves the Set worked and OP2 reverted. Future-proofing: if someone re-opens the population question, the proof artifact is already in their logs.
- **`ISSUES.md` rewritten** - earlier "slot-capacity sentinels" entry replaced with a closed entry documenting OP2's god-mode behavior, the proof, and the AI-vs-AI / AI-vs-Human design implications.
- **Tile blacklist removed from AIv2** - was hijacking convec destinations mid-trip during base-building, causing Plymouth's base to sprawl and the smelter to land outside CC tube reach. Bumped `BUILD_REISSUE_COOLDOWN_TICKS` from 30 to 100 in AIv2 to give convecs a full Mark to deliver kits before another goal redirects them. AIv2 weight tables also softened (LaunchStarship 1.5 → 1.1, other personalities pulled to subtler multipliers) to stop early-game LaunchStarship from dominating the priority list before any economy exists.
- **3rd player added** to cTest.opm: ID=2, Eden Green, TechCor AI, starts top-right at (140,18). Native plugin `LevelMain.cpp` bumped from `NumPlayers=2` to `NumPlayers=3`; cTest.dll rebuilt. Lets us run TechCor-vs-AIv2-vs-TechCor to cross-check AI implementations and watch all three bots' Timeline / BuildEvents in parallel.
- **Em-dashes purged from log output** - PowerShell bulk replace of `—` (U+2014) with `-` across 13 source files, also re-saved all AIv2 / AI_Blank source files with UTF-8 BOM (originals were written without BOM by the namespace-rewrite script, causing the C# compiler to misread non-ASCII chars as Windows-1252 and emit mojibake into the logs).
- **SDK_VERSION constant** in `CustomLogic.cs` displayed in the in-game Communications panel and `DotNetLog.txt` at mission start. Currently `v0.3.0`. Bump per release.

## AI_Test - from-scratch reference bot - 2026-05-27 (late session)

Added a fourth AI implementation, `MissionSDK/AI_Test/`, written from scratch as a tight reference for what a working bot looks like without TechCor's full goal/task machinery. Single file, ~400 LOC, runs entirely on the main thread out of `IBotPlayer.Update`. See [`MissionSDK/AI_Test/README.md`](DotNetMissionSDK/MissionSDK/AI_Test/README.md) for full behavior, throttling constants, placement rules, and extension points.

Confirmed end-to-end on cTest: deploys all 6 starting buildings in priority order (CC -> Smelter -> Tokamak -> Agridome -> StructureFactory), places Tokamak with an 8-tile separation buffer so a meltdown can't take the colony with it, deploys a mine via `DoDeployMiner` on the nearest unmined common beacon, and drives cargo trucks through a dock / unload / nudge-off-dock pipeline so ore actually accumulates. Plateaus once starting kits are gone (by design - it's a reference bot, not a tournament competitor).

The bot exercises a chunk of SDK surface area that wasn't covered by the existing TechCor / AIv2 implementations:
- `Pathfinder.GetClosestValidTile` with a custom `IsValidTile` callback (passability, no buildings, no enemy vehicles, tube-connectivity for non-power buildings, separation buffer for meltdown-prone power plants)
- `commandMap.ConnectsTo` to keep tube-requiring buildings inside CC reach
- `UnitEx.DoDeployMiner` for mine placement on a surveyed beacon
- `UnitEx.DoDock` + `StructureState.DoUnloadCargo` truck pipeline with `truck.IsOnDock(structure)` check
- `UnitEx.DoBuildWall(map_id.Tube, rect)` for earthworker tube laying (rarely fires - placement is good enough that buildings are born connected)
- Per-tile build-claim cooldown to stop two convecs being sent to overlapping deploy spots
- `ClearDeployArea` pattern - issue `DoMove` to friendly vehicles inside the proposed footprint before `DoBuild` so OP2 accepts the deploy

The folder was originally named `TestAI` in early iterations; renamed to `AI_Test` so the four AI folders share a consistent naming pattern (`AI/`, `AIv2/`, `AI_Test/`, `AI_Blank/`). Factory dispatch in `MissionLogic.StartMission` updated, `.opm` `AIImpl` value updated, all docs updated.

## MissionContext + StartingMode plumbing - 2026-05-27 (late session)

Added a mission-wide context object that every `IBotPlayer` receives at construction so AI implementations can branch on what kind of game they're playing.

- New `MissionSDK/AI/MissionContext.cs` POCO with `startingMode`, `missionType`, `numPlayers`, `maxTechLevel`. Read-only after construction.
- New `StartingMode` enum: `LandRush` (kit-loaded convecs, no base) and `LastOneStanding` (fully-built base on tick 0).
- New `protected virtual StartingMode GetStartingMode()` on `MissionLogic`, defaults to `LandRush`. `CustomLogic` overrides for per-mission declarations.
- `StartingMode` lives in C# mission code, **not** in the `.opm`. The `.opm` is data-only - OP2MissionEditor produces those files and adding fields means coordinating with that tool. C# code is the natural place for strategic intent.
- All four `BotPlayer` constructors (`AI/`, `AIv2/`, `AI_Test/`, `AI_Blank/`) updated to accept an optional `MissionContext context = null` and store it on a public `context` property.
- `MissionLogic.StartMission` builds the context once and passes it to every bot via the factory dispatch.
- Bot construct log line now includes `startingMode=LandRush` or `startingMode=LastOneStanding` so you can see at a glance what each bot was told. Mission init writes the full context to `MissionSDK.log`.
- **No AI-side branching yet** - every bot stores the context as dead-storage. When we ship a LastOneStanding mission and want bots to skip the deploy-convecs phase, that's where the branching gets added (each bot reads `this.context.startingMode`).
