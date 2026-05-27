using System;
using System.Collections.Generic;
using System.IO;

namespace DotNetMissionSDK.AI
{
	/// <summary>
	/// Per-bot diagnostic log. One file per ownerID: BotPlayer_{ownerID}.txt in CWD.
	/// Thread-safe (used from AsyncPump worker threads).
	/// </summary>
	public class BotLog
	{
		private static readonly object s_InstancesLock = new object();
		private static readonly Dictionary<int, BotLog> s_Instances = new Dictionary<int, BotLog>();

		public static BotLog Get(int ownerID)
		{
			lock (s_InstancesLock)
			{
				BotLog log;
				if (!s_Instances.TryGetValue(ownerID, out log))
					s_Instances[ownerID] = log = new BotLog(ownerID);
				return log;
			}
		}

		public static void CloseAll()
		{
			lock (s_InstancesLock)
			{
				foreach (var l in s_Instances.Values)
					l.Close();
				s_Instances.Clear();
			}
		}

		/// <summary>Broadcast a message to every open BotLog. Used for mission-wide events like "Mission Ended".</summary>
		public static void WriteAll(int tick, string message)
		{
			lock (s_InstancesLock)
			{
				foreach (var l in s_Instances.Values)
					l.Write(tick, message);
			}
		}

		public int ownerID { get; private set; }

		private readonly object m_WriteLock = new object();
		private StreamWriter m_Writer;

		private BotLog(int ownerID)
		{
			this.ownerID = ownerID;
			try
			{
				Directory.CreateDirectory("logs");
				FileStream fs = new FileStream(Path.Combine("logs", "BotPlayer_" + ownerID + ".txt"), FileMode.Create, FileAccess.Write, FileShare.Read);
				m_Writer = new StreamWriter(fs) { AutoFlush = true };

				// Wall-clock header line so this log can be correlated with MissionSDK.log
				string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
				m_Writer.WriteLine("# BotLog opened at " + stamp + " for player " + ownerID);
				Write(0, "BotLog opened");
			}
			catch (Exception e)
			{
				Console.WriteLine("BotLog init failed for player " + ownerID + ": " + e.Message);
			}
		}

		/// <summary>Write a log line prefixed with wall-clock time-of-day and the game tick. Safe from any thread.</summary>
		public void Write(int tick, string message)
		{
			if (m_Writer == null) return;
			lock (m_WriteLock)
			{
				try
				{
					string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
					m_Writer.WriteLine("[" + stamp + " t=" + tick + "] " + message);
				}
				catch { }
			}
		}

		private void Close()
		{
			lock (m_WriteLock)
			{
				if (m_Writer != null)
				{
					try
					{
						string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
						m_Writer.WriteLine("# BotLog closed at " + stamp);
						m_Writer.Close();
					}
					catch { }
					m_Writer = null;
				}
			}
		}
	}
}
