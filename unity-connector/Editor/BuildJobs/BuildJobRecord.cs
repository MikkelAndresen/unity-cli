using System;
using Newtonsoft.Json;

namespace UnityCliConnector.BuildJobs
{
    /// <summary>
    /// One player-build job. The same instance is read by the HTTP request thread and the
    /// heartbeat beater while the main thread runs the build, so mutations happen only through
    /// BuildJobStore (under its lock) and readers get a Clone. Field names are the JSON contract
    /// the Go CLI's build progress reads, and the record is what survives a domain reload.
    /// </summary>
    public sealed class BuildJobRecord
    {
        public const string PhaseQueued = "queued";
        public const string PhaseBuilding = "building";
        public const string PhaseSucceeded = "succeeded";
        public const string PhaseFailed = "failed";
        public const string PhaseCancelled = "cancelled";
        public const string PhaseInterrupted = "interrupted";

        public string id;
        public string phase;

        public string profile;
        public string profile_path;
        public string target;
        public string output;
        public bool development;
        public string[] scenes = Array.Empty<string>();
        public string scenes_source;
        public string message;

        public long queued_at_ms;
        public long started_at_ms;
        public long finished_at_ms;
        public long updated_at_ms;

        // Written while the build runs. elapsed_seconds comes from the background beater (wall
        // clock only — Unity's Progress API is main-thread only, see BuildJobRunner), and the
        // progress fields are filled by the main thread when the build pumps the editor loop.
        public double elapsed_seconds;
        public int progress_items;
        public string progress_name;
        public string progress_description;
        public double? progress_percent;

        // From the BuildReport once the build returns.
        public string result;
        public long total_bytes;
        public double total_seconds;
        public int error_count;
        public int warning_count;
        public string[] errors = Array.Empty<string>();
        public string build_report_profile;
        public int scene_count;

        // Not serialized: the payload's contract is its snake_case fields, and phase already says
        // whether a job is in flight.
        [JsonIgnore] public bool IsRunning => phase == PhaseQueued || phase == PhaseBuilding;

        /// <summary>
        /// A snapshot the request thread can serialize while the build thread keeps going. The arrays
        /// are copied as well: a caller that holds a snapshot must never see it change underneath.
        /// </summary>
        public BuildJobRecord Clone()
        {
            var copy = (BuildJobRecord) MemberwiseClone();
            copy.scenes = scenes == null ? Array.Empty<string>() : (string[]) scenes.Clone();
            copy.errors = errors == null ? Array.Empty<string>() : (string[]) errors.Clone();
            return copy;
        }
    }
}
