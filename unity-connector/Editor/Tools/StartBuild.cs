using Newtonsoft.Json.Linq;
using UnityCliConnector.BuildJobs;

namespace UnityCliConnector.Tools
{
    /// <summary>
    /// Starts a player build and returns immediately. BuildPipeline.BuildPlayer blocks the main
    /// thread for the whole build, so the actual build runs on a later editor update tick, after
    /// this handler has released the command lock.
    /// </summary>
    [UnityCliTool(Name = "build",
        Description = "Queue a player build and return its job id immediately; poll it with build_status.")]
    public static class StartBuild
    {
        public class Parameters
        {
            [ToolParameter("BuildProfile name or asset path (e.g. macOS_dev). Mutually exclusive with target.")]
            public string Profile { get; set; }

            [ToolParameter("BuildTarget (StandaloneOSX, StandaloneWindows64, StandaloneLinux64, …). " +
                           "Mutually exclusive with profile.")]
            public string Target { get; set; }

            [ToolParameter("Output path for the player, absolute or relative to the project root. Required.",
                Required = true)]
            public string Output { get; set; }

            [ToolParameter("Scenes to include, comma-separated AssetDatabase paths. Defaults to the profile's " +
                           "scenes, else the enabled scenes in Build Settings.")]
            public string Scenes { get; set; }

            [ToolParameter("Set BuildOptions.Development.")]
            public bool Development { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var running = BuildJobStore.Running();
            if (running != null)
                return new ErrorResponse(
                    $"A build job is already {running.phase}: {running.id}. " +
                    $"Wait for it with 'unity-cli build status {running.id}' before starting another.");

            var p = new ToolParams(@params ?? new JObject());
            if (!BuildJobRequest.TryCreate(p, out var job, out var error))
                return new ErrorResponse(error);

            BuildJobStore.Add(job);
            BuildJobRunner.Schedule(job);

            return new SuccessResponse($"Build job {job.id} queued.", job.Clone());
        }
    }
}
