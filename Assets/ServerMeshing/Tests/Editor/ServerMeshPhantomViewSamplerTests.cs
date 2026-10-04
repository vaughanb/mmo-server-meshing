using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPhantomViewSamplerTests
    {
        private const long ConnectionA = 1_000_000;
        private const long ConnectionB = 1_000_001;

        private sealed class ViewSamplerTruth : IServerMeshPhantomTruth
        {
            public readonly Dictionary<uint, Vector3> positions = new Dictionary<uint, Vector3>();
            public readonly Dictionary<uint, int> asked = new Dictionary<uint, int>();

            public bool TryGetPosition(uint objectId, out Vector3 position)
            {
                asked[objectId] = asked.TryGetValue(objectId, out int count) ? count + 1 : 1;
                return positions.TryGetValue(objectId, out position);
            }
        }

        private static byte[] KitPayload(uint flags, Vector3 position)
        {
            NetDataWriter writer = new NetDataWriter();
            MeshKinematicCharacterBody.WriteKitTransform(writer, flags, 0, position, 0f);
            return writer.CopyData();
        }

        private static NetDataWriter RawPacket(int declaredCount, params (uint id, byte[] payload)[] records)
        {
            NetDataWriter writer = new NetDataWriter();
            writer.PutPackedUShort(119);
            writer.PutPackedLong(1_790_000_000_000L);
            writer.Put(declaredCount);
            foreach ((uint id, byte[] payload) in records)
            {
                writer.PutPackedUInt(id);
                writer.Put(payload.Length);
                writer.Put(payload);
            }
            return writer;
        }

        private static NetDataWriter RawPacket(params (uint id, byte[] payload)[] records) => RawPacket(records.Length, records);

        private static ServerMeshPhantomViewSampler SampledA(out ViewSamplerTruth truth)
        {
            ServerMeshPhantomViewSampler sampler = new ServerMeshPhantomViewSampler(4, 0);
            sampler.Track(ConnectionA);
            sampler.BeginWindow(0);
            truth = new ViewSamplerTruth();
            return sampler;
        }

        private static void Swallow(ServerMeshPhantomViewSampler sampler, long connectionId, NetDataWriter packet, int frame)
        {
            sampler.OnSwallowed(connectionId, packet.Data, packet.Length, frame);
        }

        [Test]
        public void Constructor_NoObservers_ThrowsNamingIt()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new ServerMeshPhantomViewSampler(0, 1));

            Assert.That(thrown.ParamName, Is.EqualTo("maxObservers"));
        }

        [Test]
        public void OnSwallowed_UntrackedConnection_IsIgnored()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[5] = new Vector3(3f, 0f, 4f);
            ServerMeshPhantomWindow window = new ServerMeshPhantomWindow();

            Swallow(sampler, ConnectionB, RawPacket((5u, KitPayload(17, Vector3.zero))), 10);

            Assert.That(sampler.IsSampled(ConnectionB), Is.False);
            Assert.That(sampler.Sample(64, truth, window, 10, 6), Is.EqualTo(0));
            Assert.That(window.ViewErrSamples, Is.EqualTo(0));
        }

        [Test]
        public void OnSwallowed_TrackedButNotSampled_IsIgnored()
        {
            ServerMeshPhantomViewSampler sampler = new ServerMeshPhantomViewSampler(4, 0);
            sampler.Track(ConnectionA);
            ViewSamplerTruth truth = new ViewSamplerTruth();
            truth.positions[5] = Vector3.zero;

            Swallow(sampler, ConnectionA, RawPacket((5u, KitPayload(17, Vector3.zero))), 10);

            Assert.That(sampler.IsSampled(ConnectionA), Is.False);
            Assert.That(sampler.Sample(64, truth, new ServerMeshPhantomWindow(), 10, 6), Is.EqualTo(0));
        }

        [Test]
        public void OnSwallowed_AnotherMessageType_IsIgnored()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[5] = Vector3.zero;
            NetDataWriter packet = RawPacket((5u, KitPayload(17, Vector3.zero)));
            packet.Data[0] = 118;

            Swallow(sampler, ConnectionA, packet, 10);

            Assert.That(sampler.Sample(64, truth, new ServerMeshPhantomWindow(), 10, 6), Is.EqualTo(0));
            Assert.That(sampler.Truncated, Is.EqualTo(0));
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(7)]
        public void OnSwallowed_PacketCutInsideItsHeader_CountsTruncatedAndStoresNothing(int length)
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[5] = Vector3.zero;
            NetDataWriter packet = RawPacket((5u, KitPayload(17, Vector3.zero)));

            sampler.OnSwallowed(ConnectionA, packet.Data, length, 10);

            Assert.That(sampler.Truncated, Is.EqualTo(1));
            Assert.That(sampler.Sample(64, truth, new ServerMeshPhantomWindow(), 10, 6), Is.EqualTo(0));
        }

        [Test]
        public void OnSwallowed_RecordLengthPastThePacket_StopsTheDecodeAfterTheRecordsBefore()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[5] = new Vector3(3f, 0f, 4f);
            truth.positions[6] = Vector3.zero;
            NetDataWriter packet = RawPacket(2, (5u, KitPayload(17, Vector3.zero)));
            packet.PutPackedUInt(6u);
            packet.Put(500);
            packet.Put(KitPayload(17, Vector3.one));
            ServerMeshPhantomWindow window = new ServerMeshPhantomWindow { ShowViewErr = true };

            sampler.OnSwallowed(ConnectionA, packet.Data, packet.Length, 10);

            Assert.That(sampler.Truncated, Is.EqualTo(1));
            Assert.That(sampler.Sample(64, truth, window, 10, 6), Is.EqualTo(1));
            Assert.That(window.ToLine(), Does.Contain("| viewErr n 1 p50 5.00 p95 5.00 max 5.00 m"));
        }

        [Test]
        public void OnSwallowed_DeclaredCountAboveTheRecords_CountsTruncated()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[5] = Vector3.zero;

            Swallow(sampler, ConnectionA, RawPacket(3, (5u, KitPayload(17, Vector3.zero))), 10);

            Assert.That(sampler.Truncated, Is.EqualTo(1));
            Assert.That(sampler.Sample(64, truth, new ServerMeshPhantomWindow(), 10, 6), Is.EqualTo(1));
        }

        [Test]
        public void OnSwallowed_RecordUnderFourteenBytes_IsSkippedAndTheNextIsRead()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[5] = Vector3.zero;
            truth.positions[6] = new Vector3(0f, 0f, 2f);
            byte[] shortPayload = new byte[13];

            Swallow(sampler, ConnectionA, RawPacket((5u, shortPayload), (6u, KitPayload(17, Vector3.zero))), 10);

            ServerMeshPhantomWindow window = new ServerMeshPhantomWindow { ShowViewErr = true };
            Assert.That(sampler.Sample(64, truth, window, 10, 6), Is.EqualTo(1));
            Assert.That(window.ToLine(), Does.Contain("| viewErr n 1 p50 2.00 p95 2.00 max 2.00 m"));
            Assert.That(sampler.Truncated, Is.EqualTo(0));
        }

        [Test]
        public void OnSwallowed_FullTable_CountsOverflowWithoutGrowing()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            byte[] payload = KitPayload(17, Vector3.zero);
            for (uint id = 1; id <= ServerMeshPhantomViewSampler.TableSize + 3; ++id)
                Swallow(sampler, ConnectionA, RawPacket((id, payload)), 10);

            Assert.That(sampler.Overflow, Is.EqualTo(3));
        }

        [Test]
        public void Sample_EntryOlderThanTheAgeLimit_IsNotCompared()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[5] = Vector3.zero;
            Swallow(sampler, ConnectionA, RawPacket((5u, KitPayload(17, Vector3.zero))), 10);

            Assert.That(sampler.Sample(64, truth, new ServerMeshPhantomWindow(), 17, 6), Is.EqualTo(0));
            Assert.That(sampler.Sample(64, truth, new ServerMeshPhantomWindow(), 16, 6), Is.EqualTo(1));
        }

        [Test]
        public void Sample_ObjectTheTruthDoesNotKnow_IsNotCompared()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            Swallow(sampler, ConnectionA, RawPacket((5u, KitPayload(17, Vector3.zero))), 10);

            Assert.That(sampler.Sample(64, truth, new ServerMeshPhantomWindow(), 10, 6), Is.EqualTo(0));
        }

        [Test]
        public void BeginWindow_ObserverThatLeftAndReenteredTheSample_StartsWithAnEmptyTable()
        {
            ServerMeshPhantomViewSampler sampler = new ServerMeshPhantomViewSampler(4, 0);
            sampler.Track(ConnectionA);
            sampler.Track(ConnectionB);
            ViewSamplerTruth truth = new ViewSamplerTruth();
            truth.positions[5] = Vector3.zero;
            sampler.BeginWindow(0);
            Assert.That(sampler.IsSampled(ConnectionA), Is.True);
            Assert.That(sampler.IsSampled(ConnectionB), Is.False);
            Swallow(sampler, ConnectionA, RawPacket((5u, KitPayload(17, Vector3.zero))), 10);

            sampler.BeginWindow(1);
            Assert.That(sampler.IsSampled(ConnectionA), Is.False);
            Assert.That(sampler.IsSampled(ConnectionB), Is.True);
            sampler.BeginWindow(2);

            Assert.That(sampler.IsSampled(ConnectionA), Is.True);
            Assert.That(sampler.Sample(64, truth, new ServerMeshPhantomWindow(), 10, 6), Is.EqualTo(0));
        }

        [Test]
        public void Untrack_RemovesTheConnectionFromTheSample()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[5] = Vector3.zero;
            Swallow(sampler, ConnectionA, RawPacket((5u, KitPayload(17, Vector3.zero))), 10);

            sampler.Untrack(ConnectionA);

            Assert.That(sampler.IsSampled(ConnectionA), Is.False);
            Assert.That(sampler.TrackedCount, Is.EqualTo(0));
            Assert.That(sampler.Sample(64, truth, new ServerMeshPhantomWindow(), 10, 6), Is.EqualTo(0));
        }

        [Test]
        public void Track_BeyondTheTables_IsRefusedAndCounted()
        {
            ServerMeshPhantomViewSampler sampler = new ServerMeshPhantomViewSampler(1, 0);
            sampler.Track(ConnectionA);
            sampler.Track(ConnectionA);

            sampler.Track(ConnectionB);

            Assert.That(sampler.TrackedCount, Is.EqualTo(1));
            Assert.That(sampler.TrackRefused, Is.EqualTo(1));
        }

        [Test]
        public void BeginWindow_TakesATenthOfTheTrackedConnectionsAndRotates()
        {
            ServerMeshPhantomViewSampler sampler = new ServerMeshPhantomViewSampler(30, 0);
            for (long k = 0; k < 20; ++k)
                sampler.Track(ConnectionA + k);
            HashSet<long> seen = new HashSet<long>();

            for (int round = 0; round < 10; ++round)
            {
                sampler.BeginWindow(round);
                int sampled = 0;
                for (long k = 0; k < 20; ++k)
                {
                    if (!sampler.IsSampled(ConnectionA + k))
                        continue;
                    sampled++;
                    seen.Add(ConnectionA + k);
                }
                Assert.That(sampled, Is.EqualTo(2));
            }

            Assert.That(seen.Count, Is.EqualTo(20));
        }

        [Test]
        public void OnSwallowed_PacketFromTheStateWriter_DecodesOneAndTwoByteFlags()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[7] = new Vector3(3f, 50f, 4f);
            truth.positions[300] = new Vector3(10f, 0f, -2f);
            MeshStatePacketWriter packet = new MeshStatePacketWriter();
            packet.Begin(1_790_000_000_000L);
            byte[] oneByteFlags = KitPayload(17, Vector3.zero);
            byte[] twoByteFlags = KitPayload((uint)MovementState.IsDash | 17u, new Vector3(10f, 0f, -1f));
            Assert.That(packet.TryAppend(7, oneByteFlags, 0, oneByteFlags.Length), Is.True);
            Assert.That(packet.TryAppend(300, twoByteFlags, 0, twoByteFlags.Length), Is.True);
            NetDataWriter finished = packet.Finish();
            ServerMeshPhantomWindow window = new ServerMeshPhantomWindow { ShowViewErr = true };

            sampler.OnSwallowed(ConnectionA, finished.Data, finished.Length, 20);

            Assert.That(sampler.Sample(64, truth, window, 20, 6), Is.EqualTo(2));
            Assert.That(window.ToLine(), Does.Contain("| viewErr n 2 p50 1.00 p95 5.00 max 5.00 m"));
            Assert.That(sampler.Truncated, Is.EqualTo(0));
        }

        [Test]
        public void Sample_StopsAtMaxEntriesAndContinuesRoundRobinWithWrapAround()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            byte[] payload = KitPayload(17, Vector3.zero);
            for (uint id = 1; id <= 5; ++id)
            {
                truth.positions[id] = Vector3.zero;
                Swallow(sampler, ConnectionA, RawPacket((id, payload)), 10);
            }
            ServerMeshPhantomWindow window = new ServerMeshPhantomWindow();

            Assert.That(sampler.Sample(3, truth, window, 10, 6), Is.EqualTo(3));
            Assert.That(sampler.Sample(3, truth, window, 10, 6), Is.EqualTo(3));
            Assert.That(window.ViewErrSamples, Is.EqualTo(6));
            Assert.That(truth.asked.Count, Is.EqualTo(5));
            int askedTwice = 0;
            foreach (int count in truth.asked.Values)
            {
                Assert.That(count, Is.InRange(1, 2));
                if (count == 2)
                    askedTwice++;
            }
            Assert.That(askedTwice, Is.EqualTo(1));
        }

        [Test]
        public void OnSwallowed_AllocatesNothing()
        {
            ServerMeshPhantomViewSampler sampler = SampledA(out ViewSamplerTruth truth);
            truth.positions[5] = Vector3.zero;
            NetDataWriter packet = RawPacket((5u, KitPayload(17, Vector3.zero)), (6u, KitPayload(2048, Vector3.one)));
            byte[] data = packet.Data;
            int length = packet.Length;
            sampler.OnSwallowed(ConnectionA, data, length, 1);

            Assert.That(() =>
            {
                sampler.OnSwallowed(ConnectionA, data, length, 2);
                sampler.IsSampled(ConnectionA);
            }, Is.Not.AllocatingGCMemory());
        }

        [TestCase(0.1, 60.0, 6)]
        [TestCase(0.1, 30.0, 3)]
        [TestCase(0.1, 144.0, 15)]
        [TestCase(0.1, 0.0, 6)]
        [TestCase(0.1, double.PositiveInfinity, 6)]
        [TestCase(0.001, 60.0, 1)]
        public void MaxAgeFrames_IsTheAgeInFramesRoundedUp(double seconds, double framesPerSecond, int expected)
        {
            Assert.That(ServerMeshPhantomUploads.MaxAgeFrames(seconds, framesPerSecond), Is.EqualTo(expected));
        }
    }
}
