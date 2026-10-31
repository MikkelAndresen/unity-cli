using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityCliConnector.Modal;

namespace UnityCliConnector
{
    [InitializeOnLoad]
    public static class Heartbeat
    {
        static readonly string s_Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "instances");

        static double s_LastWrite;
        const double INTERVAL = 0.5;
        const string CONNECTOR_VERSION = "0.7.0";
        static string s_ForcedState;
        static double s_CompileRequestTime;
        static string s_FilePath;

        // Snapshot of main-thread-only values so the file can be rewritten from the
        // modal watchdog's thread while the main thread is blocked.
        static readonly object s_WriteLock = new();
        static string s_ProjectPath;
        static string s_UnityVersion;
        static int s_Pid;
        static string s_LastState = "ready";
        static bool s_LastCompileErrors;

        static Heartbeat()
        {
            EditorApplication.update += Tick;
            EditorApplication.quitting += Cleanup;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += () => { s_ForcedState = null; s_LastWrite = 0; };
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnBeforeAssemblyReload()
        {
            WriteState("reloading");
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
                WriteState("entering_playmode");
        }

        static void WriteState(string state)
        {
            s_ForcedState = state;
            Write();
        }

        /// <summary>
        /// Marks that a compile was requested. Keeps "compiling" state forced
        /// for a grace period so the CLI poller never sees a premature "ready".
        /// </summary>
        public static void MarkCompileRequested()
        {
            s_CompileRequestTime = EditorApplication.timeSinceStartup;
            WriteState("compiling");
        }

        static void Tick()
        {
            if (!HttpServer.IsRunning) return;

            var now = EditorApplication.timeSinceStartup;
            if (now - s_LastWrite < INTERVAL) return;
            s_LastWrite = now;

            if (s_CompileRequestTime > 0)
            {
                if (now - s_CompileRequestTime < 3.0 && EditorApplication.isCompiling == false)
                {
                    Write();
                    return;
                }
                s_CompileRequestTime = 0;
            }

            s_ForcedState = null;
            Write();
        }

        static string GetFilePath()
        {
            if (s_FilePath != null) return s_FilePath;
            var projectPath = Application.dataPath.Replace("/Assets", "");
            using var md5 = MD5.Create();
            var hash = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(projectPath)))
                .Replace("-", "").Substring(0, 16).ToLower();
            s_FilePath = Path.Combine(s_Dir, $"{hash}.json");
            return s_FilePath;
        }

        static void Write()
        {
            if (s_ProjectPath == null)
            {
                s_ProjectPath = Application.dataPath.Replace("/Assets", "");
                s_UnityVersion = Application.unityVersion;
                s_Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                GetFilePath();
            }

            s_LastState = s_ForcedState ?? GetState();
            s_LastCompileErrors = EditorUtility.scriptCompilationFailed;
            WriteFile();
        }

        /// <summary>
        /// Rewrites the heartbeat from the last main-thread snapshot plus the current
        /// modal dialog. Safe to call from any thread; used by ModalWatchdog while the
        /// main thread is parked in a modal loop.
        /// </summary>
        internal static void WriteFromAnyThread()
        {
            if (s_ProjectPath == null) return;
            WriteFile();
        }

        static void WriteFile()
        {
            var modal = s_LastState == "stopped" ? null : ModalWatchdog.Current;
            var status = new
            {
                state = modal != null ? "modal" : s_LastState,
                projectPath = s_ProjectPath,
                port = HttpServer.Port,
                pid = s_Pid,
                unityVersion = s_UnityVersion,
                connectorVersion = GetConnectorVersion(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                compileErrors = s_LastCompileErrors,
                modal,
            };

            lock (s_WriteLock)
            {
                try
                {
                    Directory.CreateDirectory(s_Dir);
                    File.WriteAllText(s_FilePath, JsonConvert.SerializeObject(status,
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                }
                catch
                {
                }
            }
        }

        static string GetConnectorVersion()
        {
            return CONNECTOR_VERSION;
        }

        static string GetState()
        {
            if (EditorApplication.isCompiling) return "compiling";
            if (EditorApplication.isUpdating) return "refreshing";
            if (EditorApplication.isPlaying)
                return EditorApplication.isPaused ? "paused" : "playing";
            return "ready";
        }

        public static void Cleanup()
        {
            MarkStopped();
        }

        public static void MarkStopped()
        {
            s_ForcedState = "stopped";
            Write();
        }
    }
}
