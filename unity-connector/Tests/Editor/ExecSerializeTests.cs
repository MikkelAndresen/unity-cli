using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;
using UnityEngine;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// Covers issue #23: Serialize used to reflect only public fields and fall back to
    /// ToString, so anonymous types and property-based editor API values arrived as opaque
    /// strings. These are pure function tests; they do not compile an exec snippet.
    /// </summary>
    [TestFixture]
    public class ExecSerializeTests
    {
        class PropertyOnly
        {
            public int A { get; set; }
            public string B { get; set; }
        }

        struct PropertyStruct
        {
            public int A { get; set; }
            public string B { get; set; }
        }

        class Mixed
        {
            public int Field;
            public string Prop { get; set; }
        }

        class ThrowingProperty
        {
            public int Fine => 7;
            public int Bad => throw new InvalidOperationException("boom");
        }

        class WithIndexer
        {
            public int this[int index] => index;
            public int Value => 3;
        }

        class Node
        {
            public string Name;
            public Node Next;
        }

        class ThrowingEnumerable : IEnumerable
        {
            public IEnumerator GetEnumerator() => throw new InvalidOperationException("enum boom");
        }

        static object Ser(object value) => ExecuteCsharp.Serialize(value);

        static JObject Json(object value) => JObject.FromObject(value);

        [Test]
        public void AnonymousType_SerializesItsProperties()
        {
            var json = Json(Ser(new { a = 1, b = "x" }));

            Assert.AreEqual(1, json["a"].Value<int>());
            Assert.AreEqual("x", json["b"].Value<string>());
        }

        [Test]
        public void PropertyOnlyClass_SerializesItsProperties()
        {
            var json = Json(Ser(new PropertyOnly { A = 2, B = "y" }));

            Assert.AreEqual(2, json["A"].Value<int>());
            Assert.AreEqual("y", json["B"].Value<string>());
        }

        [Test]
        public void PropertyOnlyStruct_SerializesItsProperties()
        {
            var json = Json(Ser(new PropertyStruct { A = 3, B = "z" }));

            Assert.AreEqual(3, json["A"].Value<int>());
            Assert.AreEqual("z", json["B"].Value<string>());
        }

        [Test]
        public void FieldsAndProperties_AreBothSerialized()
        {
            var json = Json(Ser(new Mixed { Field = 4, Prop = "p" }));

            Assert.AreEqual(4, json["Field"].Value<int>());
            Assert.AreEqual("p", json["Prop"].Value<string>());
        }

        [Test]
        public void ListOfAnonymousObjects_SerializesEachItemAsAnObject()
        {
            var list = (List<object>)Ser(new List<object> { new { a = 1 }, new { a = 2 } });

            Assert.AreEqual(2, list.Count);
            Assert.AreEqual(1, Json(list[0])["a"].Value<int>());
            Assert.AreEqual(2, Json(list[1])["a"].Value<int>());
        }

        [Test]
        public void Dictionary_SerializesKeysAndValues()
        {
            var json = Json(Ser(new Dictionary<string, object> { ["t"] = 42, ["n"] = null }));

            Assert.AreEqual(42, json["t"].Value<int>());
            Assert.AreEqual(JTokenType.Null, json["n"].Type);
        }

        [Test]
        public void NestedAnonymousObject_StaysNested()
        {
            var json = Json(Ser(new { outer = new { inner = "v" } }));

            Assert.AreEqual("v", json["outer"]["inner"].Value<string>());
        }

        [Test]
        public void CyclicGraph_IsCutAtTheBackReference()
        {
            var node = new Node { Name = "root" };
            node.Next = node;

            var json = Json(Ser(node));

            Assert.AreEqual("root", json["Name"].Value<string>());
            StringAssert.Contains("cycle", json["Next"].Value<string>());
        }

        [Test]
        public void DeepGraph_IsTruncatedAtTheDepthLimit()
        {
            var root = new Node { Name = "0" };
            var current = root;
            for (var i = 1; i <= 10; i++)
            {
                current.Next = new Node { Name = i.ToString() };
                current = current.Next;
            }

            var text = Json(Ser(root)).ToString();

            StringAssert.Contains("truncated at depth", text);
        }

        [Test]
        public void LongEnumerable_IsCappedAtOneHundredItems()
        {
            var list = (List<object>)Ser(Enumerable.Range(0, 150).ToList());

            Assert.AreEqual(101, list.Count);
            StringAssert.Contains("truncated at 100", (string)list[100]);
        }

        [Test]
        public void LargeDictionary_IsCappedAtOneHundredEntries()
        {
            var source = new Dictionary<string, int>();
            for (var i = 0; i < 150; i++) source["k" + i] = i;

            var result = (Dictionary<string, object>)Ser(source);

            Assert.AreEqual(101, result.Count);
            Assert.IsTrue(result.ContainsKey("... (truncated at 100)"));
        }

        [Test]
        public void ThrowingProperty_IsRecordedWithoutLosingTheOtherMembers()
        {
            var json = Json(Ser(new ThrowingProperty()));

            Assert.AreEqual(7, json["Fine"].Value<int>());
            StringAssert.Contains("<error:", json["Bad"].Value<string>());
            StringAssert.Contains("boom", json["Bad"].Value<string>());
        }

        [Test]
        public void Indexer_IsSkipped()
        {
            var json = Json(Ser(new WithIndexer()));

            Assert.IsNull(json["Item"]);
            Assert.AreEqual(3, json["Value"].Value<int>());
        }

        [Test]
        public void Vector3_UsesItsFieldsNotComputedProperties()
        {
            var json = Json(Ser(new Vector3(1, 2, 3)));

            Assert.AreEqual(1f, json["x"].Value<float>());
            Assert.AreEqual(2f, json["y"].Value<float>());
            Assert.AreEqual(3f, json["z"].Value<float>());
            Assert.IsNull(json["normalized"], "normalized returns another Vector3 and must not be walked");
            Assert.IsNull(json["magnitude"]);
        }

        [Test]
        public void UnityMathStructs_UseDataMembersNotDerivedProperties()
        {
            var quaternion = Json(Ser(new Quaternion(0, 0, 0, 1)));
            Assert.AreEqual(1f, quaternion["w"].Value<float>());
            Assert.IsNull(quaternion["eulerAngles"]);

            var color = Json(Ser(new Color(0.1f, 0.2f, 0.3f, 0.4f)));
            Assert.AreEqual(0.4f, color["a"].Value<float>());
            Assert.IsNull(color["grayscale"]);

            var rect = Json(Ser(new Rect(1, 2, 3, 4)));
            Assert.AreEqual(1f, rect["x"].Value<float>());
            Assert.AreEqual(3f, rect["width"].Value<float>());
            Assert.IsNull(rect["center"]);

            var bounds = Json(Ser(new Bounds(new Vector3(1, 2, 3), new Vector3(4, 5, 6))));
            Assert.IsNotNull(bounds["center"]);
            Assert.IsNotNull(bounds["extents"]);
            Assert.IsNull(bounds["size"], "size is derived from extents, so only the data members are walked");

            var matrix = Json(Ser(new Matrix4x4()));
            Assert.IsNotNull(matrix["m00"]);
            Assert.IsNull(matrix["determinant"]);
        }

        [Test]
        public void UnityObject_KeepsItsIdentityString()
        {
            var go = new GameObject("SerializeProbe");
            try
            {
                var data = Ser(go);

                Assert.IsInstanceOf<string>(data);
                StringAssert.Contains("SerializeProbe", (string)data);
                StringAssert.Contains("GameObject", (string)data);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DateTime_SerializesAsAString()
        {
            Assert.IsInstanceOf<string>(Ser(new DateTime(2020, 1, 2, 3, 4, 5)));
        }

        [Test]
        public void Enum_SerializesAsItsName()
        {
            Assert.AreEqual("Friday", Ser(DayOfWeek.Friday));
        }

        [Test]
        public void Null_StaysNull()
        {
            Assert.IsNull(Ser(null));
        }

        [Test]
        public void SerializationFailure_FallsBackToStringAndReportsTheError()
        {
            string error;
            var data = ExecuteCsharp.Serialize(new ThrowingEnumerable(), out error);

            Assert.IsInstanceOf<string>(data, "the handler must still return a value");
            Assert.IsNotNull(error);
            StringAssert.Contains("enum boom", error);
        }
    }
}
