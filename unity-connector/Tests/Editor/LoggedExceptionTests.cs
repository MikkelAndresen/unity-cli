using NUnit.Framework;
using UnityCliConnector.TestRunner;

namespace UnityCliConnector.Tests
{
    /// Exceptions a failing test logged (ITestResultAdaptor.Output) are reported with the failure.
    [TestFixture]
    public class LoggedExceptionTests
    {
        // Captured from a real run: Debug.Log, then Debug.LogException of a caught exception.
        private const string Output =
            "probe plain log line\n" +
            "InvalidOperationException: probe-boom: ECB entity used before playback\n" +
            "Ns.ProbeTests.Probe () (at ./Library/PackageCache/com.unity.entities@b4620332ec2d/Unity.Entities/EntityComponentStoreDebug.cs:184)\n" +
            "UnityEngine.Debug:LogException(Exception)\n" +
            "Ns.ProbeTests:Probe() (at /elsewhere/ProbeTests.cs:15)\n" +
            "\n";

        [Test]
        public void ExceptionLine_AndItsThrowSite_AreReported()
        {
            var found = RunTests.ExceptionsLogged("Ns.ProbeTests.Probe", Output);

            Assert.AreEqual(1, found.Count);
            Assert.AreEqual("Ns.ProbeTests.Probe", found[0].test);
            Assert.AreEqual("InvalidOperationException: probe-boom: ECB entity used before playback", found[0].exception);
            Assert.AreEqual("Library/PackageCache/com.unity.entities@b4620332ec2d/Unity.Entities/EntityComponentStoreDebug.cs:184",
                found[0].at, "the first frame is the throw site; a ./ prefix is dropped");
        }

        [Test]
        public void PlainLogs_AreNotExceptions()
        {
            CollectionAssert.IsEmpty(RunTests.ExceptionsLogged("T", "probe plain log line\nSomething about Exceptions here\n"));
            CollectionAssert.IsEmpty(RunTests.ExceptionsLogged("T", null));
        }

        [Test]
        public void RepeatedException_IsReportedOnce_AndAtMostThree()
        {
            var once = "NullReferenceException: x\nA.B () (at a.cs:1)\n";
            Assert.AreEqual(1, RunTests.ExceptionsLogged("T", once + once + once).Count);

            var many = "";
            for (var i = 0; i < 5; i++) many += $"ArgumentException: {i}\nA.B () (at a.cs:{i})\n";
            Assert.AreEqual(3, RunTests.ExceptionsLogged("T", many).Count);
        }
    }
}
