using IBotPlayer = DotNetMissionSDK.AI.IBotPlayer;
using BotLog = DotNetMissionSDK.AI.BotLog;
using DotNetMissionSDK.AIv2.Managers;
using DotNetMissionSDK.Async;
using DotNetMissionSDK.HFL;
using DotNetMissionSDK.State;
using DotNetMissionSDK.State.Snapshot;
using DotNetMissionSDK.State.Snapshot.Units;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DotNetMissionSDK.AIv2
{
	/// <summary>
	/// Represents different predefined bot goal weights.
	/// </summary>
	public enum BotType
	{
		None,					// Bot does nothing.
		PopulationGrowth,		// Bot focuses on growing population. Keeps enough defense to avoid being killed. Will build Recreation, DIRT and other optional structures.
		LaunchStarship,			// Bot focuses on launching starship. Keeps enough defense to avoid being killed.
		EconomicGrowth,			// Bot focuses on resource acquisition. Keeps enough defense to avoid being killed.
		Passive,				// Bot does not build new structures. Keeps enough defense to avoid being killed.
		Defender,				// Bot will build military units and defend itself and allies. Does not attack.
		Balanced,				// Bot will build military units and defend itself and allies. Attacks with best available strategy.
		Aggressive,				// Bot will build military units and won't defend itself or allies. Attacks with best available strategy.
		Harassment,				// Bot will build military units and harass cargo trucks, power plants, and unescorted or poorly defended utility vehicles.
		Wreckless,				// Bot will build military units and send them to attack even against overwhelming odds.
	}

	public class BotPlayer : IBotPlayer
	{
		public BotType botType						{ get; set; }
		/*
		// Customizable Flags - Can be changed while bot is active.
		public bool canResearchOptionalStructures = true;   // If true, bot will research recreational/forum, GORF, DIRT, consumer factory and other optional structures.
		public bool canBuildGuardPosts = true;              // If true, bot will build guard posts.
		public bool canBuildLightTowers;                    // If true, bot will build light posts around the exterior of its bases.
		public bool canBuildEvacTransports;                 // If true, bot will build enough evac transports for its population. Transports will roam around the colony.
		public bool canBuildScouts;                         // If true, bot will build scouts. Scouts will roam the map and investigate enemy activity.
		public bool canBuildMilitaryUnits = true;           // If true, bot will build military units (lynx, panther, tiger). Units will not move without military commander.
		public bool canLaunchEvacModule;                    // If true, bot will launch the 200 population evac module from the spaceport.
		*/
		public BaseManager baseManager				{ get; private set; }
		public LaborManager laborManager			{ get; private set; }
		public CombatManager combatManager			{ get; private set; }

		public int playerID						{ get; private set; }		// OP2 player slot this bot controls
		public bool isActive						{ get; private set; }		// Is the bot controlling the player?
		public DotNetMissionSDK.AI.MissionContext context  { get; private set; }  // Mission-wide context

		// Wall-clock time when this bot was constructed. Surfaced in
		// BotPlayer_<N>_Status.txt as "Current Runtime" so you can correlate
		// the bot's state with how long the mission has been running without
		// flipping back to MissionSDK.log timestamps.
		private readonly DateTime m_ConstructionWallTime = DateTime.Now;

		// How often to write the status snapshot (in game ticks).
		// At ~10 ticks/sec, 100 ticks â‰ˆ 10 seconds of game time.
		private const int STATUS_WRITE_INTERVAL_TICKS = 100;

		// Attack-wave behavior: once the bot's military count crosses this
		// threshold AND ATTACK_WAVE_COOLDOWN_TICKS has passed since the last
		// wave, send ATTACK_WAVE_PERCENT of the military force toward the
		// nearest enemy CC. The rest stays for base defense. This is the main
		// behavioral difference from TechCor's reference AI - AIv2 actually
		// goes on the offensive instead of just stockpiling units.
		private const int ATTACK_WAVE_THRESHOLD = 10;
		private const int ATTACK_WAVE_PERCENT = 60;          // 60% offense, 40% defense
		private const int ATTACK_WAVE_COOLDOWN_TICKS = 500;  // 5 Marks
		private int m_LastAttackWaveTick = -100000;

		// Diagnostic: after launching a wave, record each unit's starting
		// position. WAVE_CHECK_DELAY_TICKS later we look them up in the
		// snapshot and log how many are still alive, how many actually
		// moved, and how close they got to the target. Reveals whether
		// DoAttack(Unit) is being silently ignored (no movement), units
		// dying en route (count drops), or arriving but not engaging
		// (close to target but target unscathed).
		private class WaveCheck
		{
			public int launchTick;
			public LOCATION target;
			public List<int> unitIDs = new List<int>();
			public List<LOCATION> startPositions = new List<LOCATION>();
		}
		private List<WaveCheck> m_PendingWaveChecks = new List<WaveCheck>();
		private const int WAVE_CHECK_DELAY_TICKS = 500;

		// Units currently on attack-wave duty. CombatManager.PopulateCombatGroups
		// reads this set and skips these units when assigning to defensive
		// vehicle groups - so our DoAttack commands aren't immediately
		// overridden by group DoMove commands. Removed when the unit dies or
		// there are no enemy CCs left to target.
		public System.Collections.Generic.HashSet<int> activeAttackers
		{
			get { return m_ActiveAttackers; }
		}
		private System.Collections.Generic.HashSet<int> m_ActiveAttackers = new System.Collections.Generic.HashSet<int>();


		public BotPlayer(BotType botType, int playerToControlID, DotNetMissionSDK.AI.MissionContext context = null)
		{
			ThreadAssert.MainThreadRequired();

			this.botType = botType;
			this.playerID = playerToControlID;
			this.context = context;

			BotLog.Get(playerToControlID).Write(TethysGame.Time(),
				"BotPlayer construct: type=" + botType + " startingMode=" + (context?.startingMode.ToString() ?? "?"));

			baseManager = new BaseManager(this, playerToControlID);
			laborManager = new LaborManager(this, playerToControlID);
			combatManager = new CombatManager(this, playerToControlID);
		}

		public void Start()
		{
			isActive = true;
		}

		public void Stop()
		{
			isActive = false;
		}

		public void Update(StateSnapshot stateSnapshot)
		{
			ThreadAssert.MainThreadRequired();

			if (!isActive)
				return;

			// Update managers
			baseManager.Update(stateSnapshot);
			laborManager.Update(stateSnapshot);

			// CombatManager handles vehicle group assignment AND drives
			// MaintainArmyGoal's slot count, so we need it running. But its
			// PopulateCombatGroups skips any unit in this.activeAttackers so
			// our attack-wave commands aren't overridden by defensive group
			// DoMove orders. See AIv2/Managers/CombatManager.cs.
			combatManager.Update(stateSnapshot);

			// AIv2-specific: launch periodic attack waves once we have enough
			// military units. CombatManager handles defense via vehicle
			// groups; this layers offensive pressure on top. Wrapped in a
			// try/catch so a bad target reference (e.g. enemy CC destroyed
			// between snapshot capture and DoAttack P/Invoke) can't kill the
			// rest of the bot's Update.
			try
			{
				TryLaunchAttackWave(stateSnapshot);
				ReinforceActiveAttacks(stateSnapshot);   // overrides CombatManager's defensive re-task
				EvaluateWaveCheckFollowups(stateSnapshot);
			}
			catch (Exception attackEx)
			{
				BotLog.Get(playerID).Write(stateSnapshot.time,
					"AIv2: TryLaunchAttackWave EXCEPTION: " + attackEx.GetType().Name + ": " + attackEx.Message);
				BotLog.Get(playerID).Write(stateSnapshot.time, "  Stack: " + attackEx.StackTrace);
			}

			// Periodic status snapshot - overwrites logs/BotPlayer_<N>_Status.txt
			// every STATUS_WRITE_INTERVAL_TICKS so you can poll current resources,
			// building/unit counts, combat strength etc. while the mission runs.
			if (stateSnapshot.time % STATUS_WRITE_INTERVAL_TICKS == 0)
			{
				WriteStatus(stateSnapshot);
				WriteResearchStatus(stateSnapshot);
			}
		}

		// Writes a human-readable snapshot of this bot's player state to
		// logs/BotPlayer_<N>_Status.txt. Overwrite each call (FileMode.Create implicit
		// via File.WriteAllText). Swallows IO exceptions - status writing failure must
		// never crash the bot.
		// Once we've accumulated >= ATTACK_WAVE_THRESHOLD military units, every
		// ATTACK_WAVE_COOLDOWN_TICKS send ATTACK_WAVE_PERCENT of them attack-
		// moving toward the nearest enemy CC. The rest stays behind for base
		// defense.
		// Units already in moAttack state are skipped (still attacking from a
		// previous wave). Looks for the closest enemy CC; if no enemy CCs
		// remain on the map, no wave fires.
		private void TryLaunchAttackWave(StateSnapshot snap)
		{
			if (snap.time - m_LastAttackWaveTick < ATTACK_WAVE_COOLDOWN_TICKS)
				return;

			PlayerState me = snap.players[playerID];

			// Gather all our military units (combat vehicles with weapons).
			List<VehicleState> military = new List<VehicleState>();
			foreach (VehicleState v in me.units.lynx) military.Add(v);
			foreach (VehicleState v in me.units.panthers) military.Add(v);
			foreach (VehicleState v in me.units.tigers) military.Add(v);
			foreach (VehicleState v in me.units.spiders) military.Add(v);
			foreach (VehicleState v in me.units.scorpions) military.Add(v);

			if (military.Count < ATTACK_WAVE_THRESHOLD)
				return;

			// Find the closest enemy CC. Enemies = any non-allied player slot
			// that isn't us.
			StructureState targetCC = null;
			int bestDist = int.MaxValue;
			LOCATION myCenter = me.units.commandCenters.Count > 0
				? me.units.commandCenters[0].position
				: military[0].position;

			for (int pid = 0; pid < snap.players.Count; ++pid)
			{
				if (pid == playerID) continue;
				PlayerState other = snap.players[pid];
				if (other == null) continue;

				// Skip allies. PlayerEx.IsAlliedTo would be authoritative but
				// it's main-thread-only; for the snapshot path we only attack
				// if the player slot is genuinely active and not ourselves.
				// (If you want allies to be respected, add an alliedTo bitmap
				// to PlayerState during snapshot build.)

				foreach (StructureState cc in other.units.commandCenters)
				{
					int dx = cc.position.x - myCenter.x;
					int dy = cc.position.y - myCenter.y;
					int d = dx * dx + dy * dy;
					if (d < bestDist) { bestDist = d; targetCC = cc; }
				}
			}

			if (targetCC == null)
				return;  // no enemy CCs left

			// Take the first ATTACK_WAVE_PERCENT of military units. OP2 has no
			// clean "is attacking" action state we can filter on (weaponMove
			// covers attack moves but also weapon-targeting moves), so we
			// just reissue DoAttack each wave - units already heading to the
			// same spot have their target reaffirmed, no harm done.
			int waveSize = Math.Max(1, (military.Count * ATTACK_WAVE_PERCENT) / 100);
			if (waveSize == 0) return;

			LOCATION target = targetCC.position;

			// Resolve the live Unit handle of the target CC so we can use the
			// Unit-targeted DoAttack overload (DoAttack(Unit), not
			// DoAttack(x,y)). The tile-targeted version is just attack-MOVE -
			// units patrol near the tile but don't fire on the stationary
			// building. The Unit-targeted version makes them path into weapon
			// range and actually engage. Fall back to the tile version if the
			// CC's live unit can't be resolved (shouldn't happen).
			UnitEx liveTarget = GameState.GetUnit(targetCC.unitID);

			BotLog.Get(playerID).Write(snap.time,
				"AIv2: launching attack wave of " + waveSize + "/" + military.Count +
				" military units toward enemy CC #" + targetCC.unitID + " at (" + target.x + "," + target.y + ")" +
				(liveTarget == null ? " [FALLBACK: tile-move only, no Unit handle]" : ""));

			// Record wave for the follow-up diagnostic
			WaveCheck record = new WaveCheck { launchTick = snap.time, target = target };

			for (int i = 0; i < waveSize; ++i)
			{
				VehicleState v = military[i];
				UnitEx live = GameState.GetUnit(v.unitID);
				if (live == null) continue;

				record.unitIDs.Add(v.unitID);
				record.startPositions.Add(v.position);

				// Mark for persistent re-issue so CombatManager can't pull
				// them back to defensive groups. Lock to coordinate with the
				// AsyncPump worker that reads this set in PopulateCombatGroups.
				lock (m_ActiveAttackers) { m_ActiveAttackers.Add(v.unitID); }

				if (liveTarget != null)
					live.DoAttack(liveTarget);
				else
					live.DoAttack(target.x, target.y);
			}

			m_PendingWaveChecks.Add(record);
			m_LastAttackWaveTick = snap.time;
		}

		// Every Update (after CombatManager runs and potentially re-tasks
		// units back to defense), re-issue DoAttack to all units that are on
		// active attack-wave duty. This wins the command race: our DoAttack is
		// the last thing OP2 sees this tick. Dead units get pruned. If no
		// enemy CC is left, the attacker set is cleared.
		private void ReinforceActiveAttacks(StateSnapshot snap)
		{
			if (m_ActiveAttackers.Count == 0) return;

			StructureState targetCC = FindNearestEnemyCC(snap);
			if (targetCC == null)
			{
				lock (m_ActiveAttackers) { m_ActiveAttackers.Clear(); }
				return;
			}

			UnitEx liveTarget = GameState.GetUnit(targetCC.unitID);
			if (liveTarget == null) return;  // target temporarily unresolvable; try next tick

			// Snapshot the set under lock so we can iterate safely
			int[] ids;
			lock (m_ActiveAttackers)
			{
				ids = new int[m_ActiveAttackers.Count];
				m_ActiveAttackers.CopyTo(ids);
			}
			foreach (int unitID in ids)
			{
				UnitState u = snap.GetUnit(unitID);
				if (u == null)
				{
					lock (m_ActiveAttackers) { m_ActiveAttackers.Remove(unitID); }   // dead or vanished
					continue;
				}

				UnitEx live = GameState.GetUnit(unitID);
				if (live == null) continue;
				live.DoAttack(liveTarget);
			}
		}

		// Shared helper - find the closest enemy CC across all non-self
		// players. Returns null if none exists (we've won, or no enemy bases).
		private StructureState FindNearestEnemyCC(StateSnapshot snap)
		{
			PlayerState me = snap.players[playerID];
			LOCATION myCenter = me.units.commandCenters.Count > 0
				? me.units.commandCenters[0].position
				: new LOCATION(0, 0);

			StructureState best = null;
			int bestDist = int.MaxValue;
			for (int pid = 0; pid < snap.players.Count; ++pid)
			{
				if (pid == playerID) continue;
				PlayerState other = snap.players[pid];
				if (other == null) continue;
				foreach (StructureState cc in other.units.commandCenters)
				{
					int dx = cc.position.x - myCenter.x;
					int dy = cc.position.y - myCenter.y;
					int d = dx * dx + dy * dy;
					if (d < bestDist) { bestDist = d; best = cc; }
				}
			}
			return best;
		}

		// Follow up on previously-launched waves. WAVE_CHECK_DELAY_TICKS after
		// a wave fired, see what happened to its units. Logged as a single
		// summary line per wave so the attack-effect signal is easy to grep.
		private void EvaluateWaveCheckFollowups(StateSnapshot snap)
		{
			for (int i = m_PendingWaveChecks.Count - 1; i >= 0; --i)
			{
				WaveCheck rec = m_PendingWaveChecks[i];
				if (snap.time - rec.launchTick < WAVE_CHECK_DELAY_TICKS) continue;

				int total = rec.unitIDs.Count;
				int alive = 0;
				int moved = 0;          // Manhattan distance from start > 2 tiles
				long sumDistFromStart = 0;
				long sumDistFromTarget = 0;
				int countedForDistance = 0;

				// Tally action-state distribution. moAttack-family = unit is
				// engaging an enemy. moMove = navigating somewhere. moDone =
				// idle. weaponMove = aiming or attack-moving (often the state
				// for units actively attacking).
				int countAttack = 0;
				int countMove = 0;
				int countDone = 0;
				int countWeaponMove = 0;
				int countOther = 0;

				System.Text.StringBuilder positions = new System.Text.StringBuilder();

				for (int u = 0; u < total; ++u)
				{
					UnitState now = snap.GetUnit(rec.unitIDs[u]);
					if (now == null) continue;   // unit died (or vanished)
					alive++;

					LOCATION start = rec.startPositions[u];
					int distFromStart = System.Math.Abs(now.position.x - start.x) + System.Math.Abs(now.position.y - start.y);
					int distFromTarget = System.Math.Abs(now.position.x - rec.target.x) + System.Math.Abs(now.position.y - rec.target.y);
					sumDistFromStart  += distFromStart;
					sumDistFromTarget += distFromTarget;
					countedForDistance++;
					if (distFromStart > 2) moved++;

					// Action-state tally
					switch (now.curAction)
					{
						case ActionType.moMove:       countMove++; break;
						case ActionType.moDone:       countDone++; break;
						case ActionType.weaponMove:   countWeaponMove++; break;
						default:                      countOther++; break;
					}

					// Per-unit position + action sample (limit to first 5 to
					// keep log line readable)
					if (u < 5)
					{
						if (positions.Length > 0) positions.Append(", ");
						positions.Append("#").Append(now.unitID).Append("@(")
							.Append(now.position.x).Append(",").Append(now.position.y)
							.Append(",").Append(now.curAction).Append(")");
					}
				}

				int avgFromStart  = countedForDistance > 0 ? (int)(sumDistFromStart / countedForDistance)  : -1;
				int avgFromTarget = countedForDistance > 0 ? (int)(sumDistFromTarget / countedForDistance) : -1;

				BotLog.Get(playerID).Write(snap.time,
					"AIv2 wave-followup [launched t=" + rec.launchTick + "]: " +
					alive + "/" + total + " alive, " +
					moved + " moved>2tiles, " +
					"avgDistFromStart=" + avgFromStart + ", " +
					"avgDistFromTarget=" + avgFromTarget + ", " +
					"actions: weaponMove=" + countWeaponMove + " move=" + countMove + " done=" + countDone + " other=" + countOther);

				BotLog.Get(playerID).Write(snap.time,
					"  sample positions: " + positions.ToString());

				m_PendingWaveChecks.RemoveAt(i);
			}
		}

		private void WriteStatus(StateSnapshot stateSnapshot)
		{
			if (!DotNetMissionSDK.AI.BotLog.Enabled)
				return;

			try
			{
				if (playerID < 0 || playerID >= stateSnapshot.players.Count)
					return;

				PlayerState p = stateSnapshot.players[playerID];
				if (p == null)
					return;

				StringBuilder sb = new StringBuilder(4096);
				string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

				TimeSpan runtime = DateTime.Now - m_ConstructionWallTime;
				string runtimeStr = ((int)runtime.TotalMinutes).ToString() + "m " + runtime.Seconds.ToString("D2") + "s";
				sb.AppendLine("# BotPlayer " + playerID + " Status - " + botType + (p.isEden ? " (Eden)" : " (Plymouth)"));
				sb.AppendLine("# Updated " + stamp + " | tick=" + stateSnapshot.time + " (Mark " + (stateSnapshot.time / 100) + ")");
				sb.AppendLine("# Current Runtime: " + runtimeStr);
				sb.AppendLine();

				sb.AppendLine("RESOURCES");
				sb.AppendLine("  Common Ore:    " + p.ore.ToString("N0") + "  (max " + p.maxCommonOre.ToString("N0") + ")");
				sb.AppendLine("  Rare Ore:      " + p.rareOre.ToString("N0") + "  (max " + p.maxRareOre.ToString("N0") + ")");
				sb.AppendLine("  Food Stored:   " + p.foodStored.ToString("N0"));
				sb.AppendLine("    Production:  " + p.totalFoodProduction);
				sb.AppendLine("    Consumption: " + p.totalFoodConsumption);
				sb.AppendLine("    Net:         " + (p.netFoodProduction >= 0 ? "+" : "") + p.netFoodProduction
					+ (p.foodLacking > 0 ? "  (lacking " + p.foodLacking + ")" : ""));
				sb.AppendLine("    Status:      " + p.foodSupply);
				sb.AppendLine();

				// Population values come from HFL's GetKids/GetWorkers/GetScientists
				// (OP2Player struct offsets 148/152/156). Confirmed real when the
				// mission has at least one IsHuman=true seat. When all players are
				// AI, OP2 overrides .opm values with engine defaults (256/4096/4096
				// = 8448) - see ISSUES.md.
				int totalColonists = p.kids + p.workers + p.scientists;
				sb.AppendLine("POPULATION (" + totalColonists + " total)");
				sb.AppendLine("  Kids:        " + p.kids);
				sb.AppendLine("  Workers:     " + p.workers);
				sb.AppendLine("  Scientists:  " + p.scientists);
				sb.AppendLine("  Morale:      " + p.moraleLevel);
				sb.AppendLine();
				sb.AppendLine("WORKFORCE ASSIGNMENT");
				sb.AppendLine("  Workers assigned to buildings:    " + p.numWorkersRequired);
				sb.AppendLine("  Scientists assigned to buildings: " + p.numScientistsRequired);
				sb.AppendLine("    of which researching:           " + p.numScientistsAssignedToResearch);
				sb.AppendLine("    of which doing worker jobs:     " + p.numScientistsAsWorkers);
				sb.AppendLine();

				sb.AppendLine("POWER");
				sb.AppendLine("  Generated:   " + p.amountPowerGenerated);
				sb.AppendLine("  Consumed:    " + p.amountPowerConsumed);
				sb.AppendLine("  Available:   " + p.amountPowerAvailable);
				sb.AppendLine("  Inactive capacity:     " + p.inactivePowerCapacity);
				sb.AppendLine("  Unpowered structures:  " + p.numUnpoweredStructures);
				sb.AppendLine();

				sb.AppendLine("BUILDINGS (" + p.numBuildings + " total - " + p.numActiveBuildings + " active, " + p.numIdleBuildings + " idle)");
				PlayerUnitState u = p.units;
				AppendCount(sb, "Command Centers",   u.commandCenters.Count);
				AppendCount(sb, "Structure Factories", u.structureFactories.Count);
				AppendCount(sb, "Vehicle Factories",  u.vehicleFactories.Count);
				AppendCount(sb, "Arachnid Factories", u.arachnidFactories.Count);
				AppendCount(sb, "Consumer Factories", u.consumerFactories.Count);
				AppendCount(sb, "Tokamaks",           u.tokamaks.Count);
				AppendCount(sb, "Solar Power Arrays", u.solarPowerArrays.Count);
				AppendCount(sb, "MHD Generators",     u.mhdGenerators.Count);
				AppendCount(sb, "Geothermal Plants",  u.geothermalPlants.Count);
				AppendCount(sb, "Standard Labs",      u.standardLabs.Count);
				AppendCount(sb, "Advanced Labs",      u.advancedLabs.Count);
				AppendCount(sb, "Basic Labs",         u.basicLabs.Count);
				AppendCount(sb, "Universities",       u.universities.Count);
				AppendCount(sb, "Common Ore Mines",   u.commonOreMines.Count);
				AppendCount(sb, "Common Smelters",    u.commonOreSmelters.Count);
				AppendCount(sb, "Common Storages",    u.commonStorages.Count);
				AppendCount(sb, "Rare Ore Mines",     u.rareOreMines.Count);
				AppendCount(sb, "Rare Smelters",      u.rareOreSmelters.Count);
				AppendCount(sb, "Rare Storages",      u.rareStorages.Count);
				AppendCount(sb, "Magma Wells",        u.magmaWells.Count);
				AppendCount(sb, "Agridomes",          u.agridomes.Count);
				AppendCount(sb, "DIRTs",              u.dirts.Count);
				AppendCount(sb, "Residences",         u.residences.Count);
				AppendCount(sb, "Reinforced Resid.",  u.reinforcedResidences.Count);
				AppendCount(sb, "Advanced Resid.",    u.advancedResidences.Count);
				AppendCount(sb, "Medical Centers",    u.medicalCenters.Count);
				AppendCount(sb, "Nurseries",          u.nurseries.Count);
				AppendCount(sb, "Recreation",         u.recreationFacilities.Count);
				AppendCount(sb, "Forums",             u.forums.Count);
				AppendCount(sb, "GORFs",              u.gorfs.Count);
				AppendCount(sb, "Trade Centers",      u.tradeCenters.Count);
				AppendCount(sb, "Robot Commands",     u.robotCommands.Count);
				AppendCount(sb, "Observatories",      u.observatories.Count);
				AppendCount(sb, "Meteor Defenses",    u.meteorDefenses.Count);
				AppendCount(sb, "Guard Posts",        u.guardPosts.Count);
				AppendCount(sb, "Light Towers",       u.lightTowers.Count);
				AppendCount(sb, "Spaceports",         u.spaceports.Count);
				AppendCount(sb, "Garages",            u.garages.Count);
				sb.AppendLine();

				int totalVehicles = u.convecs.Count + u.cargoTrucks.Count + u.roboSurveyors.Count + u.roboMiners.Count
					+ u.geoCons.Count + u.scouts.Count + u.roboDozers.Count + u.evacTransports.Count
					+ u.repairVehicles.Count + u.earthWorkers.Count
					+ u.lynx.Count + u.panthers.Count + u.tigers.Count + u.spiders.Count + u.scorpions.Count;

				sb.AppendLine("VEHICLES (" + totalVehicles + " total)");
				sb.AppendLine("  Civilian:");
				AppendCount(sb, "  ConVecs",          u.convecs.Count);
				AppendCount(sb, "  Cargo Trucks",     u.cargoTrucks.Count);
				AppendCount(sb, "  Robo Surveyors",   u.roboSurveyors.Count);
				AppendCount(sb, "  Robo Miners",      u.roboMiners.Count);
				AppendCount(sb, "  GeoCons",          u.geoCons.Count);
				AppendCount(sb, "  Scouts",           u.scouts.Count);
				AppendCount(sb, "  Robo Dozers",      u.roboDozers.Count);
				AppendCount(sb, "  Evac Transports",  u.evacTransports.Count);
				AppendCount(sb, "  Repair Vehicles",  u.repairVehicles.Count);
				AppendCount(sb, "  Earth Workers",    u.earthWorkers.Count);
				sb.AppendLine("  Military:");
				AppendCount(sb, "  Lynx",             u.lynx.Count);
				AppendCount(sb, "  Panthers",         u.panthers.Count);
				AppendCount(sb, "  Tigers",           u.tigers.Count);
				AppendCount(sb, "  Spiders",          u.spiders.Count);
				AppendCount(sb, "  Scorpions",        u.scorpions.Count);
				sb.AppendLine();

				sb.AppendLine("COMBAT");
				sb.AppendLine("  Total Offensive Strength: " + p.totalOffensiveStrength);
				sb.AppendLine();

				sb.AppendLine("STARSHIP MODULES (in spaceport storage)");
				sb.AppendLine("  EDWARD Satellites:  " + u.EDWARDSatelliteCount);
				sb.AppendLine("  Solar Satellites:   " + u.solarSatelliteCount);
				sb.AppendLine("  Ion Drive Modules:  " + u.ionDriveModuleCount);
				sb.AppendLine("  Fusion Drive Mods:  " + u.fusionDriveModuleCount);
				sb.AppendLine("  Command Modules:    " + u.commandModuleCount);
				sb.AppendLine("  Fueling Systems:    " + u.fuelingSystemsCount);

				string path = Path.Combine("logs", "BotPlayer_" + playerID + "_Status.txt");
				File.WriteAllText(path, sb.ToString());
			}
			catch
			{
				// Status writing failure must never crash the bot.
			}
		}

		// Writes completed research + lab availability to logs/BotPlayer_<N>_Research.txt.
		// Overwrites each call. Swallows IO exceptions.
		private void WriteResearchStatus(StateSnapshot stateSnapshot)
		{
			if (!DotNetMissionSDK.AI.BotLog.Enabled)
				return;

			try
			{
				if (playerID < 0 || playerID >= stateSnapshot.players.Count)
					return;

				PlayerState p = stateSnapshot.players[playerID];
				if (p == null)
					return;

				PlayerUnitState u = p.units;
				Player op2Player = TethysGame.GetPlayer(playerID);

				StringBuilder sb = new StringBuilder(8192);
				string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

				sb.AppendLine("# BotPlayer " + playerID + " Research - " + botType + (p.isEden ? " (Eden)" : " (Plymouth)"));
				sb.AppendLine("# Updated " + stamp + " | tick=" + stateSnapshot.time);
				sb.AppendLine();

				sb.AppendLine("LABS");
				sb.AppendLine("  Basic Labs:     " + u.basicLabs.Count);
				sb.AppendLine("  Standard Labs:  " + u.standardLabs.Count);
				sb.AppendLine("  Advanced Labs:  " + u.advancedLabs.Count);
				sb.AppendLine("  Scientists researching: " + p.numScientistsAssignedToResearch);
				sb.AppendLine();

				// Bucket completed techs by category.
				Dictionary<TechCategory, List<string>> byCategory = new Dictionary<TechCategory, List<string>>();
				int totalCompleted = 0;
				int techCount = Research.GetTechCount();

				for (int i = 0; i < techCount; ++i)
				{
					TechInfo info = Research.GetTechInfo(i);
					if (!info.IsValid())
						continue;

					int techID = info.GetTechID();
					if (!op2Player.HasTechnology(techID))
						continue;

					TechCategory cat = info.GetCategory();
					string name = info.GetTechName() ?? ("Tech " + techID);
					int level = info.GetTechLevel();
					LabType lab = info.GetLab();

					string labShort = lab == LabType.ltBasic ? "B" : lab == LabType.ltStandard ? "S" : lab == LabType.ltAdvanced ? "A" : "-";
					string line = "  [L" + level + "/" + labShort + "] " + name + "  (id " + techID + ")";

					List<string> bucket;
					if (!byCategory.TryGetValue(cat, out bucket))
					{
						bucket = new List<string>();
						byCategory[cat] = bucket;
					}
					bucket.Add(line);
					totalCompleted++;
				}

				sb.AppendLine("COMPLETED RESEARCH (" + totalCompleted + " techs)");
				sb.AppendLine();

				// Emit in TechCategory enum order so the file is stable.
				foreach (TechCategory cat in Enum.GetValues(typeof(TechCategory)))
				{
					List<string> bucket;
					if (!byCategory.TryGetValue(cat, out bucket) || bucket.Count == 0)
						continue;

					sb.AppendLine(cat.ToString() + " (" + bucket.Count + ")");
					foreach (string line in bucket)
						sb.AppendLine(line);
					sb.AppendLine();
				}

				string path = Path.Combine("logs", "BotPlayer_" + playerID + "_Research.txt");
				File.WriteAllText(path, sb.ToString());
			}
			catch
			{
				// Status writing failure must never crash the bot.
			}
		}

		private static void AppendCount(StringBuilder sb, string label, int count)
		{
			if (count > 0)
				sb.AppendLine("  " + label.PadRight(20) + count);
		}
	}
}
