using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;
using UnityCliConnector.BuildJobs;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// The build job record is read over HTTP while the main thread is inside BuildPlayer, and it is
    /// the only thing that describes an in-flight build across a domain reload. Both paths — the JSON
    /// shape the Go CLI parses, and the restore that flips a job the previous domain left mid-flight —
    /// are asserted here; no test starts a real build.
    /// </summary>
    [TestFixture]
    public class BuildJobTests
    {
        const string TempSceneFolder = "Assets/TempBuildJobTests";

        static BuildJobRecord Job(string phase) => new()
        {
            id = "build-20240101-000000-abc123",
            phase = phase,
            profile = "macOS_dev",
            profile_path = "Assets/Settings/Build Profiles/macOS_dev.asset",
            target = "StandaloneOSX",
            output = "/tmp/App.app",
            development = true,
            scenes = new[] { "Assets/A.unity", "Assets/B.unity" },
            scenes_source = "profile",
            queued_at_ms = 1,
            started_at_ms = 2,
            elapsed_seconds = 12.5,
            progress_items = 2,
            progress_name = "Compiling",
            progress_description = "Compiling scripts",
            progress_percent = 0.42,
        };

        [Test]
        public void Serialize_RoundTripsEveryFieldTheCLIParses()
        {
            var job = Job(BuildJobRecord.PhaseBuilding);
            job.result = "Succeeded";
            job.total_bytes = 142_000_000;
            job.total_seconds = 187.25;
            job.error_count = 3;
            job.warning_count = 7;
            job.errors = new[] { "step: boom", "step: bang" };
            job.build_report_profile = "Assets/Settings/Build Profiles/macOS_dev.asset";
            job.scene_count = 2;

            var restored = BuildJobStore.Deserialize(BuildJobStore.Serialize(new List<BuildJobRecord> { job }));

            Assert.AreEqual(1, restored.Count);
            var got = restored[0];
            Assert.AreEqual(job.id, got.id);
            Assert.AreEqual(BuildJobRecord.PhaseBuilding, got.phase);
            Assert.AreEqual(job.profile_path, got.profile_path);
            Assert.AreEqual(job.output, got.output);
            Assert.IsTrue(got.development);
            Assert.AreEqual(job.scenes, got.scenes);
            Assert.AreEqual(job.elapsed_seconds, got.elapsed_seconds);
            Assert.AreEqual(job.progress_percent, got.progress_percent);
            Assert.AreEqual(job.total_bytes, got.total_bytes);
            Assert.AreEqual(job.total_seconds, got.total_seconds);
            Assert.AreEqual(job.errors, got.errors);
            Assert.AreEqual(job.build_report_profile, got.build_report_profile);
            Assert.IsTrue(got.IsRunning, "a restored building job must still count as in flight");
        }

        [Test]
        public void Serialize_RoundTripsAnUnreportedPercentAsAbsentRatherThanZero()
        {
            // A 0 percent an indefinite build never reported would be printed as a real 0%.
            var job = Job(BuildJobRecord.PhaseBuilding);
            job.progress_percent = null;

            var json = JArray.Parse(BuildJobStore.Serialize(new List<BuildJobRecord> { job }))[0];
            Assert.AreEqual(JTokenType.Null, json["progress_percent"].Type);

            var restored = BuildJobStore.Deserialize(BuildJobStore.Serialize(new List<BuildJobRecord> { job }))[0];
            Assert.IsNull(restored.progress_percent);
        }

        [Test]
        public void MarkInterrupted_FlipsJobsThePreviousDomainLeftMidFlight()
        {
            var jobs = new List<BuildJobRecord>
            {
                Job(BuildJobRecord.PhaseQueued),
                Job(BuildJobRecord.PhaseBuilding),
                Job(BuildJobRecord.PhaseSucceeded),
            };
            jobs[2].finished_at_ms = 99;

            var flipped = BuildJobStore.MarkInterrupted(jobs, 5_000);

            Assert.AreEqual(2, flipped);
            Assert.AreEqual(BuildJobRecord.PhaseInterrupted, jobs[0].phase);
            Assert.AreEqual(BuildJobRecord.PhaseInterrupted, jobs[1].phase);
            Assert.AreEqual(BuildJobRecord.PhaseSucceeded, jobs[2].phase);
            Assert.AreEqual(99, jobs[2].finished_at_ms);
            Assert.AreEqual(5_000, jobs[1].finished_at_ms);
            Assert.IsFalse(jobs[1].IsRunning);
            Assert.That(jobs[1].message, Does.Contain("outcome is unknown"));
        }

        [Test]
        public void Clone_IsIndependentOfTheJobBeingMutated()
        {
            // build_status serializes a clone while the beater thread advances elapsed_seconds.
            var job = Job(BuildJobRecord.PhaseBuilding);
            var copy = job.Clone();
            job.elapsed_seconds = 99;
            job.scenes[0] = "Assets/Changed.unity";

            Assert.AreEqual(12.5, copy.elapsed_seconds);
            Assert.AreEqual("Assets/A.unity", copy.scenes[0]);
        }

        [Test]
        public void StorePath_LivesUnderTheProjectsTempFolder()
        {
            var path = BuildJobStore.FilePath();
            var projectRoot = Path.GetDirectoryName(Application.dataPath);

            Assert.IsNotNull(path);
            Assert.AreEqual(
                Path.Combine(projectRoot, "Temp", "unity-cli", "build-jobs.json").Replace('\\', '/'),
                path.Replace('\\', '/'));
            Assert.That(path, Does.Contain("/Temp/"), "build state belongs in Temp/, not in Assets/");
        }

        [Test]
        public void BuildStatusToolAnswersWhileTheMainThreadIsBuilding()
        {
            // The HTTP server only skips the main thread for tools carrying the attribute; without it
            // build_status would queue behind the build it is meant to report on.
            Assert.IsTrue(ToolDiscovery.IsOffMainThreadTool("build_status"));
            Assert.IsFalse(ToolDiscovery.IsOffMainThreadTool("build"));
        }

        [Test]
        public void TryCreate_RejectsUnknownProfileAndNamesTheAvailableOnes()
        {
            var ok = BuildJobRequest.TryCreate(Params(("profile", "no-such-profile"), ("output", "Temp/x.app")),
                out var job, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(job);
            Assert.That(error, Does.Contain("no-such-profile"));
            Assert.That(error, Does.Contain("Available:"));
            Assume.That(ProfileNames(), Has.Length.Positive);
            Assert.That(error, Does.Contain(ProfileNames()[0]));
        }

        [Test]
        public void TryCreate_RejectsProfileAndTargetTogether()
        {
            var ok = BuildJobRequest.TryCreate(
                Params(("profile", "macOS"), ("target", "StandaloneOSX"), ("output", "Temp/x.app")),
                out _, out var error);

            Assert.IsFalse(ok);
            Assert.That(error, Does.Contain("not both"));
        }

        [Test]
        public void TryCreate_RejectsMissingProfileOrTarget()
        {
            var ok = BuildJobRequest.TryCreate(Params(("output", "Temp/x.app")), out _, out var error);

            Assert.IsFalse(ok);
            Assert.That(error, Does.Contain("'profile'"));
            Assert.That(error, Does.Contain("'target'"));
        }

        [Test]
        public void TryCreate_RejectsMissingOutput()
        {
            var ok = BuildJobRequest.TryCreate(Params(("target", "StandaloneOSX")), out _, out var error);

            Assert.IsFalse(ok);
            Assert.That(error, Does.Contain("'output' is required"));
        }

        [Test]
        public void TryCreate_RejectsANonexistentTargetAndNamesRealOnes()
        {
            var ok = BuildJobRequest.TryCreate(
                Params(("target", "ToastSmasher"), ("output", "Temp/x.app")), out _, out var error);

            Assert.IsFalse(ok);
            Assert.That(error, Does.Contain("not a BuildTarget"));
            Assert.That(error, Does.Contain("StandaloneOSX"));
        }

        [Test]
        public void TryCreate_RejectsASceneThatDoesNotExist()
        {
            var ok = BuildJobRequest.TryCreate(
                Params(("target", "StandaloneOSX"), ("output", "Temp/x.app"), ("scenes", "Assets/Nope.unity")),
                out _, out var error);

            Assert.IsFalse(ok);
            Assert.That(error, Does.Contain("Scene not found"));
        }

        [Test]
        public void TryCreate_ResolvesAProjectRelativeOutput()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var scene = CreateTempScene();

            var ok = BuildJobRequest.TryCreate(
                Params(("target", "StandaloneOSX"), ("output", "Temp/CliBuild/App.app"),
                    ("scenes", scene)), out var job, out var error);

            Assert.IsTrue(ok, error ?? "accepted");
            Assert.AreEqual(Path.Combine(projectRoot, "Temp", "CliBuild", "App.app").Replace('\\', '/'),
                job.output.Replace('\\', '/'));
            Assert.AreEqual("param", job.scenes_source);
            Assert.AreEqual(1, job.scene_count);
            Assert.AreEqual(BuildJobRecord.PhaseQueued, job.phase);
            Assert.That(job.id, Does.StartWith("build-"));
            Assert.Greater(job.queued_at_ms, 0);
        }

        [Test]
        public void TryValidateScenes_RejectsScenesThatAreGoneFromTheProject()
        {
            // Build Settings keeps entries for deleted scenes; the build's own "Verify Build setup"
            // step answers them with "incorrect path for a scene file" minutes into a job.
            var ok = BuildJobRequest.TryValidateScenes(
                new[] { "Assets/_Remake assets/Scenes/Lobby.unity", "Assets/Gone.unity" },
                "build_settings", out var error);

            Assert.IsFalse(ok);
            Assert.That(error, Does.Contain("not buildable"));
            Assert.That(error, Does.Contain("File > Build Settings"));
            Assert.That(error, Does.Contain("Assets/Gone.unity"));
        }

        [Test]
        public void TryValidateScenes_AcceptsASceneThatIsOnDisk()
        {
            var scene = CreateTempScene();

            var ok = BuildJobRequest.TryValidateScenes(new[] { scene }, "param", out var error);

            Assert.IsTrue(ok, error ?? "accepted");
        }

        [Test]
        public void TryValidateScenes_RejectsAnAssetThatIsNotAScene()
        {
            // Any asset path passed here would only fail inside BuildPlayer, minutes later.
            Assume.That(ExistingAssetPath(), Is.Not.Null, "this project needs at least one non-scene asset");

            var ok = BuildJobRequest.TryValidateScenes(new[] { ExistingAssetPath() }, "param", out var error);

            Assert.IsFalse(ok, "a build profile is not a scene");
            Assert.That(error, Does.Contain("Scene not found"));
        }

        [TearDown]
        public void DeleteTempScenes()
        {
            if (AssetDatabase.IsValidFolder(TempSceneFolder)) AssetDatabase.DeleteAsset(TempSceneFolder);
        }

        [Test]
        public void TryValidateScenes_LeavesTheEmptyListToTheCaller()
        {
            Assert.IsTrue(BuildJobRequest.TryValidateScenes(new string[0], "build_settings", out var error));
            Assert.IsNull(error);
        }

        [Test]
        public void TryCreate_RefusesAnOutputInsideAssets()
        {
            // Unity's own refusal only surfaces after the build has run, minutes of main thread later.
            var ok = BuildJobRequest.TryCreate(
                Params(("target", "StandaloneOSX"), ("output", "Assets/Build/App.app")), out _, out var error);

            Assert.IsFalse(ok);
            Assert.That(error, Does.Contain("must be outside Assets/"));
        }

        [Test]
        public void ReadScenes_AcceptsAnArrayOrASeparatorList()
        {
            Assert.AreEqual(new[] { "A", "B" },
                BuildJobRequest.ReadScenes(JArray.FromObject(new[] { "A", "B" })));
            Assert.AreEqual(new[] { "A", "B" }, BuildJobRequest.ReadScenes(new JValue("A,B")));
            Assert.AreEqual(new[] { "A", "B" }, BuildJobRequest.ReadScenes(new JValue(" A ; B ")));
            Assert.AreEqual(Array.Empty<string>(), BuildJobRequest.ReadScenes(null));
        }

        // Written by hand because EditorSceneManager.NewScene(Additive) refuses while the editor's
        // untitled startup scene is unsaved, and this project ships no scenes to copy.
        const string MinimalScene = @"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!29 &1
OcclusionCullingSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_OcclusionBakeSettings:
    smallestOccluder: 5
    smallestHole: 0.25
    backfaceThreshold: 100
  m_SceneGUID: 00000000000000000000000000000000
  m_OcclusionCullingData: {fileID: 0}
";
        static string CreateTempScene()
        {
            if (!AssetDatabase.IsValidFolder(TempSceneFolder)) AssetDatabase.CreateFolder("Assets", "TempBuildJobTests");
            var path = TempSceneFolder + "/ValidationScene.unity";
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), path), MinimalScene);
            AssetDatabase.ImportAsset(path);
            return path;
        }

        /// <summary>Any non-scene asset whose file is really on disk.</summary>
        static string ExistingAssetPath()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:BuildProfile"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), path))) return path;
            }

            return null;
        }

        static string[] ProfileNames() => AssetDatabase
            .FindAssets("t:BuildProfile")
            .Select(guid => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid)))
            .ToArray();

        static ToolParams Params(params (string key, string value)[] pairs)
        {
            var obj = new JObject();
            foreach (var (key, value) in pairs) obj[key] = value;
            return new ToolParams(obj);
        }
    }
}
