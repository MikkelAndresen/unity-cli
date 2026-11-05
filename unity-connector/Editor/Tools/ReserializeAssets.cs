using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "reserialize", Description = "Force reserialize Unity assets. No params = entire project.")]
    public static class ReserializeAssets
    {
        public class Parameters
        {
            [ToolParameter("Single asset path to reserialize")]
            public string Path { get; set; }

            [ToolParameter("Multiple asset paths to reserialize")]
            public string[] Paths { get; set; }
        }

        public static object HandleCommand(JObject parameters)
        {
            var p = new ToolParams(parameters);
            var argsToken = p.GetRaw("args") as JArray;
            var pathToken = p.GetRaw("path");
            var pathsToken = p.GetRaw("paths");

            string[] paths;
            if (argsToken != null && argsToken.Count > 0)
                paths = argsToken.ToObject<string[]>();
            else if (pathsToken != null && pathsToken.Type == JTokenType.Array)
                paths = pathsToken.ToObject<string[]>();
            else if (pathToken != null)
                paths = new[] { pathToken.ToString() };
            else
                paths = null;

            if (paths == null || paths.Length == 0)
            {
                AssetDatabase.ForceReserializeAssets();
                Debug.Log("[UnityCliConnector] ForceReserializeAssets: entire project");
                return new SuccessResponse("Reserialized entire project");
            }

            // ForceReserializeAssets ignores a path that does not exist, so a typo answered exactly
            // like a success. Name every missing path before touching any of them, so a partial run
            // cannot make the answer ambiguous.
            var missing = new List<string>();
            foreach (var path in paths)
                if (!AssetExists(path))
                    missing.Add(path);
            if (missing.Count > 0)
            {
                return new ErrorResponse(
                    $"No asset at {(missing.Count == 1 ? "this path" : "these paths")}: {string.Join(", ", missing)}. " +
                    "Nothing was reserialized; check the path with 'unity-cli exec' + AssetDatabase or pass a folder.",
                    new { missing_paths = missing, paths });
            }

            AssetDatabase.ForceReserializeAssets(paths);
            Debug.Log($"[UnityCliConnector] ForceReserializeAssets: {string.Join(", ", paths)}");
            return new SuccessResponse($"Reserialized {paths.Length} asset(s)", new { paths });
        }

        /// Folders count: ForceReserializeAssets walks a folder path, and the CLI passes whatever the
        /// caller typed.
        internal static bool AssetExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var normalized = path.TrimEnd('/');
            return AssetDatabase.IsValidFolder(normalized) ||
                   AssetDatabase.LoadMainAssetAtPath(normalized) != null;
        }
    }
}
