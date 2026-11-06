using System.Threading.Tasks;
using NUnit.Framework;
using UnityCliConnector;

namespace UnityCliConnector.Tests
{
    /// The Burst tool's bounded wait: it must not block when Burst cannot answer "idle", must
    /// return at once when Burst is already idle, and must give up at the caller's deadline.
    [TestFixture]
    public class BurstWaitTests
    {
        [Test]
        public async Task WhenBurstCannotAnswerIdleKnownIsFalse_AndNothingIsWaited()
        {
            var polls = 0;
            var result = await BurstWait.ForIdleAsync(() => false, () => { polls++; return false; }, 5f, 1);

            Assert.IsFalse(result.IdleKnown);
            Assert.IsFalse(result.TimedOut);
            Assert.AreEqual(0, polls, "an unknown queue state must not be polled");
        }

        [Test]
        public async Task AlreadyIdle_ReturnsCompiledWithoutWaiting()
        {
            var result = await BurstWait.ForIdleAsync(() => true, () => true, 5f, 1);

            Assert.IsTrue(result.IdleKnown);
            Assert.IsTrue(result.Idle);
            Assert.IsFalse(result.TimedOut);
        }

        [Test]
        public async Task NeverIdle_StopsAtTheDeadlineAndSaysItTimedOut()
        {
            var polls = 0;
            var result = await BurstWait.ForIdleAsync(() => true, () => { polls++; return false; }, 0.05f, 5);

            Assert.IsTrue(result.IdleKnown);
            Assert.IsFalse(result.Idle);
            Assert.IsTrue(result.TimedOut);
            Assert.Greater(polls, 0, "the wait has to ask again while it waits");
        }

        [Test]
        public async Task ZeroWait_AnswersTheCurrentStateWithoutTimingOut()
        {
            var result = await BurstWait.ForIdleAsync(() => true, () => false, 0f, 1);

            Assert.IsTrue(result.IdleKnown);
            Assert.IsFalse(result.Idle);
            Assert.IsFalse(result.TimedOut, "a 0-second wait is \"answer now\", not a timeout");
        }
    }
}
