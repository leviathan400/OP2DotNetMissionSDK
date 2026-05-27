using IBotPlayer = DotNetMissionSDK.AI.IBotPlayer;
using DotNetMissionSDK.AI;
using DotNetMissionSDK.Async;
using DotNetMissionSDK.HFL;
using DotNetMissionSDK.Pathfinding;
using DotNetMissionSDK.State;
using DotNetMissionSDK.State.Snapshot;
using DotNetMissionSDK.State.Snapshot.Units;
using DotNetMissionSDK.State.Snapshot.UnitTypeInfo;
using System.Linq;

namespace DotNetMissionSDK.TestAI
{
	// TestAI - a deliberately small bot written from scratch to demonstrate
	// the pluggable AI architecture. Does NOT use TechCor's goal/task tree,
	// weighted goals, AsyncPump, or any of the heavy machinery. Runs entirely
	// on the main thread out of the IBotPlayer.Update hook.
	//
	// Strategy:
	//   1. Deploy every idle convec that has a kit loaded - first the CC,
	//      then everything else near the existing CC
	//   2. Send each idle RoboMiner to the nearest un-mined common beacon
	//   3. Cargo trucks carrying CommonMetal dock at the nearest smelter
	//      and unload (kicks off the smelter -> structure factory pipeline)
	//   4. Earthworkers lay tubes back to the CC tube network for any
	//      building that's currently disconnected (idle/unpowered)
	//   5. Heartbeat log every Mark so we can confirm the bot is alive
	//
	// What it does NOT do (yet):
	//   - Build new convecs / earthworkers / cargo trucks
	//   - Smelter -> factory truck routing (just unloads, doesn't go back)
	//   - Research, military, repair, anything past the initial deploy
	public class BotPlayer : IBotPlayer
	{
		public BotType botType					{ get; set; }
		public int playerID						{ get; private set; }
		public bool isActive					{ get; private set; }

		// Tick throttling. We only consider issuing new commands every
		// ACTION_INTERVAL_TICKS to avoid spamming OP2's command queue.
		private const int ACTION_INTERVAL_TICKS = 30;
		private const int HEARTBEAT_INTERVAL_TICKS = 100;
		private int m_LastActionTick = -1000;

		// Per-convec / per-miner cooldown so we don't reissue the same
		// DoBuild to a convec that's already moving toward its target.
		private System.Collections.Generic.Dictionary<int, int> m_LastCommandTickByUnit = new System.Collections.Generic.Dictionary<int, int>();
		private const int PER_UNIT_COOLDOWN_TICKS = 100;

		// Earthworker tube placement: per-tile cooldown so the bot doesn't
		// hammer the same (x,y) when DoBuildWall silently fails (earthworker
		// can't reach, tile obstructed, etc). After this long without success
		// we let some other tube candidate be picked instead.
		private System.Collections.Generic.Dictionary<long, int> m_LastTubeAttemptTickByTile = new System.Collections.Generic.Dictionary<long, int>();
		private const int PER_TILE_TUBE_COOLDOWN_TICKS = 500;

		// Tiles claimed by a pending DoBuild command. Prevents two convecs
		// from being sent to overlapping deploy spots (which is the only
		// reason TestAI used to "lose" buildings - the second convec arrives
		// at a now-occupied tile and fails). Cleared after the build has had
		// time to complete and the actual structure shows up in the snapshot.
		private System.Collections.Generic.Dictionary<long, int> m_LastBuildClaimTickByTile = new System.Collections.Generic.Dictionary<long, int>();
		private const int PER_TILE_BUILD_CLAIM_TICKS = 500;


		public BotPlayer(BotType botType, int playerToControlID)
		{
			ThreadAssert.MainThreadRequired();
			this.botType = botType;
			this.playerID = playerToControlID;
			BotLog.Get(playerToControlID).Write(TethysGame.Time(), "TestAI construct: type=" + botType);
		}

		public void Start()
		{
			isActive = true;
			BotLog.Get(playerID).Write(TethysGame.Time(), "TestAI: Start");
		}

		public void Stop()
		{
			isActive = false;
			BotLog.Get(playerID).Write(TethysGame.Time(), "TestAI: Stop");
		}

		public void Update(StateSnapshot snap)
		{
			ThreadAssert.MainThreadRequired();
			if (!isActive)
				return;

			if (snap.time % HEARTBEAT_INTERVAL_TICKS == 0)
			{
				PlayerState p = snap.players[playerID];
				BotLog.Get(playerID).Write(snap.time,
					"TestAI heartbeat: convecs=" + p.units.convecs.Count +
					" buildings=" + p.units.GetStructures().Count() +
					" mines=" + p.units.commonOreMines.Count);
			}

			if (snap.time - m_LastActionTick < ACTION_INTERVAL_TICKS)
				return;
			m_LastActionTick = snap.time;

			TryDeployOneConvec(snap);
			TryDeployOneMine(snap);
			TryUnloadOneTruck(snap);
			TryLayOneTube(snap);
		}

		// Deploy priority order. CC always first (needed as the tube-network
		// anchor). Then Smelter (activates ore storage for incoming trucks +
		// must be connected, so good 2nd). Then Tokamak (power for everything
		// else). Then Agridome (food). Then StructureFactory (to build kits
		// later). Anything not on this list is deployed last in convec-list
		// order.
		private static readonly map_id[] s_DeployPriority = new map_id[]
		{
			map_id.CommandCenter,
			map_id.CommonOreSmelter,
			map_id.Tokamak,
			map_id.Agridome,
			map_id.StructureFactory,
		};

		// Find the next convec to deploy according to the priority order.
		// One DoBuild per call to keep things slow and observable in the log.
		private void TryDeployOneConvec(StateSnapshot snap)
		{
			PlayerState p = snap.players[playerID];

			// Determine anchor: if no CC, the CC convec uses its own position;
			// everything else uses the CC's position once it exists.
			bool ccExists = p.units.commandCenters.Count > 0;
			LOCATION ccAnchor = ccExists ? p.units.commandCenters[0].position : new LOCATION(0, 0);

			// Pass 1: try priority kits in order
			foreach (map_id priorityKit in s_DeployPriority)
			{
				// CC is the only kit allowed to deploy before the CC itself
				// exists; everything else waits until we have a CC anchor.
				if (!ccExists && priorityKit != map_id.CommandCenter)
					return;

				foreach (ConvecState convec in p.units.convecs)
				{
					if (convec.cargoType != priorityKit) continue;
					if (convec.curAction != ActionType.moDone) continue;
					if (IsOnCooldown(convec.unitID, snap.time)) continue;

					LOCATION anchor = (priorityKit == map_id.CommandCenter) ? convec.position : ccAnchor;
					if (TryDeployConvec(snap, convec, anchor)) return;
				}
			}

			// Pass 2: anything not in the priority list, in convec-list order.
			if (!ccExists) return;
			foreach (ConvecState convec in p.units.convecs)
			{
				if (convec.curAction != ActionType.moDone) continue;
				if (convec.cargoType == map_id.None) continue;
				if (IsOnCooldown(convec.unitID, snap.time)) continue;
				if (System.Array.IndexOf(s_DeployPriority, convec.cargoType) >= 0) continue;
				if (TryDeployConvec(snap, convec, ccAnchor)) return;
			}
		}

		private bool TryDeployConvec(StateSnapshot snap, ConvecState convec, LOCATION anchor)
		{
			map_id kit = convec.cargoType;

			LOCATION foundPt;
			bool found = Pathfinder.GetClosestValidTile(
				anchor,
				(x, y) => GetTileCost(snap, x, y),
				(x, y) => IsValidTile(snap, kit, x, y),
				out foundPt);

			if (!found)
			{
				BotLog.Get(playerID).Write(snap.time,
					"TestAI: no valid tile for " + kit + " near (" + anchor.x + "," + anchor.y + ")");
				MarkCooldown(convec.unitID, snap.time);
				return false;
			}

			// Before issuing DoBuild, kick any friendly vehicles out of the
			// footprint so OP2 doesn't reject the deploy. Mirrors TechCor's
			// BuildStructureTask.ClearDeployArea.
			ClearDeployArea(snap, convec, kit, foundPt);

			BotLog.Get(playerID).Write(snap.time,
				"TestAI: deploy " + kit + " at (" + foundPt.x + "," + foundPt.y + ") convec=" + convec.unitID);

			UnitEx u = GameState.GetUnit(convec.unitID);
			u?.DoBuild(kit, foundPt.x, foundPt.y);
			MarkCooldown(convec.unitID, snap.time);

			// Claim the footprint area so subsequent IsValidTile checks
			// reject this region while this build is in progress.
			GlobalStructureInfo info = snap.structureInfo[kit];
			LOCATION sz = info.GetSize(true);
			MAP_RECT claim = new MAP_RECT(foundPt.x - sz.x + 1, foundPt.y - sz.y + 1, sz.x, sz.y);
			for (int tx = claim.xMin; tx < claim.xMax; ++tx)
				for (int ty = claim.yMin; ty < claim.yMax; ++ty)
					m_LastBuildClaimTickByTile[TileKey(new LOCATION(tx, ty))] = snap.time;
			return true;
		}

		// Find an idle RoboMiner and send it to deploy on the closest
		// common-ore beacon that no one has mined yet.
		private void TryDeployOneMine(StateSnapshot snap)
		{
			PlayerState p = snap.players[playerID];

			VehicleState miner = p.units.roboMiners.FirstOrDefault(m =>
				m.curAction == ActionType.moDone && !IsOnCooldown(m.unitID, snap.time));
			if (miner == null) return;

			MiningBeaconState bestBeacon = null;
			int bestDist = int.MaxValue;
			foreach (MiningBeaconState beacon in snap.gaia.miningBeacons)
			{
				if (beacon.oreType != BeaconType.Common) continue;
				if (IsBeaconAlreadyMined(snap, beacon)) continue;

				int dx = beacon.position.x - miner.position.x;
				int dy = beacon.position.y - miner.position.y;
				int dist = dx * dx + dy * dy;
				if (dist < bestDist) { bestDist = dist; bestBeacon = beacon; }
			}

			if (bestBeacon == null) return;

			BotLog.Get(playerID).Write(snap.time,
				"TestAI: deploy mine at beacon (" + bestBeacon.position.x + "," + bestBeacon.position.y + ") miner=" + miner.unitID);
			UnitEx u = GameState.GetUnit(miner.unitID);
			u?.DoDeployMiner(bestBeacon.position.x, bestBeacon.position.y);
			MarkCooldown(miner.unitID, snap.time);
		}

		// Per-truck state tracking for the post-unload nudge. We remember how
		// long each truck has been in its current ActionType so we can detect
		// trucks that finished unloading but are still parked on the dock.
		private class TruckActionState { public ActionType lastAction; public int stateChangedTick; }
		private System.Collections.Generic.Dictionary<int, TruckActionState> m_TruckActionState =
			new System.Collections.Generic.Dictionary<int, TruckActionState>();

		// Move any friendly vehicles (other than the deploying convec itself)
		// out of the deploy footprint so OP2 accepts the DoBuild. One DoMove
		// per blocker, away from the deploy centre, to the first passable
		// adjacent tile. If we can't find a clear adjacent tile we skip - OP2
		// will reject DoBuild and we'll retry with a different location.
		private void ClearDeployArea(StateSnapshot snap, ConvecState deployConvec, map_id kit, LOCATION deployPt)
		{
			GlobalStructureInfo info = snap.structureInfo[kit];
			LOCATION size = info.GetSize(true);
			MAP_RECT area = new MAP_RECT(deployPt.x - size.x + 1, deployPt.y - size.y + 1, size.x, size.y);

			foreach (UnitState u in snap.unitMap.GetUnitsInArea(area))
			{
				if (!u.isVehicle) continue;
				if (u.unitID == deployConvec.unitID) continue;  // deploying convec - OP2 positions it itself
				if (u.ownerID != playerID) continue;            // only push our own units

				// Direction from the deploy centre to the unit, clamped to one tile.
				LOCATION dir = u.position - deployPt;
				if (dir.x == 0 && dir.y == 0) dir.x = 1;
				if (dir.x >  1) dir.x =  1;
				if (dir.y >  1) dir.y =  1;
				if (dir.x < -1) dir.x = -1;
				if (dir.y < -1) dir.y = -1;
				LOCATION dest = u.position + dir;

				if (!snap.tileMap.IsTilePassable(dest)) continue;

				BotLog.Get(playerID).Write(snap.time,
					"TestAI: clear area, move unit=" + u.unitID + " from (" + u.position.x + "," + u.position.y +
					") to (" + dest.x + "," + dest.y + ") for " + kit + " deploy");
				UnitEx live = GameState.GetUnit(u.unitID);
				live?.DoMove(dest.x, dest.y);
			}
		}

		// Cargo truck behavior. Every cycle, evaluate every truck with metal
		// cargo. Any truck already on the dock gets its smelter pumped and
		// nudged off when idle. New DoDock commands are issued ONE AT A
		// TIME so trucks don't all converge on the same dock and bottleneck.
		// Mirrors TechCor's UnloadSuppliesTask including the FixTruckUnloading
		// nudge that kicks idle trucks off the dock after they've unloaded.
		private void TryUnloadOneTruck(StateSnapshot snap)
		{
			PlayerState p = snap.players[playerID];
			if (p.units.commonOreSmelters.Count == 0) return;

			// Garbage-collect state for destroyed trucks
			System.Collections.Generic.HashSet<int> liveIDs = new System.Collections.Generic.HashSet<int>();
			foreach (CargoTruckState t in p.units.cargoTrucks) liveIDs.Add(t.unitID);
			foreach (int id in System.Linq.Enumerable.ToList(m_TruckActionState.Keys))
				if (!liveIDs.Contains(id)) m_TruckActionState.Remove(id);

			// Only one truck per cycle is allowed to start a NEW dock approach;
			// already-docked trucks always get unload+nudge processed.
			bool dockCommandIssuedThisCycle = false;

			foreach (CargoTruckState truck in p.units.cargoTrucks)
			{
				// Track state-change time for the nudge logic below
				TruckActionState st;
				if (!m_TruckActionState.TryGetValue(truck.unitID, out st))
				{
					st = new TruckActionState { lastAction = truck.curAction, stateChangedTick = snap.time };
					m_TruckActionState[truck.unitID] = st;
				}
				else if (st.lastAction != truck.curAction)
				{
					st.lastAction = truck.curAction;
					st.stateChangedTick = snap.time;
				}

				if (truck.cargoType != TruckCargo.CommonMetal) continue;
				if (truck.cargoAmount <= 0) continue;
				if (truck.curAction == ActionType.moObjDocking) continue;

				// Closest smelter. Don't filter on isDisabled - a smelter that's
				// disconnected from the CC tube network shows as disabled but we
				// still want the truck to head there; once the earthworker
				// connects the tube the smelter will activate and accept ore.
				StructureState smelter = null;
				int bestDist = int.MaxValue;
				foreach (StructureState s in p.units.commonOreSmelters)
				{
					int dx = s.position.x - truck.position.x;
					int dy = s.position.y - truck.position.y;
					int d = dx * dx + dy * dy;
					if (d < bestDist) { bestDist = d; smelter = s; }
				}
				if (smelter == null) continue;

				UnitEx liveTruck = GameState.GetUnit(truck.unitID);
				UnitEx liveSmelter = GameState.GetUnit(smelter.unitID);
				if (liveTruck == null || liveSmelter == null) continue;

				if (!truck.IsOnDock(smelter))
				{
					// Only one truck per cycle gets dispatched. If this truck
					// is already underway (moMove) skip the re-issue too.
					if (dockCommandIssuedThisCycle) continue;
					if (truck.curAction == ActionType.moMove) continue;

					BotLog.Get(playerID).Write(snap.time,
						"TestAI: docking truck=" + truck.unitID + " at smelter " + smelter.unitID +
						" (" + smelter.position.x + "," + smelter.position.y + ")");
					liveTruck.DoDock(liveSmelter);
					dockCommandIssuedThisCycle = true;
				}
				else
				{
					// On dock - tell the smelter to suck the cargo out, then
					// nudge the truck a tile away if it's been sitting idle
					// post-unload (TechCor's FixTruckUnloading trick).
					BotLog.Get(playerID).Write(snap.time,
						"TestAI: unloading truck=" + truck.unitID + " at smelter " + smelter.unitID);
					liveSmelter.DoUnloadCargo();

					if (truck.curAction == ActionType.moDone &&
						snap.time - st.stateChangedTick > 8)
					{
						BotLog.Get(playerID).Write(snap.time,
							"TestAI: nudge truck=" + truck.unitID + " off dock");
						liveTruck.DoMove(truck.position.x, truck.position.y + 1);
					}
				}
				// no break - dispatch every eligible truck this cycle
			}
		}

		// Earthworker behavior: find any building that's currently disconnected
		// from the CC's tube network and lay one tube tile along the path
		// back. Repeats next cycle to build the next segment.
		private void TryLayOneTube(StateSnapshot snap)
		{
			PlayerState p = snap.players[playerID];
			if (p.units.earthWorkers.Count == 0) return;
			if (p.units.commandCenters.Count == 0) return;

			VehicleState earthworker = p.units.earthWorkers.FirstOrDefault(e =>
				e.curAction == ActionType.moDone && !IsOnCooldown(e.unitID, snap.time));
			if (earthworker == null) return;

			// Find a disconnected structure
			StructureState disconnected = null;
			foreach (UnitState u in p.units.GetStructures())
			{
				StructureState s = u as StructureState;
				if (s == null) continue;
				if (s.unitType == map_id.CommandCenter) continue;  // CC IS the network root
				if (s.unitType == map_id.Tokamak) continue;        // Tokamak doesn't need a tube
				if (s.unitType == map_id.SolarPowerArray) continue;
				if (s.unitType == map_id.MHDGenerator) continue;
				if (s.unitType == map_id.GeothermalPlant) continue;
				if (s.unitType == map_id.CommonOreMine) continue;
				if (s.unitType == map_id.RareOreMine) continue;
				if (s.unitType == map_id.MagmaWell) continue;

				MAP_RECT rect = snap.structureInfo[s.unitType].GetRect(s.position);
				if (!snap.commandMap.ConnectsTo(playerID, rect))
				{
					disconnected = s;
					break;
				}
			}
			if (disconnected == null) return;

			// Path from disconnected building back to the network
			MAP_RECT buildingRect = snap.structureInfo[disconnected.unitType].GetRect(disconnected.position);
			LOCATION[] path = snap.commandMap.GetPathToClosestConnectedTile(playerID, buildingRect);
			if (path == null || path.Length == 0) return;

			// Walk path from the building side toward the network; place one
			// tube on the first non-tube tile that's not blocked AND not on
			// per-tile cooldown.
			for (int i = path.Length - 1; i >= 0; --i)
			{
				LOCATION tile = path[i];
				if (snap.tileMap.GetCellType(tile) == CellType.Tube0) continue;

				UnitState onTile = snap.unitMap.GetUnitOnTile(tile);
				if (onTile != null && onTile.unitID != earthworker.unitID) continue;

				if (IsTubeTileOnCooldown(tile, snap.time)) continue;

				BotLog.Get(playerID).Write(snap.time,
					"TestAI: lay tube at (" + tile.x + "," + tile.y + ") earthworker=" + earthworker.unitID +
					" pos=(" + earthworker.position.x + "," + earthworker.position.y + ") connecting " +
					disconnected.unitType + " #" + disconnected.unitID);

				UnitEx u = GameState.GetUnit(earthworker.unitID);
				u?.DoBuildWall(map_id.Tube, new MAP_RECT(tile, new LOCATION(1, 1)));
				MarkCooldown(earthworker.unitID, snap.time);
				MarkTubeTileCooldown(tile, snap.time);
				return;
			}
		}

		private static long TileKey(LOCATION t)
		{
			return ((long)(t.x & 0xFFFF) << 16) | (long)(t.y & 0xFFFF);
		}

		private bool IsTubeTileOnCooldown(LOCATION tile, int currentTick)
		{
			int last;
			if (!m_LastTubeAttemptTickByTile.TryGetValue(TileKey(tile), out last)) return false;
			return currentTick - last < PER_TILE_TUBE_COOLDOWN_TICKS;
		}

		private bool IsTileClaimedForBuild(LOCATION tile, int currentTick)
		{
			int last;
			if (!m_LastBuildClaimTickByTile.TryGetValue(TileKey(tile), out last)) return false;
			return currentTick - last < PER_TILE_BUILD_CLAIM_TICKS;
		}

		private void MarkTubeTileCooldown(LOCATION tile, int currentTick)
		{
			m_LastTubeAttemptTickByTile[TileKey(tile)] = currentTick;
		}

		private static bool IsBeaconAlreadyMined(StateSnapshot snap, MiningBeaconState beacon)
		{
			foreach (PlayerState p in snap.players)
			{
				foreach (StructureState mine in p.units.commonOreMines)
				{
					int dx = mine.position.x - beacon.position.x;
					int dy = mine.position.y - beacon.position.y;
					if (dx * dx + dy * dy <= 4) return true;
				}
			}
			return false;
		}

		private bool IsOnCooldown(int unitID, int currentTick)
		{
			int last;
			if (!m_LastCommandTickByUnit.TryGetValue(unitID, out last)) return false;
			return currentTick - last < PER_UNIT_COOLDOWN_TICKS;
		}

		private void MarkCooldown(int unitID, int currentTick)
		{
			m_LastCommandTickByUnit[unitID] = currentTick;
		}

		// Pathfinder callbacks - mirror what BuildStructureTask uses so
		// tile-selection logic stays consistent across AIs.
		private static int GetTileCost(StateSnapshot snap, int x, int y)
		{
			if (!snap.tileMap.IsTilePassable(x, y)) return Pathfinder.Impassable;
			return 1;
		}

		private bool IsValidTile(StateSnapshot snap, map_id kit, int x, int y)
		{
			GlobalStructureInfo info = snap.structureInfo[kit];
			LOCATION size = info.GetSize(true);
			MAP_RECT area = new MAP_RECT(x - size.x + 1, y - size.y + 1, size.x, size.y);

			for (int tx = area.xMin; tx < area.xMax; ++tx)
				for (int ty = area.yMin; ty < area.yMax; ++ty)
				{
					if (!snap.tileMap.IsTilePassable(tx, ty)) return false;
					if (IsTileClaimedForBuild(new LOCATION(tx, ty), snap.time)) return false;
				}

			// Some buildings need empty space around them - Tokamak in
			// particular melts down when damaged and the explosion damages
			// neighbours, so we want it at least ~8 tiles from other
			// structures. Inflate the area we check for nearby buildings.
			int separation = GetMinSeparation(kit);
			MAP_RECT blockCheck = area;
			if (separation > 0) blockCheck.Inflate(separation, separation);

			foreach (UnitState u in snap.unitMap.GetUnitsInArea(blockCheck))
			{
				if (u.isBuilding) return false;
				if (u.isVehicle && u.ownerID != playerID && blockCheck.Contains(u.position)) return false;
			}

			// If this building needs a tube to function, require placement
			// inside the CC tube-coverage area. Otherwise we end up with
			// smelters/factories/labs that look built but are idle because
			// they have no connection to a CC. (Buildings that work without
			// a tube - CC itself, power plants, mines - are exempt.)
			if (NeedsTubeConnection(kit))
			{
				MAP_RECT inner = area;
				inner.Inflate(-1, -1);
				if (!snap.commandMap.ConnectsTo(playerID, inner))
					return false;
			}

			return true;
		}

		// Minimum buffer (in tiles) to require between this building and any
		// neighbouring structure. Tokamak/MHD are blast-prone; everything
		// else is fine at zero buffer (just don't overlap).
		private static int GetMinSeparation(map_id kit)
		{
			switch (kit)
			{
				case map_id.Tokamak:        return 8;
				case map_id.MHDGenerator:   return 8;
				default:                    return 0;
			}
		}

		private static bool NeedsTubeConnection(map_id type)
		{
			switch (type)
			{
				case map_id.CommandCenter:
				case map_id.LightTower:
				case map_id.CommonOreMine:
				case map_id.RareOreMine:
				case map_id.MagmaWell:
				case map_id.Tokamak:
				case map_id.SolarPowerArray:
				case map_id.MHDGenerator:
				case map_id.GeothermalPlant:
					return false;
				default:
					return true;
			}
		}
	}
}
