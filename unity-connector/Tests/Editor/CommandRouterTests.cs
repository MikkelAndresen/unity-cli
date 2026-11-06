using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;

namespace UnityCliConnector.Tests
{
    /// A misspelled flag used to reach the handler as an unused key: `test --filtre X` ran the whole
    /// suite and answered as if the filter had applied. The router now refuses a parameter the tool's
    /// schema does not declare, so the failure is loud and names the parameters the tool does take.
    [TestFixture]
    public class CommandRouterTests
    {
        // Validated directly rather than through CommandRouter.Dispatch: the test runner itself is a
        // dispatched command and holds the router's lock for the whole EditMode run, so dispatching
        // from inside a test would deadlock the editor.
        static JObject Dispatch(string command, JObject parameters)
        {
            var tool = ToolDiscovery.FindTool(command);
            var result = CommandRouter.UnknownParameters(tool, parameters);
            if (result == null) return new JObject { ["success"] = true };
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }

        static JObject Params(params (string Key, object Value)[] entries)
        {
            var p = new JObject();
            foreach (var (key, value) in entries)
                p[key] = JToken.FromObject(value);
            return p;
        }

        [Test]
        public void UnknownParameter_IsRefusedWithTheCodeTheCLIKnows()
        {
            var json = Dispatch("api", Params(("args", new[] { "Vector3" }), ("bogus", true)));

            Assert.IsFalse(json["success"].Value<bool>());
            Assert.AreEqual(ErrorCodes.UnknownParameter, json["data"]["code"].Value<string>());
            Assert.AreEqual("api", json["data"]["command"].Value<string>());
            CollectionAssert.AreEqual(new[] { "bogus" },
                json["data"]["unknown_parameters"].ToObject<string[]>());
            StringAssert.Contains("unknown parameter --bogus", json["message"].Value<string>());
            StringAssert.Contains("known parameters:", json["message"].Value<string>());
        }

        [Test]
        public void UnknownParameter_ListsTheDeclaredOnesSoTheCallerCanFixTheSpelling()
        {
            var json = Dispatch("api", Params(("args", new[] { "Vector3" }), ("limt", 3)));
            var known = json["data"]["known_parameters"].ToObject<string[]>();

            CollectionAssert.Contains(known, "limit");
            CollectionAssert.Contains(known, "query");
            StringAssert.Contains("limit", json["message"].Value<string>());
        }

        [Test]
        public void ReservedParameters_AreAlwaysAllowed()
        {
            // The CLI injects `args`; HttpServer reads `queue_deadline_ms`/`deadline_ms` before
            // dispatch; CompilationGate owns `skip_compile_check`. None of them is a tool's to declare.
            var json = Dispatch("api", Params(
                ("args", new[] { "Vector3.Lerp" }),
                ("queue_deadline_ms", 5000),
                ("deadline_ms", 6000),
                ("skip_compile_check", true),
                ("limit", 1)));

            Assert.IsTrue(json["success"].Value<bool>(), json["message"]?.Value<string>());
        }

        [Test]
        public void CamelCaseSpelling_StillReachesToolsThatReadItAsAnAlias()
        {
            // Every tool also reads the older camelCase spelling of its parameters, and the schema
            // has to accept it or a working invocation would start failing.
            var json = Dispatch("api", Params(("args", new[] { "Vector3" }), ("maxTypes", 1)));

            Assert.IsTrue(json["success"].Value<bool>(), json["message"]?.Value<string>());
        }

        [Test]
        public void LegacyAlias_DeclaredOnTheParameter_IsAccepted()
        {
            // `compile_status` still reads `scan_budget_ms`; the alias is part of its schema.
            var json = Dispatch("compile_status", Params(("scan_budget_ms", 100)));

            Assert.IsTrue(json["success"].Value<bool>(), json["message"]?.Value<string>());
        }

        [Test]
        public void DeclaredParameterNames_CoverBothSpellingsAndAliases()
        {
            var names = ToolDiscovery.DeclaredParameterNames(
                typeof(UnityCliConnector.Tools.CompileStatus.Parameters));

            CollectionAssert.Contains(names, "scan_budget");
            CollectionAssert.Contains(names, "scanBudget");
            CollectionAssert.Contains(names, "scan_budget_ms");
        }

        [Test]
        public void Tools_AreScannedOncePerDomain()
        {
            // The scan walked every loaded assembly's types on the main thread per request, which cost
            // ~0.6-0.9 s of the latency of every single command.
            Assert.AreSame(ToolDiscovery.Tools(), ToolDiscovery.Tools());
            Assert.IsTrue(ToolDiscovery.Tools().Values.Any(t => t.Name == "exec"));
        }

        [Test]
        public void SchemaLessTools_KeepAcceptingAnything()
        {
            CollectionAssert.IsEmpty(ToolDiscovery.DeclaredParameterNames(null));
        }
    }
}
