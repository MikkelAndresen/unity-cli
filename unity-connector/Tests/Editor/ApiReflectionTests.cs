using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityCliConnector;
using UnityEngine;

namespace UnityCliConnector.Tests
{
    /// Signature formatting, C# type naming, query matching and [Obsolete] parsing.
    ///
    /// The probe types sit at namespace level on purpose: nested types carry their outer type's
    /// name into every expected string, which hides what the formatter is actually doing.
    internal class SignatureProbe
    {
        public int Field;
        public static readonly string ReadonlyField = "x";
        public const int ConstField = 3;

        public int ReadWrite { get; set; }
        public int ReadOnly { get; }
        public static double StaticProp { get; }

        public event Action Changed;

        public string Method(int a, string b = "x") => a + b;
        public static void Refs(ref int r, out long o, in float i, params string[] rest) { o = r; }
        public T Generic<T>(T value) where T : class, new() => value;
        public TNum Numbers<TNum>(TNum a) where TNum : struct => a;
        public int? Nullable(int? v) => v;
        public List<Dictionary<string, int[]>> Nested(int[,] matrix, out SignatureProbe[] arr) { arr = null; return null; }
        public void Raise() => Changed?.Invoke();

        public class NestedType
        {
        }
    }

    internal class SignatureProbeGeneric<TValue>
    {
    }

    [TestFixture]
    public class ApiReflectionTests
    {
        // ─── C# type names ────────────────────────────────────

        [Test]
        public void CSharpName_UsesKeywordsWithoutReflectionArity()
        {
            Assert.AreEqual("int", ApiReflection.CSharpName(typeof(int)));
            Assert.AreEqual("string", ApiReflection.CSharpName(typeof(string)));
            Assert.AreEqual("void", ApiReflection.CSharpName(typeof(void)));
            Assert.AreEqual("object", ApiReflection.CSharpName(typeof(object)));
            Assert.AreEqual("int?", ApiReflection.CSharpName(typeof(int?)));
            Assert.AreEqual("int[]", ApiReflection.CSharpName(typeof(int[])));
            Assert.AreEqual("int[,]", ApiReflection.CSharpName(typeof(int[,])));
            Assert.AreEqual("List<string>", ApiReflection.CSharpName(typeof(List<string>)));
            Assert.AreEqual("List<int>", ApiReflection.CSharpName(typeof(List<int>)));
            Assert.AreEqual("Dictionary<string, int[]>",
                ApiReflection.CSharpName(typeof(Dictionary<string, int[]>)));
            Assert.AreEqual("List<T>", ApiReflection.CSharpName(typeof(List<>)));
            Assert.AreEqual("SignatureProbe.NestedType", ApiReflection.CSharpName(typeof(SignatureProbe.NestedType)));
            Assert.AreEqual("Vector3", ApiReflection.CSharpName(typeof(Vector3)));
        }

        [Test]
        public void QualifiedName_PrefixesTheNamespace()
        {
            Assert.AreEqual("UnityEngine.Vector3", ApiReflection.QualifiedName(typeof(Vector3)));
            Assert.AreEqual("System.Collections.Generic.List<int>", ApiReflection.QualifiedName(typeof(List<int>)));
        }

        [Test]
        public void TypeKind_NamesWhatTheDeclarationSays()
        {
            Assert.AreEqual("struct", ApiReflection.TypeKind(typeof(Vector3)));
            Assert.AreEqual("class", ApiReflection.TypeKind(typeof(GameObject)));
            Assert.AreEqual("static class", ApiReflection.TypeKind(typeof(JsonUtility)));
            Assert.AreEqual("interface", ApiReflection.TypeKind(typeof(IList<int>)));
            Assert.AreEqual("enum", ApiReflection.TypeKind(typeof(FullScreenMode)));
            Assert.AreEqual("delegate", ApiReflection.TypeKind(typeof(Action)));
        }

        // ─── Signatures ───────────────────────────────────────

        [Test]
        public void SignatureOf_Method_FormsModifiersAndParameters()
        {
            Assert.AreEqual("public string Method(int a, string b)", Signature("Method", typeof(int), typeof(string)));
            Assert.AreEqual("public static void Refs(ref int r, out long o, in float i, params string[] rest)",
                Signature("Refs"));
            Assert.AreEqual("public T Generic<T>(T value) where T : class, new()", Signature("Generic"));
            Assert.AreEqual("public TNum Numbers<TNum>(TNum a) where TNum : struct", Signature("Numbers"));
            Assert.AreEqual("public int? Nullable(int? v)", Signature("Nullable"));
            Assert.AreEqual("public List<Dictionary<string, int[]>> Nested(int[,] matrix, out SignatureProbe[] arr)",
                Signature("Nested"));
        }

        [Test]
        public void SignatureOf_RealUnityMethod_ReadsLikeSource()
        {
            var lerp = typeof(Vector3).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Lerp" && m.GetParameters().Length == 3 &&
                            !m.GetParameters()[0].ParameterType.IsByRef);
            Assert.AreEqual("public static Vector3 Lerp(Vector3 a, Vector3 b, float t)",
                ApiReflection.SignatureOf(lerp));
        }

        [Test]
        public void SignatureOf_Constructor_NamesTheTypeAndTakesNoReturnType()
        {
            var ctor = typeof(SignatureProbe).GetConstructors()[0];
            Assert.AreEqual("public SignatureProbe()", ApiReflection.SignatureOf(ctor));
            Assert.AreEqual("constructor", ApiReflection.KindOf(ctor));
            Assert.AreEqual("SignatureProbe()", ApiReflection.ShortSignature(ctor));
        }

        [Test]
        public void SignatureOf_PropertyFieldEvent_FormsAccessorsAndModifiers()
        {
            Assert.AreEqual("public int ReadWrite { get; set; }", Signature("ReadWrite"));
            Assert.AreEqual("public int ReadOnly { get; }", Signature("ReadOnly"));
            Assert.AreEqual("public static double StaticProp { get; }", Signature("StaticProp"));
            Assert.AreEqual("public int Field;", Signature("Field"));
            Assert.AreEqual("public static readonly string ReadonlyField;", Signature("ReadonlyField"));
            Assert.AreEqual("public const int ConstField;", Signature("ConstField"));
            Assert.AreEqual("public event Action Changed;", Signature("Changed"));
            Assert.AreEqual("class SignatureProbe.NestedType", Signature("NestedType"));
        }

        [Test]
        public void ShortSignature_DropsModifiersReturnTypesAndNames()
        {
            var lerp = typeof(Vector3).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Lerp" && m.GetParameters().Length == 3 &&
                            !m.GetParameters()[0].ParameterType.IsByRef);
            Assert.AreEqual("Vector3.Lerp(Vector3,Vector3,float)", ApiReflection.ShortSignature(lerp));
            Assert.AreEqual("SignatureProbe.Nested(int[,],SignatureProbe[])",
                ApiReflection.ShortSignature(typeof(SignatureProbe).GetMethod("Nested",
                    BindingFlags.Instance | BindingFlags.Public)));
            Assert.AreEqual("Vector3.magnitude",
                ApiReflection.ShortSignature(typeof(Vector3).GetProperty("magnitude")));
        }

        [Test]
        public void KindOf_SeparatesEveryMemberKind()
        {
            Assert.AreEqual("method", ApiReflection.KindOf(typeof(SignatureProbe).GetMethod("Method")));
            Assert.AreEqual("constructor", ApiReflection.KindOf(typeof(SignatureProbe).GetConstructors()[0]));
            Assert.AreEqual("property", ApiReflection.KindOf(typeof(SignatureProbe).GetProperty("ReadWrite")));
            Assert.AreEqual("field", ApiReflection.KindOf(typeof(SignatureProbe).GetField("Field")));
            Assert.AreEqual("event", ApiReflection.KindOf(typeof(SignatureProbe).GetEvent("Changed")));
            Assert.AreEqual("nested type", ApiReflection.KindOf(typeof(SignatureProbe.NestedType)));
        }

        private static string Signature(string name, params Type[] types)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            MemberInfo member = types.Length == 0
                ? typeof(SignatureProbe).GetMember(name, flags)[0]
                : typeof(SignatureProbe).GetMethod(name, flags, null, types, null);
            if (member == null) member = typeof(SignatureProbe).GetMember(name, flags)[0];
            return ApiReflection.SignatureOf(member);
        }

        // ─── Query matching ───────────────────────────────────

        [Test]
        public void QueryMatching_AcceptsSimpleFullNameAndEitherGenericSpelling()
        {
            foreach (var query in new[] { "SignatureProbeGeneric", "SignatureProbeGeneric<T>",
                          "SignatureProbeGeneric`1", "Tests.SignatureProbeGeneric",
                          "UnityCliConnector.Tests.SignatureProbeGeneric" })
                Assert.IsTrue(ApiReflection.QueryMatches(typeof(SignatureProbeGeneric<>), query),
                    $"query '{query}' should match {typeof(SignatureProbeGeneric<>).FullName}");

            Assert.IsTrue(ApiReflection.QueryMatches(typeof(Vector3), "vector3"), "matching is case-insensitive");
            Assert.IsFalse(ApiReflection.QueryMatches(typeof(Vector3), "Quaternion"));
            Assert.IsFalse(ApiReflection.QueryMatches(typeof(Vector3), ""));
        }

        [Test]
        public void QueryRank_PutsExactAheadOfPrefixAheadOfTypo()
        {
            Assert.AreEqual(0, ApiReflection.QueryRank(typeof(Vector3), "Vector3"));
            Assert.AreEqual(0, ApiReflection.QueryRank(typeof(SignatureProbeGeneric<>), "SignatureProbeGeneric`1"),
                "the reflection arity spelling is the same name");
            Assert.AreEqual(1, ApiReflection.QueryRank(typeof(Vector3), "Vector"), "prefix");
            Assert.AreEqual(3, ApiReflection.QueryRank(typeof(Vector2), "Vector3"), "one edit away");
            Assert.AreEqual(-1, ApiReflection.QueryRank(typeof(Quaternion), "Vector3"));
        }

        [Test]
        public void QueryRank_PrefersTheNamespaceTheQueryAskedFor()
        {
            Assert.AreEqual(0, ApiReflection.QueryRank(typeof(UnityEngine.Quaternion), "UnityEngine.Quaternion"));
            Assert.AreEqual(1, ApiReflection.QueryRank(typeof(System.Numerics.Quaternion), "UnityEngine.Quaternion"),
                "right name, other namespace: still a candidate, ranked behind");
        }

        // ─── Name similarity ──────────────────────────────────

        [Test]
        public void EditDistance_IgnoresCaseAndReportsNothingBeyondTheCap()
        {
            Assert.AreEqual(0, ApiReflection.EditDistance("position", "Position", 2));
            Assert.AreEqual(1, ApiReflection.EditDistance("positon", "position", 2));
            Assert.AreEqual(-1, ApiReflection.EditDistance("positon", "localPosition", 2));
        }

        [Test]
        public void NameScore_OrdersExactAheadOfPrefixAheadOfTypo()
        {
            const string query = "positon";
            var names = new[] { "name", "position", "localPosition", "positions" };
            var ranked = names
                .Select(n => (Name: n, Score: ApiReflection.NameScore(n, query)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Name)
                .ToList();

            Assert.AreEqual("position", ranked[0], "the typo's one-edit neighbour leads");
            Assert.IsTrue(ranked.Contains("positions"));
            Assert.IsFalse(ranked.Contains("localPosition"), "too far from the typed name to suggest");
            Assert.IsFalse(ranked.Contains("name"), "unrelated names are not candidates");
        }

        [Test]
        public void NameScore_BeatsCaseDifferenceToAnEdit()
        {
            Assert.Greater(ApiReflection.NameScore("POSITON", "positon"),
                ApiReflection.NameScore("position", "positon"));
            Assert.AreEqual(0, ApiReflection.NameScore("position", ""));
        }

        // ─── [Obsolete] ───────────────────────────────────────

        [Test]
        public void ParseReplacement_ReadsUnitysRealDeprecationMessages()
        {
            Assert.AreEqual("GetEntityId",
                ApiReflection.ParseReplacement("Use GetEntityId instead."));
            Assert.AreEqual("Object.FindAnyObjectByType",
                ApiReflection.ParseReplacement("Object.FindObjectOfType has been deprecated. Use Object.FindAnyObjectByType instead."));
            Assert.AreEqual("BufferLookup<T>",
                ApiReflection.ParseReplacement("BufferFromEntity<T> is obsolete, use BufferLookup<T> instead."));
            Assert.AreEqual("Parallel",
                ApiReflection.ParseReplacement("Batched is obsolete, use Parallel or Single depending on job type. (UnityUpgradable) -> Parallel"));
            Assert.AreEqual("UnityEngine.Experimental.GlobalIllumination.RenderSettings.useRadianceAmbientProbe",
                ApiReflection.ParseReplacement("Use UnityEngine.Experimental.GlobalIllumination.useRadianceAmbientProbe instead. " +
                    "(UnityUpgradable) -> UnityEngine.Experimental.GlobalIllumination.RenderSettings.useRadianceAmbientProbe"),
                "the upgradable marker names the exact target, so it beats the prose");
            Assert.IsNull(ApiReflection.ParseReplacement("WebPlayer export is no longer supported in Unity 5.4+."));
            Assert.IsNull(ApiReflection.ParseReplacement("JobType is obsolete. The parameter should be removed. (UnityUpgradable) -> !2"),
                "a marker at a parameter index names no member");
            Assert.IsNull(ApiReflection.ParseReplacement("Property stereoMirrorMode is no longer supported. Please use single pass stereo rendering instead."),
                "prose between 'use' and 'instead' names no member");
        }

        [Test]
        public void ObsoleteOf_RealUnityMember_CarriesMessageErrorFlagAndReplacement()
        {
            var getInstanceId = typeof(UnityEngine.Object).GetMethod("GetInstanceID");
            if (getInstanceId == null)
            {
                Assert.Ignore("UnityEngine.Object.GetInstanceID is not in this Unity version.");
                return;
            }

            var info = ApiReflection.ObsoleteOf(getInstanceId);
            Assert.IsNotNull(info, "GetInstanceID is obsolete-error in Unity 6; if that changed, review the hint codes.");
            Assert.IsTrue(info.IsError);
            Assert.IsNotEmpty(info.Message);
            Assert.AreEqual("GetEntityId", info.Replacement);
        }

        [Test]
        public void ObsoleteOf_ReportsNothingForCurrentApi()
        {
            var lerp = typeof(Vector3).GetMethod("Lerp",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(Vector3), typeof(Vector3), typeof(float) }, null);
            Assert.IsNull(ApiReflection.ObsoleteOf(lerp));
        }
    }
}
