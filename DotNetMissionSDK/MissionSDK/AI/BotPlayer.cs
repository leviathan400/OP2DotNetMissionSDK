using DotNetMissionSDK.AI.Managers;
using DotNetMissionSDK.Async;
using DotNetMissionSDK.HFL;
using DotNetMissionSDK.State.Snapshot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DotNetMissionSDK.AI
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
		public MissionContext context			{ get; private set; }		// Mission-wide context (StartingMode, etc.)

		// Wall-clock time when this bot was constructed. Surfaced in
		// BotPlayer_<N>_Status.txt as "Current Runtime" so you can correlate
		// the bot's state with how long the mission has been running without
		// flipping back to MissionSDK.log timestamps.
		private readonly DateTime m_ConstructionWallTime = DateTime.Now;

		// How often to write the status snapshot (in game ticks).
		// At ~10 ticks/sec, 100 ticks ≈ 10 seconds of game time.
		private const int STATUS_WRITE_INTERVAL_TICKS = 100;


		public BotPlayer(BotType botType, int playerToControlID, MissionContext context = null)
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
			combatManager.Update(stateSnapshot);

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
		// logs/BotPlayer_<N>_Status.txt. Skipped when BotLog.Enabled is false so the
		// per-bot log family stays a single toggle. Overwrite each call (FileMode.Create implicit
		// via File.WriteAllText). Swallows IO exceptions - status writing failure must
		// never crash the bot.
		private void WriteStatus(StateSnapshot stateSnapshot)
		{
			if (!BotLog.Enabled)
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
			if (!BotLog.Enabled)
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
