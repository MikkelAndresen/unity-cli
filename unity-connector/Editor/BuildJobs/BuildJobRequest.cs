using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace UnityCliConnector.BuildJobs
{
    /// <summary>
    /// Turns build-tool parameters into a job record, refusing anything the editor would only
    /// fail on minutes later, inside a build that blocks the main thread.
    /// </summary>
    internal static class BuildJobRequest
    {
        const string DefaultProfileFolder = "Assets/Settings/Build Profiles";

        /// <summary>
        /// Reads and validates the parameters on the main thread. On failure, error explains the
        /// fix and names the options that would work.
        /// </summary>
        public static bool TryCreate(ToolParams p, out BuildJobRecord job, out string error)
        {
            job = null;
            error = null;

            var profile = p.Get("profile")?.Trim();
            var targetText = p.Get("target")?.Trim();
            var output = p.Get("output")?.Trim();
            var development = p.GetBool("development");
            var scenesParam = ReadScenes(p.GetRaw("scenes"));

            if (!string.IsNullOrEmpty(profile) && !string.IsNullOrEmpty(targetText))
            {
                error = "Set either 'profile' (a BuildProfile asset) or 'target' (a BuildTarget), not both.";
                return false;
            }
            if (string.IsNullOrEmpty(profile) && string.IsNullOrEmpty(targetText))
            {
                error = "'profile' (build profile name or asset path) or 'target' " +
                        "(e.g. StandaloneOSX, StandaloneWindows64, StandaloneLinux64) is required.";
                return false;
            }
            if (string.IsNullOrEmpty(output))
            {
                error = "'output' is required: where the player goes, absolute or relative to the project root " +
                        "(e.g. Builds/MacOS/App.app).";
                return false;
            }

            if (!TryResolveOutput(output, out var outputPath, out error)) return false;

            var scenes = RequestedScenes(scenesParam);
            var scenesSource = scenes.Length > 0 ? "param" : null;

            BuildProfile buildProfile = null;
            BuildTarget target;

            if (!string.IsNullOrEmpty(profile))
            {
                if (!TryResolveProfile(profile, out buildProfile, out error)) return false;

                var profileTarget = ProfileTarget(buildProfile);
                if (profileTarget.HasValue && profileTarget.Value != EditorUserBuildSettings.activeBuildTarget)
                {
                    error = $"'{buildProfile.name}' targets {profileTarget.Value}, but the editor's active build " +
                            $"target is {EditorUserBuildSettings.activeBuildTarget}. Switch the editor's target " +
                            "(File > Build Settings) or pick a profile for the active target.";
                    return false;
                }

                target = profileTarget ?? EditorUserBuildSettings.activeBuildTarget;

                if (scenes.Length == 0)
                {
                    scenes = ScenePaths(buildProfile.GetScenesForBuild());
                    if (scenes.Length > 0) scenesSource = "profile";
                }
            }
            else
            {
                if (!TryResolveTarget(targetText, out target, out error)) return false;
            }

            if (scenes.Length == 0)
            {
                scenes = ScenePaths(EditorBuildSettings.scenes.Where(s => s.enabled).ToArray());
                scenesSource = "build_settings";
            }
            if (scenes.Length == 0)
            {
                error = "No scenes to build. Pass 'scenes' or enable scenes in File > Build Settings " +
                        "(a build profile whose own scene list is empty falls back to those).";
                return false;
            }
            if (!TryValidateScenes(scenes, scenesSource, out error)) return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            job = new BuildJobRecord
            {
                id = NewId(),
                phase = BuildJobRecord.PhaseQueued,
                profile = buildProfile != null ? buildProfile.name : null,
                profile_path = buildProfile != null ? AssetDatabase.GetAssetPath(buildProfile) : null,
                target = target.ToString(),
                output = outputPath,
                development = development,
                scenes = scenes,
                scenes_source = scenesSource,
                scene_count = scenes.Length,
                queued_at_ms = now,
                updated_at_ms = now,
            };
            return true;
        }

        static string NewId()
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            return $"build-{stamp}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
        }

        /// <summary>
        /// Absolute output path. Relative paths resolve against the project root, which is what the
        /// CLI documents, and Assets/ is refused because Unity rejects a player output there only
        /// after the whole build has run.
        /// </summary>
        internal static bool TryResolveOutput(string output, out string absolute, out string error)
        {
            absolute = null;
            error = null;

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectRoot))
            {
                error = "Cannot resolve the project root; pass an absolute 'output' path.";
                return false;
            }

            absolute = Path.GetFullPath(Path.IsPathRooted(output) ? output : Path.Combine(projectRoot, output));

            var assetsRoot = Path.GetFullPath(Path.Combine(projectRoot, "Assets"));
            if (absolute == assetsRoot ||
                absolute.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                error = $"'output' must be outside Assets/ (Unity refuses to build a player there): {absolute}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Scenes in the order Unity builds them: explicit parameter, else the profile's list, else
        /// the enabled scenes in Build Settings. The chosen list is then validated as a whole.
        /// </summary>
        static string[] RequestedScenes(string[] requested) => requested ?? Array.Empty<string>();

        /// <summary>
        /// Every scene the build will be handed, whatever its source, must still be an asset and still
        /// be on disk. Build Settings keeps entries for scenes that were deleted, and the build's own
        /// "Verify Build setup" step then fails with a misleading "incorrect path for a scene file"
        /// after the job has been accepted.
        /// </summary>
        internal static bool TryValidateScenes(string[] scenes, string source, out string error)
        {
            error = null;
            if (scenes == null || scenes.Length == 0) return true;

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var missing = scenes.Where(scene => !SceneExists(scene, projectRoot)).ToArray();
            if (missing.Length == 0) return true;

            var listed = string.Join(", ", missing.Take(3));
            if (missing.Length > 3) listed += $", … ({missing.Length} total)";

            error = source == "param"
                ? $"Scene not found: {listed}. Pass AssetDatabase paths that exist, e.g. Assets/Scenes/Main.unity."
                : $"The scenes from {source} are not buildable: {listed} " +
                  (missing.Length == 1 ? "is" : "are") + " missing from the project. " +
                  "Pick them again in File > Build Settings, pass 'scenes', or use a profile that lists them.";
            return false;
        }

        static bool SceneExists(string scene, string projectRoot)
        {
            if (string.IsNullOrEmpty(scene)) return false;
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scene) == null) return false;
            return string.IsNullOrEmpty(projectRoot) ||
                   File.Exists(Path.GetFullPath(Path.Combine(projectRoot, scene)));
        }

        static bool TryResolveProfile(string profile, out BuildProfile asset, out string error)
        {
            error = null;
            asset = null;

            var path = profile.Contains('/') || profile.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                ? profile
                : $"{DefaultProfileFolder}/{profile}.asset";
            path = path.Replace('\\', '/');

            asset = AssetDatabase.LoadAssetAtPath<BuildProfile>(path) ?? BuildProfile.GetBuildProfileAtPath(path);
            if (asset != null) return true;

            // A name outside the default folder still resolves, because profiles are commonly moved
            // under Assets/<company>/Build Profiles.
            foreach (var guid in AssetDatabase.FindAssets("t:BuildProfile"))
            {
                var candidatePath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(Path.GetFileNameWithoutExtension(candidatePath), profile,
                        StringComparison.OrdinalIgnoreCase)) continue;

                asset = AssetDatabase.LoadAssetAtPath<BuildProfile>(candidatePath);
                if (asset != null) return true;
            }

            error = $"Build profile '{profile}' not found. Available: {ProfileNames()}";
            return false;
        }

        static string ProfileNames()
        {
            var names = AssetDatabase.FindAssets("t:BuildProfile")
                .Select(guid => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid)))
                .Distinct();
            var listed = string.Join(", ", names);
            return string.IsNullOrEmpty(listed) ? "none (create one in File > Build Profiles)" : listed;
        }

        internal static bool TryResolveTarget(string text, out BuildTarget target, out string error)
        {
            error = null;
            if (!Enum.TryParse(text, true, out target))
            {
                error = $"'{text}' is not a BuildTarget. Examples: StandaloneOSX, StandaloneWindows64, " +
                        "StandaloneLinux64, iOS, Android.";
                return false;
            }

            if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
            {
                error = $"Build target '{target}' is not supported by this editor installation — its module is " +
                        "not installed (File > Build Settings shows what is).";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The profile's own platform. BuildProfile exposes no public target getter, and building for
        /// a profile whose platform is not the active one makes Unity want to switch platforms inside
        /// the build call, so the internal property is read and checked before the job is accepted.
        /// Null means a Unity version without that property: assume the active target, which is what
        /// the build would use anyway.
        /// </summary>
        static BuildTarget? ProfileTarget(BuildProfile profile)
        {
            var property = typeof(BuildProfile)
                .GetProperty("buildTarget", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(profile) as BuildTarget?;
        }

        static string[] ScenePaths(UnityEditor.EditorBuildSettingsScene[] scenes)
        {
            if (scenes == null) return Array.Empty<string>();
            return scenes.Select(s => s.path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        }

        internal static string[] ReadScenes(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return Array.Empty<string>();

            if (token is JArray array)
                return array.Select(v => v.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToArray();

            return token.ToString()
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }
    }
}
