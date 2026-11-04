using NUnit.Framework;
using UnityCliConnector;

namespace UnityCliConnector.Tests
{
    /// Compile-error hints: what the messages look like (real Unity 6 / Roslyn text, plus the old
    /// Mono spelling), which candidates win, and how the text is bounded.
    [TestFixture]
    public class CompileHintsTests
    {
        // Messages copied from this editor's own compile output; the legacy ones are the Mono
        // compiler's spelling, which the console fallback can still carry.
        private const string Cs1061Roslyn =
            "'Transform' does not contain a definition for 'positon' and no accessible extension method " +
            "'positon' accepting a first argument of type 'Transform' could be found " +
            "(are you missing a using directive or an assembly reference?)";
        private const string Cs1061Legacy =
            "A type `Transform' does not contain a definition for `positon' and no extension method " +
            "`positon' of type `Transform' could not be found. Are you missing a using directive or an assembly reference?";
        private const string Cs0117 = "'Vector3' does not contain a definition for 'Lerpp'";
        private const string Cs0246 =
            "The type or namespace name 'Vectr3' could not be found " +
            "(are you missing a using directive or an assembly reference?)";
        private const string Cs0246Legacy =
            "The type or namespace name `Vectr3' could not be found. Are you missing a using directive or assembly reference?";
        private const string Cs0234 =
            "The type or namespace name 'Quaterninion' does not exist in the namespace 'UnityEngine' " +
            "(are you missing an assembly reference?)";
        private const string Cs0619InstanceId =
            "'Object.GetInstanceID()' is obsolete: 'Use GetEntityId instead.'";
        private const string Cs0618FindObjectOfType =
            "'Object.FindObjectOfType(Type)' is obsolete: 'Object.FindObjectOfType has been deprecated. " +
            "Use Object.FindAnyObjectByType instead.'";
        private const string Cs0619ObsoleteType =
            "'BufferFromEntity<T>' is obsolete: 'BufferFromEntity<T> is obsolete, use BufferLookup<T> instead.'";
        private const string Cs0618ProseOnly =
            "'Camera.stereoMirrorMode' is obsolete: 'Property stereoMirrorMode is no longer supported. " +
            "Please use single pass stereo rendering instead.'";

        [SetUp]
        public void SetUp() => CompileHints.ResetForTests();

        [TearDown]
        public void TearDown() => CompileHints.ResetForTests();

        // ─── Which codes get a hint ───────────────────────────

        [TestCase("CS0117", true)]
        [TestCase("CS1061", true)]
        [TestCase("CS0246", true)]
        [TestCase("CS0234", true)]
        [TestCase("CS0619", true)]
        [TestCase("CS0618", true)]
        [TestCase("CS0103", false)]
        [TestCase("CS0117 ", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void Supports_OnlyTheCodesWithAnAnswerToGive(string code, bool expected)
        {
            Assert.AreEqual(expected, CompileHints.Supports(code));
        }

        // ─── Message parsing ──────────────────────────────────

        [Test]
        public void ParseTarget_Cs1061_ReadsTypeAndMember()
        {
            var target = CompileHints.ParseTarget("CS1061", Cs1061Roslyn);
            Assert.IsNotNull(target);
            Assert.AreEqual("Transform", target.TypeQuery);
            Assert.AreEqual("positon", target.MemberQuery);
        }

        [Test]
        public void ParseTarget_Cs1061_LegacySpelling_ReadsTheSame()
        {
            var target = CompileHints.ParseTarget("CS1061", Cs1061Legacy);
            Assert.IsNotNull(target, "the old Mono quoting must parse too");
            Assert.AreEqual("Transform", target.TypeQuery);
            Assert.AreEqual("positon", target.MemberQuery);
        }

        [Test]
        public void ParseTarget_Cs0117_ReadsTypeAndMember()
        {
            var target = CompileHints.ParseTarget("CS0117", Cs0117);
            Assert.IsNotNull(target);
            Assert.AreEqual("Vector3", target.TypeQuery);
            Assert.AreEqual("Lerpp", target.MemberQuery);
        }

        [Test]
        public void ParseTarget_Cs0246_ReadsTheMissingType()
        {
            var target = CompileHints.ParseTarget("CS0246", Cs0246);
            Assert.IsNotNull(target);
            Assert.AreEqual("Vectr3", target.TypeQuery);
            Assert.IsNull(target.MemberQuery);

            var legacy = CompileHints.ParseTarget("CS0246", Cs0246Legacy);
            Assert.IsNotNull(legacy, "the old Mono quoting must parse too");
            Assert.AreEqual("Vectr3", legacy.TypeQuery);
        }

        [Test]
        public void ParseTarget_Cs0234_ReadsTheNamespaceToo()
        {
            var target = CompileHints.ParseTarget("CS0234", Cs0234);
            Assert.IsNotNull(target);
            Assert.AreEqual("Quaterninion", target.TypeQuery);
            Assert.AreEqual("UnityEngine", target.NamespaceQuery);
        }

        [Test]
        public void ParseTarget_Cs0619_SplitsTheObsoleteMemberAndKeepsTheReplacement()
        {
            var target = CompileHints.ParseTarget("CS0619", Cs0619InstanceId);
            Assert.IsNotNull(target);
            Assert.AreEqual("Object", target.TypeQuery);
            Assert.AreEqual("GetInstanceID", target.MemberQuery);
            Assert.AreEqual("Use GetEntityId instead.", target.ObsoleteMessage);
            Assert.AreEqual("GetEntityId", target.Replacement);
        }

        [Test]
        public void ParseTarget_ObsoleteType_NamesTheTypeAndTheReplacement()
        {
            var target = CompileHints.ParseTarget("CS0619", Cs0619ObsoleteType);
            Assert.IsNotNull(target);
            Assert.AreEqual("BufferFromEntity", target.TypeQuery);
            Assert.IsNull(target.MemberQuery);
            Assert.AreEqual("BufferLookup<T>", target.Replacement);
        }

        [Test]
        public void ParseTarget_ObsoleteNoticeWithoutAMemberNamed_ReportsNoReplacement()
        {
            var target = CompileHints.ParseTarget("CS0618", Cs0618ProseOnly);
            Assert.IsNotNull(target, "the member and message are still worth reading");
            Assert.AreEqual("Camera", target.TypeQuery);
            Assert.AreEqual("stereoMirrorMode", target.MemberQuery);
            Assert.IsNull(target.Replacement, "'please use single pass stereo rendering' names no member");
        }

        [Test]
        public void ParseTarget_MessageThisCodeDoesNotUse_ReturnsNull()
        {
            Assert.IsNull(CompileHints.ParseTarget("CS0117", "Assets/Foo.cs(1,1): nothing to parse here"));
            Assert.IsNull(CompileHints.ParseTarget("CS0246", "The name 'x' does not exist in the current context"));
            Assert.IsNull(CompileHints.ParseTarget("CS0619", "'Foo.Bar' is not obsolete"));
            Assert.IsNull(CompileHints.ParseTarget("CS0117", ""));
        }

        // ─── End to end: the hint an agent sees ───────────────

        [Test]
        public void Hint_MemberOnKnownType_PutsThatTypesNeighbourFirst()
        {
            var hint = CompileHints.ForDiagnostic("CS1061", Cs1061Roslyn, 5000);
            Assert.IsNotNull(hint);
            Assert.IsTrue(hint.StartsWith("closest: "));
            Assert.IsTrue(hint.Contains("Transform.position"), $"hint was: {hint}");
        }

        [Test]
        public void Hint_StaticMemberOnKnownType_SuggestsTheRealOverload()
        {
            var hint = CompileHints.ForDiagnostic("CS0117", Cs0117, 5000);
            Assert.IsNotNull(hint);
            Assert.IsTrue(hint.Contains("Vector3.Lerp("), $"hint was: {hint}");
            Assert.IsFalse(hint.Contains("Lerpp"), "the hint must not repeat the typo back");
        }

        [Test]
        public void Hint_MissingType_SuggestsRealTypesWithWhereTheyLive()
        {
            var hint = CompileHints.ForDiagnostic("CS0246", Cs0246, 5000);
            Assert.IsNotNull(hint);
            Assert.IsTrue(hint.StartsWith("closest types: "));
            Assert.IsTrue(hint.Contains("UnityEngine.Vector3 (UnityEngine.CoreModule)"), $"hint was: {hint}");
        }

        [Test]
        public void Hint_MissingTypeInANamedNamespace_PrefersThatNamespace()
        {
            var hint = CompileHints.ForDiagnostic("CS0234", Cs0234, 5000);
            Assert.IsNotNull(hint);
            Assert.IsTrue(hint.StartsWith("closest types: UnityEngine.Quaternion"), $"hint was: {hint}");
        }

        [Test]
        public void Hint_MemberTheNamedTypeDoesNotDeclare_FindsTheSameNameOnOtherTypes()
        {
            // The issue's example shape: the member exists, just not on the type it was called on.
            var message = "'GameObject' does not contain a definition for 'FindAnyObjectByType'";
            var hint = CompileHints.ForDiagnostic("CS0117", message, 5000);
            Assert.IsNotNull(hint);
            Assert.IsTrue(hint.Contains("Object.FindAnyObjectByType"), $"hint was: {hint}");
        }

        [Test]
        public void Hint_ObsoleteMember_ResolvesTheReplacementsSignature()
        {
            var hint = CompileHints.ForDiagnostic("CS0619", Cs0619InstanceId, 5000);
            Assert.IsNotNull(hint);
            Assert.IsTrue(hint.StartsWith("use "), $"hint was: {hint}");
            Assert.IsTrue(hint.Contains("GetEntityId"));
            Assert.IsTrue(hint.Contains("UnityEngine.Object"), "resolved, not the raw notice text");
        }

        [Test]
        public void Hint_ObsoleteWarningCode_GetTheSameTreatmentAsAnError()
        {
            var hint = CompileHints.ForDiagnostic("CS0618", Cs0618FindObjectOfType, 5000);
            Assert.IsNotNull(hint);
            Assert.IsTrue(hint.Contains("FindAnyObjectByType"), $"hint was: {hint}");
        }

        [Test]
        public void Hint_ObsoleteNoticeWithNoMemberNamed_SaysNothing()
        {
            Assert.IsNull(CompileHints.ForDiagnostic("CS0618", Cs0618ProseOnly, 5000),
                "inventing a member from prose would send the caller somewhere worse");
        }

        [Test]
        public void Hint_UnrelatedCode_SaysNothing()
        {
            Assert.IsNull(CompileHints.ForDiagnostic("CS0103",
                "The name 'speed' does not exist in the current context", 5000));
        }

        [Test]
        public void Hint_MissOnAPartialIndex_IsNotCached()
        {
            ApiIndex.ResetForTests();
            CompileHints.ResetForTests();

            Assert.IsNull(CompileHints.ForDiagnostic("CS0246", Cs0246, 0),
                "a spent budget indexes nothing, so the cold index has no candidate to suggest");

            ApiIndex.EnsureIndexed(5000);
            var hint = CompileHints.ForDiagnostic("CS0246", Cs0246, 5000);
            StringAssert.Contains("Vector3", hint,
                "a miss recorded while the index was partial must not shadow the answer a later call can find");
        }

        [Test]
        public void Hint_SpentBudget_DoesNotKeepIndexing()
        {
            ApiIndex.ResetForTests();
            CompileHints.ResetForTests();

            CompileHints.ForDiagnostic("CS0246", Cs0246, 0);
            Assert.AreEqual(0, ApiIndex.IndexedAssemblyCount,
                "one budget bounds the indexing for the whole call, including after it is spent");
        }

        [Test]
        public void Hint_IsAlwaysWithinTheSizeBudget()
        {
            foreach (var diag in new[]
            {
                ("CS1061", Cs1061Roslyn), ("CS0117", Cs0117), ("CS0246", Cs0246), ("CS0234", Cs0234),
                ("CS0619", Cs0619InstanceId), ("CS0618", Cs0618FindObjectOfType),
                ("CS0619", Cs0619ObsoleteType),
            })
            {
                var hint = CompileHints.ForDiagnostic(diag.Item1, diag.Item2, 5000);
                if (hint == null) continue;
                Assert.LessOrEqual(hint.Length, CompileHints.MaxHintChars,
                    $"{diag.Item1} {diag.Item2}: '{hint}' ({hint.Length} chars)");
                Assert.LessOrEqual(CountCandidates(hint), CompileHints.MaxCandidates,
                    $"{diag.Item1} listed more than {CompileHints.MaxCandidates} candidates: {hint}");
            }
        }

        private static int CountCandidates(string hint)
        {
            var listPart = hint.StartsWith("closest types: ") || hint.StartsWith("closest: ")
                ? hint.Substring(hint.IndexOf(": ") + 2)
                : "";
            return listPart.Length == 0 ? 0 : listPart.Split(new[] { ", " }, System.StringSplitOptions.None).Length;
        }

        // ─── Bounded formatting ───────────────────────────────

        [Test]
        public void JoinCapped_ListsCandidatesAndCountsWhatItDrops()
        {
            Assert.AreEqual("closest: a, b, c",
                CompileHints.JoinCapped(new[] { "a", "b", "c" }, 200, "closest: "));

            var capped = CompileHints.JoinCapped(new[] { "aaaa", "bbbb", "cccc", "dddd" }, 30, "closest: ");
            Assert.AreEqual("closest: aaaa, bbbb (+2 more)", capped);
            Assert.LessOrEqual(capped.Length, 30);
        }

        [Test]
        public void JoinCapped_SingleOversizedItem_IsCutToTheLimit()
        {
            var capped = CompileHints.JoinCapped(new[] { new string('x', 50) }, 30, "use ");
            Assert.AreEqual("use " + new string('x', 26), capped);
        }
    }
}
