using Newtonsoft.Json.Linq;
using UnityCliConnector.BuildJobs;

namespace UnityCliConnector.Tools
{
    /// <summary>
    /// Reports a build job. Off the main thread on purpose: while a build runs, the main thread is
    /// inside BuildPlayer, and this is the only command that can still answer about it. It touches
    /// nothing but BuildJobStore's thread-safe snapshots.
    /// </summary>
    [UnityCliTool(Name = "build_status",
        Description = "Report a build job: phase, elapsed time, progress, and the final BuildReport summary.",
        RunOffMainThread = true)]
    public static class BuildStatus
    {
        public class Parameters
        {
            [ToolParameter("Job id from build. Omit for the newest job (a running one when there is one).")]
            public string Id { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            var id = p.Get("id")
                     ?? (p.GetRaw("args") as JArray)?[0]?.ToString();

            var job = string.IsNullOrEmpty(id) || id == "latest"
                ? BuildJobStore.Latest()
                : BuildJobStore.Find(id);

            if (job == null)
                return new ErrorResponse(id == null || id == "latest"
                    ? "No build jobs in this editor session. Start one with 'unity-cli build --profile <name> --output <path>'."
                    : $"No build job with id '{id}'. 'unity-cli build status' reports the newest one.");

            var verb = job.IsRunning ? "running" : job.phase;
            return new SuccessResponse($"{job.id} {verb} ({job.elapsed_seconds:0.#}s)", job);
        }
    }
}
