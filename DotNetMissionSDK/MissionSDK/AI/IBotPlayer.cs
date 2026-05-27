using DotNetMissionSDK.State.Snapshot;

namespace DotNetMissionSDK.AI
{
	// Contract every AI implementation honors. The SDK constructs and ticks
	// implementations via this interface; the .opm "AIImpl" field selects which
	// concrete type to instantiate per player slot. Implementations live in
	// sibling folders (DotNetMissionSDK/MissionSDK/AI/ for TechCor's reference
	// bot, DotNetMissionSDK/MissionSDK/AIv2/ for the next one, etc.) so each
	// can evolve independently without touching the others.
	public interface IBotPlayer
	{
		int playerID { get; }
		bool isActive { get; }

		void Start();
		void Stop();
		void Update(StateSnapshot stateSnapshot);
	}
}
