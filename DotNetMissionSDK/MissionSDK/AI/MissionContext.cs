namespace DotNetMissionSDK.AI
{
	// Mission-wide context handed to each IBotPlayer at construction time so
	// the AI can branch on what kind of game it's playing. Built once in
	// MissionLogic.StartMission from LevelDetails / MissionRoot and passed
	// to every bot. Read-only after construction.
	//
	// Add fields here when an AI implementation needs to know something
	// mission-wide that doesn't fit on PlayerData or StateSnapshot.
	public class MissionContext
	{
		// What does the world look like on tick 0?
		public StartingMode startingMode  { get; private set; }

		// Raw mission-type string from .opm LevelDetails.MissionType
		// ("Colony", "MultiLandRush", "MultiLastOneStanding", etc.) Kept as
		// a string so we don't pull in the Outpost2DLL MissionType enum
		// here - bots that care about the exact game-type rules can parse it
		// themselves.
		public string missionType         { get; private set; }

		// Number of player slots active in this mission.
		public int numPlayers             { get; private set; }

		// Max tech level the mission allows (research cap).
		public int maxTechLevel           { get; private set; }

		public MissionContext(StartingMode startingMode, string missionType, int numPlayers, int maxTechLevel)
		{
			this.startingMode = startingMode;
			this.missionType = missionType ?? "Colony";
			this.numPlayers = numPlayers;
			this.maxTechLevel = maxTechLevel;
		}

		public override string ToString()
		{
			return "MissionContext{startingMode=" + startingMode + " missionType=" + missionType +
				" numPlayers=" + numPlayers + " maxTechLevel=" + maxTechLevel + "}";
		}
	}

	// What the AI sees on tick 0. Drives whether it spends the first 5-10
	// Marks deploying convecs or jumps straight to military/research.
	public enum StartingMode
	{
		LandRush,        // players start with kit-loaded convecs, no buildings
		LastOneStanding  // players start with a fully-built base
	}
}
