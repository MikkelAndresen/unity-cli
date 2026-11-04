using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// The `api` tool's contract: how a query is read, and what comes back.
    [TestFixture]
    public class ApiLookupTests
    {
        private static JObject Run(string query, params (string Key, object Value)[] flags)
        {
            var p = new JObject { ["args"] = new JArray(query) };
            foreach (var (key, value) in flags)
                p[key] = JToken.FromObject(value);
            var response = JObject.FromObject(ApiLookup.HandleCommand(p));
            Assert.IsTrue(response["success"].Value<bool>(),
                $"`api {query}` answered with an error: {response["message"]?.Value<string>()}");
            return response["data"] as JObject;
        }

        [Test]
        public void MemberQuery_ListsEveryOverloadAsCSharp()
        {
            var data = Run("Vector3.Lerp", ("limit", 10));
            Assert.AreEqual("type.member", data["matched_by"].Value<string>());
            Assert.AreEqual("Lerp", data["member"].Value<string>());

            var vector = ((JArray)data["types"]).First((JToken t) => t["type"].Value<string>() == "UnityEngine.Vector3");
            var signatures = ((JArray)vector["members"])
                .Select((JToken m) => m["signature"].Value<string>())
                .ToList();
            Assert.GreaterOrEqual(signatures.Count, 2, "Vector3 has both a by-value and an in-parameter Lerp");
            Assert.IsTrue(signatures.Any((string s) => s == "public static Vector3 Lerp(Vector3 a, Vector3 b, float t)"),
                string.Join(" | ", signatures));
            Assert.AreEqual("UnityEngine", vector["namespace"].Value<string>());
            Assert.AreEqual("UnityEngine.CoreModule", vector["assembly"].Value<string>());
            Assert.AreEqual("struct", vector["kind"].Value<string>());
        }

        [Test]
        public void TypeQuery_ReportsShapeAndMemberCountsNotEveryMember()
        {
            var data = Run("UnityEngine.Vector3", ("limit", 3));
            var type = (JObject)((JArray)data["types"])[0];
            Assert.AreEqual("type", data["matched_by"].Value<string>(), "the query names this type exactly");
            Assert.AreEqual("UnityEngine.Vector3", type["type"].Value<string>());
            Assert.AreEqual("ValueType", type["base_type"].Value<string>());
            Assert.IsTrue(((JArray)type["interfaces"])
                .Any((JToken i) => i.Value<string>().StartsWith("IEquatable<Vector3>")));

            var counts = (JObject)type["counts"];
            Assert.Greater(counts["methods"].Value<int>(), 10);
            Assert.AreEqual(3, type["members_listed"].Value<int>(), "--limit caps what is listed");
            Assert.AreEqual(3, ((JArray)type["members"]).Count);
            Assert.Greater(type["member_total"].Value<int>(), 3, "and the total says what was left out");
        }

        [Test]
        public void GenericSpellings_AllResolveToTheSameType()
        {
            foreach (var query in new[] { "List", "List<>", "List`1", "System.Collections.Generic.List<T>" })
            {
                var data = Run(query, ("limit", 3));
                var names = ((JArray)data["types"])
                    .Select((JToken t) => t["type"].Value<string>())
                    .ToList();
                Assert.IsTrue(names.Contains("System.Collections.Generic.List<T>"),
                    $"'{query}' should resolve to List<T>; got {string.Join(", ", names)}");
            }
        }

        [Test]
        public void MemberOnlyQuery_FindsTheTypesThatDeclareIt()
        {
            var data = Run("isCompiling", ("limit", 5));
            Assert.AreEqual("member", data["matched_by"].Value<string>());
            Assert.IsTrue(((JArray)data["types"])
                .Any((JToken t) => t["type"].Value<string>() == "UnityEditor.EditorApplication"));
        }

        [Test]
        public void ObsoleteMember_ReportsMessageErrorFlagAndReplacement()
        {
            var data = Run("UnityEngine.Object.GetInstanceID");
            var type = (JObject)((JArray)data["types"])[0];
            var member = ((JArray)type["members"])
                .Single((JToken m) => m["name"].Value<string>() == "GetInstanceID");
            var obsolete = (JObject)member["obsolete"];
            Assert.IsNotNull(obsolete, "GetInstanceID is obsolete-error in Unity 6; if that changed, review the hint codes.");
            Assert.IsTrue(obsolete["is_error"].Value<bool>());
            Assert.IsNotEmpty(obsolete["message"].Value<string>());
            Assert.AreEqual("GetEntityId", obsolete["replacement"].Value<string>());
        }

        [Test]
        public void AssemblyFilter_KeepsOnlyThoseAssemblies()
        {
            var data = Run("Vector3.Lerp", ("assembly", "System.Numerics"), ("limit", 5));
            var types = (JArray)data["types"];
            Assert.IsNotEmpty(types);
            foreach (var type in types)
                StringAssert.Contains("System.Numerics", type["assembly"].Value<string>());
        }

        [Test]
        public void AssemblyFilter_IsAppliedBeforeTheTypeLimit()
        {
            ApiIndex.EnsureIndexed(int.MaxValue);
            var data = Run("Vector3", ("assembly", "System.Numerics"), ("max_types", 1));
            var types = (JArray)data["types"];
            Assert.IsNotEmpty(types, "--max_types must not truncate away the only assembly the filter allows");
            Assert.AreEqual("System.Numerics.Vector3", types[0]["type"].Value<string>());
        }

        [Test]
        public void Docs_AreOffByDefaultAndComeFromTheXmlFileWhenAsked()
        {
            var withoutDocs = (JObject)((JArray)Run("UnityEngine.Vector3")["types"])[0];
            Assert.AreEqual(JTokenType.Null, withoutDocs["docs"].Type,
                "--docs is opt-in: the lookup stays out of the doc file otherwise");

            var withDocs = (JObject)((JArray)Run("UnityEngine.Vector3", ("docs", true))["types"])[0];
            Assert.IsNotEmpty(withDocs["docs"].Value<string>(),
                "UnityEngine.CoreModule.xml sits next to the assembly, so --docs has an answer");

            var member = ((JArray)withDocs["members"]).First((JToken m) => m["name"].Value<string>() == "Lerp");
            Assert.IsNotEmpty(member["docs"].Value<string>(), "the doc file carries a summary for Vector3.Lerp");
        }

        [Test]
        public void UnknownQuery_SaysSoInsteadOfGuessing()
        {
            var data = Run("NoSuchTypeAnywhereInTheDomain");
            Assert.AreEqual(0, data["matches"].Value<int>());
            Assert.AreEqual("closest_types", data["matched_by"].Value<string>());
            Assert.IsEmpty((JArray)data["types"]);
        }

        [Test]
        public void MissingQuery_IsAnError()
        {
            var response = (ErrorResponse)ApiLookup.HandleCommand(new JObject());
            Assert.IsFalse(response.success);
            StringAssert.Contains("needs a type", response.message);
        }
    }
}
