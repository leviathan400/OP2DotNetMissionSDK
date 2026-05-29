# Writing a Mission with OP2DotNetMissionSDK

A practical walk-through for new mission authors. By the end of this guide you'll have a custom Outpost 2 mission running in your OPU install — built from Visual Studio, designed visually in OP2MissionEditor, no command-line work required.

## Prerequisites

- **Outpost 2** + the **OPU 1.4.1** community patch installed (default location `D:\Outpost 2\` with `D:\Outpost 2\OPU\` for the patch).
- **Visual Studio 2022** with the **".NET desktop development"** and **"Desktop development with C++"** workloads. The Community edition is free and sufficient.
- **OP2MissionEditor** — visual editor for `.opm` files. Strongly recommended; you can hand-edit JSON but the editor saves you from mistakes.
- A copy of this repository (`OP2DotNetMissionSDK`) — either downloaded as a zip from GitHub or cloned via Git.

## How the SDK is structured

Your mission ships as **two files**:

| File | What it is | How it's built |
|---|---|---|
| `YourMission.opm` | JSON describing the mission — map, players, starting units, beacons, triggers | Edited in OP2MissionEditor or any text editor |
| `YourMission.dll` | Tiny C++ native plugin with your mission's metadata baked in (description, map name, player count). Tells OP2 "I'm a mission." Calls into the shared C# runtime to do the actual work. | Built in Visual Studio |

The shared `DotNetMissionSDK_v0.dll` and `DotNetInterop.dll` live in `OPU\` and are used by *every* mission. You don't ship them with your mission — OPU users already have them.

Final layout for your mission once shipped:

```
D:\Outpost 2\OPU\maps\YourMission\
├── YourMission.dll      (the native plugin you build)
└── YourMission.opm      (the JSON you design)
```

That's it — two files. The OPU launcher discovers your mission by scanning `OPU\maps\`.

---

## Step 1 — Open the SDK in Visual Studio

1. Download or clone the SDK to a folder, e.g. `D:\OP2DotNetMissionSDK\`.
2. Open `DotNetMissionSDK.sln` in Visual Studio 2022.
3. In Solution Explorer you'll see several projects. The two that matter for you:
   - **`DotNetMissionSDK`** (C#) — the shared runtime. You won't usually edit this.
   - **`NativePlugin`** (C++) — produces the per-mission `cTest.dll`. This is the one you'll customise for your mission.

## Step 2 — First-time build (verify everything works)

In Visual Studio:

1. Set the build configuration to **Release** (top toolbar dropdown, change from Debug to Release).
2. From the menu: **Build → Build Solution** (or press <kbd>Ctrl+Shift+B</kbd>).
3. Watch the Output window. You should see each project succeed in turn:
   - `Outpost2DLL` → `Outpost2DLL.lib`
   - `HFL` → `HFL.lib`
   - `DotNetInterop` → `DotNetInterop.dll`
   - `NativePlugin` → `cTest.dll`
   - `DotNetMissionSDK` → `DotNetMissionSDK_v0.dll`

If any project fails: ensure both the **".NET desktop development"** and **"Desktop development with C++"** workloads are installed in the Visual Studio Installer, then close VS and re-open the solution.

## Step 3 — Plan your mission

Before touching any files, decide:

- **Map** — pick one of the `.map` files in your OP2 install. Common choices: `on6_01.map` (Pie Chart Plateau, 6-player), `on4_02.map` (Flood Plain, 4-player), `mpdemo.map` (Multi-Player Demo).
- **Number of players** — 1 human + 1 AI is a good starter. The SDK supports up to 7.
- **Each player's role**:
  - **Human seat**: `IsHuman: true`, `BotType: "None"` — you control this slot.
  - **AI seat**: `IsHuman: false`, `BotType: "LaunchStarship"` (or any other personality), and `AIImpl` to pick which bot drives it.
- **Mission type** — `Colony` is the standard skirmish format. Other options exist (`MultiLandRush`, `MultiLastOneStanding`, etc.) but Colony is the safe default.

## Step 4 — Create your `.opm`

You have two options. Use OP2MissionEditor unless you have a specific reason not to.

### Option A — Visual editor (recommended)

1. Open `OP2MissionEditor.exe`.
2. **File → Open** an existing `.opm` from this repo as a starting point. The cleanest example is `Outpost2\cTest.opm` — Colony game, 2 players, starting kits placed on `on6_01.map`.
3. **File → Save As** → `YourMission.opm` in a folder of your choice.
4. Use the editor to:
   - Change the **Level Description** (shown in the script picker)
   - Change the **Map Name** (must match a `.map` file in the OP2 install)
   - Edit the **Players** list — set `IsHuman` and `BotType` per slot, place starting units where you want them on the map, choose colors and alliances
   - Place **Beacons** (mining sites, fumaroles, magma vents) — these appear in-game as the ore deposits the player and AI mine
5. Save. The editor will re-validate the JSON and (optionally) compile a fresh `YourMission.dll` for you.

### Option B — Hand-edit the JSON

If you want to edit the `.opm` directly: it's plain JSON, copy `Outpost2\cTest.opm` to `YourMission.opm` and edit in your favourite text editor. The top-level shape is:

```json
{
  "SDKVersion": "0",
  "LevelDetails": {
    "LevelDescription": "Your Mission Description",
    "MapName": "on6_01.map",
    "TechTreeName": "MULTITEK.TXT",
    "MissionType": "Colony",
    "NumPlayers": 2,
    "MaxTechLevel": 12,
    "UnitOnlyMission": false
  },
  "MasterVariant": {
    "TethysGame": { "Beacons": [...], "Markers": [...], "Wreckage": [...] },
    "Players":    [ ... ]
  },
  "Disasters": [],
  "Triggers":  []
}
```

Each `Players[]` entry looks like:

```json
{
  "ID": 0,
  "IsEden": true,
  "IsHuman": true,
  "BotType": "None",
  "AIImpl": "",
  "Color": "Blue",
  "Allies": [],
  "Resources": {
    "TechLevel": 0,
    "MoraleLevel": "Excellent",
    "FreeMorale": true,
    "CenterView": { "X": 27, "Y": 38 },
    "Kids": 10, "Workers": 44, "Scientists": 23,
    "CommonOre": 27000, "RareOre": 25000, "Food": 7000,
    "SolarSatellites": 1,
    "Units": [ ... ]
  }
}
```

### Player field reference

- **`BotType`** — one of: `None` (human, no AI), `PopulationGrowth`, `LaunchStarship`, `EconomicGrowth`, `Passive`, `Defender`, `Balanced`, `Aggressive`, `Harassment`, `Wreckless`. Picks the AI's strategic personality.
- **`AIImpl`** — picks which bot implementation drives the slot. Options:
  - `""` (empty) or `"TechCor"` — the original reference bot
  - `"AIv2"` — improved version with offensive attack waves
  - `"AI_Test"` — minimal from-scratch reference bot
  - `"AI_Blank"` — heartbeat-only stub (useful as a starting point if writing your own bot)
- **`IsHuman`** — `true` for the human seat; `false` for AI seats. Important: OP2's engine applies "god-mode" population (fixed 256/4096/4096 colonists, no food simulation) to any `IsHuman: false` seat, so AI bots don't starve themselves. Setting `IsHuman: true` on an AI seat gives that bot real population dynamics; the SDK still wires the bot as long as `BotType != None`.

## Step 5 — Update the native plugin for your mission

Each mission needs its own native `.dll` because OP2 reads the mission metadata (description, map, player count) from exports baked into the DLL.

1. In Visual Studio's Solution Explorer, expand the **NativePlugin** project.
2. Open **`LevelMain.cpp`**. Near the top you'll find this line:

   ```cpp
   ExportLevelDetailsEx("Colony Game, Eden", "on6_01.map", "MULTITEK.TXT", Colony, 2, 12, false)
   ```

3. Edit the arguments to match your mission:
   - **`"Colony Game, Eden"`** → your level description (shown in the script picker)
   - **`"on6_01.map"`** → your map file
   - **`"MULTITEK.TXT"`** → tech tree (`MULTITEK.TXT` is the standard multi-player tree, leave as-is unless you know better)
   - **`Colony`** → mission type (`Colony`, `MultiLandRush`, etc.)
   - **`2`** → number of players (must match your `.opm` `NumPlayers`)
   - **`12`** → max tech level (12 = all techs available)
   - **`false`** → `true` for unit-only missions (no morale, no colony) — usually `false`
4. **Important** — these values must match the equivalent fields in your `.opm`. If `LevelMain.cpp` says 2 players and your `.opm` declares 3, OP2 will get confused.

5. Change the **output filename** so your `.dll` doesn't overwrite `cTest.dll`:
   - In Solution Explorer, right-click **NativePlugin** → **Properties**
   - Configuration: **Release**, Platform: **Win32**
   - Configuration Properties → General → **Target Name** → change `cTest` to `YourMission`
   - Click OK

## Step 6 — Build your mission's `.dll`

1. With Release / Win32 selected:
2. From the menu: **Build → Build Solution** (or <kbd>Ctrl+Shift+B</kbd>).
3. Your DLL appears at `Release\YourMission.dll`.

## Step 7 — Deploy and test

1. Create the mission folder in your OPU install:

   ```
   D:\Outpost 2\OPU\maps\YourMission\
   ```

2. Copy your built files into it:
   - `Release\YourMission.dll` → `OPU\maps\YourMission\YourMission.dll`
   - `YourMission.opm` (from wherever you saved it in OP2MissionEditor) → `OPU\maps\YourMission\YourMission.opm`

3. Launch **`D:\Outpost 2\OPU\OPULauncher.exe`** and pick a mission slot. Your mission's level description appears in the script picker.

4. While the mission runs, the SDK writes diagnostic logs to `D:\Outpost 2\OPU\logs\`:
   - `MissionSDK.log` — DLL load + init lifecycle (append-mode, persists across runs)
   - `DotNetLog.txt` — every `Console.WriteLine` from C# code
   - `BotPlayer_<N>*.txt` — per-bot trace (one set per AI seat — only created if enabled in `CustomLogic.cs`)

If something doesn't work, the logs are your first stop.

---

## Optional — adding C# logic to your mission

The vast majority of missions don't need any C#. If you want **scripted events**, **custom victory conditions**, or **anything the trigger system can't express**, open `DotNetMissionSDK\CustomLogic.cs`. You'll find:

```csharp
public override bool InitializeNewMission() { /* called on mission start */ }
public override void Update(StateSnapshot stateSnapshot) { /* called every game tick */ }
protected override void OnTriggerExecuted(TriggerStub trigger) { /* called when a trigger fires */ }
```

Add your logic inside these methods. The shared `DotNetMissionSDK_v0.dll` carries one `CustomLogic` class globally, so if you customise it, every mission built against that DLL gets that logic. For mission-specific behaviour, branch on the mission name:

```csharp
if (m_MissionDLLName == "YourMission") { /* mission-specific code */ }
```

After editing `CustomLogic.cs`, **Build → Build Solution** rebuilds `DotNetMissionSDK_v0.dll`. Copy that into `OPU\` and your changes take effect for every mission that uses that runtime.

---

## Worked examples in this repo

| Folder | Description | Players | What it shows |
|---|---|---|---|
| [`Outpost2\cTest.opm`](Outpost2/cTest.opm) | Original demo | 2 AI | The simplest complete mission |
| [`Release\Outpost2\OPU\maps\cSDK3\cSDKPieChart.opm`](Release/Outpost2/OPU/maps/cSDK3/cSDKPieChart.opm) | Pie Chart Plateau AI vs AI | TechCor vs AIv2 | Two bot implementations side-by-side |
| [`Release\Outpost2\OPU\maps\cSDK3\cSDKFloodPlain.opm`](Release/Outpost2/OPU/maps/cSDK3/cSDKFloodPlain.opm) | Flood Plain three-way | TechCor vs AIv2 vs TechCor | Three-player free-for-all |
| [`Release\Outpost2\OPU\maps\cSDK3\cUnsettledEarth.opm`](Release/Outpost2/OPU/maps/cSDK3/cUnsettledEarth.opm) | Unsettled Earth mixed | TechCor vs AI_Test vs AIv2 | All three AI implementations in one mission |
| [`Release\Outpost2\OPU\maps\cSDK3\cPvAIPieChart.opm`](Release/Outpost2/OPU/maps/cSDK3/cPvAIPieChart.opm) | Player vs AI | You vs AIv2 | The Player-vs-AI pattern |

Copy whichever is closest to what you want and adjust from there.

---

## Reference

For the authoritative `.opm` schema, see these C# files — they declare every field the SDK reads:

- [`DotNetMissionSDK\MissionReader\Json\MissionRoot.cs`](DotNetMissionSDK/MissionReader/Json/MissionRoot.cs) — top-level structure
- [`DotNetMissionSDK\MissionReader\Json\PlayerData.cs`](DotNetMissionSDK/MissionReader/Json/PlayerData.cs) — per-player + resources
- [`DotNetMissionSDK\MissionReader\Json\UnitData.cs`](DotNetMissionSDK/MissionReader/Json/UnitData.cs) — per-unit
- [`DotNetMissionSDK\MissionReader\Json\GameData.cs`](DotNetMissionSDK/MissionReader/Json/GameData.cs) — `TethysGame` block (beacons, markers, music)

For the AI overview — what each bot implementation does and why — see [`AI_OVERVIEW.md`](AI_OVERVIEW.md).
