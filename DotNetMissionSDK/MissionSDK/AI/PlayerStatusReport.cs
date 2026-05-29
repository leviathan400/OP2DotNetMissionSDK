using DotNetMissionSDK.State.Snapshot;
using System;
using System.IO;
using System.Text;

namespace DotNetMissionSDK.AI
{
	/// <summary>
	/// Shared formatter for the per-player "_Status" report. Used by:
	///   - BotPlayer (AI seats) -> logs/BotPlayer_<N>_Status.txt
	///   - CustomLogic          -> logs/Player_<N>_Status.txt (human seats)
	///
	/// The single formatter keeps human + bot status files diffable.
	/// </summary>
	public static class PlayerStatusReport
	{
		/// <summary>
		/// Writes a full status snapshot (resources, population, power, buildings,
		/// vehicles, combat, starship modules) for one player to <paramref name="outputPath"/>.
		/// Overwrites the file each call. Swallows all IO exceptions - status
		/// writing must never crash the caller.
		/// </summary>
		public static void Write(int playerID, string headerLabel, StateSnapshot snap, TimeSpan runtime, string outputPath)
		{
			try
			{
				if (playerID < 0 || playerID >= snap.players.Count)
					return;

				PlayerState p = snap.players[playerID];
				if (p?.units == null)
					return;

				StringBuilder sb = new StringBuilder(4096);
				string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
				string runtimeStr = ((int)runtime.TotalMinutes).ToString() + "m " + runtime.Seconds.ToString("D2") + "s";

				sb.AppendLine("# Player " + playerID + " Status - " + headerLabel + (p.isEden ? " (Eden)" : " (Plymouth)"));
				sb.AppendLine("# Updated " + stamp + " | tick=" + snap.time + " (Mark " + (snap.time / 100) + ")");
				sb.AppendLine("# Current Runtime: " + runtimeStr);
				sb.AppendLine("# isHuman: " + p.isHuman);
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

				PlayerUnitState u = p.units;
				sb.AppendLine("BUILDINGS (" + p.numBuildings + " total - " + p.numActiveBuildings + " active, " + p.numIdleBuildings + " idle)");
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

				Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
				File.WriteAllText(outputPath, sb.ToString());
			}
			catch
			{
				// Status writing failure must never crash the caller.
			}
		}

		private static void AppendCount(StringBuilder sb, string label, int count)
		{
			if (count > 0)
				sb.AppendLine("  " + label.PadRight(20) + count);
		}
	}
}
