using System;
using System.Threading;
using DotNetMissionSDK.Async;
using Xunit;

namespace DotNetMissionSDK.Tests
{
	// AsyncPump is static, so these tests share one game clock and must not run in parallel.
	[Collection("Static game state")]
	public class AsyncPumpTests : IDisposable
	{
		private int m_Time;

		public AsyncPumpTests()
		{
			ThreadAssert.Initialize();
			AsyncPump.Release();
			AsyncPump.getTime = () => m_Time;
		}

		public void Dispose()
		{
			AsyncPump.Release();
		}

		private void PumpUntil(int time)
		{
			while (m_Time < time)
			{
				++m_Time;
				AsyncPump.Update();
			}
		}

		[Fact]
		public void CallbackReceivesResultAtTargetTick()
		{
			object result = null;
			AsyncPump.Run(() => 42, r => result = r, timeToAdd: 6);

			PumpUntil(5);
			Assert.Null(result);

			PumpUntil(6);
			Assert.Equal(42, result);
		}

		[Fact]
		public void CallbackIsNotDeliveredBeforeTargetTickEvenIfActionFinishedEarly()
		{
			bool delivered = false;
			AsyncPump.Run(() => 1, r => delivered = true, timeToAdd: 6);
			Thread.Sleep(50);

			PumpUntil(5);
			Assert.False(delivered);
		}

		[Fact]
		public void ThrowingActionDeliversNullToCallback()
		{
			object result = "unset";
			AsyncPump.Run(() => { throw new InvalidOperationException("boom"); }, r => result = r);

			PumpUntil(6);
			Assert.Null(result);
		}

		[Fact]
		public void ThrowingActionDoesNotDropOtherOperationsOnTheSameTick()
		{
			object result = null;
			AsyncPump.Run(() => { throw new InvalidOperationException("boom"); }, r => { });
			AsyncPump.Run(() => 7, r => result = r);

			PumpUntil(6);
			Assert.Equal(7, result);
		}

		[Fact]
		public void ThrowingCallbackDoesNotDropLaterCallbacks()
		{
			bool ranAfter = false;
			AsyncPump.Run(() => 1, r => { throw new InvalidOperationException("callback boom"); });
			AsyncPump.Run(() => 2, r => ranAfter = true);

			PumpUntil(6);
			Assert.True(ranAfter);
		}

		[Fact]
		public void OperationWhoseTargetTickWasMissedIsStillDelivered()
		{
			object result = null;
			AsyncPump.Run(() => { Thread.Sleep(50); return 9; }, r => result = r, timeToAdd: 6);

			// Ticks 1..9 are never pumped, as when an exception earlier in the tick skips AsyncPump.Update.
			m_Time = 10;
			AsyncPump.Update();

			Assert.Equal(9, result);
		}

		[Fact]
		public void RunRejectsNonPositiveTimeToAdd()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => AsyncPump.Run(() => null, r => { }, timeToAdd: 0));
		}
	}
}
