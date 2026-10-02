using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshRectTests
    {
        private static readonly MeshRect Square = new MeshRect(0f, 0f, 10f, 10f);

        private static IEnumerable<TestCaseData> EmptyCases()
        {
            yield return new TestCaseData(MeshRect.Empty, true).SetName("IsEmpty_Zero_IsEmpty");
            yield return new TestCaseData(new MeshRect(5f, 0f, 5f, 10f), true).SetName("IsEmpty_ZeroWidth_IsEmpty");
            yield return new TestCaseData(new MeshRect(0f, 10f, 10f, 0f), true).SetName("IsEmpty_Inverted_IsEmpty");
            yield return new TestCaseData(Square, false).SetName("IsEmpty_Square_IsNot");
        }

        [TestCaseSource(nameof(EmptyCases))]
        public void IsEmpty(MeshRect rect, bool expected)
        {
            Assert.That(rect.IsEmpty, Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> ContainsCases()
        {
            yield return new TestCaseData(MeshRect.Empty, 0f, 0f, false).SetName("Contains_Empty_Nothing");
            yield return new TestCaseData(Square, 10f, 5f, false).SetName("Contains_MaxEdge_IsOutside");
            yield return new TestCaseData(Square, 0f, 0f, true).SetName("Contains_MinCorner_IsInside");
            yield return new TestCaseData(Square, 5f, 5f, true).SetName("Contains_Middle_IsInside");
        }

        [TestCaseSource(nameof(ContainsCases))]
        public void Contains(MeshRect rect, float x, float z, bool expected)
        {
            Assert.That(rect.Contains(x, z), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> GapCases()
        {
            yield return new TestCaseData(Square, MeshRect.Empty, float.PositiveInfinity).SetName("GapTo_Empty_IsInfinite");
            yield return new TestCaseData(Square, new MeshRect(20f, 0f, 30f, 10f), 10f).SetName("GapTo_Apart_IsTheGap");
            yield return new TestCaseData(Square, new MeshRect(10f, 0f, 20f, 10f), 0f).SetName("GapTo_SharedEdge_IsZero");
            yield return new TestCaseData(Square, new MeshRect(10f, 10f, 20f, 20f), 0f).SetName("GapTo_SharedCorner_IsZero");
        }

        [TestCaseSource(nameof(GapCases))]
        public void GapTo(MeshRect a, MeshRect b, float expected)
        {
            Assert.That(a.GapTo(b), Is.EqualTo(expected));
            Assert.That(b.GapTo(a), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> EdgeCases()
        {
            yield return new TestCaseData(MeshRect.Empty, 0f, 0f, float.NegativeInfinity).SetName("DistanceToEdge_Empty_IsNegativeInfinity");
            yield return new TestCaseData(Square, 12f, 5f, -2f).SetName("DistanceToEdge_Outside_IsNegative");
            yield return new TestCaseData(Square, 3f, 5f, 3f).SetName("DistanceToEdge_Inside_IsPositive");
        }

        [TestCaseSource(nameof(EdgeCases))]
        public void DistanceToEdge(MeshRect rect, float x, float z, float expected)
        {
            Assert.That(rect.DistanceToEdge(x, z), Is.EqualTo(expected));
        }

        [Test]
        public void DistanceTo_EmptyIsInfinite_InsideIsZero()
        {
            Assert.That(MeshRect.Empty.DistanceTo(0f, 0f), Is.EqualTo(float.PositiveInfinity));
            Assert.That(Square.DistanceTo(5f, 5f), Is.EqualTo(0f));
            Assert.That(Square.DistanceTo(13f, 5f), Is.EqualTo(3f));
        }

        private static IEnumerable<TestCaseData> EnclosesCases()
        {
            yield return new TestCaseData(Square, new MeshRect(5f, 5f, 11f, 10f), false).SetName("Encloses_StickingOut_IsFalse");
            yield return new TestCaseData(MeshRect.Empty, Square, false).SetName("Encloses_EmptyOuter_IsFalse");
            yield return new TestCaseData(Square, MeshRect.Empty, true).SetName("Encloses_EmptyInner_IsTrue");
            yield return new TestCaseData(Square, Square, true).SetName("Encloses_Itself_IsTrue");
        }

        [TestCaseSource(nameof(EnclosesCases))]
        public void Encloses(MeshRect outer, MeshRect inner, bool expected)
        {
            Assert.That(outer.Encloses(inner), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> OverlapCases()
        {
            yield return new TestCaseData(Square, new MeshRect(10f, 0f, 20f, 10f), false).SetName("Overlaps_Touching_IsFalse");
            yield return new TestCaseData(Square, MeshRect.Empty, false).SetName("Overlaps_Empty_IsFalse");
            yield return new TestCaseData(Square, new MeshRect(9f, 0f, 20f, 10f), true).SetName("Overlaps_OneMetre_IsTrue");
        }

        [TestCaseSource(nameof(OverlapCases))]
        public void Overlaps(MeshRect a, MeshRect b, bool expected)
        {
            Assert.That(a.Overlaps(b), Is.EqualTo(expected));
        }
    }

    public class MeshLayoutPairTests
    {
        private static IEnumerable<TestCaseData> OrderCases()
        {
            yield return new TestCaseData(new MeshLayoutPair(1, 5), new MeshLayoutPair(1, 6), -1).SetName("CompareTo_LaterVersion_IsNewer");
            yield return new TestCaseData(new MeshLayoutPair(1, 99), new MeshLayoutPair(2, 0), -1).SetName("CompareTo_LaterIncarnation_WinsOverVersion");
            yield return new TestCaseData(MeshLayoutPair.None, new MeshLayoutPair(1, 0), -1).SetName("CompareTo_None_IsOldest");
            yield return new TestCaseData(new MeshLayoutPair(3, 4), new MeshLayoutPair(3, 4), 0).SetName("CompareTo_Equal_IsZero");
        }

        [TestCaseSource(nameof(OrderCases))]
        public void CompareTo(MeshLayoutPair a, MeshLayoutPair b, int expected)
        {
            Assert.That(System.Math.Sign(a.CompareTo(b)), Is.EqualTo(expected));
            Assert.That(System.Math.Sign(b.CompareTo(a)), Is.EqualTo(-expected));
            Assert.That(b.IsNewerThan(a), Is.EqualTo(expected < 0));
        }

        [Test]
        public void None_IsNone_AndPrintsInHex()
        {
            Assert.That(MeshLayoutPair.None.IsNone, Is.True);
            Assert.That(new MeshLayoutPair(255, 7).ToString(), Is.EqualTo("ff/7"));
            Assert.That(new MeshLayoutPair(2, 3) == new MeshLayoutPair(2, 3), Is.True);
        }
    }

    public class MeshLayoutSnapshotEqualityTests
    {
        private static MeshLayoutSnapshot Of(params MeshLayoutEntry[] entries)
        {
            MeshLayoutSnapshot snapshot = new MeshLayoutSnapshot { pair = new MeshLayoutPair(1, 1) };
            snapshot.entries.AddRange(entries);
            return snapshot;
        }

        private static MeshLayoutEntry E(string id, float maxX = 100f, MeshCellState state = MeshCellState.Active)
        {
            MeshRect rect = new MeshRect(0f, 0f, maxX, 100f);
            return new MeshLayoutEntry { id = id, mapId = "Map", baseId = "C", state = state, owned = rect, interest = rect };
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            yield return new TestCaseData(Of(E("C.1")), Of(E("C.1", maxX: 90f)), false).SetName("SameEntries_OtherRectangle_Differ");
            yield return new TestCaseData(Of(E("C.1")), Of(E("C.1", state: MeshCellState.Draining)), false).SetName("SameEntries_OtherState_Differ");
            yield return new TestCaseData(Of(E("C.1"), E("C.2")), Of(E("C.2"), E("C.1")), false).SetName("SameEntries_OtherOrder_Differ");
            yield return new TestCaseData(Of(E("C.1")), Of(), false).SetName("SameEntries_OtherCount_Differ");
            yield return new TestCaseData(Of(), null, true).SetName("SameEntries_EmptyAndNull_Agree");
            yield return new TestCaseData(Of(E("c.1")), Of(E("C.1")), true).SetName("SameEntries_IdCaseOnly_Agree");
            yield return new TestCaseData(Of(E("C.1"), E("C.2")), Of(E("C.1"), E("C.2")), true).SetName("SameEntries_Equal_Agree");
        }

        [TestCaseSource(nameof(Cases))]
        public void SameEntries(MeshLayoutSnapshot a, MeshLayoutSnapshot b, bool expected)
        {
            Assert.That(MeshLayoutSnapshot.SameEntries(a, b), Is.EqualTo(expected));
            Assert.That(MeshLayoutSnapshot.SameEntries(b, a), Is.EqualTo(expected));
        }
    }
}
