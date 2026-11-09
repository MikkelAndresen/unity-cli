using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
    /// Keeps macOS App Nap off while the connector has work (EditorWake's holds drive it).
    ///
    /// An editor that is not the focused app and has sat idle for ~40 s is napped: a domain reload
    /// then took 34 s instead of 6 s and the EditMode tests after it 18 s instead of 3 s, every phase
    /// ~5x slower (bench project, 2026-11-05). A worker's verify after a minute of thinking hit
    /// exactly that. SignalTick only wakes the run loop; it does not lift the nap.
    ///
    /// The activity is native and process-wide, so it outlives a domain reload, which is the slow
    /// part it has to cover. Its pointer is kept in SessionState and the next domain adopts it.
    [InitializeOnLoad]
    internal static class AppActivity
    {
        const string TokenKey = "UnityCliConnector.AppActivity.Token";
        // After the last hold is released: a verify job resumes in the next domain a few updates
        // after its reload, and the CLI re-sends its request; neither should start napped.
        const long LingerMs = 5000;

        // NSActivityUserInitiatedAllowingIdleSystemSleep | NSActivityLatencyCritical (NSProcessInfo.h).
        const ulong Options = 0x00EFFFFFUL | 0xFF00000000UL;

        static readonly bool s_Enabled;
        static readonly object s_Lock = new();
        static IntPtr s_Token;
        static long s_ReleasedAtMs = -1;

        static AppActivity()
        {
            s_Enabled = Application.platform == RuntimePlatform.OSXEditor && !AssetDatabase.IsAssetImportWorkerProcess();
            if (!s_Enabled) return;
            s_Token = new IntPtr(long.Parse(SessionState.GetString(TokenKey, "0")));
            if (s_Token != IntPtr.Zero) s_ReleasedAtMs = NowMs();
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Persist;
        }

        internal static bool Active { get { lock (s_Lock) return s_Token != IntPtr.Zero; } }

        /// Called when EditorWake's first hold is taken, from any thread.
        internal static void Begin()
        {
            if (!s_Enabled) return;
            lock (s_Lock)
            {
                s_ReleasedAtMs = -1;
                if (s_Token != IntPtr.Zero) return;
                try
                {
                    var reason = Objc.String("unity-cli: request in progress");
                    var info = Objc.Send(Objc.Class("NSProcessInfo"), Objc.Sel("processInfo"));
                    var token = Objc.SendBegin(info, Objc.Sel("beginActivityWithOptions:reason:"), Options, reason);
                    if (token != IntPtr.Zero) s_Token = Objc.Send(token, Objc.Sel("retain"));
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning("[UnityCliConnector] could not hold off App Nap: " + e.Message);
                }
            }
        }

        /// Called when EditorWake's last hold is released, from any thread; the activity ends
        /// LingerMs later unless another hold is taken.
        internal static void Released()
        {
            if (!s_Enabled) return;
            lock (s_Lock) s_ReleasedAtMs = NowMs();
        }

        static void Tick()
        {
            Persist();
            lock (s_Lock)
            {
                if (s_Token == IntPtr.Zero || s_ReleasedAtMs < 0 || EditorWake.HoldCount > 0) return;
                if (NowMs() - s_ReleasedAtMs < LingerMs) return;
                End();
            }
        }

        static void Persist()
        {
            IntPtr token;
            lock (s_Lock) token = s_Token;
            var value = token.ToInt64().ToString();
            if (SessionState.GetString(TokenKey, "0") != value) SessionState.SetString(TokenKey, value);
        }

        // Under s_Lock.
        static void End()
        {
            var token = s_Token;
            s_Token = IntPtr.Zero;
            s_ReleasedAtMs = -1;
            SessionState.EraseString(TokenKey);
            try
            {
                var info = Objc.Send(Objc.Class("NSProcessInfo"), Objc.Sel("processInfo"));
                Objc.Send(info, Objc.Sel("endActivity:"), token);
                Objc.Send(token, Objc.Sel("release"));
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[UnityCliConnector] could not end the App Nap hold: " + e.Message);
            }
        }

        static long NowMs() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

        static class Objc
        {
            const string Lib = "/usr/lib/libobjc.A.dylib";

            [DllImport(Lib, EntryPoint = "objc_getClass")] internal static extern IntPtr Class(string name);
            [DllImport(Lib, EntryPoint = "sel_registerName")] internal static extern IntPtr Sel(string name);

            // One prototype per signature: arm64 passes arguments by the exact method prototype.
            [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr receiver, IntPtr selector);
            [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
            [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr SendBegin(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);

            internal static IntPtr String(string s)
            {
                var utf8 = Marshal.StringToCoTaskMemUTF8(s);
                try { return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8); }
                finally { Marshal.FreeCoTaskMem(utf8); }
            }
        }
    }
}
