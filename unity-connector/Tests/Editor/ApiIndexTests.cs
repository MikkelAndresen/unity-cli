using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityCliConnector;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tests
{
    /// The lazy index the `api` tool and the compile hints share: what it can see, what it leaves
    /// out, and how bounded scans behave.
    [TestFixture]
    public class ApiIndexTests
    {
        [Test]
        public void EnsureIndexed_WithEnoughBudget_CoversTheWholeDomain()
        {
            var stats = ApiIndex.EnsureIndexed(5000);
            Assert.IsTrue(stats.Complete, $"a 5 s scan should finish the domain; got {stats}");
            Assert.Greater(stats.TypesIndexed, 10000, "the editor loads Unity plus the test framework");
            Assert.Greater(stats.AssembliesIndexed, 20);
        }

        [Test]
        public void EnsureIndexed_SpentBudget_KeepsWhatWasAlreadyIndexed()
        {
            var warmed = ApiIndex.EnsureIndexed(5000);
            var again = ApiIndex.EnsureIndexed(0);
            Assert.AreEqual(warmed.TypesIndexed, again.TypesIndexed, "a spent budget must not lose work");
            Assert.IsTrue(again.Complete, "everything eligible is already indexed");
        }

        [Test]
        public void FindTypes_RanksUnitysOwnTypeFirst()
        {
            ApiIndex.EnsureIndexed(5000);
            var found = ApiIndex.FindTypes("Vector3", 5);
            Assert.AreNotEqual(0, found.Count);
            Assert.AreEqual(typeof(Vector3), found[0], "UnityEngine.CoreModule leads the lookup order");
        }

        [Test]
        public void FindTypes_ListsEveryTypeTheQueryNames()
        {
            ApiIndex.EnsureIndexed(5000);
            var found = ApiIndex.FindTypes("List", 10);
            Assert.IsTrue(found.Any(t => t == typeof(System.Collections.Generic.List<>)),
                "the open generic is what a type query names");
            Assert.Greater(found.Count, 1, "several types share the name, and all of them are answers");
        }

        [Test]
        public void FindTypes_ExactOnlyMode_RefusesTypoNeighbours()
        {
            ApiIndex.EnsureIndexed(5000);
            Assert.AreEqual(0, ApiIndex.FindTypes("Vectr3", 5, 0).Count,
                "nothing is named Vectr3, so an exact lookup finds nothing");
            Assert.IsTrue(ApiIndex.FindTypes("Vectr3", 5).Any(t => t == typeof(Vector3)),
                "the loose lookup is the one that suggests the typo's neighbour");
        }

        [Test]
        public void FindTypes_AritySpellingAndNamespacePrefix_MatchTheSameType()
        {
            ApiIndex.EnsureIndexed(5000);
            foreach (var query in new[] { "List", "List<>", "List`1",
                          "Collections.Generic.List<T>", "System.Collections.Generic.List<T>" })
            {
                var found = ApiIndex.FindTypes(query, 5, 0);
                Assert.IsTrue(found.Any(t => t == typeof(System.Collections.Generic.List<>)),
                    $"query '{query}' should resolve to List<T>");
            }
        }

        [Test]
        public void PublicMembers_ListsWhatCallersSeeAndSkipsAccessorNoise()
        {
            var members = ApiIndex.PublicMembers(typeof(Vector3));
            Assert.IsTrue(members.Any(m => m.Name == "Lerp"));
            Assert.IsTrue(members.Any(m => m.Name == "Magnitude"), "properties are members too");
            Assert.IsEmpty(members.Where(m => m.Name.StartsWith("get_") || m.Name.StartsWith("set_")),
                "accessors are reported as properties, not as methods");
        }

        [Test]
        public void PublicMembers_KeepsOperators()
        {
            var members = ApiIndex.PublicMembers(typeof(Vector3));
            Assert.IsTrue(members.Any(m => m is MethodInfo mi && mi.Name == "op_Addition"),
                "operators are callable members; only accessors are noise");
            Assert.IsTrue(members.Any(m => m.Name == "op_Multiply"));
            Assert.IsEmpty(members.Where(m => m.Name.StartsWith("get_") || m.Name.StartsWith("set_")),
                "accessors are still reported as properties, not as methods");
        }

        [Test]
        public void PublicApi_AddsConstructorsAndNestedTypes()
        {
            var vector = ApiIndex.PublicApi(typeof(Vector3));
            Assert.IsTrue(vector.Any(m => m is System.Reflection.ConstructorInfo),
                "constructors are part of what a type offers");

            var editor = ApiIndex.PublicApi(typeof(EditorApplication));
            Assert.IsTrue(editor.Any(m => m is Type), "nested types are part of it too");
        }

        [Test]
        public void TypesWithMember_FindsTheTypeDeclaringAnExactMemberName()
        {
            ApiIndex.EnsureIndexed(5000);
            var types = ApiIndex.TypesWithMember("isCompiling", 10, new Deadline(3000));
            Assert.IsTrue(types.Contains(typeof(EditorApplication)),
                "the member-name reading is how 'api <Member>' answers at all");
        }

        [Test]
        public void FindMembersByName_IsCaseInsensitiveAndDeclaredOnly()
        {
            ApiIndex.EnsureIndexed(5000);
            var assembly = typeof(Vector3).Assembly;
            var found = ApiIndex.FindMembersByName(assembly, "lerp");
            Assert.IsTrue(found.Any(m => m.DeclaringType == typeof(Vector3) && m.Name == "Lerp"),
                "a lower-case query still finds the PascalCase member");
            Assert.IsTrue(found.All(m => string.Equals(m.Name, "lerp", StringComparison.OrdinalIgnoreCase)));

            var named = ApiIndex.FindMembersByName(assembly, "name");
            Assert.IsTrue(named.Any(m => m.DeclaringType == typeof(UnityEngine.Object)),
                "UnityEngine.Object declares name");
            Assert.IsEmpty(named.Where(m => m.DeclaringType == typeof(Component)),
                "an inherited member is not listed under the type that only inherits it");
        }
    }
}
