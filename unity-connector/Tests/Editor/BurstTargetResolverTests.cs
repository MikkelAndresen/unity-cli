using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// Which assembly declares each Burst target, and the on-disk cache that keeps that answer across
    /// the domain reload every compile causes.
    [TestFixture]
    public class BurstTargetResolverTests
    {
        public struct NestedProbeJob { }

        string _path;
        static Assembly Tests => typeof(BurstTargetResolverTests).Assembly;
        static Assembly Json => typeof(Newtonsoft.Json.JsonConvert).Assembly;
        static Dictionary<string, Assembly> ProjectIsTests => new() { [Tests.GetName().Name] = Tests };

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "burst-resolver-" + Path.GetRandomFileName() + ".json");
            BurstTargetResolver.PathOverride = _path;
            BurstTargetResolver.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            BurstTargetResolver.PathOverride = null;
            BurstTargetResolver.ResetForTests();
            if (File.Exists(_path)) File.Delete(_path);
        }

        static BurstTargetInfo ProjectTarget() => new()
        {
            // Burst spells nested types with '.'; the loaded name uses '+'.
            DeclaringType = "UnityCliConnector.Tests.BurstTargetResolverTests.NestedProbeJob",
            Scope = "UnityCliConnector.Tests.BurstTargetResolverTests",
        };

        static BurstTargetInfo PackageTarget() => new() { DeclaringType = "Newtonsoft.Json.JsonConvert" };

        JObject Cache() => JObject.Parse(File.ReadAllText(_path));

        [Test]
        public void ProjectTarget_ResolvesToItsAssembly_AndIsCached()
        {
            var target = ProjectTarget();
            BurstTargetResolver.Resolve(new[] { target }, ProjectIsTests, includePackages: false);

            Assert.AreEqual(Tests.GetName().Name, target.Assembly);
            Assert.AreEqual(typeof(NestedProbeJob).FullName, target.DeclaringType);
            StringAssert.Contains(Tests.GetName().Name, Cache()["types"].ToString());
        }

        [Test]
        public void PackageTarget_IsLeftUnresolved_UnlessPackagesAreAsked()
        {
            var target = PackageTarget();
            BurstTargetResolver.Resolve(new[] { target }, ProjectIsTests, includePackages: false);
            Assert.AreEqual("", target.Assembly, "outside the project assemblies and not asked for");

            var listed = PackageTarget();
            BurstTargetResolver.Resolve(new[] { listed }, ProjectIsTests, includePackages: true);
            Assert.AreEqual(Json.GetName().Name, listed.Assembly);
        }

        [Test]
        public void CachedAnswers_SurviveANewDomain()
        {
            BurstTargetResolver.Resolve(new[] { ProjectTarget(), PackageTarget() }, ProjectIsTests, includePackages: false);
            BurstTargetResolver.ResetForTests(); // what a domain reload does to the static

            var project = ProjectTarget();
            var package = PackageTarget();
            BurstTargetResolver.Resolve(new[] { project, package }, ProjectIsTests, includePackages: false);

            Assert.AreEqual(Tests.GetName().Name, project.Assembly);
            Assert.AreEqual("", package.Assembly);
        }

        [Test]
        public void StaleCachedAssembly_IsResolvedAgain()
        {
            BurstTargetResolver.Resolve(new[] { ProjectTarget() }, ProjectIsTests, includePackages: false);
            var cache = Cache();
            var key = ((JObject)cache["types"]).Properties().GetEnumerator();
            key.MoveNext();
            cache["types"][key.Current.Name] = Json.GetName().Name; // the type "moved"
            File.WriteAllText(_path, cache.ToString());
            BurstTargetResolver.ResetForTests();

            var target = ProjectTarget();
            BurstTargetResolver.Resolve(new[] { target }, ProjectIsTests, includePackages: false);
            Assert.AreEqual(Tests.GetName().Name, target.Assembly);
        }

        [Test]
        public void NotInProjectAnswers_AreDropped_WhenTheProjectAssembliesChange()
        {
            BurstTargetResolver.Resolve(new[] { PackageTarget() }, ProjectIsTests, includePackages: false);
            BurstTargetResolver.ResetForTests();

            // The package became one the project develops (switched to file:).
            var now = new Dictionary<string, Assembly>(ProjectIsTests) { [Json.GetName().Name] = Json };
            var target = PackageTarget();
            BurstTargetResolver.Resolve(new[] { target }, now, includePackages: false);
            Assert.AreEqual(Json.GetName().Name, target.Assembly);
        }
    }
}
