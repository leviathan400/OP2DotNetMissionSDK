using DotNetMissionSDK.State.Snapshot;
using DotNetMissionSDK.State.Snapshot.Units;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DotNetMissionSDK.AI
{
	// Per-bot telemetry sink. Three log files per active bot, written once per
	// Mark (100 ticks). Sits at the SDK level so every IBotPlayer
	// implementation gets identical telemetry without each having to wire it.
	//
	//   BotPlayer_<N>_Timeline.csv     append, one row per Mark - numeric trajectory for graphs
	//   BotPlayer_<N>_Buildings.txt    overwrite per Mark      - current building list with positions
	//   BotPlayer_<N>_BuildEvents.txt  append, only on diffs   - build/destroy events
	//
	// Files are truncated on the first Write call of each mission (see
	// ResetForNewMission). They live in OPU/logs/ alongside the existing
	// BotPlayer_<N>.txt / _Status.txt / _Research.txt files.
	public static class BotTelemetry
	{
		// Per-player first-write flags. False ⇒ next Write truncates the file
		// and prepends a header. Reset by ResetForNewMission so a second
		// mission in the same OP2 process starts fresh.
		private static readonly object s_Lock = new object();
		private static readonly HashSet<int> s_TimelineHeaderWritten = new HashSet<int>();
		private static readonly HashSet<int> s_BuildEventsHeaderWritten = new HashSet<int>();

		// Previous-Mark building unitID set per player, for BuildEvents diffing.
		// New IDs ⇒ BUILT lines. Missing IDs ⇒ DESTROYED lines.
		private static readonly Dictionary<int, HashSet<int>> s_PrevBuildingIDs = new Dictionary<int, HashSet<int>>();

		public static void ResetForNewMission()
		{
			lock (s_Lock)
			{
				s_TimelineHeaderWritten.Clear();
				s_BuildEventsHeaderWritten.Clear();
				s_PrevBuildingIDs.Clear();
			}
		}

		private static string LogPath(string suffix, int playerID)
		{
			return Path.Combine("logs", "BotPlayer_" + playerID + "_" + suffix);
		}

		public static void WriteAll(int playerID, StateSnapshot snap)
		{
			try
			{
				if (playerID < 0 || playerID >= snap.players.Count)
					return;
				PlayerState p = snap.players[playerID];
				if (p == null)
					return;

				WriteTimeline(playerID, snap, p);
				WriteBuildings(playerID, snap, p);
				WriteBuildEvents(playerID, snap, p);
			}
			catch
			{
				// Telemetry must never crash the bot.
			}
		}

		// One CSV row per Mark - append. Header written on first call per mission.
		private static void WriteTimeline(int playerID, StateSnapshot snap, PlayerState p)
		{
			string path = LogPath("Timeline.csv", playerID);

			bool needHeader;
			lock (s_Lock)
			{
				needHeader = !s_TimelineHeaderWritten.Contains(playerID);
				if (needHeader)
					s_TimelineHeaderWritten.Add(playerID);
			}

			if (needHeader)
			{
				File.WriteAllText(path,
					"tick,mark,wallTime,commonOre,maxCommonOre,rareOre,maxRareOre,foodStored," +
					"powerGen,powerUsed,powerAvailable," +
					"kids,workers,scientists,totalColonists," +
					"workersAssigned,scientistsAssigned,scientistsResearching,scientistsAsWorkers," +
					"netFoodProduction,foodLacking," +
					"numBuildings,numActiveBuildings,numIdleBuildings,numUnpoweredStructures," +
					"numVehicles,offensiveStrength,morale\n");
			}

			int numVehicles = p.units.GetVehicles().Count();
			int totalColonists = p.kids + p.workers + p.scientists;

			string row = string.Join(",",
				snap.time.ToString(),
				(snap.time / 100).ToString(),
				DateTime.Now.ToString("HH:mm:ss"),
				p.ore.ToString(),
				p.maxCommonOre.ToString(),
				p.rareOre.ToString(),
				p.maxRareOre.ToString(),
				p.foodStored.ToString(),
				p.amountPowerGenerated.ToString(),
				p.amountPowerConsumed.ToString(),
				p.amountPowerAvailable.ToString(),
				p.kids.ToString(),
				p.workers.ToString(),
				p.scientists.ToString(),
				totalColonists.ToString(),
				p.numWorkersRequired.ToString(),
				p.numScientistsRequired.ToString(),
				p.numScientistsAssignedToResearch.ToString(),
				p.numScientistsAsWorkers.ToString(),
				p.netFoodProduction.ToString(),
				p.foodLacking.ToString(),
				p.numBuildings.ToString(),
				p.numActiveBuildings.ToString(),
				p.numIdleBuildings.ToString(),
				p.numUnpoweredStructures.ToString(),
				numVehicles.ToString(),
				p.totalOffensiveStrength.ToString(),
				p.moraleLevel.ToString());

			File.AppendAllText(path, row + "\n");
		}

		// Current building list - overwritten every Mark.
		private static void WriteBuildings(int playerID, StateSnapshot snap, PlayerState p)
		{
			string path = LogPath("Buildings.txt", playerID);

			StringBuilder sb = new StringBuilder(8192);
			string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			sb.AppendLine("# Buildings for player " + playerID + " - tick=" + snap.time + " mark=" + (snap.time / 100) + " (" + stamp + ")");
			sb.AppendLine("# Format: unitID,type,x,y,hp,maxHp,hasPower,hasWorkers,isCritical,isDisabled");

			foreach (UnitState u in p.units.GetStructures())
			{
				StructureState s = u as StructureState;
				if (s == null) continue;
				int maxHp = s.structureInfo.hitPoints;
				int hp = maxHp - s.damage;

				sb.Append(s.unitID).Append(",")
				  .Append(s.unitType).Append(",")
				  .Append(s.position.x).Append(",")
				  .Append(s.position.y).Append(",")
				  .Append(hp).Append(",")
				  .Append(maxHp).Append(",")
				  .Append(s.hasPower ? 1 : 0).Append(",")
				  .Append(s.hasWorkers ? 1 : 0).Append(",")
				  .Append(s.isCritical ? 1 : 0).Append(",")
				  .Append(s.isDisabled ? 1 : 0)
				  .AppendLine();
			}

			File.WriteAllText(path, sb.ToString());
		}

		// Diffed event log - append. Compares current building IDs vs previous Mark's IDs.
		private static void WriteBuildEvents(int playerID, StateSnapshot snap, PlayerState p)
		{
			string path = LogPath("BuildEvents.txt", playerID);

			bool needHeader;
			lock (s_Lock)
			{
				needHeader = !s_BuildEventsHeaderWritten.Contains(playerID);
				if (needHeader)
					s_BuildEventsHeaderWritten.Add(playerID);
			}

			if (needHeader)
			{
				File.WriteAllText(path,
					"# Building lifecycle events for player " + playerID + "\n" +
					"# Lines: [tick=N mark=M] EVENT type unitID@(x,y)\n" +
					"# EVENT is BUILT (first seen) or DESTROYED (vanished from snapshot)\n");
			}

			HashSet<int> currentIDs = new HashSet<int>();
			Dictionary<int, StructureState> currentByID = new Dictionary<int, StructureState>();
			foreach (UnitState u in p.units.GetStructures())
			{
				StructureState s = u as StructureState;
				if (s == null) continue;
				currentIDs.Add(s.unitID);
				currentByID[s.unitID] = s;
			}

			HashSet<int> prevIDs;
			lock (s_Lock)
			{
				if (!s_PrevBuildingIDs.TryGetValue(playerID, out prevIDs))
					prevIDs = new HashSet<int>();
				s_PrevBuildingIDs[playerID] = currentIDs;
			}

			// Append BUILT for IDs that appeared
			StringBuilder sb = new StringBuilder();
			int mark = snap.time / 100;
			foreach (int id in currentIDs)
			{
				if (prevIDs.Contains(id)) continue;
				StructureState s = currentByID[id];
				sb.Append("[tick=").Append(snap.time)
				  .Append(" mark=").Append(mark).Append("] BUILT ")
				  .Append(s.unitType).Append(" #").Append(id)
				  .Append("@(").Append(s.position.x).Append(",").Append(s.position.y).Append(")")
				  .AppendLine();
			}
			// Append DESTROYED for IDs that vanished
			foreach (int id in prevIDs)
			{
				if (currentIDs.Contains(id)) continue;
				sb.Append("[tick=").Append(snap.time)
				  .Append(" mark=").Append(mark).Append("] DESTROYED #").Append(id)
				  .AppendLine();
			}

			if (sb.Length > 0)
				File.AppendAllText(path, sb.ToString());
		}
	}
}
