using System;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
    /// Keeps an editor that is not the focused app updating while the connector has work.
    ///
    /// Unity 6000.6 (macOS) drops a background editor to 10 updates/s regardless of Interaction
    /// Mode (`Application::UpdateInteractionModeSettings`), and timer jitter skips every other one:
    /// 200 ms between updates in practice. Every queued request, every `await` continuation and
    /// every step of a stepped test run waits for one of those updates.
    ///
    /// The internal `EditorApplication.SignalTick()` is thread-safe and clears the throttle's
    /// last-update time before waking the main run loop, so it causes an update within ~1 ms. It is
    /// called from a thread of our own on purpose: called from inside `update`, as Unity's Pipeline
    /// package does, it only reached 10 updates/s here; from a thread every 16 ms it reached ~60/s
    /// at ~8% CPU (2% idle). Measured on the bench project, 2026-11-05.
    [InitializeOnLoad]
    internal static class EditorWake
    {
        internal const int PumpIntervalMs = 16;

        static readonly Action s_SignalTick = BindSignalTick();
        static readonly object s_Lock = new();
        static int s_Holds;
        static Thread s_Pump;
        static bool s_Stopped;

        static EditorWake()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            if (s_SignalTick == null)
                Debug.LogWarning("[UnityCliConnector] EditorApplication.SignalTick was not found; requests to an " +
                                 "unfocused editor wait for Unity's background update rate.");
        }

        internal static bool Available => s_SignalTick != null;
        internal static int HoldCount { get { lock (s_Lock) return s_Holds; } }
        internal static bool Pumping { get { lock (s_Lock) return s_Pump != null; } }

        /// Asks for an editor update now. Callable from any thread.
        internal static void Now()
        {
            try { s_SignalTick?.Invoke(); }
            catch { }
        }

        /// Keeps the editor updating every <see cref="PumpIntervalMs"/> until the returned handle is
        /// disposed. Held state is per domain: a domain reload stops the pump, and whoever still
        /// has work in the new domain takes a new hold.
        internal static IDisposable Hold()
        {
            bool first;
            lock (s_Lock) first = s_Holds++ == 0;
            // Outside s_Lock: AppActivity's update takes its own lock and then reads HoldCount.
            if (first) AppActivity.Begin();
            if (s_SignalTick == null) return new Handle();
            lock (s_Lock)
            {
                if (s_Pump == null && !s_Stopped)
                {
                    s_Pump = new Thread(Pump) { IsBackground = true, Name = "UnityCliConnector.EditorWake" };
                    s_Pump.Start();
                }
            }
            return new Handle();
        }

        static void Pump()
        {
            while (true)
            {
                lock (s_Lock)
                {
                    if (s_Holds == 0 || s_Stopped)
                    {
                        s_Pump = null;
                        return;
                    }
                }
                Now();
                Thread.Sleep(PumpIntervalMs);
            }
        }

        static void Stop()
        {
            lock (s_Lock) s_Stopped = true;
        }

        static Action BindSignalTick()
        {
            // Internal API, bound by name (checked against 6000.6.5f1); null if Unity renames or drops it.
            var method = typeof(EditorApplication).GetMethod("SignalTick",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(void)) return null;
            try { return (Action)Delegate.CreateDelegate(typeof(Action), method); }
            catch { return null; }
        }

        sealed class Handle : IDisposable
        {
            int m_Disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref m_Disposed, 1) != 0) return;
                bool last;
                lock (s_Lock) last = --s_Holds == 0;
                if (last) AppActivity.Released();
            }
        }
    }
}
