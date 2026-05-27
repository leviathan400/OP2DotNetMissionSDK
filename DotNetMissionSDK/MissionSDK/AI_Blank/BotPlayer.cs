using IBotPlayer = DotNetMissionSDK.AI.IBotPlayer;
using DotNetMissionSDK.AI;
using DotNetMissionSDK.Async;
using DotNetMissionSDK.State.Snapshot;

namespace DotNetMissionSDK.AI_Blank
{
	// Minimal template for community AI authors. Copy this folder, rename
	// the namespace, register the new name in MissionLogic.StartMission's
	// factory, and start filling in Update with whatever logic you want.
	//
	// The blank bot does nothing - it logs a heartbeat once per Mark so you
	// can confirm the SDK actually loaded your implementation, then yields.
	// Units sit where they were placed.
	//
	// To run this bot: set "AIImpl": "AI_Blank" on a player in your .opm.
	public class BotPlayer : IBotPlayer
	{
		// BotType carried through from .opm. AI_Blank ignores it; it's there
		// so the constructor signature matches the other implementations.
		public BotType botType	{ get; set; }
		public int playerID							{ get; private set; }
		public bool isActive						{ get; private set; }

		private const int HEARTBEAT_INTERVAL_TICKS = 100;

		public BotPlayer(BotType botType, int playerToControlID)
		{
			ThreadAssert.MainThreadRequired();

			this.botType = botType;
			this.playerID = playerToControlID;

			BotLog.Get(playerToControlID).Write(TethysGame.Time(), "AI_Blank construct: type=" + botType);
		}

		public void Start()
		{
			isActive = true;
			BotLog.Get(playerID).Write(TethysGame.Time(), "AI_Blank: Start");
		}

		public void Stop()
		{
			isActive = false;
			BotLog.Get(playerID).Write(TethysGame.Time(), "AI_Blank: Stop");
		}

		public void Update(StateSnapshot stateSnapshot)
		{
			ThreadAssert.MainThreadRequired();

			if (!isActive)
				return;

			// Heartbeat - proves the plugin is loaded and ticking.
			// Replace with real logic.
			if (stateSnapshot.time % HEARTBEAT_INTERVAL_TICKS == 0)
				BotLog.Get(playerID).Write(stateSnapshot.time, "AI_Blank: heartbeat (no actions yet)");
		}
	}
}
