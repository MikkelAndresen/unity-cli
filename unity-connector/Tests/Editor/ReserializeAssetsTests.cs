using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// `reserialize Assets/E2E/Nope.asset` used to echo the path back as if it had been reserialized:
    /// ForceReserializeAssets ignores a path that does not exist, so a typo was indistinguishable from
    /// success.
    [TestFixture]
    public class ReserializeAssetsTests
    {
        static JObject Run(params object[] args)
        {
            var p = new JObject { ["args"] = JArray.FromObject(args) };
            return JObject.Parse(JsonConvert.SerializeObject(ReserializeAssets.HandleCommand(p)));
        }

        [Test]
        public void MissingPath_IsAnErrorThatNamesIt()
        {
            var json = Run("Assets/NoSuchFolderForReserializeTests/Nope.asset");

            Assert.IsFalse(json["success"].Value<bool>(), "a missing path must not read as success");
            StringAssert.Contains("NoSuchFolderForReserializeTests/Nope.asset", json["message"].Value<string>());
            CollectionAssert.AreEqual(new[] { "Assets/NoSuchFolderForReserializeTests/Nope.asset" },
                json["data"]["missing_paths"].ToObject<string[]>());
        }

        [Test]
        public void EveryMissingPath_IsListed_SoOneRetryFixesThemAll()
        {
            var json = Run("Assets/Missing1.asset", "Assets/Missing2.asset");

            var missing = json["data"]["missing_paths"].ToObject<string[]>();
            CollectionAssert.AreEqual(new[] { "Assets/Missing1.asset", "Assets/Missing2.asset" }, missing);
        }

        [Test]
        public void AssetExists_AcceptsAFolderAndAResolvableFile()
        {
            // A folder is a legitimate argument (ForceReserializeAssets walks it), so the check cannot
            // be a plain asset load. The package root is used rather than an Assets/ path so the test
            // works wherever the connector is installed from.
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ReserializeAssetsTests).Assembly);
            Assert.IsNotNull(package, "the connector is a package in every supported install");
            Assert.IsTrue(ReserializeAssets.AssetExists(package.assetPath), "a package folder path");
            Assert.IsTrue(ReserializeAssets.AssetExists(package.assetPath + "/Editor/Tools/ReserializeAssets.cs"),
                "a single asset file is the normal argument");
        }

        [Test]
        public void AssetExists_RefusesANameThatIsNotThere()
        {
            Assert.IsFalse(ReserializeAssets.AssetExists("Assets/NoSuchAssetForReserializeTests.asset"));
        }
    }
}
