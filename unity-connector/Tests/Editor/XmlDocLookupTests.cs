using System;
using System.Linq;
using NUnit.Framework;
using UnityCliConnector;
using UnityEngine;

namespace UnityCliConnector.Tests
{
    /// The doc files Unity ships spell member names slightly differently from the XML-doc ID
    /// convention other assemblies emit. Both have to resolve, and an overload must not be answered
    /// with another overload's summary through the short-key fallback.
    [TestFixture]
    public class XmlDocLookupTests
    {
        [Test]
        public void MemberKey_UsesDocIdSpellingForNestedByRefAndGenericParameters()
        {
            Assert.AreEqual("T:System.Environment.SpecialFolder",
                XmlDocLookup.TypeKey(typeof(Environment.SpecialFolder)),
                "nested types are joined with '.' in doc IDs, not reflection's '+'");

            var tryParse = typeof(int).GetMethod("TryParse", new[] { typeof(string), typeof(int).MakeByRefType() });
            Assert.AreEqual("M:System.Int32.TryParse(System.String,System.Int32@)",
                XmlDocLookup.MemberKey(tryParse));

            var add = typeof(System.Collections.Generic.List<>).GetMethod("Add");
            Assert.AreEqual("M:System.Collections.Generic.List`1.Add(`0)",
                XmlDocLookup.MemberKey(add));
        }

        [Test]
        public void MemberKey_NamesAConstructedGenericParameterTheWayUnityWritesIt()
        {
            var getKeys = typeof(AnimationCurve).GetMethods()
                .First(m => m.Name == "GetKeys" && m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType.IsGenericType);

            Assert.AreEqual("M:UnityEngine.AnimationCurve.GetKeys(System.Span`1{UnityEngine.Keyframe})",
                XmlDocLookup.MemberKey(getKeys));
            Assert.AreEqual("M:UnityEngine.AnimationCurve.GetKeys(System.Span`1{UnityEngine.Keyframe})",
                XmlDocLookup.NormalizeKey("M:UnityEngine.AnimationCurve.GetKeys(System.Span`1<UnityEngine.Keyframe>)"),
                "Unity's files use angle brackets; the lookup normalises them to the doc-ID form");
        }

        [Test]
        public void Summary_ResolvesTheExactOverloadRatherThanTheFirstOne()
        {
            var getKeys = typeof(AnimationCurve).GetMethods()
                .First(m => m.Name == "GetKeys" && m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType.IsGenericType);

            Assert.IsTrue(XmlDocLookup.TryGetMemberSummary(getKeys, out var summary),
                "UnityEngine.CoreModule.xml sits next to the assembly and documents this overload");
            StringAssert.Contains("Get all keys", summary);
        }
    }
}
