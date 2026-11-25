using System.IO;
using DotNetMissionReader;
using Xunit;

namespace DotNetMissionSDK.Tests
{
	public class MissionReaderTests
	{
		private static string SamplePath => Path.Combine(System.AppContext.BaseDirectory, "Data", "cTest.opm");

		[Fact]
		public void ReadsBundledSampleMission()
		{
			MissionRoot root = MissionReader.GetMissionData(SamplePath);

			Assert.Equal("on6_01.map", root.LevelDetails.MapName);
			Assert.Equal("Colony", root.LevelDetails.MissionType);
			Assert.Equal(2, root.LevelDetails.NumPlayers);
			Assert.Equal(root.LevelDetails.NumPlayers, root.MasterVariant.Players.Count);
			Assert.NotNull(root.MasterVariant.TethysGame);
		}

		[Fact]
		public void WrittenMissionReadsBackTheSame()
		{
			MissionRoot original = MissionReader.GetMissionData(SamplePath);
			string tempPath = Path.GetTempFileName();

			try
			{
				MissionReader.WriteMissionData(tempPath, original);
				MissionRoot copy = MissionReader.GetMissionData(tempPath);

				Assert.Equal(original.LevelDetails.MapName, copy.LevelDetails.MapName);
				Assert.Equal(original.LevelDetails.MaxTechLevel, copy.LevelDetails.MaxTechLevel);
				Assert.Equal(original.MasterVariant.Players.Count, copy.MasterVariant.Players.Count);
				Assert.Equal(original.Triggers.Length, copy.Triggers.Length);
				Assert.Equal(original.Disasters.Length, copy.Disasters.Length);
			}
			finally
			{
				File.Delete(tempPath);
			}
		}
	}
}
