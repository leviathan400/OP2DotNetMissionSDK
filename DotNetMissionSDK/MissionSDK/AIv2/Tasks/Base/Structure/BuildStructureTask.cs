using DotNetMissionSDK.HFL;
using DotNetMissionSDK.Pathfinding;
using DotNetMissionSDK.State;
using DotNetMissionSDK.State.Snapshot;
using DotNetMissionSDK.State.Snapshot.Units;
using DotNetMissionSDK.State.Snapshot.UnitTypeInfo;
using System.Collections.Generic;
using System.Linq;

namespace DotNetMissionSDK.AIv2.Tasks.Base.Structure
{
	/// <summary>
	/// This abstract class finds a suitable location and deploys a structure.
	/// Should only ever be used as a prerequisite to MaintainStructure tasks, as this task is only complete when the structure is disconnected.
	/// </summary>
	public abstract class BuildStructureTask : Task
	{
		// Cross-instance throttle to prevent re-issuing DoBuild to the same convec within a short
		// window. Multiple goals each instantiate their own BuildStructureTask (per kit type), and
		// they all find the same convec carrying that kit. Without this throttle, the same convec
		// receives the same DoBuild command 5+ times per cycle, producing the "Building command
		// not successful" spam in OP2's comms log and what looks like a memory/state buildup that
		// crashes OP2 after extended runs. Verified in BotPlayer_*.txt: convec 35 hammered with
		// Tokamak builds for thousands of ticks before OP2 crashed at ~40 min runtime.
		// Window during which a convec is treated as "still working on its last
		// DoBuild" - subsequent BuildStructureTask instances will silently skip
		// reissuing a command to it. Bumped from 30 to 100 (1 Mark) so the
		// convec gets a full Mark to actually deliver a kit and start building
		// before another goal redirects it elsewhere.
		private const int BUILD_REISSUE_COOLDOWN_TICKS = 100;
		private static readonly object s_BuildThrottleLock = new object();
		private static readonly System.Collections.Generic.Dictionary<int, int> s_LastBuildIssuedTickByConvec = new System.Collections.Generic.Dictionary<int, int>();

		protected map_id m_KitToBuild = map_id.Agridome;
		protected int m_DesiredDistance = 0;                // Desired minimum distance to nearest structure

		private bool m_CanBuildDisconnected;
		//private bool m_IsSearchingForDeployLocation;

		private bool m_OverrideLocation = false;
		private LOCATION m_TargetLocation;

		// Dedupe state-message log spam ("no convec", "convec busy", "no valid tile") -
		// these are repeated every cycle until the underlying condition changes. Events like
		// "issuing DoBuild" are NOT deduped because they're real actions, not states.
		private string m_LastLoggedStateMessage;

		public BuildStructureKitTask buildKitTask				{ get; protected set;	}


		public BuildStructureTask(int ownerID) : base(ownerID) { }

		public override bool IsTaskComplete(StateSnapshot stateSnapshot)
		{
			/*PlayerState owner = stateSnapshot.players[ownerID];

			// Task is complete if a structure is not connected.
			foreach (UnitState unit in owner.units.GetListForType(m_KitToBuild))
			{
				StructureState building = (StructureState)unit;

				if (!stateSnapshot.commandMap.ConnectsTo(ownerID, building.GetRect()))
					return true;
			}*/

			// Task is never complete. Always try to build another one.
			return false;
		}

		/// <summary>
		/// Sets the target location to build around.
		/// </summary>
		public void SetLocation(LOCATION targetPosition)
		{
			m_OverrideLocation = true;
			m_TargetLocation = targetPosition;

			buildKitTask.SetLocation(targetPosition);
		}

		public override void GeneratePrerequisites()
		{
		}

		protected override bool CanPerformTask(StateSnapshot stateSnapshot)
		{
			PlayerState owner = stateSnapshot.players[ownerID];

			// Get convec with kit
			if (owner.units.convecs.FirstOrDefault((unit) => unit.cargoType == m_KitToBuild) != null)
				return true;

			if (owner.CanBuildUnit(m_KitToBuild))
				return true;

			return false;
		}

		protected override TaskResult PerformTask(StateSnapshot stateSnapshot, TaskRequirements restrictedRequirements, BotCommands unitActions)
		{
			PlayerState owner = stateSnapshot.players[ownerID];
			DotNetMissionSDK.AI.BotLog log = DotNetMissionSDK.AI.BotLog.Get(ownerID);

			// Get idle convec with kit
			ConvecState convec = owner.units.convecs.FirstOrDefault((unit) =>
			{
				return unit.cargoType == m_KitToBuild;
			});

			if (convec == null)
			{
				LogStateOnce(log, stateSnapshot.time, "BuildStructureTask(" + m_KitToBuild + "): no convec carrying this kit");
				return new TaskResult(TaskRequirements.None);
			}

			// Wait for docking or building to complete
			if (convec.curAction != ActionType.moDone)
			{
				LogStateOnce(log, stateSnapshot.time, "BuildStructureTask(" + m_KitToBuild + "): convec " + convec.unitID + " busy action=" + convec.curAction);
				return new TaskResult(TaskRequirements.None);
			}

			// Throttle: silently skip if this convec just got a DoBuild from any BuildStructureTask
			// instance. A convec can only execute one command at a time, so per-convec throttle is
			// sufficient and prevents the multi-goal pile-on. Silent skip - no log entry - because
			// the throttle would otherwise produce its own line-storm.
			lock (s_BuildThrottleLock)
			{
				int lastTick;
				if (s_LastBuildIssuedTickByConvec.TryGetValue(convec.unitID, out lastTick))
				{
					int sinceLast = stateSnapshot.time - lastTick;
					if (sinceLast >= 0 && sinceLast < BUILD_REISSUE_COOLDOWN_TICKS)
						return new TaskResult(TaskRequirements.None);
				}
			}

			// If we can build earthworkers or have one, we can deploy disconnected structures
			m_CanBuildDisconnected = owner.units.earthWorkers.Count > 0 || owner.units.vehicleFactories.Count > 0 || !NeedsTube(m_KitToBuild);

			if (!m_OverrideLocation)
			{
				// Find closest CC
				UnitState closestCC = owner.units.GetClosestUnitOfType(map_id.CommandCenter, convec.position);
				if (closestCC != null)
					m_TargetLocation = closestCC.position;
			}

			// Wait for search to complete
			//if (m_IsSearchingForDeployLocation)
			//	return true;

			// Find open location near CC
			LOCATION foundPt;
			if (!Pathfinder.GetClosestValidTile(m_TargetLocation, (x, y) => GetTileCost(stateSnapshot, x, y), (x, y) => IsValidTile(stateSnapshot, x, y), out foundPt))
			{
				LogStateOnce(log, stateSnapshot.time, "BuildStructureTask(" + m_KitToBuild + "): NO VALID TILE near (" + m_TargetLocation.x + "," + m_TargetLocation.y + ") canDisconnect=" + m_CanBuildDisconnected);
				return new TaskResult(TaskRequirements.None);
			}

			// TODO: Run GetClosestValidTile asynchronously? ^^^

			ClearDeployArea(convec, convec.cargoType, foundPt, stateSnapshot, ownerID, unitActions);

			// Record issuance BEFORE adding the command so subsequent BuildStructureTask instances
			// running later in the same async cycle see the cooldown and don't pile on the same convec.
			lock (s_BuildThrottleLock)
			{
				s_LastBuildIssuedTickByConvec[convec.unitID] = stateSnapshot.time;
			}

			// Build structure - this is an EVENT (not a state), so always log.
			// Also clear the state-message dedup since we transitioned out of a stuck state.
			m_LastLoggedStateMessage = null;
			log.Write(stateSnapshot.time, "BuildStructureTask(" + m_KitToBuild + "): issuing DoBuild at (" + foundPt.x + "," + foundPt.y + ") convec=" + convec.unitID);
			unitActions.AddUnitCommand(convec.unitID, 2, () => GameState.GetUnit(convec.unitID)?.DoBuild(m_KitToBuild, foundPt.x, foundPt.y));

			return new TaskResult(TaskRequirements.None);
		}

		// Log a state message only if it differs from the previous state message logged.
		// Suppresses noise from per-cycle repetition of "convec busy", "no valid tile", etc.
		private void LogStateOnce(DotNetMissionSDK.AI.BotLog log, int tick, string message)
		{
			if (message != m_LastLoggedStateMessage)
			{
				log.Write(tick, message);
				m_LastLoggedStateMessage = message;
			}
		}

		public static void ClearDeployArea(UnitState deployUnit, map_id buildingType, LOCATION deployPt, StateSnapshot stateSnapshot, int ownerID, BotCommands unitActions)
		{
			// Get area to deploy structure
			GlobalStructureInfo info = stateSnapshot.structureInfo[buildingType];

			LOCATION size = info.GetSize(true);
			MAP_RECT targetArea = new MAP_RECT(deployPt.x-size.x+1, deployPt.y-size.y+1, size.x,size.y);

			// Order all units except this convec to clear the area
			foreach (UnitState unit in stateSnapshot.unitMap.GetUnitsInArea(targetArea))
			{
				if (!unit.isVehicle)
					continue;

				if (unit.unitID == deployUnit.unitID)
					continue;

				LOCATION position = unit.position;

				// Move units away from center
				LOCATION dir = position - deployPt;
				if (dir.x == 0 && dir.y == 0)
					dir.x = 1;

				if (dir.x > 1) dir.x = 1;
				if (dir.y > 1) dir.y = 1;
				if (dir.x < -1) dir.x = -1;
				if (dir.y < -1) dir.y = -1;

				position += dir;

				LOCATION normal = dir.normal;

				if (!stateSnapshot.tileMap.IsTilePassable(position) || IsAreaBlocked(stateSnapshot, new MAP_RECT(position, new LOCATION(1,1)), ownerID))
					position = unit.position + normal;
				if (!stateSnapshot.tileMap.IsTilePassable(position) || IsAreaBlocked(stateSnapshot, new MAP_RECT(position, new LOCATION(1,1)), ownerID))
					position = unit.position - normal;
				if (!stateSnapshot.tileMap.IsTilePassable(position) || IsAreaBlocked(stateSnapshot, new MAP_RECT(position, new LOCATION(1,1)), ownerID))
					continue;

				unitActions.AddUnitCommand(unit.unitID, 1, () => GameState.GetUnit(unit.unitID)?.DoMove(position.x, position.y));
			}
		}

		// Callback for determining tile cost
		public static int GetTileCost(StateSnapshot stateSnapshot, int x, int y)
		{
			if (!stateSnapshot.tileMap.IsTilePassable(x,y))
				return Pathfinder.Impassable;

			return 1;
		}

		// Callback for determining if tile is a valid place point
		protected bool IsValidTile(StateSnapshot stateSnapshot, int x, int y)
		{
			PlayerState owner = stateSnapshot.players[ownerID];

			GlobalStructureInfo info = stateSnapshot.structureInfo[m_KitToBuild];

			// Get area to deploy structure
			LOCATION size = info.GetSize(true);
			MAP_RECT targetArea = new MAP_RECT(x-size.x+1, y-size.y+1, size.x,size.y);

			if (!AreTilesPassable(stateSnapshot, targetArea, x, y))
				return false;

			// Apply minimum distance if we can build this disconnected
			if (m_CanBuildDisconnected)
				targetArea.Inflate(m_DesiredDistance, m_DesiredDistance);
			else
			{
				// Force structure to build on connected ground
				MAP_RECT unbulldozedArea = targetArea;
				unbulldozedArea.Inflate(-1, -1);
				if (!stateSnapshot.commandMap.ConnectsTo(ownerID, unbulldozedArea))
					return false;
			}

			// Check if area is blocked by structure or enemy
			if (IsAreaBlocked(stateSnapshot, targetArea, owner.playerID))
				return false;

			return true;
		}

		public static bool AreTilesPassable(StateSnapshot stateSnapshot, MAP_RECT targetArea, int x, int y)
		{
			// Check if target tiles are impassable
			for (int tx=targetArea.xMin; tx < targetArea.xMax; ++tx)
			{
				for (int ty=targetArea.yMin; ty < targetArea.yMax; ++ty)
				{
					if (!stateSnapshot.tileMap.IsTilePassable(tx, ty))
						return false;
				}
			}

			return true;
		}

		public static bool IsAreaBlocked(StateSnapshot stateSnapshot, MAP_RECT targetArea, int ownerID, bool includeBulldozedArea=false)
		{
			// Check if area is blocked by structure or enemy
			foreach (UnitState unit in stateSnapshot.unitMap.GetUnitsInArea(targetArea))
			{
				if (unit.isBuilding)
				{
					MAP_RECT unitArea = ((StructureState)unit).GetRect(includeBulldozedArea);
					if (targetArea.DoesRectIntersect(unitArea))
						return true;
				}
				else if (unit.isVehicle)
				{
					if (unit.ownerID != ownerID && targetArea.Contains(unit.position))
						return true;
				}
			}

			// Don't allow structure to be built on ground where a mine can be deployed
			foreach (GaiaUnitState beacon in stateSnapshot.gaia)
			{
				map_id beaconType = beacon.unitType;

				if (beaconType != map_id.MiningBeacon &&
					beaconType != map_id.Fumarole &&
					beaconType != map_id.MagmaVent)
					continue;

				if (targetArea.DoesRectIntersect(new MAP_RECT(beacon.position.x-2, beacon.position.y-1, 5,3)))
					return true;
			}

			return false;
		}

		public static bool NeedsTube(map_id typeID)
		{
			switch (typeID)
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
			}

			return IsStructure(typeID);
		}

		private static bool IsStructure(map_id typeID)
		{
			return (int)typeID >= 21 && (int)typeID <= 58;
		}
	}
}
