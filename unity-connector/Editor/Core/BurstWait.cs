using System;
using System.Threading.Tasks;

namespace UnityCliConnector
{
    /// Bounded wait on Burst's own compile queue. Lives outside the Burst-gated tool assembly so
    /// the polling/timeout logic is testable in a project without the Burst package.
    internal static class BurstWait
    {
        internal const int PollIntervalMs = 100;

        internal static async Task<(bool IdleKnown, bool Idle, int WaitedMs, bool TimedOut)> ForIdleAsync(
            Func<bool> canQueryIdle, Func<bool> isIdle, float waitSeconds, int pollIntervalMs = PollIntervalMs)
        {
            if (canQueryIdle == null) throw new ArgumentNullException(nameof(canQueryIdle));
            if (isIdle == null) throw new ArgumentNullException(nameof(isIdle));

            var idleKnown = canQueryIdle();
            var start = DateTime.UtcNow;
            var deadline = start.AddSeconds(waitSeconds);
            var idle = idleKnown && isIdle();
            while (idleKnown && !idle && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(pollIntervalMs));
                idle = isIdle();
            }
            var waited = (int)(DateTime.UtcNow - start).TotalMilliseconds;
            return (idleKnown, idle, waited, idleKnown && !idle && waitSeconds > 0f);
        }
    }
}
