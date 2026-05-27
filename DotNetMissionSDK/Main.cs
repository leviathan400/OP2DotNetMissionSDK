using DotNetMissionReader;
using DotNetMissionSDK.Async;
using DotNetMissionSDK.HFL;
using DotNetMissionSDK.State;
using DotNetMissionSDK.State.Snapshot;
using DotNetMissionSDK.Triggers;
using System;
using System.IO;


namespace DotNetMissionSDK
{
	/// <summary>
	/// The class containing entry points from Outpost 2.
	/// </summary>
    public class DotNetMissionEntry
	{
		// Debug log streams
		private FileStream m_LogFileStream;
		private StreamWriter m_LogWriter;

		// Mission Data
		private string m_MissionDLLName;
		MissionRoot m_MissionData;
		private SaveBuffer<SaveData> m_SaveBuffer = new SaveBuffer<SaveData>();

		// Essential Systems
		TriggerManager m_Triggers;

		// Mission logic
		private MissionLogic m_MissionLogic;


		/// <summary>
		/// Called when DLL is first loaded.
		/// </summary>
		/// <param name="dllPath">The path to the mission DLL.</param>
		/// <returns>True on success.</returns>
		public bool Attach(string dllPath)
		{
			// Put code that must be called from DLLMain.ProcessAttach here.
			// There should not be much need to put code here in most cases.
			// DO NOT PUT MISSION INIT CODE HERE.

			InitializeLog();

			m_MissionDLLName = Path.GetFileNameWithoutExtension(dllPath);

			MissionSdkLog.Write("==================================================");
			MissionSdkLog.Write("SDK DLL loaded: " + MissionSdkLog.GetSdkIdentity());
			MissionSdkLog.Write("Mission Attach: " + m_MissionDLLName + " (" + dllPath + ")");

			Console.WriteLine("Initializing DotNet DLL...");
			Console.WriteLine("Mission DLLName: " + m_MissionDLLName);

			if (CustomLogic.useJson)
			{
				Console.WriteLine("Initializing JSON...");

				// Resolve .opm path. Look next to the mission DLL first (the natural place),
				// then fall back to the current working directory (legacy behavior — OPU's
				// launcher sets CWD to its own subdir, so users used to need duplicate copies).
				string opmFileName = m_MissionDLLName + ".opm";
				string dllDir = Path.GetDirectoryName(dllPath) ?? string.Empty;
				string opmPath = Path.Combine(dllDir, opmFileName);

				if (!File.Exists(opmPath))
				{
					if (File.Exists(opmFileName))
					{
						MissionSdkLog.Write("JSON not next to DLL; falling back to CWD: " + opmFileName);
						opmPath = opmFileName;
					}
					else
					{
						Console.WriteLine("JSON data file '" + opmFileName + "' not found!");
						MissionSdkLog.Write("Attach FAILED: JSON not found at '" + opmPath + "' or in CWD");
						return false;
					}
				}

				// Load JSON data
				try
				{
					m_MissionData = MissionReader.GetMissionData(opmPath);
					MissionSdkLog.Write("JSON loaded: " + opmPath);
				}
				catch (Exception ex)
				{
					Console.WriteLine(ex.Message);
					MissionSdkLog.Write("Attach FAILED: JSON parse error: " + ex.Message);
					return false;
				}
			}

			Console.WriteLine("DLL Init complete.");
			MissionSdkLog.Write("Attach succeeded");

			return true;
		}

		private void InitializeLog()
		{
			// Initialize debug log. Console.Out/Error are redirected to logs/DotNetLog.txt,
			// wrapped so every line gets a wall-clock timestamp automatically.
			try
			{
				Directory.CreateDirectory("logs");
				m_LogFileStream = new FileStream(Path.Combine("logs", "DotNetLog.txt"), FileMode.Create, FileAccess.Write, FileShare.Read);
				m_LogWriter = new StreamWriter(m_LogFileStream);
				m_LogWriter.AutoFlush = true;

				TimestampedTextWriter stamped = new TimestampedTextWriter(m_LogWriter);
				Console.SetOut(stamped);
				Console.SetError(stamped);
			}
			catch (Exception e)
			{
				Console.WriteLine(e);
			}
		}

		/// <summary>
		/// Called when the mission is first loaded.
		/// NOTE: This is NOT called when the mission is loaded from a save file!
		/// </summary>
		/// <returns>True on success.</returns>
		public bool Initialize()
		{
			MissionSdkLog.Write("Initialize: begin");
			try
			{
				InitializeSystems();

				// Initialize mission with JSON data
				bool ok = m_MissionLogic.InitializeNewMission();
				MissionSdkLog.Write("Initialize: " + (ok ? "succeeded" : "returned false"));
				return ok;
			}
			catch (Exception ex)
			{
				Console.WriteLine("FATAL during Initialize:");
				Console.WriteLine(ex.ToString());
				MissionSdkLog.Write("Initialize FAILED: " + ex.GetType().Name + ": " + ex.Message);
				return false;
			}
		}

		private void InitializeSystems()
		{
			// Prepare save buffer
			m_SaveBuffer.Load();

			// Initialize core
			ThreadAssert.Initialize();
			AsyncRandom.Initialize(TethysGame.GetRand(int.MaxValue));

			if (!HFLCore.Init())
				Console.WriteLine("Could not initialize HFL!");

			GameMap.Initialize();

			// Initialize mission systems
			m_Triggers = new TriggerManager(m_SaveBuffer.saveData);
			
			m_MissionLogic = new CustomLogic(m_MissionData, m_SaveBuffer.saveData, m_Triggers);
		}

		/// <summary>
		/// Called every update cycle.
		/// </summary>
		public void Update()
		{
			// Wrap the whole per-tick body so a C# exception can't propagate through the C++/CLI
			// shim into native OP2 (which crashes on managed exceptions). On any unhandled exception
			// we log it and let OP2 continue ticking — far better than a hard crash with no info.
			try
			{
				// Load the save buffer if it isn't loaded
				if (!m_SaveBuffer.isLoaded)
				{
					InitializeSystems();
					m_MissionLogic.LoadMission();
				}

				// Update essential systems
				m_Triggers.Update();
				AsyncPump.Update();
				GameState.Update();
				StateSnapshot stateSnapshot = StateSnapshot.Create();

				try
				{
					// Update mission logic
					m_MissionLogic.Update(stateSnapshot);
				}
				finally
				{
					// Always release the snapshot to avoid pool leaks even if Update threw.
					stateSnapshot.Release();
				}

				// Update save buffer
				m_SaveBuffer.Save();
			}
			catch (Exception ex)
			{
				Console.WriteLine("EXCEPTION during Update tick:");
				Console.WriteLine(ex.ToString());
				try
				{
					MissionSdkLog.Write("EXCEPTION during Update: " + ex.GetType().Name + ": " + ex.Message + " | " + ex.StackTrace);
				}
				catch { }
			}
		}

		/// <summary>
		/// Gets the save buffer for mission variables that need to be saved.
		/// </summary>
		/// <returns>The save buffer.</returns>
		public IntPtr GetSaveBuffer()
		{
			return m_SaveBuffer.GetSaveBuffer();
		}

		/// <summary>
		/// Gets the save buffer length for mission variables that need to be saved.
		/// </summary>
		/// <returns>The length of the save buffer.</returns>
		public int GetSaveBufferLength()
		{
			return m_SaveBuffer.GetSaveBufferLength();
		}

		/// <summary>
		/// Called when the DLL is detached.
		/// </summary>
		public void Detach()
		{
			MissionSdkLog.Write("Detach: begin");

			m_MissionLogic.Dispose();

			// Close bot logs (writes a wall-clock footer to each)
			DotNetMissionSDK.AI.BotLog.CloseAll();

			m_SaveBuffer.Dispose();
			StateSnapshot.Destroy();
			AsyncPump.Release();

			// Dispose log file
			if (m_LogFileStream != null)
			{
				m_LogWriter.Close();
				m_LogFileStream.Close();
			}

			HFLCore.Cleanup();

			// Restore console
			StreamWriter sOut = new StreamWriter(Console.OpenStandardOutput());
			StreamWriter sError = new StreamWriter(Console.OpenStandardError());
			sOut.AutoFlush = true;
			sError.AutoFlush = true;
			Console.SetOut(sOut);
			Console.SetError(sError);

			MissionSdkLog.Write("Detach: complete");
			MissionSdkLog.Close();
		}
	}
}
