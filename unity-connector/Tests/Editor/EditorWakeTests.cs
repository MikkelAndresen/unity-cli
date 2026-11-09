using NUnit.Framework;

namespace UnityCliConnector.Tests
{
    // The run that executes these tests usually holds the editor awake itself (its request, a
    // verify job), so counts are checked relative to what was held before.
    [TestFixture]
    public class EditorWakeTests
    {
        [Test]
        public void SignalTick_IsBound()
        {
            Assert.That(EditorWake.Available, Is.True,
                "EditorApplication.SignalTick is gone: an unfocused editor answers at Unity's background rate again");
        }

        [Test]
        public void Hold_PumpsUntilDisposed()
        {
            var before = EditorWake.HoldCount;
            var hold = EditorWake.Hold();
            Assert.That(EditorWake.HoldCount, Is.EqualTo(before + 1));
            Assert.That(EditorWake.Pumping, Is.True);

            hold.Dispose();
            Assert.That(EditorWake.HoldCount, Is.EqualTo(before));
        }

        [Test]
        public void Dispose_Twice_ReleasesOnce()
        {
            var before = EditorWake.HoldCount;
            var hold = EditorWake.Hold();
            hold.Dispose();
            hold.Dispose();
            Assert.That(EditorWake.HoldCount, Is.EqualTo(before));
        }
    }
}
