using System;
using System.Threading;
using UnityEditor;

namespace UnityCliConnector.Modal
{
    /// <summary>
    /// Background poller that notices when the editor is stuck in a modal dialog
    /// and keeps the heartbeat file fresh with state "modal" plus the dialog's
    /// contents. The heartbeat's own tick runs on EditorApplication.update, which
    /// is frozen for as long as the dialog is up, so without this the CLI only
    /// sees a stale heartbeat and waits until it times out.
    /// </summary>
    [InitializeOnLoad]
    internal static class ModalWatchdog
    {
        const int INTERVAL_MS = 500;
        static readonly TimeSpan CAPTURE_TIMEOUT = TimeSpan.FromSeconds(2);

        static Timer s_Timer;
        static int s_Ticking;
        static volatile ModalDialog s_Current;

        static ModalWatchdog()
        {
            if (!ModalDispatcher.IsSupported) return;
            s_Timer = new Timer(_ => Tick(), null, INTERVAL_MS, INTERVAL_MS);
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        /// The dialog currently blocking the main thread, or null. Any thread.
        public static ModalDialog Current => s_Current;

        /// Records a dialog state observed outside the poller (e.g. right after an
        /// answer dismissed the dialog) so the heartbeat stops reporting "modal"
        /// without waiting for the next tick.
        public static void Publish(ModalDialog dialog)
        {
            s_Current = dialog;
            Heartbeat.WriteFromAnyThread();
        }

        static void Stop()
        {
            s_Timer?.Dispose();
            s_Timer = null;
            s_Current = null;
        }

        static void Tick()
        {
            if (Interlocked.Exchange(ref s_Ticking, 1) == 1) return;
            try
            {
                ModalDialog dialog = null;
                if (ModalDispatcher.IsMainThreadInModalLoop())
                    ModalDispatcher.TryRunDuringModal(ModalInspector.CaptureCurrent, CAPTURE_TIMEOUT, out dialog);

                var previous = s_Current;
                s_Current = dialog;
                if (dialog != null || previous != null)
                    Heartbeat.WriteFromAnyThread();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UnityCliConnector] Modal watchdog tick failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref s_Ticking, 0);
            }
        }
    }
}
