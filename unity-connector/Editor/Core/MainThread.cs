using System.Threading;
using UnityEditor;

namespace UnityCliConnector
{
    /// <summary>
    /// Which thread Unity's API may be called from, as seen when this connector image loaded.
    ///
    /// A tool that awaits work started on the thread pool only lands back on the main thread
    /// while the UnitySynchronizationContext of *this* domain keeps pumping — a script recompile
    /// mid-await leaves the continuation on a thread-pool thread. Anything that then touches
    /// Unity APIs throws inside the snippet, so it checks this instead.
    /// </summary>
    [InitializeOnLoad]
    internal static class MainThread
    {
        static int s_Id;

        static MainThread()
        {
            s_Id = Thread.CurrentThread.ManagedThreadId;
        }

        internal static bool IsCurrent => Thread.CurrentThread.ManagedThreadId == s_Id;
    }
}
