using System;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityCliConnector
{
    /// Completes on the next EditorApplication.update. Shared by exec's yields and the screenshot
    /// capture wait so both pump the editor loop the same way.
    internal static class EditorUpdateAwaiter
    {
        /// <param name="onMainThread">Runs inside the update callback, where Unity API calls are
        /// safe. The continuation itself must not touch Unity when <paramref name="offContext"/> is
        /// set: it resumes on the thread pool, not on the editor's synchronization context.</param>
        internal static Task Next(Action onMainThread = null, bool offContext = false)
        {
            var tcs = new TaskCompletionSource<bool>(offContext
                ? TaskCreationOptions.RunContinuationsAsynchronously
                : TaskCreationOptions.None);

            void Handler()
            {
                EditorApplication.update -= Handler;
                onMainThread?.Invoke();
                tcs.TrySetResult(true);
            }

            EditorApplication.update += Handler;
            return tcs.Task;
        }
    }
}
