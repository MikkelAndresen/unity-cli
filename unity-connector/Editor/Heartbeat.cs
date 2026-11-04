using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityCliConnector.BuildJobs;
using UnityCliConnector.Modal;

namespace UnityCliConnector
{
    [InitializeOnLoad]
    public static class Heartbeat
    {
        static readonly string s_Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "instances");

        static double s_LastWrite;
        // Keeps "starting" fresh while the main thread has not ticked yet (a large import can hold
        // it for longer than the CLI's silence threshold). Disposed by the first Tick.
        static Timer s_StartupBeater;
        const double INTERVAL = 0.5;
        const string CONNECTOR_VERSION = "0.7.0";
        // A requested compile that never starts must not keep the state "compiling" forever; the
        // CLI's nothing_to_compile grace is shorter than this, so an expiry only bounds a rare
        // queued-behind-import case, it is not the normal path.
        const double COMPILE_PENDING_MAX = 60.0;
        const string TickedSessionKey = "unity-cli/heartbeat-ticked";
        static string s_ForcedState;
        static double s_CompileRequestTime;
        static long s_CompileStartBaseline;
        static long s_CompileRequestWallMs;
        // Unix ms of the listener coming up in this editor process; published while "starting" so
        // the CLI can show how long the editor has been waiting for its first tick (the beater keeps
        // the heartbeat itself fresh, which hides that age).
        static long s_StartingSince;
        static bool s_TickedThisDomain;
        static string s_FilePath;

        // Snapshot of main-thread-only values so the file can be rewritten from the
        // modal watchdog's thread while the main thread is blocked.
        static readonly object s_WriteLock = new();
        static string s_ProjectPath;
        static string s_UnityVersion;
        static int s_Pid;
        static string s_LastState = "ready";
        static bool s_LastCompileErrors;
        static long s_LastCompileFinishedAt;

        /// <summary>The state of the last heartbeat written. Readable from any thread — it is the
        /// main-thread snapshot, which is what a request thread answers with when the main thread is
        /// not ticking.</summary>
        internal static string LastState => Volatile.Read(ref s_LastState);

        // Set while a build job holds the main thread. GetState is main-thread only, so the flag is
        // what keeps a heartbeat written from the build's own beater thread saying "building".
        static volatile bool s_BuildActive;
        static volatile BuildHeartbeatSnapshot s_BuildInfo;

        static Heartbeat()
        {
            // An AssetImportWorker shares the editor's projectPath, so its heartbeat would land in the
            // same file with the worker's pid — e.g. MarkStopped on the worker's quit.
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;

            EditorApplication.update += Tick;
            EditorApplication.quitting += Cleanup;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnBeforeAssemblyReload()
        {
            StopStartupBeater();
            WriteState("reloading");
        }

        static void OnAfterAssemblyReload()
        {
            s_ForcedState = null;
            s_LastWrite = 0;
            // InitializeOnLoad order is not guaranteed: HttpServer.Start may have run first, in which
            // case this is the write that puts the new session's first heartbeat on disk.
            if (HttpServer.IsRunning) OnListenerStarted();
        }

        /// <summary>
        /// Publishes a heartbeat as soon as the listener is up. The listener starts before
        /// EditorApplication.update does any useful work on a cold import, and until the first tick
        /// the old Tick early-return left the daemon with no heartbeat at all ("state None") even
        /// though the socket was answering. The first tick replaces "starting" with the real state.
        /// </summary>
        internal static void OnListenerStarted()
        {
            // A reload is not a startup: this process has already ticked, so its real state is
            // knowable and "starting" would make the CLI's reload note read a routine compile as a
            // hung launch. SessionState survives the reload that resets the statics above.
            if (s_LastWrite == 0 && s_StartingSince == 0 && !TickedBeforeThisReload())
            {
                s_StartingSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                WriteState("starting");
                StartStartupBeater();
                return;
            }

            Write();
        }

        static bool TickedBeforeThisReload()
        {
            try
            {
                return SessionState.GetBool(TickedSessionKey, false);
            }
            catch
            {
                return false;
            }
        }

        static void MarkTicked()
        {
            if (s_TickedThisDomain) return;
            s_TickedThisDomain = true;
            try
            {
                SessionState.SetBool(TickedSessionKey, true);
            }
            catch
            {
                // SessionState is unavailable very early in a launch; losing the marker only
                // costs one extra "starting" write after the next reload.
            }
        }

        static void StartStartupBeater()
        {
            StopStartupBeater();
            s_StartupBeater = new Timer(_ =>
            {
                if (s_LastWrite != 0)
                {
                    StopStartupBeater();
                    return;
                }
                WriteFromAnyThread();
            }, null, 500, 500);
        }

        static void StopStartupBeater()
        {
            var timer = s_StartupBeater;
            s_StartupBeater = null;
            timer?.Dispose();
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
        /// Marks that a compile was requested. Keep "compiling" forced until the compile actually
        /// starts (its start clock moves) or finishes: a request can sit behind a long asset import,
        /// and a fixed short grace would let the CLI conclude nothing_to_compile before it runs.
        /// </summary>
        public static void MarkCompileRequested()
        {
            s_CompileRequestTime = EditorApplication.timeSinceStartup;
            s_CompileRequestWallMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            s_CompileStartBaseline = CompilationGate.LastCompileStartedAt;
            WriteState("compiling");
        }

        /// <summary>
        /// Publishes what the running build job is doing. Safe from any thread; the value is an
        /// immutable snapshot, so the heartbeat never serializes a half-updated one.
        /// </summary>
        internal static void PublishBuild(BuildHeartbeatSnapshot info)
        {
            s_BuildInfo = info;
        }

        /// <summary>
        /// Marks the editor as building. Two paths keep the heartbeat saying so: the state written here
        /// is what the build's beater thread re-writes while BuildPlayer holds the main thread, and the
        /// flag is what GetState returns if the main thread does tick during a build.
        /// </summary>
        internal static void MarkBuildStarted()
        {
            s_BuildActive = true;
            WriteState(BuildJobRecord.PhaseBuilding);
        }

        internal static void MarkBuildFinished()
        {
            s_BuildActive = false;
            s_BuildInfo = null;
            // MarkBuildStarted forced the state; leaving it would write "building" with no build.
            if (s_ForcedState == BuildJobRecord.PhaseBuilding) s_ForcedState = null;
            Write();
        }

        static void Tick()
        {
            if (!HttpServer.IsRunning) return;
            StopStartupBeater();

            var now = EditorApplication.timeSinceStartup;
            if (now - s_LastWrite < INTERVAL) return;
            s_LastWrite = now;

            if (s_CompileRequestTime > 0)
            {
                var started = CompilationGate.LastCompileStartedAt != s_CompileStartBaseline;
                var finished = CompilationGate.LastCompileFinishedAt > s_CompileRequestWallMs;
                var expired = now - s_CompileRequestTime >= COMPILE_PENDING_MAX;
                if (!expired && !finished && (EditorApplication.isCompiling || !started))
                {
                    Write();
                    return;
                }
                s_CompileRequestTime = 0;
            }

            s_ForcedState = null;
            s_StartingSince = 0;
            MarkTicked();
            Write();
        }

        // The hash key must match internal/editor/heartbeat.go: forward slashes, no trailing
        // separator, and the dataPath's own "/Assets" suffix removed. Only the suffix is stripped:
        // a project directory that itself contains an "Assets" segment keeps it, because the CLI
        // hashes the user-supplied project path and a replace-anywhere would diverge from it.
        static string ProjectRoot() => ProjectRootFromDataPath(Application.dataPath);

        internal static string ProjectRootFromDataPath(string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath)) return "";
            var path = dataPath.Replace('\\', '/').TrimEnd('/');
            const string suffix = "/Assets";
            return path.EndsWith(suffix, StringComparison.Ordinal)
                ? path.Substring(0, path.Length - suffix.Length)
                : path;
        }

        internal static string InstanceFileNameFor(string projectPath)
        {
            using var md5 = MD5.Create();
            var hash = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(projectPath)))
                .Replace("-", "").Substring(0, 16).ToLower();
            return $"{hash}.json";
        }

        static string GetFilePath()
        {
            if (s_FilePath != null) return s_FilePath;
            s_FilePath = Path.Combine(s_Dir, InstanceFileNameFor(ProjectRoot()));
            return s_FilePath;
        }

        static void Write()
        {
            if (s_ProjectPath == null)
            {
                s_ProjectPath = ProjectRoot();
                s_UnityVersion = Application.unityVersion;
                s_Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                GetFilePath();
            }

            s_LastState = s_ForcedState ?? GetState();
            s_LastCompileErrors = EditorUtility.scriptCompilationFailed;
            s_LastCompileFinishedAt = CompilationGate.LastCompileFinishedAt;
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
            var state = modal != null ? "modal" : s_LastState;
            var status = BuildStatus(
                state,
                s_LastCompileErrors,
                s_LastCompileFinishedAt,
                modal,
                s_BuildActive ? s_BuildInfo : null,
                state == "starting" ? s_StartingSince : 0);

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

        /// <summary>
        /// Builds the heartbeat payload. Property names here are the contract with the Go CLI's
        /// client.Instance struct, so renaming one breaks polling silently.
        /// </summary>
        internal static object BuildStatus(string state, bool compileErrors, long compileFinishedAt,
            ModalDialog modal, BuildHeartbeatSnapshot build = null, long startingSince = 0)
        {
            return new
            {
                state,
                projectPath = s_ProjectPath,
                port = HttpServer.Port,
                pid = s_Pid,
                unityVersion = s_UnityVersion,
                connectorVersion = GetConnectorVersion(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                // Only meaningful while the state is "starting"; older CLI versions ignore it.
                startingSince = startingSince > 0 ? startingSince : (long?)null,
                compileErrors,
                compileFinishedAt,
                modal,
                build,
            };
        }

        static string GetConnectorVersion()
        {
            return CONNECTOR_VERSION;
        }

        static string GetState()
        {
            // A build blocks the main thread for minutes without touching isCompiling/isUpdating;
            // without this the heartbeat would say "ready" while nothing can be answered.
            if (s_BuildActive) return BuildJobRecord.PhaseBuilding;
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
            StopStartupBeater();
            s_ForcedState = "stopped";
            Write();
        }

        // A cold launch's "starting" is written before the first Tick, so the test path needs the
        // same pre-tick state the real listener has.
        internal static void ResetStartupForTests()
        {
            StopStartupBeater();
            s_LastWrite = 0;
            s_ForcedState = null;
            s_StartingSince = 0;
            s_TickedThisDomain = false;
            try { SessionState.EraseBool(TickedSessionKey); }
            catch { }
        }

        internal static void MarkTickedForTests()
        {
            s_TickedThisDomain = true;
            try { SessionState.SetBool(TickedSessionKey, true); }
            catch { }
        }

        internal static void TickForTests() => Tick();

        internal static void ResetTickIntervalForTests() => s_LastWrite = 0;

        internal static string FilePathForTests() => GetFilePath();
    }
}
