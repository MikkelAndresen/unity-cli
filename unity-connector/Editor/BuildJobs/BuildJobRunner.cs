using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityCliConnector.BuildJobs
{
    /// <summary>
    /// The small, immutable view of a running build that the heartbeat carries. Written from the
    /// heartbeat writer's thread, so it is built under BuildJobStore's lock and never mutated.
    /// Key names are read by the Go CLI's client.Instance.Build.
    /// </summary>
    internal sealed class BuildHeartbeatSnapshot
    {
        public string id;
        public string phase;
        public string profile;
        public string target;
        public string output;
        public double elapsed_seconds;
        public double? progress_percent;
        public string progress_description;
    }

    /// <summary>
    /// Runs a queued build on a later editor update tick, outside the command lock, and keeps
    /// reporting on it while it blocks the main thread.
    ///
    /// BuildPipeline.BuildPlayer only runs on the main thread and never returns until the player is
    /// written, so during a build the only writers left are this class's timer thread and — when the
    /// build pumps the editor loop — the update handler that samples Unity's Progress items.
    /// </summary>
    [InitializeOnLoad]
    internal static class BuildJobRunner
    {
        const int BEAT_INTERVAL_MS = 500;
        const double SAMPLE_INTERVAL_S = 0.5;
        const int MaxErrorMessages = 10;
        const int MaxMessageChars = 400;

        static volatile BuildJobRecord s_Job;
        static Timer s_Beater;
        static long s_StartedUtcMs;
        static double s_LastSampleTime;
        static int s_Beats;

        static BuildJobRunner()
        {
            // A reload abandons Run without running its finally, so the beater that keeps rewriting
            // the heartbeat from another thread must be stopped here, as ModalWatchdog does.
            AssemblyReloadEvents.beforeAssemblyReload += StopBeater;
            EditorApplication.quitting += StopBeater;
        }

        static BuildJobRecord s_Pending;

        /// <summary>
        /// Hands the job to the next update tick and returns. The caller has already added it to
        /// the store, so the CLI can poll it from the moment this returns.
        /// </summary>
        public static void Schedule(BuildJobRecord job)
        {
            // Running the build from the command handler would hold the command lock for the whole
            // build, which is the wedge this job exists to avoid, so it starts a tick later.
            //
            // A one-shot update handler and not delayCall: measured on a backgrounded editor, the
            // delayCall listener was never invoked at all (a job left "queued" for minutes) while
            // update handlers kept running — the heartbeat rides the same tick.
            s_Pending = job;
            EditorApplication.update += StartPending;
        }

        static void StartPending()
        {
            EditorApplication.update -= StartPending;
            var job = s_Pending;
            s_Pending = null;
            if (job == null) return;
            Run(job);
        }

        static void Run(BuildJobRecord job)
        {
            if (!BuildJobStore.IsCurrent(job)) return;
            if (job.phase != BuildJobRecord.PhaseQueued) return;

            var previousProfile = BuildProfile.GetActiveBuildProfile();
            var startedUtcMs = NowMs();

            s_Job = job;
            s_StartedUtcMs = startedUtcMs;
            s_Beats = 0;
            s_LastSampleTime = 0;

            BuildJobStore.Update(job, j =>
            {
                j.phase = BuildJobRecord.PhaseBuilding;
                j.started_at_ms = startedUtcMs;
            });

            Heartbeat.PublishBuild(SnapshotOf(job));
            Heartbeat.MarkBuildStarted();
            StartBeater();
            EditorApplication.update += SampleProgress;

            try
            {
                var report = BuildPlayer(job);
                Complete(job, report);
            }
            catch (Exception ex)
            {
                Fail(job, ex);
            }
            finally
            {
                EditorApplication.update -= SampleProgress;
                StopBeater();
                s_Job = null;
                Heartbeat.PublishBuild(null);
                Heartbeat.MarkBuildFinished();

                RestoreActiveProfile(previousProfile);
            }
        }

        static BuildReport BuildPlayer(BuildJobRecord job)
        {
            var options = BuildOptions.None;
            if (job.development) options |= BuildOptions.Development;

            if (!string.IsNullOrEmpty(job.profile_path))
            {
                var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(job.profile_path)
                              ?? BuildProfile.GetBuildProfileAtPath(job.profile_path);
                if (profile == null)
                    throw new InvalidOperationException(
                        $"Build profile '{job.profile}' is gone; it was deleted or moved after the job was queued.");

                // BuildPlayer(BuildPlayerOptions) applies the *active* profile's settings, and unlike
                // BuildPlayerWithProfileOptions it takes a scene list. That matters for a profile whose
                // scene-list override is on but empty: measured on 6000.6.5f1 such a profile builds
                // nothing and Unity fails its setup check with "Cannot build untitled scene."
                BuildProfile.SetActiveBuildProfile(profile);
            }

            if (!Enum.TryParse(job.target, out BuildTarget target))
                throw new InvalidOperationException($"Build target '{job.target}' is no longer understood.");

            return UnityEditor.BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = job.scenes,
                locationPathName = job.output,
                target = target,
                targetGroup = UnityEditor.BuildPipeline.GetBuildTargetGroup(target),
                options = options,
            });
        }

        static void Complete(BuildJobRecord job, BuildReport report)
        {
            if (report == null)
            {
                Fail(job, new InvalidOperationException("BuildPlayer returned no report."));
                return;
            }

            var summary = report.summary;
            var phase = summary.result switch
            {
                BuildResult.Succeeded => BuildJobRecord.PhaseSucceeded,
                BuildResult.Cancelled => BuildJobRecord.PhaseCancelled,
                _ => BuildJobRecord.PhaseFailed,
            };

            BuildJobStore.Update(job, j =>
            {
                j.phase = phase;
                j.result = summary.result.ToString();
                j.total_bytes = (long) summary.totalSize;
                j.total_seconds = summary.totalTime.TotalSeconds;
                j.elapsed_seconds = summary.totalTime.TotalSeconds;
                j.error_count = summary.totalErrors;
                j.warning_count = summary.totalWarnings;
                j.errors = ErrorMessages(report);
                j.build_report_profile = summary.buildProfilePath;
                j.finished_at_ms = NowMs();
                j.message = phase == BuildJobRecord.PhaseSucceeded
                    ? $"Build succeeded: {job.output}"
                    : $"Build finished with result {summary.result}.";
            });
        }

        static void Fail(BuildJobRecord job, Exception ex)
        {
            Debug.LogException(ex);
            BuildJobStore.Update(job, j =>
            {
                j.phase = BuildJobRecord.PhaseFailed;
                j.result = j.result ?? "Failed";
                j.message = $"{ex.GetType().Name}: {ex.Message}";
                j.finished_at_ms = NowMs();
            });
        }

        /// <summary>
        /// The first few error messages of the report. Steps are scanned in build order; the scan
        /// stops as soon as the cap is reached because a failed build can log tens of thousands of
        /// messages and the report is read while the main thread is still inside the build.
        /// </summary>
        static string[] ErrorMessages(BuildReport report)
        {
            var messages = new List<string>(MaxErrorMessages);
            if (report.steps == null) return Array.Empty<string>();

            foreach (var step in report.steps)
            {
                if (step.messages == null) continue;
                foreach (var message in step.messages)
                {
                    if (message.type != LogType.Error && message.type != LogType.Exception) continue;
                    messages.Add(Truncate($"{step.name}: {message.content}"));
                    if (messages.Count >= MaxErrorMessages) return messages.ToArray();
                }
            }

            return messages.ToArray();
        }

        static string Truncate(string text) =>
            text.Length <= MaxMessageChars ? text : text.Substring(0, MaxMessageChars) + "…";

        /// <summary>
        /// Main-thread sampler for Unity's Progress window, for the case where a build pumps the
        /// editor update loop. Measured on 6000.6.5f1 it does not: a 9s player build left a 9.4s
        /// hole in EditorApplication.update, and every Progress API throws off the main thread
        /// ("GetCount can only be called from the main thread"), so a running build reports elapsed
        /// time only. Kept because it costs one throttled handler and is the only way a percentage
        /// can ever reach the CLI.
        /// </summary>
        static void SampleProgress()
        {
            var job = s_Job;
            if (job == null) return;

            var now = EditorApplication.timeSinceStartup;
            if (now - s_LastSampleTime < SAMPLE_INTERVAL_S) return;
            s_LastSampleTime = now;

            try
            {
                int items = 0;
                string name = null, description = null;
                float percent = -1f;

                foreach (var item in Progress.EnumerateItems())
                {
                    if (item.status != Progress.Status.Running) continue;
                    items++;
                    name = item.name;
                    description = item.description;
                    percent = item.progress;
                }

                if (items == 0) return;

                BuildHeartbeatSnapshot snapshot = null;
                BuildJobStore.Update(job, j =>
                {
                    j.progress_items = items;
                    j.progress_name = name;
                    j.progress_description = description;
                    j.progress_percent = percent >= 0f ? (double) percent : (double?) null;
                    snapshot = SnapshotOf(j);
                }, persist: false);

                Heartbeat.PublishBuild(snapshot);
            }
            catch (Exception ex)
            {
                // Progress is cosmetic here; a build must not be reported as failed because the
                // progress window was torn down underneath us.
                System.Diagnostics.Debug.WriteLine($"[UnityCliConnector] Build progress sample failed: {ex.Message}");
            }
        }

        static void StartBeater()
        {
            s_Beater = new Timer(_ => Beat(), null, BEAT_INTERVAL_MS, BEAT_INTERVAL_MS);
        }

        static void StopBeater()
        {
            var timer = s_Beater;
            s_Beater = null;
            timer?.Dispose();
        }

        /// <summary>
        /// Keeps the heartbeat beating while the main thread is inside BuildPlayer. Without it the
        /// CLI sees a stale timestamp, calls the editor unresponsive, and waiters time out — the
        /// exact wedge this feature is meant to make observable.
        /// </summary>
        static void Beat()
        {
            var job = s_Job;
            if (job == null) return;

            try
            {
                var elapsed = (NowMs() - Volatile.Read(ref s_StartedUtcMs)) / 1000.0;
                BuildHeartbeatSnapshot snapshot = null;

                // The heartbeat needs to stay hot every beat; the job file only needs to be current
                // enough for a waiter that reads it after a crash, so it is written less often.
                var persist = Interlocked.Increment(ref s_Beats) % 4 == 0;
                BuildJobStore.Update(job, j =>
                {
                    j.elapsed_seconds = elapsed;
                    snapshot = SnapshotOf(j);
                }, persist);

                Heartbeat.PublishBuild(snapshot);
                Heartbeat.WriteFromAnyThread();
            }
            catch (Exception ex)
            {
                // An exception escaping a timer thread takes the editor process with it.
                System.Diagnostics.Debug.WriteLine($"[UnityCliConnector] Build beat failed: {ex.Message}");
            }
        }

        static BuildHeartbeatSnapshot SnapshotOf(BuildJobRecord job) => new()
        {
            id = job.id,
            phase = job.phase,
            profile = job.profile,
            target = job.target,
            output = job.output,
            elapsed_seconds = Math.Round(job.elapsed_seconds, 1),
            progress_percent = job.progress_percent,
            progress_description = job.progress_description ?? job.progress_name,
        };

        /// <summary>
        /// Puts back the build profile that was active before this job. BuildPlayer(BuildPlayerOptions)
        /// applies the *active* profile, which is why the job sets one at all, and the profile decides
        /// what EditorBuildSettings.scenes reports (a profile may override that list), so leaving it
        /// switched would change the editor for every later command. Null means none was active.
        /// </summary>
        static void RestoreActiveProfile(BuildProfile previous)
        {
            try
            {
                if (ReferenceEquals(previous, ActiveProfile())) return;
                BuildProfile.SetActiveBuildProfile(previous);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityCliConnector] Build job left the active build profile switched: {ex.Message}");
            }
        }

        static BuildProfile ActiveProfile()
        {
            try
            {
                return BuildProfile.GetActiveBuildProfile();
            }
            catch (Exception)
            {
                return null;
            }
        }

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
