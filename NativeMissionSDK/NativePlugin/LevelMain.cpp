#include <Outpost2DLL/Outpost2DLL.h>		// Main Outpost 2 header to interface with the game

#include "../DotNetInterop/CppInterface.h"	// Header for calling C# functions

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>						// Required for DLLMain


// ====================================================================
// MISSION METADATA — edit these for your own mission
// ====================================================================
// These exports are required by Outpost2.exe and define what shows up
// in the script picker. The values below configure the bundled cTest
// demo (Colony Game vs LaunchStarship AI on on6_01.map).
//
// Parameters of ExportLevelDetailsEx (from OP2MissionSDK/Outpost2DLL):
//   Description   — Shown in the OP2 "PICK A SCRIPT" listbox
//   MapFile       — .map filename, must exist in OP2 dir (or OPU/maps/)
//   TechTree      — .txt tech tree file (MULTITEK.TXT, EDEN0.TXT, etc.)
//   MissionType   — Colony / MultiLandRush / MultiSpaceRace /
//                   MultiResourceRace / MultiMidas / MultiLastOneStanding
//                   / AutoDemo / Tutorial (see Outpost2DLL/RequiredExports.h)
//   NumPlayers    — Active player slots (must match .opm Players count)
//   MaxTechLevel  — 0–13, caps research progression
//   UnitOnlyMission — true = morale/colony disabled, false = full game
// --------------------------------------------------------------------
ExportLevelDetailsEx("Colony Game, Eden", "on6_01.map", "MULTITEK.TXT", Colony, 2, 12, false)

// SdkPath: the C# DLL that contains the mission logic. For most missions
// using JSON .opm files, leave this as the shared DotNetMissionSDK_v0.dll.
// For custom C# missions, set this to your own DLL name (e.g. "MyMission_DotNet.dll").
// The path is resolved relative to this native plugin DLL's directory.
Export const char SdkPath[] = "DotNetMissionSDK_v0.dll";
// ====================================================================

static bool CLR_INIT = false;
static char* moduleFileName;


bool Attach()
{
	CLR_INIT = true;

	return DotNetInterop::Attach(moduleFileName, SdkPath);
}

Export void __cdecl GetSaveRegions(struct BufferDesc &bufDesc)
{
	if (!CLR_INIT)
		Attach();

	bufDesc.bufferStart = DotNetInterop::GetSaveBuffer();
	bufDesc.length = DotNetInterop::GetSaveBufferLength();
}


// Note: The following function is called once by Outpost2.exe when the
//		 level is first initialized. This is where you want to create
//		 all the initial units and structures as well as setup any 
//		 map/level environment settings such as day and night.
// Note: Returns true if level loaded successfully and is playable, false to abort
Export int InitProc()
{
	if (!CLR_INIT)
	{
		if (!Attach())
			return 0;
	}

	return DotNetInterop::Initialize();
}


// Note: The following function seems to be intended for use in
//		 controlling an AI. It is called once every game cycle. 
//		 Use it for whatever code needs to run on a continual basis.
// Note: The standard level DLLs released by Sierra leave this function
//		 empty and handle all AI controls through triggers.
Export void AIProc() 
{
	if (!CLR_INIT)
		Attach();

	DotNetInterop::Update();
}


// Note: This is a trigger callback function. This function is
//		 intentionally left empty and is used as the trigger
//		 callback function for triggers that don't want or need
//		 any special callback function.
// Note: The use of Export is used by all trigger functions
//		 to ensure they are exported correctly.
// Note: The volatile sink prevents the linker's identical-COMDAT-folding
//		 (/OPT:ICF) from merging this empty function with a CFG stub
//		 (`@_guard_check_icall_nop@4`). When that fold happens OP2 calls
//		 the CFG stub via cdecl but it's __fastcall, blowing the stack
//		 and crashing ~10s into the mission with no log output.
Export void NoResponseToTrigger()
{
	static volatile int s_NoResponseSink = 0;
	s_NoResponseSink++;
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved)
{
	int len;
	LPSTR filePath;
	//bool result;

	switch (fdwReason)
	{
	case DLL_PROCESS_ATTACH:
		len = MAX_PATH;
		filePath = new char[len];

		// Get DLL path
		while (GetModuleFileName(hinstDLL, filePath, len) == len)
		{
			delete[] filePath;
			len += MAX_PATH;
			filePath = new char[len];
		}

		//result = DotNetInterop::Attach(filePath, USE_CUSTOM_DLL);

		//delete[] filePath;

		//return result;

		moduleFileName = filePath;

		return true;

		break;

	case DLL_PROCESS_DETACH:
		DotNetInterop::Detach();
		break;
	}

	return true;
}

