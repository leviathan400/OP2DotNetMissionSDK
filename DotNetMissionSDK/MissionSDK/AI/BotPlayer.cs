using DotNetMissionSDK.AI.Managers;
using DotNetMissionSDK.Async;
using DotNetMissionSDK.State.Snapshot;
using System;
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

	public class BotPlayer
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

		// How often to write the status snapshot (in game ticks).
		// At ~10 ticks/sec, 100 ticks ≈ 10 seconds of game time.
		private const int STATUS_WRITE_INTERVAL_TICKS = 100;


		public BotPlayer(BotType botType, int playerToControlID)
		{
			ThreadAssert.MainThreadRequired();

			this.botType = botType;
			this.playerID = playerToControlID;

			BotLog.Get(playerToControlID).Write(TethysGame.Time(), "BotPlayer construct: type=" + botType);

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

			// Periodic status snapshot — overwrites logs/BotPlayer_<N>_Status.txt
			// every STATUS_WRITE_INTERVAL_TICKS so you can poll current resources,
			// building/unit counts, combat strength etc. while the mission runs.
			if (stateSnapshot.time % STATUS_WRITE_INTERVAL_TICKS == 0)
				WriteStatus(stateSnapshot);
		}

		// Writes a human-readable snapshot of this bot's player state to
		// logs/BotPlayer_<N>_Status.txt. Overwrite each call (FileMode.Create implicit
		// via File.WriteAllText). Swallows IO exceptions — status writing failure must
		// never crash the bot.
		private void WriteStatus(StateSnapshot stateSnapshot)
		{
			try
			{
				if (playerID < 0 || playerID >= stateSnapshot.players.Count)
					return;

				PlayerState p = stateSnapshot.players[playerID];
				if (p == null)
					return;

				StringBuilder sb = new StringBuilder(4096);
				string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

				sb.AppendLine("# BotPlayer " + playerID + " Status — " + botType + (p.isEden ? " (Eden)" : " (Plymouth)"));
				sb.AppendLine("# Updated " + stamp + " | tick=" + stateSnapshot.time);
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

				sb.AppendLine("POPULATION (total " + p.totalPopulation + ")");
				sb.AppendLine("  Kids:        " + p.kids);
				sb.AppendLine("  Workers:     " + p.workers + "  (required " + p.numWorkersRequired + ", available " + p.numAvailableWorkers + ")");
				sb.AppendLine("  Scientists:  " + p.scientists + "  (required " + p.numScientistsRequired + ", available " + p.numAvailableScientists + ")");
				sb.AppendLine("    as workers:  " + p.numScientistsAsWorkers);
				sb.AppendLine("    researching: " + p.numScientistsAssignedToResearch);
				sb.AppendLine("  Morale:      " + p.moraleLevel);
				sb.AppendLine();

				sb.AppendLine("POWER");
				sb.AppendLine("  Generated:   " + p.amountPowerGenerated);
				sb.AppendLine("  Consumed:    " + p.amountPowerConsumed);
				sb.AppendLine("  Available:   " + p.amountPowerAvailable);
				sb.AppendLine("  Inactive capacity:     " + p.inactivePowerCapacity);
				sb.AppendLine("  Unpowered structures:  " + p.numUnpoweredStructures);
				sb.AppendLine();

				sb.AppendLine("BUILDINGS (" + p.numBuildings + " total — " + p.numActiveBuildings + " active, " + p.numIdleBuildings + " idle)");
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

		private static void AppendCount(StringBuilder sb, string label, int count)
		{
			if (count > 0)
				sb.AppendLine("  " + label.PadRight(20) + count);
		}
	}
}
