using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityCliConnector.TestRunner;

namespace UnityCliConnector.Tests
{
    /// `test --filter` / `verify --filter`: which test full names a filter selects.
    [TestFixture]
    public class TestFilterPatternTests
    {
        private const string Test = "Fae.Gameplay.Tests.Editor.Bow.BowTests.Release_FiresArrow";
        private const string Param = "Fae.Gameplay.Tests.Editor.Bow.BowTests.Draw(0.5f)";

        private static bool Selects(string filter, string fullName) =>
            Regex.IsMatch(fullName, RunTests.FilterPattern(filter));

        [TestCase("BowTests")]
        [TestCase("Bow.BowTests")]
        [TestCase("Fae.Gameplay.Tests.Editor.Bow.BowTests")]
        [TestCase("BowTests.Release_FiresArrow")]
        [TestCase("Fae.Gameplay")]
        [TestCase(Test)]
        public void PlainName_SelectsWholeSegments(string filter)
        {
            Assert.IsTrue(Selects(filter, Test));
        }

        [TestCase("Bow.Bow")]
        [TestCase("BowTest")]
        [TestCase("Tests.Release")]
        [TestCase("Gameplay.Tests.Editor.Bow.BowTests.Release")]
        public void PartialSegment_SelectsNothing(string filter)
        {
            Assert.IsFalse(Selects(filter, Test));
        }

        [Test]
        public void ParameterizedTest_IsSelectedByItsMethodName()
        {
            Assert.IsTrue(Selects("BowTests.Draw", Param));
            Assert.IsTrue(Selects("BowTests.Draw(0.5f)", Param), "parentheses are literal, not a regex group");
        }

        [Test]
        public void RegexSyntax_IsPassedThrough()
        {
            Assert.IsTrue(Selects("^Fae\\..*Bow", Test));
            Assert.IsFalse(Selects("^Bow", Test));
        }
    }
}
