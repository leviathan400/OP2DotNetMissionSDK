using System;
using System.IO;
using System.Reflection;

namespace DotNetMissionSDK
{
	/// <summary>
	/// SDK lifecycle log. One shared file (MissionSDK.log) opened in APPEND mode,
	/// so history persists across mission runs.
	/// Every line is prefixed with a wall-clock timestamp (yyyy-MM-dd HH:mm:ss.fff).
	/// Use this for SDK-level events: DLL load, mission attach, init, detach.
	/// For per-bot AI events use BotLog. For C# Console output use DotNetLog.txt
	/// (which is what Console.Out is redirected to).
	/// </summary>
	public static class MissionSdkLog
	{
		private const string LogFileName = "MissionSDK.log";

		private static readonly object s_Lock = new object();
		private static StreamWriter s_Writer;
		private static bool s_Initialized;

		private static void EnsureOpen()
		{
			if (s_Initialized) return;
			s_Initialized = true;
			try
			{
				FileStream fs = new FileStream(LogFileName, FileMode.Append, FileAccess.Write, FileShare.Read);
				s_Writer = new StreamWriter(fs) { AutoFlush = true };
			}
			catch (Exception e)
			{
				Console.WriteLine("MissionSdkLog open failed: " + e.Message);
			}
		}

		public static void Write(string message)
		{
			lock (s_Lock)
			{
				EnsureOpen();
				if (s_Writer == null) return;
				try
				{
					string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
					s_Writer.WriteLine("[" + stamp + "] " + message);
				}
				catch { }
			}
		}

		/// <summary>Returns "{assembly name} v{version} @ {location}" for the current SDK DLL.</summary>
		public static string GetSdkIdentity()
		{
			try
			{
				Assembly asm = typeof(MissionSdkLog).Assembly;
				AssemblyName n = asm.GetName();
				return n.Name + " v" + n.Version + " @ " + asm.Location;
			}
			catch (Exception e)
			{
				return "<unavailable: " + e.Message + ">";
			}
		}

		public static void Close()
		{
			lock (s_Lock)
			{
				if (s_Writer != null)
				{
					try { s_Writer.Close(); } catch { }
					s_Writer = null;
				}
				s_Initialized = false;
			}
		}
	}
}
