using DotNetMissionReader;
using DotNetMissionSDK.AI;
using DotNetMissionSDK.State.Snapshot;
using DotNetMissionSDK.Triggers;
using System;

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
		public const bool enableBotPlayerLogs = false;

		public CustomLogic(MissionRoot root, SaveData saveData, TriggerManager triggerManager) : base(root, saveData, triggerManager)
		{
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

			// Announce SDK version in the Communications panel so anyone watching
			// a game can tell at a glance which build is running.
			TethysGame.AddMessage(0, 0, "DotNetMissionSDK " + SDK_VERSION, -1, 0);
			Console.WriteLine("DotNetMissionSDK " + SDK_VERSION);

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

				default:
					base.OnTriggerExecuted(trigger);
					break;
			}
		}

		/// <summary>
		/// Called every game cycle.
		/// </summary>
		/// <param name="stateSnapshot">The current immutable state of the game.</param>
		public override void Update(StateSnapshot stateSnapshot)
		{
			base.Update(stateSnapshot);

			// *** Add custom update code here ***
		}

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

