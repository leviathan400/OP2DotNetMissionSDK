using System;
using System.Collections.Generic;
using DotNetMissionSDK.Pathfinding;
using Xunit;

namespace DotNetMissionSDK.Tests
{
	// Pathfinder reads the static GameMap bounds, so these tests share state with other static-state tests.
	[Collection("Static game state")]
	public class PathfinderTests
	{
		// A non-wrapping map, in OP2 map coordinates (x starts at 32 on clamped maps).
		private const int MinX = 32, MinY = 0, Width = 40, Height = 30;

		public PathfinderTests()
		{
			SetStaticProperty(typeof(GameMap), "doesWrap", false);
			SetStaticProperty(typeof(GameMap), "bounds", new MAP_RECT(MinX, MinY, Width, Height));
		}

		private static void SetStaticProperty(Type type, string name, object value)
		{
			type.GetProperty(name).SetValue(null, value);
		}

		private static int OpenGround(int x, int y)
		{
			return 1;
		}

		private static void AssertContiguous(LOCATION[] path, LOCATION start, LOCATION goal)
		{
			Assert.NotNull(path);
			Assert.Equal(start, path[0]);
			Assert.Equal(goal, path[path.Length - 1]);

			for (int i = 1; i < path.Length; ++i)
			{
				int dx = Math.Abs(path[i].x - path[i - 1].x);
				int dy = Math.Abs(path[i].y - path[i - 1].y);
				Assert.True(dx <= 1 && dy <= 1 && dx + dy > 0, $"Step {i - 1}->{i} is not to an adjacent tile: {path[i - 1]} -> {path[i]}");
			}
		}

		[Fact]
		public void FindsContiguousPathAcrossOpenGround()
		{
			LOCATION start = new LOCATION(MinX + 2, 5);
			LOCATION goal = new LOCATION(MinX + 20, 15);

			LOCATION[] path = Pathfinder.GetPath(start, goal, true, OpenGround);

			AssertContiguous(path, start, goal);
		}

		[Fact]
		public void RoutesAroundAWall()
		{
			// Vertical wall at x = MinX+10 with a single gap at y = 25.
			int wallX = MinX + 10;
			Pathfinder.TileCostCallback cost = (x, y) => (x == wallX && y != 25) ? Pathfinder.Impassable : 1;

			LOCATION start = new LOCATION(MinX + 5, 5);
			LOCATION goal = new LOCATION(MinX + 15, 5);

			LOCATION[] path = Pathfinder.GetPath(start, goal, true, cost);

			AssertContiguous(path, start, goal);
			Assert.All(path, p => Assert.NotEqual(Pathfinder.Impassable, cost(p.x, p.y)));
			Assert.Contains(new LOCATION(wallX, 25), path);
		}

		[Fact]
		public void ReturnsNullWhenGoalIsWalledOff()
		{
			LOCATION goal = new LOCATION(MinX + 20, 15);
			Pathfinder.TileCostCallback cost = (x, y) =>
				(Math.Abs(x - goal.x) <= 1 && Math.Abs(y - goal.y) <= 1 && !(x == goal.x && y == goal.y)) ? Pathfinder.Impassable : 1;

			LOCATION[] path = Pathfinder.GetPath(new LOCATION(MinX + 2, 5), goal, true, cost);

			Assert.Null(path);
		}

		[Fact]
		public void ReturnsNullForGoalOutsideMap()
		{
			LOCATION[] path = Pathfinder.GetPath(new LOCATION(MinX + 2, 5), new LOCATION(MinX + 2, Height + 50), true, OpenGround);

			Assert.Null(path);
		}

		[Fact]
		public void GetClosestValidTileFindsAMatchingTile()
		{
			LOCATION target = new LOCATION(MinX + 12, 9);

			bool found = Pathfinder.GetClosestValidTile(new LOCATION(MinX + 5, 5), OpenGround, (x, y) => x == target.x && y == target.y, out LOCATION foundPt);

			Assert.True(found);
			Assert.Equal(target, foundPt);
		}

		[Fact]
		public void GetClosestValidTileReturnsFalseWhenNothingMatches()
		{
			bool found = Pathfinder.GetClosestValidTile(new LOCATION(MinX + 5, 5), OpenGround, (x, y) => false, out LOCATION _);

			Assert.False(found);
		}
	}
}
