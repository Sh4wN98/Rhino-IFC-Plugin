using RhinoIfc.Core;
using Xunit;

namespace RhinoIfc.Core.Tests
{
    public class UserTextMatchTests
    {
        [Theory]
        [InlineData("IfcWall", "ifcwall", true)]
        [InlineData("IfcWallStandardCase", "IfcWall", false)]
        [InlineData("IfcWallStandardCase", "IfcWall*", true)]
        [InlineData("EG", "?G", true)]
        [InlineData("1.OG (a)", "1.OG (*)", true)]
        [InlineData("", "*", true)]
        public void Matches(string value, string pattern, bool expected)
        {
            Assert.Equal(expected, UserTextMatch.Matches(value, pattern));
        }

        [Theory]
        [InlineData("IfcWallStandardCase", "IfcWall", true)]
        [InlineData("IfcSlabElementedCase", "ifcslab", true)]
        [InlineData("IfcWall", "IfcWall", true)]
        [InlineData("IfcStairFlight", "IfcStair", false)]
        [InlineData("IfcWallStandardCase", "IfcWal?", false)]
        public void ClassesTakeTheirCaseSubtypes(string ifcClass, string pattern, bool expected)
        {
            Assert.Equal(expected, UserTextMatch.MatchesClass(ifcClass, pattern));
        }

        [Fact]
        public void Groups()
        {
            Assert.Equal("Pset_WallCommon", UserTextMatch.Group("Pset_WallCommon.IsExternal"));
            Assert.Equal("Pset_X", UserTextMatch.Group("Pset_X.Range.UpperBoundValue"));
            Assert.Equal("IFC", UserTextMatch.Group("IfcGlobalId"));
        }
    }

}
