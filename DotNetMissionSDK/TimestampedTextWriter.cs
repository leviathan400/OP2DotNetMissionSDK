using System;
using System.IO;
using System.Text;

namespace DotNetMissionSDK
{
	/// <summary>
	/// Wraps a TextWriter and prefixes every logical line with a wall-clock timestamp.
	/// Handles embedded newlines correctly - multi-line output (e.g. ex.ToString())
	/// gets one timestamp per line.
	/// Used to retrofit timestamps onto DotNetLog.txt without touching any Console.WriteLine
	/// call site in the codebase.
	/// </summary>
	public class TimestampedTextWriter : TextWriter
	{
		private readonly TextWriter m_Inner;
		private readonly object m_Lock = new object();
		private bool m_AtLineStart = true;

		public TimestampedTextWriter(TextWriter inner)
		{
			m_Inner = inner;
		}

		public override Encoding Encoding
		{
			get { return m_Inner.Encoding; }
		}

		private static string Stamp()
		{
			return "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] ";
		}

		public override void Write(string value)
		{
			if (value == null) return;
			lock (m_Lock)
			{
				int i = 0;
				while (i < value.Length)
				{
					if (m_AtLineStart)
					{
						m_Inner.Write(Stamp());
						m_AtLineStart = false;
					}

					int newline = value.IndexOf('\n', i);
					if (newline == -1)
					{
						m_Inner.Write(value.Substring(i));
						break;
					}

					m_Inner.Write(value.Substring(i, newline - i + 1));
					m_AtLineStart = true;
					i = newline + 1;
				}
			}
		}

		public override void Write(char value)
		{
			Write(value.ToString());
		}

		public override void WriteLine(string value)
		{
			Write((value ?? string.Empty) + Environment.NewLine);
		}

		public override void WriteLine()
		{
			Write(Environment.NewLine);
		}

		public override void Flush()
		{
			lock (m_Lock)
			{
				m_Inner.Flush();
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && m_Inner != null)
				m_Inner.Dispose();
			base.Dispose(disposing);
		}
	}
}
