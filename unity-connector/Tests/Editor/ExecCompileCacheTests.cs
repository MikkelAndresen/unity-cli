using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// Covers issue #33: every exec used to compile a fresh assembly and wait for csc on
    /// the main thread, so a session of hundreds of probes left hundreds of unloadable-
    /// forever assemblies in the domain and froze the editor for seconds at a time.
    /// </summary>
    [TestFixture]
    public class ExecCompileCacheTests
    {
        // Unique to this fixture so earlier fixtures' cache entries cannot answer for it.
        const string Snippet = "return 40 + 2;";

        [SetUp]
        public void SetUp()
        {
            ExecCompileCache.ClearForTests();
        }

        static async Task<SuccessResponse> Exec(string code, string usings = null)
        {
            var p = new JObject { ["code"] = code };
            if (usings != null) p["usings"] = usings;
            var response = await ExecuteCsharp.HandleCommand(p);
            if (response is ErrorResponse err)
                Assert.Fail($"expected success, got: {err.message}");
            return (SuccessResponse)response;
        }

        static JObject MetaOf(SuccessResponse response)
        {
            Assert.IsNotNull(response.meta, "exec must report how the snippet was compiled");
            return JObject.FromObject(response.meta);
        }

        static int AssemblyCount() => AppDomain.CurrentDomain.GetAssemblies().Length;

        [Test]
        public void KeyFor_SameSourceAndReferences_IsStable()
        {
            var a = ExecCompileCache.KeyFor("public class A {}", "csc.dll", "dotnet", new[] { "x.dll", "y.dll" });
            var b = ExecCompileCache.KeyFor("public class A {}", "csc.dll", "dotnet", new[] { "x.dll", "y.dll" });
            Assert.AreEqual(a, b);
        }

        [Test]
        public void KeyFor_AnyInputThatChangesTheIl_ChangesTheKey()
        {
            var baseline = ExecCompileCache.KeyFor("return 1;", "csc.dll", "dotnet", new[] { "x.dll" });

            Assert.AreNotEqual(baseline, ExecCompileCache.KeyFor("return 2;", "csc.dll", "dotnet", new[] { "x.dll" }),
                "a different body is a different assembly");
            Assert.AreNotEqual(baseline, ExecCompileCache.KeyFor("using System.Linq; return 1;", "csc.dll", "dotnet", new[] { "x.dll" }),
                "the generated source carries the usings, so an extra using is a different key");
            Assert.AreNotEqual(baseline, ExecCompileCache.KeyFor("return 1;", "csc.dll", "dotnet", new[] { "x.dll", "z.dll" }),
                "an added reference must not reuse an assembly compiled without it");
            Assert.AreNotEqual(baseline, ExecCompileCache.KeyFor("return 1;", "other-csc.dll", "dotnet", new[] { "x.dll" }),
                "a different compiler may produce different IL");
            Assert.AreNotEqual(baseline, ExecCompileCache.KeyFor("return 1;", "csc.dll", "other-dotnet", new[] { "x.dll" }),
                "the runtime that runs csc is part of the compile");
        }

        [Test]
        public void KeyFor_ReferenceOrder_IsIrrelevant()
        {
            var sorted = ExecCompileCache.KeyFor("return 1;", "csc.dll", "dotnet", new[] { "x.dll", "y.dll" });
            var shuffled = ExecCompileCache.KeyFor("return 1;", "csc.dll", "dotnet", new[] { "y.dll", "x.dll" });
            Assert.AreEqual(sorted, shuffled, "the domain's load order must not decide the key");
        }

        [Test]
        public void Put_BeyondTheBound_ForgetsTheOldestLookups()
        {
            var assembly = typeof(ExecCompileCacheTests).Assembly;
            var method = typeof(object).GetMethod("ToString", Type.EmptyTypes);

            for (var i = 0; i < ExecCompileCache.MaxEntries + 40; i++)
                ExecCompileCache.Put("key-" + i, new CompiledSnippet(assembly, method));

            Assert.AreEqual(ExecCompileCache.MaxEntries, ExecCompileCache.Count,
                "the bound is on lookups: Mono cannot unload an evicted snippet's assembly, so it stays in the domain either way");
            Assert.IsFalse(ExecCompileCache.TryGet("key-0", out _), "the oldest entry is the one dropped");
            Assert.IsTrue(ExecCompileCache.TryGet("key-" + (ExecCompileCache.MaxEntries + 39), out _));
        }

        [Test]
        public async Task SecondIdenticalExec_ReusesTheAssemblyAndLoadsNoOther()
        {
            var first = await Exec(Snippet);
            var afterFirst = AssemblyCount();
            Assert.AreEqual("compiled", MetaOf(first)["compile"].Value<string>());
            Assert.Greater(MetaOf(first)["compile_ms"].Value<long>(), 0, "the compile is what gets measured away");

            var second = await Exec(Snippet);
            Assert.AreEqual(42, second.data, "the cached path still runs the snippet");
            Assert.AreEqual("cached", MetaOf(second)["compile"].Value<string>());
            Assert.AreEqual(afterFirst, AssemblyCount(), "a repeat of the same snippet must load nothing");
        }

        [Test]
        public async Task ExtraUsings_ChangeTheKeySoTheSnippetCompilesAgain()
        {
            var without = await Exec("return Enumerable.Range(1, 3).Sum();", "System.Linq");
            Assert.AreEqual(6, without.data);
            Assert.AreEqual("compiled", MetaOf(without)["compile"].Value<string>());

            var withExtra = await Exec("return Enumerable.Range(1, 3).Sum();", "System.Linq,UnityEditorInternal");
            Assert.AreEqual(6, withExtra.data);
            Assert.AreEqual("compiled", MetaOf(withExtra)["compile"].Value<string>(),
                "the same body with different usings is a different compile, not a cache hit");
        }

        [Test]
        public async Task CompileError_ReportsTheDiagnosticsAndCachesNothing()
        {
            var p = new JObject { ["code"] = "return not_defined_at_all;" };
            var response = await ExecuteCsharp.HandleCommand(p);
            var err = response as ErrorResponse;
            Assert.IsNotNull(err, "a snippet that does not compile must not succeed");
            StringAssert.Contains("Compile error", err.message);

            var meta = JObject.FromObject(err.meta);
            Assert.AreEqual("failed", meta["compile"].Value<string>());
            Assert.AreEqual(0, ExecCompileCache.Count, "a snippet that does not compile must not be cached");
        }

        [Test]
        public async Task MainThread_IsTheEditorThreadInACommandHandler_OffItInsideTaskRun()
        {
            Assert.IsTrue(MainThread.IsCurrent, "the connector's tools run on the editor main thread");
            Assert.IsFalse(await Task.Run(() => MainThread.IsCurrent),
                "a thread-pool continuation is not on it, which is what exec's guard checks after csc");
        }
    }
}
