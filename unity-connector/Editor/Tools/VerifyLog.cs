using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    /// One JSON line per finished verify job in Logs/unity-cli-verify.log: when it started and ended,
    /// how many domains its steps ran in, and each step's time. A caller's wall time minus these is
    /// time spent outside the job (CLI re-attach after a reload, request latency). Test and filter
    /// names are left out: the log outlives the session and is read by tools that scan for them.
    internal static class VerifyLog
    {
        private const long MaxBytes = 1024 * 1024;

        internal static void Append(string job, long startedMs, int runs, JObject refresh, JObject result)
        {
            try
            {
                var tests = result["tests"]?["data"] as JObject;
                var line = new JObject
                {
                    ["job"] = job,
                    ["started_ms"] = startedMs,
                    ["finished_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["runs"] = runs,
                    ["refresh_nothing_to_compile"] = refresh?.Value<bool?>("nothing_to_compile"),
                    ["refresh_stale_assemblies"] = refresh?.Value<int?>("stale_assembly_count"),
                    ["compile"] = result["compile"],
                    ["timings_ms"] = result["timings_ms"],
                    ["tests_total"] = tests?.Value<int?>("total"),
                    ["stopped"] = result.Value<string>("stopped"),
                    ["error"] = result.Value<string>("error"),
                };
                var path = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Logs", "unity-cli-verify.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Delete(path);
                File.AppendAllText(path, line.ToString(Formatting.None) + "\n");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[unity-cli] verify log: " + e.Message);
            }
        }
    }
}
