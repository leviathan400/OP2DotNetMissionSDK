# DotNetMissionSDK - Quick Start Reference

This reference will help you get to work on your custom mission in no time.

(Converted from `ReadMe.txt`, originally written by TechCor for DotNetMissionSDK 2.0. Content preserved; formatting updated to Markdown; small clarifications added in the 3.0 community fork.)

---

## 1. CustomLogic

For most projects, you will want to use the [`CustomLogic.cs`](CustomLogic.cs) file as your entry point. It lives at the project root.

If you do not want to load startup data from a JSON file, you can disable it here by setting `useJson` to `false`.

The `SDK_VERSION` constant lives here too - bump it per release. It is surfaced in-game (Communications panel) and in `DotNetLog.txt` on mission start.

---

## 2. Triggers

To add OP2 triggers, in `InitializeNewMission`, call `AddTrigger()`. Pass a `TriggerStub` created from a static method.

When the trigger fires, `OnTriggerExecuted` will be called.

The `triggerID` passed to the `TriggerStub` can be used to identify which trigger you are handling.

---

## 3. Outpost2DLL and HFL

If you are already familiar with the C++ mission SDK, these folders contain the classes needed to directly access Outpost 2. The .NET SDK makes heavy use of this API for accessing the Outpost 2 internals, so it is a good idea to familiarize yourself with this functionality.

---

## 4. State.Snapshot.StateSnapshot

`StateSnapshot` contains the game state at a particular iteration of the game. With each update, the `TethysGame.Time()`, game, player, and unit data is captured and saved into an immutable `StateSnapshot` object.

Because `StateSnapshot` is immutable, it is safe to read data from across threads.

You can use the snapshot passed into `Update(StateSnapshot stateSnapshot)` to read all sorts of useful data for your mission.

---

## 5. State.GameState

`GameState` contains live access to important player data, including colony state and units.

Unlike `StateSnapshot`, you can modify the data and execute commands on the player and units.

You should never call `GameState` from outside of the main thread.

`GameState` uses specialized classes that improve performance and features over the `Outpost2DLL`/`HFL` classes. Accessing units is faster, and there is extended unit functionality such as built-in pathfinding.

---

## 6. BaseGenerator

The base generator can be used for rapidly creating bases on the map, without manually specifying each location.

It is also used in the JSON mission file in the `AutoLayout` section.

You can see how it is used in `MissionLogic.cs`.

---

## 7. BotPlayer

`BotPlayer` is a generic AI that can take control of a player from any starting state and perform its initialized settings. This is a good replacement for manually specifying AI activities, which can be quite tedious. `BotPlayer` can be started and stopped at any time.

**The `BotPlayer` can take over any player, as long as the player is set to human / `GoHuman()`. The bot requires the full player state that `GoHuman()` enables.**

It is easiest to enable `BotPlayer` through the JSON mission file, but creating a bot is as simple as calling the constructor, `Start`, and `Update` methods.

> **3.0 fork note**: that "set to human" requirement is encoded in the `.opm` as `"IsHuman": true`. The community fork **3.0** added a pluggable AI architecture - see [`AI_OVERVIEW.md`](../AI_OVERVIEW.md) for the `IBotPlayer` contract, the `MissionSDK/AI/` (TechCor reference), `MissionSDK/AIv2/`, `MissionSDK/AI_Blank/`, and `MissionSDK/TestAI/` folders, and how the `AIImpl` field in the `.opm` selects which AI implementation drives each player. Slots with `IsHuman: false` get OP2's god-mode population (256/4096/4096 colonists, no food simulation) - see [`ISSUES.md`](../ISSUES.md) for the full investigation.

---

## 8. AsyncPump

The `AsyncPump` executes a method on a worker thread to be completed at a future `TethysGame.Time()`.

You can run an asynchronous task by calling `AsyncPump.Run()` and passing your method to execute asynchronously, and the method to execute upon completion.

All asynchronous classes use the `AsyncPump` under the hood. This includes the pathfinder, which runs the cost and valid tile callbacks asynchronously.

Asynchronous operations should only use the `StateSnapshot` class. Any use of other classes run the risk of crashes and desync in multiplayer. In the completion callback, you are on the main thread and may use any class.
