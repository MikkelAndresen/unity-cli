using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Modal
{
    /// <summary>
    /// Runs code on the editor's main thread while that thread is parked inside an
    /// AppKit modal loop (EditorUtility.DisplayDialog, save prompts, file panels).
    /// EditorApplication.update and the connector's command queue are frozen then,
    /// but the main dispatch queue is still drained, so work dispatched there runs
    /// inside the modal loop and may inspect or dismiss the dialog.
    /// macOS only; on other platforms every call reports "not in a modal".
    /// </summary>
    [InitializeOnLoad]
    internal static class ModalDispatcher
    {
        const string MODAL_PANEL_RUN_LOOP_MODE = "NSModalPanelRunLoopMode";
        const double DRAIN_TIMEOUT_SECONDS = 1.0;

        static readonly bool s_IsSupported = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        // Kept in a static so the GC never collects the delegate behind the
        // native function pointer handed to libdispatch.
        static MacInterop.DispatchFunction s_Trampoline;
        static IntPtr s_TrampolinePtr;
        static int s_Pending;

        static ModalDispatcher()
        {
            if (!s_IsSupported) return;
            AssemblyReloadEvents.beforeAssemblyReload += DrainPending;
        }

        public static bool IsSupported => s_IsSupported;

        /// True when the main thread is currently inside an AppKit modal loop. Any thread.
        public static bool IsMainThreadInModalLoop()
        {
            return s_IsSupported && MacInterop.MainRunLoopMode() == MODAL_PANEL_RUN_LOOP_MODE;
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread inside the current modal
        /// loop and blocks the calling (non-main) thread until it finishes.
        /// Returns false without running anything when no modal loop is active, when
        /// the modal ended before the work was picked up, or on timeout. A timed-out
        /// job is abandoned and skipped if the main thread reaches it later.
        /// </summary>
        public static bool TryRunDuringModal<T>(Func<T> work, TimeSpan timeout, out T result)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            result = default;

            // Only dispatch while the modal loop is known to be running: a job that
            // sits in the queue while the main thread is merely busy could still be
            // pending across a domain reload, and its trampoline would then point
            // into the unloaded domain.
            if (!IsMainThreadInModalLoop()) return false;

            var job = new Job(() => work());
            var handle = GCHandle.Alloc(job);
            Interlocked.Increment(ref s_Pending);
            MacInterop.DispatchToMainQueue(GCHandle.ToIntPtr(handle), GetTrampoline());

            if (!job.Done.Wait(timeout))
            {
                job.Abandoned = true;
                return false;
            }

            if (job.Error != null) throw new InvalidOperationException(job.Error.Message, job.Error);
            if (!job.Ran) return false;
            result = (T)job.Result;
            return true;
        }

        static IntPtr GetTrampoline()
        {
            if (s_TrampolinePtr == IntPtr.Zero)
            {
                s_Trampoline = RunJob;
                s_TrampolinePtr = Marshal.GetFunctionPointerForDelegate(s_Trampoline);
            }
            return s_TrampolinePtr;
        }

        static void RunJob(IntPtr context)
        {
            var handle = GCHandle.FromIntPtr(context);
            var job = (Job)handle.Target;
            handle.Free();
            Interlocked.Decrement(ref s_Pending);

            try
            {
                if (job.Abandoned) return;
                // Re-check on the main thread: the modal may have closed between the
                // caller's check and this job being drained by the regular loop.
                if (MacInterop.MainRunLoopMode() != MODAL_PANEL_RUN_LOOP_MODE) return;
                job.Result = job.Work();
                job.Ran = true;
            }
            catch (Exception ex)
            {
                job.Error = ex;
            }
            finally
            {
                job.Done.Set();
            }
        }

        static void DrainPending()
        {
            if (Volatile.Read(ref s_Pending) == 0) return;

            var deadline = DateTime.UtcNow.AddSeconds(DRAIN_TIMEOUT_SECONDS);
            while (Volatile.Read(ref s_Pending) > 0 && DateTime.UtcNow < deadline)
                MacInterop.PumpMainRunLoop(0.01);

            if (Volatile.Read(ref s_Pending) > 0)
                Debug.LogWarning($"[UnityCliConnector] {s_Pending} modal dispatch job(s) still queued at domain reload.");
        }

        sealed class Job
        {
            public readonly Func<object> Work;
            public readonly ManualResetEventSlim Done = new(false);
            public volatile bool Abandoned;
            public bool Ran;
            public object Result;
            public Exception Error;

            public Job(Func<object> work)
            {
                Work = work;
            }
        }
    }
}
