using DotNetMissionReader;
using DotNetMissionSDK.AI;
using DotNetMissionSDK.State.Snapshot;
using DotNetMissionSDK.State.Snapshot.Units;
using DotNetMissionSDK.Triggers;
using System;
using System.IO;

namespace DotNetMissionSDK
{
	/// <summary>
	/// This is the primary class for performing custom mission logic.
	/// Use SaveData to store data that must persist when the mission is saved and loaded.
	/// Other SDK classes should not need to be modified.
	/// </summary>
	public class CustomLogic : MissionLogic
	{
		/// <summary>
		/// If true, main will check for and load the JSON data file.
		/// </summary>
		public const bool useJson = true;

		/// <summary>
		/// If true, AI-controlled vehicles place a DNA-shaped marker at their destination tile
		/// whenever they receive a path-based move command (see Vehicle.SetDebugMarker).
		/// Markers update every time the AI re-plans a vehicle's destination - useful for
		/// visualizing what the AI is trying to do. Markers only appear for the local player.
		///
		/// Set to false for shipped missions to give players a clean view.
		/// Set to true during AI development / mission debugging.
		/// </summary>
		public const bool showDebugVehicleMarkers = true;

		/// <summary>
		/// Called when the mission is first loaded, regardless of whether it is a new game or saved game.
		/// </summary>
		/// <param name="root">The filled JSON data root.</param>
		/// <param name="saveData">The save data class.</param>
		/// <param name="triggerManager">The trigger manager used for the mission.</param>
		/// <summary>
		/// Master switch for the per-bot BotPlayer_&lt;N&gt;*.txt log family
		/// (BotPlayer_N.txt, _Status.txt, _Research.txt, _Buildings.txt,
		/// _BuildEvents.txt, _Timeline.csv). MissionSDK.log and DotNetLog.txt
		/// are NOT affected by this flag - they're always written.
		///
		/// Set to false for shipped missions to keep the install dir clean.
		/// Set to true during AI development to get the full per-bot trace.
		/// </summary>
		// Default: false for shipped missions (clean install dir, no per-bot diagnostic
		// log spam). Flip to true locally during AI development to get the full trace.
		public const bool enableBotPlayerLogs = true;

		// Captured at construction so StartMission / OnTriggerExecuted can
		// branch on LevelDetails.Description without re-parsing the .opm.
		// MissionLogic's m_Root is private; this is our own copy.
		private readonly MissionRoot m_Root;

		public CustomLogic(MissionRoot root, SaveData saveData, TriggerManager triggerManager) : base(root, saveData, triggerManager)
		{
			m_Root = root;

			// Apply the per-bot log toggle BEFORE any bot is constructed so the
			// first Get(...) call lands on the correct Enabled value. The flag
			// is static on BotLog because telemetry sites (BotTelemetry, the
			// File.WriteAllText status/research writers) need to read it too.
			BotLog.Enabled = enableBotPlayerLogs;

			// Players, units and the map have not been fully initialized at this point.
			// In most cases, you want to add code to StartMission() instead.

			// *** Add custom pre-init code here ***
		}

		/// <summary>
		/// Called when a new mission should start. Performs initial setup.
		/// </summary>
		/// <returns>True on success.</returns>
		public override bool InitializeNewMission()
		{
			if (!base.InitializeNewMission())
				return false;

			// Dev test trigger - fires "Check me out!" once the local player
			// owns 4+ vehicles. Validates the trigger pipeline end-to-end during
			// SDK development. Mission-specific victory/defeat triggers live in
			// each mission's .opm Triggers array (see cPvAIPieChart.opm for an
			// example), not in C# - the .opm pipeline registers the trigger
			// metadata the SDK needs for proper dispatch.
			AddTrigger(TriggerStub.CreateVehicleCountTrigger(999, true, true, TethysGame.LocalPlayer(), 4, CompareMode.GreaterEqual));

			return true;
		}

		/// <summary>
		/// Called when a mission is loaded from a saved game. Performs reinitialization of data lost during quit.
		/// </summary>
		public override void LoadMission()
		{
			base.LoadMission();

			// *** Add custom "Load Mission" code here ***
		}

		/// <summary>
		/// Called when the mission has finished initializing, regardless of whether it is a new game or saved game.
		/// </summary>
		// SDK version surfaced in-game on mission start and to the SDK log.
		// Bump on every release. Lineage: TechCor's last upstream tag was 2.0
		// (2019-08-18, commit cff3f7e on TechCor8/OP2DotNetMissionSDK).
		// The community fork (this repo) picks up at 3.0.
		public const string SDK_VERSION = "3.0";

		/// <summary>
		/// Declares what the world looks like on tick 0 for this mission. The
		/// SDK passes the value to every IBotPlayer constructor via
		/// MissionContext so bots can branch on game style.
		///
		/// Override and return StartingMode.LastOneStanding for missions where
		/// every player starts with a fully-built base (no starter kits).
		/// Default is LandRush (kit-loaded convecs, build a base from scratch).
		/// </summary>
		protected override DotNetMissionSDK.AI.StartingMode GetStartingMode()
		{
			return DotNetMissionSDK.AI.StartingMode.LandRush;
		}

		protected override void StartMission()
		{
			base.StartMission();
			m_MissionStartWallTime = DateTime.Now;

			// Announce SDK version in the Communications panel so anyone watching
			// a game can tell at a glance which build is running.
			TethysGame.AddMessage(0, 0, "DotNetMissionSDK " + SDK_VERSION, -1, 0);
			Console.WriteLine("DotNetMissionSDK " + SDK_VERSION);

			// Force morale to Good and freeze it for the human player in PvAI
			// missions. Without this the human colony tends to death-spiral
			// before the AI even builds its CC: low power -> idle Agridome ->
			// food drops -> morale Terrible -> workers die -> more idle
			// buildings -> repeat. Forced morale lets the player focus on the
			// strategic test (build, attack, defend) without micromanaging the
			// morale feedback loop. AI-vs-AI demos don't need this because
			// IsHuman=false seats get OP2's god-mode population (fixed
			// 256/4096/4096, no food sim).
			string desc = m_Root?.LevelDetails?.Description ?? string.Empty;
			if (desc.IndexOf("PvAI", System.StringComparison.OrdinalIgnoreCase) >= 0)
			{
				TethysGame.ForceMoraleGood(-1);   // -1 = all players
				TethysGame.FreeMoraleLevel(-1);   // lock it at Good
				Console.WriteLine("PvAI: morale forced Good for all players");
			}

			// *** Add custom start code here ***
		}

		/// <summary>
		/// Called when a trigger has been executed.
		/// </summary>
		/// <param name="trigger">The trigger that was executed.</param>
		protected override void OnTriggerExecuted(TriggerStub trigger)
		{
			switch (trigger.id)
			{
				case 999:
					TethysGame.AddMessage(0, 0, "Check me out!", TethysGame.LocalPlayer(), 0);
					Console.WriteLine("Check me out!");
					break;

				case 3:
					// PvAI arming: Set trigger fires once both players have
					// built a CC. From this tick on, Update() polls each Mark
					// for the CC-destroyed transitions and fires Victory or
					// Failure dynamically (see m_pvaiArmed below).
					m_pvaiArmed = true;
					Console.WriteLine("PvAI: all players have built CCs - victory/defeat polling armed.");

					// In-game announcement so the player sees the objective
					// arm in the Communications panel during play.
					TethysGame.AddMessage(0, 0, "Battle is now joined. Destroy the Plymouth Command Center.", -1, 0);
					break;

				default:
					base.OnTriggerExecuted(trigger);
					break;
			}
		}

		/// <summary>
		/// Called every game cycle.
		/// </summary>
		/// <param name="stateSnapshot">The current immutable state of the game.</param>
		// PvAI mission state. Tracked from the case-3 Set trigger handler
		// when both players have built CCs. The mission is now MultiLast-
		// OneStanding so OP2's engine handles the actual win/loss surface;
		// we keep the polling lines as diagnostic telemetry of CC counts
		// each Mark and as the trigger point for the "Battle is now joined"
		// comm panel message.
		private bool m_pvaiArmed;

		public override void Update(StateSnapshot stateSnapshot)
		{
			base.Update(stateSnapshot);

			// Poll once per Mark.
			if (stateSnapshot.time <= 0 || stateSnapshot.time % 100 != 0)
				return;
			if (stateSnapshot.players.Count < 2)
				return;

			int p0 = stateSnapshot.players[0]?.units?.commandCenters?.Count ?? -1;
			int p1 = stateSnapshot.players[1]?.units?.commandCenters?.Count ?? -1;

			Console.WriteLine("PvAI poll  tick=" + stateSnapshot.time
				+ "  Mark=" + (stateSnapshot.time / 100)
				+ "  P0 CCs=" + p0
				+ "  P1 CCs=" + p1);

			// Per-Mark status dump for HUMAN seats only. AI seats already get
			// the same report at logs/BotPlayer_<N>_Status.txt via BotPlayer.
			// Same formatter (PlayerStatusReport.Write) so the two files diff
			// cleanly side-by-side. File name uses .txt to match the bot
			// family's extension.
			TimeSpan runtime = DateTime.Now - m_MissionStartWallTime;
			for (int i = 0; i < stateSnapshot.players.Count; ++i)
			{
				PlayerState ps = stateSnapshot.players[i];
				if (ps == null || !ps.isHuman) continue;
				string path = Path.Combine("logs", "Player_" + i + "_Status.txt");
				PlayerStatusReport.Write(i, "Human", stateSnapshot, runtime, path);
			}
		}

		// Wall-clock anchor for the human "Current Runtime" line. Stamped in
		// StartMission so it matches what the player sees on the clock.
		private DateTime m_MissionStartWallTime = DateTime.Now;

		/// <summary>
		/// Releases all mission resources.
		/// </summary>
		public override void Dispose()
		{
			// *** Add Custom "Dispose" code here ***

			base.Dispose();
		}
	}
}

