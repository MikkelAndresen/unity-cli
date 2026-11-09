using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.BuildJobs
{
    /// <summary>
    /// The build jobs this editor has run. Every change is written to
    /// <project>/Temp/unity-cli/build-jobs.json and the file is read back at load, because a
    /// build can end in a domain reload that drops the in-memory state while the CLI is still
    /// polling for the job it started. A reload also kills the runner mid-job, so a job left
    /// "queued"/"building" in the file is restored as "interrupted": nobody can say what the
    /// player binary did after the editor stopped reporting on it.
    /// </summary>
    [InitializeOnLoad]
    internal static class BuildJobStore
    {
        internal const int MaxJobs = 8;
        internal const string TempFolderName = "unity-cli";
        internal const string FileName = "build-jobs.json";

        static readonly object s_Lock = new();
        static List<BuildJobRecord> s_Jobs = new();
        static string s_Path;
        // Set in an import worker: it shares the editor's projectPath, so its store would overwrite
        // the editor's build-jobs.json with an empty list.
        static bool s_Disabled;

        static BuildJobStore()
        {
            // Loading in a worker would see the editor's running job as abandoned and rewrite the
            // record as interrupted while the build is still going.
            if (AssetDatabase.IsAssetImportWorkerProcess())
            {
                s_Disabled = true;
                return;
            }
            Load();
        }

        internal static void SetWorkerForTests(bool worker) => s_Disabled = worker;

        internal static int CountForTests
        {
            get { lock (s_Lock) return s_Jobs.Count; }
        }

        /// <summary>
        /// Adds a job and persists it. Call on the main thread.
        /// </summary>
        public static void Add(BuildJobRecord job)
        {
            if (s_Disabled) return;
            lock (s_Lock)
            {
                s_Jobs.Add(job);
                while (s_Jobs.Count > MaxJobs) s_Jobs.RemoveAt(0);
                SaveLocked();
            }
        }

        /// <summary>
        /// Applies <paramref name="mutate"/> to a job the caller already holds, then persists.
        /// Safe from any thread; keep the delegate short, the command thread waits on this lock.
        /// </summary>
        public static void Update(BuildJobRecord job, Action<BuildJobRecord> mutate, bool persist = true)
        {
            if (job == null) return;
            lock (s_Lock)
            {
                mutate(job);
                job.updated_at_ms = NowMs();
                if (persist) SaveLocked();
            }
        }

        /// <summary>A copy of one job by id, or null. Safe from any thread.</summary>
        public static BuildJobRecord Find(string id)
        {
            lock (s_Lock)
            {
                for (var i = s_Jobs.Count - 1; i >= 0; i--)
                    if (s_Jobs[i].id == id) return s_Jobs[i].Clone();
                return null;
            }
        }

        /// <summary>
        /// The job waiting or building, if any. When several are recorded the newest running one
        /// wins, so a queued job superseded by a newer request is not reported as current.
        /// </summary>
        public static BuildJobRecord Running()
        {
            lock (s_Lock)
            {
                for (var i = s_Jobs.Count - 1; i >= 0; i--)
                    if (s_Jobs[i].IsRunning) return s_Jobs[i].Clone();
                return null;
            }
        }

        /// <summary>
        /// Whether the store still holds this exact instance. A domain reload replaces the list, so
        /// a job scheduled before one and started after it is no longer the job the CLI is polling.
        /// </summary>
        public static bool IsCurrent(BuildJobRecord job)
        {
            lock (s_Lock) return job != null && s_Jobs.Contains(job);
        }

        /// <summary>The newest job whatever its phase, or null. Safe from any thread.</summary>
        public static BuildJobRecord Latest()
        {
            lock (s_Lock)
            {
                return s_Jobs.Count == 0 ? null : s_Jobs[s_Jobs.Count - 1].Clone();
            }
        }

        static void Load()
        {
            lock (s_Lock)
            {
                s_Jobs = new List<BuildJobRecord>();
                var path = FilePath();
                if (path == null || !File.Exists(path)) return;

                try
                {
                    s_Jobs.AddRange(Deserialize(File.ReadAllText(path)));
                    if (MarkInterrupted(s_Jobs, NowMs()) > 0) SaveLocked();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UnityCliConnector] Could not read {path}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Flips jobs the previous domain left mid-flight to "interrupted" and reports how many.
        /// </summary>
        internal static int MarkInterrupted(List<BuildJobRecord> jobs, long finishedAtMs)
        {
            var flipped = 0;
            foreach (var job in jobs)
            {
                if (job == null || !job.IsRunning) continue;
                job.phase = BuildJobRecord.PhaseInterrupted;
                job.message =
                    "The editor stopped reporting this job while it was still building " +
                    "(domain reload or editor exit); the build outcome is unknown.";
                job.finished_at_ms = finishedAtMs;
                flipped++;
            }
            return flipped;
        }

        internal static string Serialize(List<BuildJobRecord> jobs) =>
            JsonConvert.SerializeObject(jobs, Formatting.None);

        internal static List<BuildJobRecord> Deserialize(string json) =>
            JsonConvert.DeserializeObject<List<BuildJobRecord>>(json) ?? new List<BuildJobRecord>();

        static void SaveLocked()
        {
            if (s_Disabled) return;
            var path = FilePath();
            if (path == null) return;

            var json = Serialize(s_Jobs);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Whole-file, never in place: a CLI reading the file while a reload races the
                // write must not see half a job list.
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityCliConnector] Could not write {path}: {ex.Message}");
            }
        }

        /// <summary>Path of the job file, or null when the project root cannot be resolved.</summary>
        internal static string FilePath()
        {
            if (s_Path != null) return s_Path;
            var root = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(root)) return null;
            s_Path = Path.Combine(root, "Temp", TempFolderName, FileName);
            return s_Path;
        }

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
