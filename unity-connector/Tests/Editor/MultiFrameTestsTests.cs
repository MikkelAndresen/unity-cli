using System;
using System.Collections;
using NUnit.Framework;
using UnityCliConnector.TestRunner;
using UnityEngine.TestTools;

namespace UnityCliConnector.Tests
{
    /// Which tests force a stepped run: a synchronous one leaves out tests that span frames.
    [TestFixture]
    public class MultiFrameTestsTests
    {
        // Abstract, so NUnit never builds a fixture from a probe.
        private abstract class PlainProbe
        {
            [Test] public void Instant() { }
        }

        private abstract class UnityTestProbe
        {
            [UnityTest] public IEnumerator OverFrames() { yield return null; }
        }

        private abstract class UnitySetUpProbe
        {
            [UnitySetUp] public IEnumerator SetUp() { yield return null; }
            [Test] public void Instant() { }
        }

        private class OuterActionAttribute : Attribute, IOuterUnityTestAction
        {
            public IEnumerator BeforeTest(NUnit.Framework.Interfaces.ITest test) { yield return null; }
            public IEnumerator AfterTest(NUnit.Framework.Interfaces.ITest test) { yield return null; }
        }

        [OuterAction]
        private abstract class OuterActionProbe
        {
            [Test] public void Instant() { }
        }

        [Test]
        public void PlainTests_RunSynchronously() => Assert.IsFalse(MultiFrameTests.SpansFrames(typeof(PlainProbe)));

        [Test]
        public void UnityTest_SpansFrames() => Assert.IsTrue(MultiFrameTests.SpansFrames(typeof(UnityTestProbe)));

        [Test]
        public void UnitySetUp_SpansFrames() => Assert.IsTrue(MultiFrameTests.SpansFrames(typeof(UnitySetUpProbe)));

        [Test]
        public void OuterUnityTestAction_SpansFrames() => Assert.IsTrue(MultiFrameTests.SpansFrames(typeof(OuterActionProbe)));

        [Test]
        public void AnAssemblyWithoutThem_HasNone() =>
            Assert.IsFalse(MultiFrameTests.HasMultiFrameTests(typeof(Assert).Assembly));

        [Test]
        public void AnAssemblyWithOne_IsReported()
        {
            MultiFrameTests.ResetForTests();
            Assert.IsTrue(MultiFrameTests.AnyIn(new[] { typeof(MultiFrameTestsTests).Assembly.GetName().Name }));
        }

        [Test]
        public void UnknownAssemblyNames_HaveNone()
        {
            MultiFrameTests.ResetForTests();
            Assert.IsFalse(MultiFrameTests.AnyIn(new[] { "No.Such.Assembly" }));
        }
    }
}
