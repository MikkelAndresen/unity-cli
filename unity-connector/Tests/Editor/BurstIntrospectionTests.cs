using System;
using System.Reflection;
using NUnit.Framework;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// The Burst bridge reads private Burst members by reflection. The report must degrade to
    /// "unknown" when a member is missing or its invocation throws, instead of failing the tool.
    /// This file runs without com.unity.burst installed, which is exactly that degradation path.
    [TestFixture]
    public class BurstIntrospectionTests
    {
        [Test]
        public void Create_FindsTheBurstTypesTheEditorLoaded()
        {
            var burst = BurstIntrospection.Create();
            var compilerLoaded = Type.GetType("Unity.Burst.BurstCompiler, UnityEngine.BurstModule") != null ||
                                 Type.GetType("Unity.Burst.BurstCompiler, Unity.Burst") != null;

            if (!compilerLoaded)
            {
                Assert.IsNull(burst, "a missing Burst must be reported as absent, not as a tool failure");
                return;
            }

            Assert.IsNotNull(burst, "Unity loads Burst's module even when the package is not in the manifest");
            Assert.DoesNotThrow(() => burst.IsCompilationDone());
            Assert.DoesNotThrow(() => burst.CompilationEnabled());
            Assert.DoesNotThrow(() => burst.Eager());
            Assert.IsNotNull(burst.Targets(), "target enumeration degrades to an empty list, never null");
        }

        [Test]
        public void TryInvoke_ReportsFailureForAMissingOrThrowingMember()
        {
            Assert.IsFalse(BurstIntrospection.TryInvoke(null, null, null, out var result));
            Assert.IsNull(result);

            var throwing = typeof(BurstIntrospectionTests).GetMethod(nameof(Throws),
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsFalse(BurstIntrospection.TryInvoke(throwing, null, null, out _),
                "a reflection invocation that throws must downgrade the report, not escape the tool");

            var returnsSeven = typeof(BurstIntrospectionTests).GetMethod(nameof(ReturnsSeven),
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsTrue(BurstIntrospection.TryInvoke(returnsSeven, null, null, out result));
            Assert.AreEqual(7, result);
        }

        [Test]
        public void TypePart_KeepsAStaticMethodTargetWhole()
        {
            Assert.AreEqual("MyGame.HeavyJob", BurstIntrospection.TypePart("MyGame.HeavyJob - (IJobFor)"));
            Assert.AreEqual("MyGame.Util.Run(int)", BurstIntrospection.TypePart("MyGame.Util.Run(int)"));
            Assert.AreEqual("", BurstIntrospection.TypePart(null));
        }

        [Test]
        public void DefaultAssemblyOptions_IsZeroForTheEnumBurstExpects()
        {
            Assert.AreEqual(BurstAssemblyOptions.None,
                (BurstAssemblyOptions)BurstIntrospection.DefaultAssemblyOptions(typeof(BurstAssemblyOptions)));
            Assert.AreEqual(0, BurstIntrospection.DefaultAssemblyOptions(typeof(int)),
                "a non-enum options type still gets a fresh default instance");
        }

        enum BurstAssemblyOptions
        {
            None = 0,
            Some = 1,
        }

        static int ReturnsSeven() => 7;

        static void Throws() => throw new InvalidOperationException("Burst renamed this member");
    }
}
