#include "stdafx.h"
#include "CppInterface.h"
#include "DotNetMissionSDK.h"

namespace DotNetInterop
{
	// Singleton class for accessing DotNetMissionEntry
	public ref class DotNetMissionDLL
	{
		static Assembly^ m_Assembly;
		
	public:
		// Interface for Mission Entry in the C# mission DLL
		delegate bool AttachDelegate(String^ dllPath);
		delegate bool InitializeDelegate();
		delegate void UpdateDelegate();
		delegate IntPtr GetSaveBufferDelegate();
		delegate int GetSaveBufferLengthDelegate();
		delegate void DetachDelegate();

	public:
		static Object^ Instance;

		static AttachDelegate^ Attach;
		static InitializeDelegate^ Initialize;
		static UpdateDelegate^ Update;
		static GetSaveBufferDelegate^ GetSaveBuffer;
		static GetSaveBufferLengthDelegate^ GetSaveBufferLength;
		static DetachDelegate^ Detach;

		// Attach creates the app domain, loads the assembly and creates the entry point
		static bool Load(String^ dotNetPath)
		{
			// Load app domain and assembly
			m_Assembly = Assembly::LoadFile(dotNetPath);

			// Create entry point
			Type^ entryType = m_Assembly->GetType("DotNetMissionSDK.DotNetMissionEntry");

			Instance = Activator::CreateInstance(entryType);

			MethodInfo^ methodAttach = entryType->GetMethod("Attach");
			MethodInfo^ methodInitialize = entryType->GetMethod("Initialize");
			MethodInfo^ methodUpdate = entryType->GetMethod("Update");
			MethodInfo^ methodGetSaveBuffer = entryType->GetMethod("GetSaveBuffer");
			MethodInfo^ methodGetSaveBufferLength = entryType->GetMethod("GetSaveBufferLength");
			MethodInfo^ methodDetach = entryType->GetMethod("Detach");

			Attach				= (AttachDelegate^)				methodAttach->CreateDelegate(AttachDelegate::typeid, Instance);
			Initialize			= (InitializeDelegate^)			methodInitialize->CreateDelegate(InitializeDelegate::typeid, Instance);
			Update				= (UpdateDelegate^)				methodUpdate->CreateDelegate(UpdateDelegate::typeid, Instance);
			GetSaveBuffer		= (GetSaveBufferDelegate^)		methodGetSaveBuffer->CreateDelegate(GetSaveBufferDelegate::typeid, Instance);
			GetSaveBufferLength = (GetSaveBufferLengthDelegate^)methodGetSaveBufferLength->CreateDelegate(GetSaveBufferLengthDelegate::typeid, Instance);
			Detach				= (DetachDelegate^)				methodDetach->CreateDelegate(DetachDelegate::typeid, Instance);
			
			// Check if everything is valid
			if (Instance == nullptr)			return false;
			if (Attach == nullptr)				return false;
			if (Initialize == nullptr)			return false;
			if (Update == nullptr)				return false;
			if (GetSaveBuffer == nullptr)		return false;
			if (GetSaveBufferLength == nullptr)	return false;
			if (Detach == nullptr)				return false;

			return true;
		}
	};

	bool Attach(const char* dllPath, const char* sdkPath)
	{
		String^ strDllPath = ToManagedString(dllPath);
		String^ sdkFileName = ToManagedString(sdkPath);
		String^ missionDir = Path::GetDirectoryName(strDllPath);

		// Resolve SDK DLL path. Look in priority order:
		//   1. Mission folder        — bundled SDK takes precedence (per-mission override)
		//   2. OPU folder            — shared SDK install (Model B, the 1.4.2+ convention)
		//   3. OP2 install root      — legacy/fallback
		// If none found, default to mission folder path so the load failure has a clear error message.
		String^ dotNetPath = nullptr;

		// 1. Mission folder
		String^ tryPath = Path::Combine(missionDir, sdkFileName);
		if (File::Exists(tryPath))
			dotNetPath = tryPath;

		// 2. OPU folder (walk up two levels: <missionDir>/maps/<name>/ -> OPU/)
		if (dotNetPath == nullptr)
		{
			String^ mapsDir = Path::GetDirectoryName(missionDir);
			if (mapsDir != nullptr)
			{
				String^ opuDir = Path::GetDirectoryName(mapsDir);
				if (opuDir != nullptr)
				{
					tryPath = Path::Combine(opuDir, sdkFileName);
					if (File::Exists(tryPath))
						dotNetPath = tryPath;

					// 3. OP2 install root (one more level up from OPU)
					if (dotNetPath == nullptr)
					{
						String^ op2Dir = Path::GetDirectoryName(opuDir);
						if (op2Dir != nullptr)
						{
							tryPath = Path::Combine(op2Dir, sdkFileName);
							if (File::Exists(tryPath))
								dotNetPath = tryPath;
						}
					}
				}
			}
		}

		// Fallback: use mission folder path so failure error is clear
		if (dotNetPath == nullptr)
			dotNetPath = Path::Combine(missionDir, sdkFileName);

		// Load DLL and create mission entry instance
		if (!DotNetMissionDLL::Load(dotNetPath))
			return false;

		// Call Attach() on DLL
		return DotNetMissionDLL::Attach(strDllPath);
	}

	bool Initialize()
	{
		return DotNetMissionDLL::Initialize();
	}

	void Update()
	{
		DotNetMissionDLL::Update();
	}

	void* GetSaveBuffer()
	{
		return (void*)DotNetMissionDLL::GetSaveBuffer();
	}

	int GetSaveBufferLength()
	{
		return DotNetMissionDLL::GetSaveBufferLength();
	}

	void Detach()
	{
		DotNetMissionDLL::Detach();
	}
}


/*
std::string GetDisplayString(const char * pName, int iValue) {
 CsharpClass ^ oCsharpObject = gcnew CsharpClass();

 oCsharpObject->Name = ToManagedString(pName);
 oCsharpObject->Value = iValue;

 return ToStdString(oCsharpObject->GetDisplayString());
}


public ref class CLRToDotNet
{
public:
	bool Initialize()
	{
		DotNetLib^ lib = gcnew DotNetLib();
		bool result = lib->Initialize();
		return result;
	}
};*/