using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshSyntheticStateTests
    {
        private static ServerMeshSyntheticState Sample()
        {
            return new ServerMeshSyntheticState
            {
                id = "load-12",
                name = "Load0012",
                classId = 101,
                entityId = 202,
                level = 5,
                handoffs = 1,
                motion = ServerMeshLoadMotionKind.Kinematic,
                fight = false,
                fromZone = "C",
            };
        }

        [Test]
        public void Encode_TryDecode_RoundTripsEveryField()
        {
            ServerMeshSyntheticState sent = Sample();

            bool decoded = ServerMeshSyntheticState.TryDecode(sent.Encode(), out ServerMeshSyntheticState got);

            Assert.That(decoded, Is.True);
            Assert.That(got.id, Is.EqualTo("load-12"));
            Assert.That(got.name, Is.EqualTo("Load0012"));
            Assert.That(got.classId, Is.EqualTo(101));
            Assert.That(got.entityId, Is.EqualTo(202));
            Assert.That(got.level, Is.EqualTo(5));
            Assert.That(got.handoffs, Is.EqualTo(1));
            Assert.That(got.motion, Is.EqualTo(ServerMeshLoadMotionKind.Kinematic));
            Assert.That(got.fight, Is.False);
            Assert.That(got.fromZone, Is.EqualTo("C"));
        }

        [Test]
        public void Encode_FirstByteIsTheVersion()
        {
            Assert.That(Sample().Encode()[0], Is.EqualTo(ServerMeshSyntheticState.Version));
        }

        [Test]
        public void TryDecode_NullOrEmpty_Fails()
        {
            Assert.That(ServerMeshSyntheticState.TryDecode(null, out _), Is.False);
            Assert.That(ServerMeshSyntheticState.TryDecode(new byte[0], out _), Is.False);
        }

        [Test]
        public void TryDecode_EveryTruncation_Fails()
        {
            byte[] bytes = Sample().Encode();
            for (int length = 1; length < bytes.Length; ++length)
            {
                byte[] cut = new byte[length];
                System.Array.Copy(bytes, cut, length);
                Assert.That(ServerMeshSyntheticState.TryDecode(cut, out _), Is.False, $"length {length}");
            }
        }

        [Test]
        public void TryDecode_TrailingBytes_Fails()
        {
            byte[] bytes = Sample().Encode();
            byte[] longer = new byte[bytes.Length + 1];
            bytes.CopyTo(longer, 0);

            Assert.That(ServerMeshSyntheticState.TryDecode(longer, out _), Is.False);
        }

        [Test]
        public void TryDecode_OtherVersion_Fails()
        {
            byte[] bytes = Sample().Encode();
            bytes[0] = ServerMeshSyntheticState.Version + 1;

            Assert.That(ServerMeshSyntheticState.TryDecode(bytes, out _), Is.False);
        }

        [Test]
        public void TryDecode_UnknownMotion_Fails()
        {
            ServerMeshSyntheticState s = Sample();
            s.motion = (ServerMeshLoadMotionKind)7;

            Assert.That(ServerMeshSyntheticState.TryDecode(s.Encode(), out _), Is.False);
        }

        [Test]
        public void TryDecode_NegativeHandoffs_Fails()
        {
            ServerMeshSyntheticState s = Sample();
            s.handoffs = -1;

            Assert.That(ServerMeshSyntheticState.TryDecode(s.Encode(), out _), Is.False);
        }

        [Test]
        public void TryDecode_EmptyIdOrName_Fails()
        {
            ServerMeshSyntheticState noId = Sample();
            noId.id = null;
            ServerMeshSyntheticState noName = Sample();
            noName.name = "";

            Assert.That(ServerMeshSyntheticState.TryDecode(noId.Encode(), out _), Is.False);
            Assert.That(ServerMeshSyntheticState.TryDecode(noName.Encode(), out _), Is.False);
        }

        [Test]
        public void TryDecode_OverlongName_Fails()
        {
            ServerMeshSyntheticState s = Sample();
            s.name = new string('x', ServerMeshSyntheticState.MaxStringLength + 1);

            Assert.That(ServerMeshSyntheticState.TryDecode(s.Encode(), out _), Is.False);
        }

        [Test]
        public void TryDecode_GarbageLengthPrefix_Fails()
        {
            byte[] bytes = { ServerMeshSyntheticState.Version, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

            Assert.That(ServerMeshSyntheticState.TryDecode(bytes, out _), Is.False);
        }

        [Test]
        public void TryDecode_EmptyFromZone_Decodes()
        {
            ServerMeshSyntheticState s = Sample();
            s.fromZone = null;

            Assert.That(ServerMeshSyntheticState.TryDecode(s.Encode(), out ServerMeshSyntheticState got), Is.True);
            Assert.That(got.fromZone, Is.EqualTo(""));
        }

        [Test]
        public void BrainSeed_IsPinned_SoEveryProcessDrawsTheSameStream()
        {
            Assert.That(ServerMeshSyntheticState.BrainSeed("load-12", 1), Is.EqualTo(-1863606524));
            Assert.That(ServerMeshSyntheticState.BrainSeed("load-12", 2), Is.EqualTo(1963599439));
            Assert.That(ServerMeshSyntheticState.BrainSeed("load-C-3", 1), Is.EqualTo(2110534617));
            Assert.That(ServerMeshSyntheticState.BrainSeed(null, 0), Is.EqualTo(ServerMeshSyntheticState.BrainSeed("", 0)));
        }

        [Test]
        public void BrainSeed_ChangesWithEveryHandoff()
        {
            int a = ServerMeshSyntheticState.BrainSeed("load-12", 1);
            int b = ServerMeshSyntheticState.BrainSeed("load-12", 2);
            int c = ServerMeshSyntheticState.BrainSeed("load-12", 3);

            Assert.That(new[] { a, b, c }, Is.Unique);
        }

        [Test]
        public void BrainSeed_DiffersBetweenPlayers()
        {
            Assert.That(ServerMeshSyntheticState.BrainSeed("load-12", 1), Is.Not.EqualTo(ServerMeshSyntheticState.BrainSeed("load-13", 1)));
        }

        [Test]
        public void WanderArea_MovesTheOwnedRectIntoTheSceneFrame_AHairInside()
        {
            MeshRect owned = new MeshRect(0f, -100f, 100f, 100f);

            bool ok = ServerMeshSyntheticState.WanderArea(owned, new Vector3(-400f, 0f, 0f), out Rect area);

            Assert.That(ok, Is.True);
            Assert.That(area.xMin, Is.EqualTo(400.01f).Within(1e-3f));
            Assert.That(area.xMax, Is.EqualTo(499.99f).Within(1e-3f));
            Assert.That(area.yMin, Is.EqualTo(-99.99f).Within(1e-3f));
            Assert.That(area.yMax, Is.EqualTo(99.99f).Within(1e-3f));
        }

        [Test]
        public void WanderArea_EmptyOrHairThinRect_Fails()
        {
            Assert.That(ServerMeshSyntheticState.WanderArea(MeshRect.Empty, Vector3.zero, out _), Is.False);
            Assert.That(ServerMeshSyntheticState.WanderArea(new MeshRect(0f, 0f, 0.015f, 10f), Vector3.zero, out _), Is.False);
        }

        [Test]
        public void AdoptedLine_IsTheContractFormat()
        {
            Assert.That(ServerMeshSyntheticState.AdoptedLine("Load0012", "C", "C.1"), Is.EqualTo("Synthetic adopted Load0012 zone C->C.1"));
        }

        [Test]
        public void AdoptedLine_KeepsOneLineWithoutBraces()
        {
            string line = ServerMeshSyntheticState.AdoptedLine("Lo{a}d\n1", null, "C.1");

            Assert.That(line, Is.EqualTo("Synthetic adopted Lo(a)d 1 zone ?->C.1"));
        }

        [Test]
        public void TryIntersect_KeepsTheOverlap_AndRefusesDisjointRects()
        {
            Rect a = Rect.MinMaxRect(-100f, -100f, 100f, 100f);
            Rect b = Rect.MinMaxRect(-100f, -100f, 0f, 100f);

            Assert.That(ServerMeshSyntheticAdopter.TryIntersect(a, b, out Rect both), Is.True);
            Assert.That(both, Is.EqualTo(b));
            Assert.That(ServerMeshSyntheticAdopter.TryIntersect(b, Rect.MinMaxRect(0f, -100f, 100f, 100f), out _), Is.False);
        }
    }
}
