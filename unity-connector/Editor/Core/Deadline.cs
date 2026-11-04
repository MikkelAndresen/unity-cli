using System;
using System.Diagnostics;

namespace UnityCliConnector
{
    /// <summary>A wall-clock deadline for bounded work, counted with <see cref="Stopwatch"/>:
    /// Unity's .NET profile has no <c>Environment.TickCount64</c>, and <c>Environment.TickCount</c>
    /// wraps after ~25 days, which would make a deadline compare backwards.</summary>
    public readonly struct Deadline
    {
        private readonly long m_DeadlineTicks;

        public Deadline(int millisecondsFromNow)
        {
            var ms = Math.Max(0, millisecondsFromNow);
            m_DeadlineTicks = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        }

        public bool Expired => Stopwatch.GetTimestamp() >= m_DeadlineTicks;

        public int RemainingMs
        {
            get
            {
                var remaining = (m_DeadlineTicks - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                return (int)Math.Max(0, remaining);
            }
        }
    }
}
