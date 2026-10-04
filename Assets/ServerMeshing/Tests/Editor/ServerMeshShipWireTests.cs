using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipWireTests
    {
        private const string Compact28Golden = "0B 07 00 00 C0 3F 00 00 10 C0 00 00 7A 44 FF 03 08 60 00 49 00 00 00 C5 00 10 00 83";

        private static string Hex(NetDataWriter writer) => BitConverter.ToString(writer.CopyData()).Replace("-", " ");

        private static NetDataWriter Written(ServerMeshShipWireKind kind, in ServerMeshShipSample sample)
        {
            NetDataWriter writer = new NetDataWriter();
            switch (kind)
            {
                case ServerMeshShipWireKind.Legacy54:
                    ServerMeshShipWire.WriteLegacy54(writer, sample);
                    break;
                case ServerMeshShipWireKind.Header56:
                    ServerMeshShipWire.WriteHeader56(writer, sample);
                    break;
                default:
                    Assert.That(ServerMeshShipWire.WriteCompact28(writer, sample), Is.True);
                    break;
            }
            return writer;
        }

        private static void ReadAs(ServerMeshShipWireKind kind, NetDataReader reader, out ServerMeshShipSample sample)
        {
            switch (kind)
            {
                case ServerMeshShipWireKind.Legacy54:
                    ServerMeshShipWire.ReadLegacy54(reader, out sample);
                    break;
                case ServerMeshShipWireKind.Header56:
                    ServerMeshShipWire.ReadHeader56(reader, out sample);
                    break;
                default:
                    ServerMeshShipWire.ReadCompact28(reader, out sample);
                    break;
            }
        }

        private static IEnumerable<TestCaseData> HeaderByteThrowCases()
        {
            yield return new TestCaseData((byte)4, (byte)0, "mode").SetName("HeaderByte_Mode4_ThrowsNamingMode");
            yield return new TestCaseData((byte)255, (byte)0, "mode").SetName("HeaderByte_Mode255_ThrowsNamingMode");
            yield return new TestCaseData((byte)0, (byte)8, "FlightState").SetName("HeaderByte_FlightState8_ThrowsNamingFlightState");
            yield return new TestCaseData((byte)3, (byte)255, "FlightState").SetName("HeaderByte_FlightState255_ThrowsNamingFlightState");
        }

        [TestCaseSource(nameof(HeaderByteThrowCases))]
        public void HeaderByte_FieldOutOfRange_ThrowsNamingIt(byte mode, byte flightState, string field)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => ServerMeshShipWire.HeaderByte(mode, flightState));

            Assert.That(thrown.ParamName, Is.EqualTo(field));
        }

        private static IEnumerable<TestCaseData> FlightStateCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Header56).SetName("WriteHeader56_FlightState8_ThrowsNamingFlightStateAndWritesNothing");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28).SetName("WriteCompact28_FlightState8_ThrowsNamingFlightStateAndWritesNothing");
        }

        [TestCaseSource(nameof(FlightStateCases))]
        public void Write_FlightStateOutOfRange_ThrowsNamingItAndWritesNothing(ServerMeshShipWireKind kind)
        {
            ServerMeshShipSample sample = MeshSyncGoldenFixture.Sample();
            sample.FlightState = 8;
            NetDataWriter writer = new NetDataWriter();

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                if (kind == ServerMeshShipWireKind.Header56)
                    ServerMeshShipWire.WriteHeader56(writer, sample);
                else
                    ServerMeshShipWire.WriteCompact28(writer, sample);
            });

            Assert.That(thrown.ParamName, Is.EqualTo("FlightState"));
            Assert.That(writer.Length, Is.EqualTo(0));
        }

        private static IEnumerable<TestCaseData> NullWriterCases()
        {
            foreach (ServerMeshShipWireKind kind in new[] { ServerMeshShipWireKind.Legacy54, ServerMeshShipWireKind.Header56, ServerMeshShipWireKind.Compact28 })
                yield return new TestCaseData(kind).SetName($"Write{kind}_NullWriter_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(NullWriterCases))]
        public void Write_NullWriter_ThrowsNamingIt(ServerMeshShipWireKind kind)
        {
            ServerMeshShipSample sample = MeshSyncGoldenFixture.Sample();

            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() =>
            {
                if (kind == ServerMeshShipWireKind.Legacy54)
                    ServerMeshShipWire.WriteLegacy54(null, sample);
                else if (kind == ServerMeshShipWireKind.Header56)
                    ServerMeshShipWire.WriteHeader56(null, sample);
                else
                    ServerMeshShipWire.WriteCompact28(null, sample);
            });

            Assert.That(thrown.ParamName, Is.EqualTo("writer"));
        }

        private static IEnumerable<TestCaseData> ShortReaderCases()
        {
            foreach (ServerMeshShipWireKind kind in new[] { ServerMeshShipWireKind.Legacy54, ServerMeshShipWireKind.Header56, ServerMeshShipWireKind.Compact28 })
            {
                int size = ServerMeshShipWire.SizeOf(kind);
                foreach (int available in new[] { 0, 1, size - 1 })
                    yield return new TestCaseData(kind, available).SetName($"Read{kind}_{available}BytesLeft_ThrowsNamingTheReaderAndReadsNothing");
            }
        }

        [TestCaseSource(nameof(ShortReaderCases))]
        public void Read_TooFewBytes_ThrowsNamingTheReaderAndReadsNothing(ServerMeshShipWireKind kind, int available)
        {
            byte[] data = new byte[available];
            byte mode = kind == ServerMeshShipWireKind.Compact28 ? ServerMeshShipWire.ModeCompact : ServerMeshShipWire.ModeBubble;
            for (int i = 0; i < data.Length; ++i)
                data[i] = (byte)(0x10 | mode);
            NetDataReader reader = new NetDataReader(data);

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => ReadAs(kind, reader, out _));
            ArgumentOutOfRangeException dispatched = Assert.Throws<ArgumentOutOfRangeException>(() => ServerMeshShipWire.Read(reader, kind, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("reader"));
            Assert.That(dispatched.ParamName, Is.EqualTo("reader"));
            Assert.That(reader.AvailableBytes, Is.EqualTo(available));
        }

        [Test]
        public void Read_NullReader_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => ServerMeshShipWire.ReadLegacy54(null, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("reader"));
        }

        private static IEnumerable<TestCaseData> UnreadModeCases()
        {
            foreach (ServerMeshShipWireKind configured in new[] { ServerMeshShipWireKind.Header56, ServerMeshShipWireKind.Compact28 })
            {
                yield return new TestCaseData(configured, (byte)0x01).SetName($"Read_{configured}Configured_Mode1_ThrowsNamingTheReaderAndReadsNothing");
                yield return new TestCaseData(configured, (byte)0x02).SetName($"Read_{configured}Configured_Mode2_ThrowsNamingTheReaderAndReadsNothing");
                yield return new TestCaseData(configured, (byte)0xE9).SetName($"Read_{configured}Configured_Mode1WithReservedBits_ThrowsNamingTheReaderAndReadsNothing");
            }
        }

        [TestCaseSource(nameof(UnreadModeCases))]
        public void Read_Mode1Or2_ThrowsNamingTheReaderAndReadsNothing(ServerMeshShipWireKind configured, byte header)
        {
            byte[] data = new byte[ServerMeshShipWire.Header56Bytes];
            data[0] = header;
            NetDataReader reader = new NetDataReader(data);

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => ServerMeshShipWire.Read(reader, configured, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("reader"));
            Assert.That(reader.AvailableBytes, Is.EqualTo(ServerMeshShipWire.Header56Bytes));
        }

        private static IEnumerable<TestCaseData> WrongModeCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, ServerMeshShipWireKind.Compact28).SetName("ReadHeader56_ACompactRecord_ThrowsNamingTheReaderAndReadsNothing");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, ServerMeshShipWireKind.Header56).SetName("ReadCompact28_AHeader56Record_ThrowsNamingTheReaderAndReadsNothing");
        }

        [TestCaseSource(nameof(WrongModeCases))]
        public void Read_ARecordOfAnotherMode_ThrowsNamingTheReaderAndReadsNothing(ServerMeshShipWireKind readAs, ServerMeshShipWireKind written)
        {
            NetDataWriter writer = Written(written, MeshSyncGoldenFixture.Sample(true));
            writer.Put(new byte[ServerMeshShipWire.Header56Bytes]);
            NetDataReader reader = new NetDataReader(writer.CopyData());
            int before = reader.AvailableBytes;

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => ReadAs(readAs, reader, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("reader"));
            Assert.That(reader.AvailableBytes, Is.EqualTo(before));
        }

        private static IEnumerable<TestCaseData> CompactRefusalCases()
        {
            yield return new TestCaseData(new Vector3(float.NaN, 0f, 0f), Vector3.zero, Vector3.zero, Quaternion.identity).SetName("WriteCompact28_NaNPosition_Refuses");
            yield return new TestCaseData(new Vector3(0f, float.PositiveInfinity, 0f), Vector3.zero, Vector3.zero, Quaternion.identity).SetName("WriteCompact28_InfinitePosition_Refuses");
            yield return new TestCaseData(Vector3.zero, new Vector3(0f, float.NaN, 0f), Vector3.zero, Quaternion.identity).SetName("WriteCompact28_NaNVelocity_Refuses");
            yield return new TestCaseData(Vector3.zero, new Vector3(0f, 0f, float.NegativeInfinity), Vector3.zero, Quaternion.identity).SetName("WriteCompact28_InfiniteVelocity_Refuses");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, new Vector3(float.NaN, 0f, 0f), Quaternion.identity).SetName("WriteCompact28_NaNAngularVelocity_Refuses");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, new Vector3(0f, float.PositiveInfinity, 0f), Quaternion.identity).SetName("WriteCompact28_InfiniteAngularVelocity_Refuses");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, Vector3.zero, new Quaternion(float.NaN, 0f, 0f, 1f)).SetName("WriteCompact28_NaNRotation_Refuses");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, Vector3.zero, new Quaternion(0f, 0f, float.PositiveInfinity, 1f)).SetName("WriteCompact28_InfiniteRotation_Refuses");
        }

        [TestCaseSource(nameof(CompactRefusalCases))]
        public void WriteCompact28_NonFiniteField_ReturnsFalseAndWritesNothing(Vector3 position, Vector3 velocity, Vector3 angular, Quaternion rotation)
        {
            ServerMeshShipSample sample = new ServerMeshShipSample { Position = position, Velocity = velocity, AngularVelocity = angular, Rotation = rotation };
            NetDataWriter writer = new NetDataWriter();
            writer.Put((byte)0xEE);

            bool written = ServerMeshShipWire.WriteCompact28(writer, sample);

            Assert.That(written, Is.False);
            Assert.That(Hex(writer), Is.EqualTo("EE"));
        }

        [Test]
        public void WriteCompact28_FarPosition_IsWrittenExactly()
        {
            ServerMeshShipSample sample = new ServerMeshShipSample { Position = new Vector3(1e6f, -83886.08f, 0.001f), Rotation = Quaternion.identity };
            NetDataReader reader = new NetDataReader(Written(ServerMeshShipWireKind.Compact28, sample).CopyData());

            ServerMeshShipWire.ReadCompact28(reader, out ServerMeshShipSample read);

            Assert.That(read.Position, Is.EqualTo(sample.Position));
        }

        [Test]
        public void WriteLegacy54_GoldenSample_IsByteExact()
        {
            NetDataWriter writer = Written(ServerMeshShipWireKind.Legacy54, MeshSyncGoldenFixture.Sample());

            Assert.That(Hex(writer), Is.EqualTo(MeshSyncGoldenFixture.Legacy54));
            Assert.That(writer.Length, Is.EqualTo(ServerMeshShipWire.Legacy54Bytes));
        }

        [Test]
        public void WriteHeader56_GoldenSample_IsByteExact()
        {
            NetDataWriter writer = Written(ServerMeshShipWireKind.Header56, MeshSyncGoldenFixture.Sample(true));

            Assert.That(Hex(writer), Is.EqualTo(MeshSyncGoldenFixture.Header56));
            Assert.That(writer.Length, Is.EqualTo(ServerMeshShipWire.Header56Bytes));
        }

        private static IEnumerable<TestCaseData> SpaceHeaderRowCases()
        {
            yield return new TestCaseData(MeshSyncGoldenFixture.LowCruiseFlightState, (byte)1, MeshSyncGoldenFixture.HeaderBubbleLowCruiseEpoch1).SetName("WriteHeader56_BubbleLowCruiseEpoch1_Is0801");
            yield return new TestCaseData(MeshSyncGoldenFixture.LandedFlightState, (byte)0xFF, MeshSyncGoldenFixture.HeaderBubbleLandedEpochFF).SetName("WriteHeader56_BubbleLandedEpochFF_Is10FF");
        }

        [TestCaseSource(nameof(SpaceHeaderRowCases))]
        public void WriteHeader56_SpaceHeaderRows_AreByteExact(byte flightState, byte epoch, string header)
        {
            ServerMeshShipSample sample = MeshSyncGoldenFixture.Sample();
            sample.FlightState = flightState;
            sample.Epoch = epoch;

            NetDataWriter writer = Written(ServerMeshShipWireKind.Header56, sample);

            Assert.That(Hex(writer), Is.EqualTo(header + " " + MeshSyncGoldenFixture.Legacy54));
        }

        [Test, Ignore("compact layout is rig-only until the space team decides")]
        public void WriteCompact28_GoldenSample_IsByteExact()
        {
            NetDataWriter writer = Written(ServerMeshShipWireKind.Compact28, MeshSyncGoldenFixture.Sample(true));

            Assert.That(Hex(writer), Is.EqualTo(Compact28Golden));
        }

        [Test]
        public void Writers_OnlyAppend()
        {
            ServerMeshShipSample sample = MeshSyncGoldenFixture.Sample(true);
            NetDataWriter writer = new NetDataWriter();
            writer.Put((byte)0xEE);

            ServerMeshShipWire.WriteLegacy54(writer, sample);
            ServerMeshShipWire.WriteHeader56(writer, sample);
            ServerMeshShipWire.WriteCompact28(writer, sample);

            Assert.That(writer.Length, Is.EqualTo(1 + 54 + 56 + 28));
            Assert.That(writer.Data[0], Is.EqualTo(0xEE));
        }

        private static IEnumerable<TestCaseData> SizeCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, 54).SetName("SizeOf_Legacy54_Is54");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 56).SetName("SizeOf_Header56_Is56");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, 28).SetName("SizeOf_Compact28_Is28");
        }

        [TestCaseSource(nameof(SizeCases))]
        public void SizeOf_AndTheWrittenLength_Agree(ServerMeshShipWireKind kind, int expected)
        {
            Assert.That(ServerMeshShipWire.SizeOf(kind), Is.EqualTo(expected));
            Assert.That(Written(kind, MeshSyncGoldenFixture.Sample(true)).Length, Is.EqualTo(expected));
        }

        [Test]
        public void SizeOf_UndefinedKind_ThrowsNamingIt()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => ServerMeshShipWire.SizeOf((ServerMeshShipWireKind)7));

            Assert.That(thrown.ParamName, Is.EqualTo("kind"));
        }

        [TestCaseSource(nameof(SizeCases))]
        public void Reader_ConsumesExactlyTheWrittenLength(ServerMeshShipWireKind kind, int expected)
        {
            NetDataWriter writer = Written(kind, MeshSyncGoldenFixture.Sample(true));
            writer.Put((byte)0xAB);
            NetDataReader reader = new NetDataReader(writer.CopyData());
            int before = reader.AvailableBytes;

            ReadAs(kind, reader, out _);

            Assert.That(before - reader.AvailableBytes, Is.EqualTo(expected));
            Assert.That(reader.GetByte(), Is.EqualTo(0xAB));
        }

        private static IEnumerable<TestCaseData> ExactRoundTripCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54).SetName("Legacy54_RoundTripsExactly");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56).SetName("Header56_RoundTripsExactly");
        }

        [TestCaseSource(nameof(ExactRoundTripCases))]
        public void FullRecords_RoundTripExactly(ServerMeshShipWireKind kind)
        {
            ServerMeshShipSample sample = new ServerMeshShipSample
            {
                Position = new Vector3(-12345.678f, 0.001f, 98765.43f),
                Rotation = new Quaternion(0.1f, -0.2f, 0.3f, 0.927362f),
                Velocity = new Vector3(-125.33f, 7.5f, 0.25f),
                AngularVelocity = new Vector3(0.01f, -1.2f, 3f),
                Forward = true,
                TeleportByte = 250,
                FlightState = 7,
                Epoch = 255,
            };
            NetDataReader reader = new NetDataReader(Written(kind, sample).CopyData());

            ReadAs(kind, reader, out ServerMeshShipSample read);

            Assert.That(read.Position, Is.EqualTo(sample.Position));
            Assert.That(read.Rotation, Is.EqualTo(sample.Rotation));
            Assert.That(read.Velocity, Is.EqualTo(sample.Velocity));
            Assert.That(read.AngularVelocity, Is.EqualTo(sample.AngularVelocity));
            Assert.That(read.Forward, Is.True);
            Assert.That(read.TeleportByte, Is.EqualTo(250));
            Assert.That(read.Compact, Is.False);
            bool header = kind == ServerMeshShipWireKind.Header56;
            Assert.That(read.FlightState, Is.EqualTo(header ? 7 : 0));
            Assert.That(read.Epoch, Is.EqualTo(header ? 255 : 0));
        }

        [Test]
        public void ReadHeader56_ReservedBits_AreIgnored()
        {
            byte[] data = Written(ServerMeshShipWireKind.Header56, MeshSyncGoldenFixture.Sample(true)).CopyData();
            data[0] |= 0xE0;

            ServerMeshShipWire.Read(new NetDataReader(data), ServerMeshShipWireKind.Header56, out ServerMeshShipSample read);

            Assert.That(read.FlightState, Is.EqualTo(MeshSyncGoldenFixture.HeaderFlightState));
            Assert.That(read.Epoch, Is.EqualTo(MeshSyncGoldenFixture.HeaderEpoch));
            Assert.That(read.Compact, Is.False);
        }

        [Test]
        public void Compact28_RoundTripsWithinTheQuantisation()
        {
            const float AngularTolerance = ServerMeshShipWire.CompactAngularFullScale / 127f;
            System.Random random = new System.Random(20261004);
            for (int i = 0; i < 500; ++i)
            {
                Quaternion rotation = new Quaternion(Signed(random), Signed(random), Signed(random), Signed(random));
                float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
                rotation = new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
                float speed = i % 5 == 0 ? 0.01f : 600f;
                ServerMeshShipSample sample = new ServerMeshShipSample
                {
                    Position = new Vector3(Signed(random) * 1e5f, Signed(random) * 1000f, Signed(random) * 1e5f),
                    Rotation = rotation,
                    Velocity = new Vector3(Signed(random) * speed, Signed(random) * speed, Signed(random) * speed),
                    AngularVelocity = new Vector3(Signed(random) * 4f, Signed(random) * 4f, Signed(random) * 4f),
                    Forward = i % 2 == 0,
                    TeleportByte = (byte)i,
                    FlightState = (byte)(i % 8),
                    Epoch = (byte)(i * 7),
                };
                NetDataReader reader = new NetDataReader(Written(ServerMeshShipWireKind.Compact28, sample).CopyData());

                ServerMeshShipWire.ReadCompact28(reader, out ServerMeshShipSample read);

                Assert.That(read.Position, Is.EqualTo(sample.Position), $"sample {i} position");
                AssertVelocityWithin(read.Velocity.x, sample.Velocity.x, $"sample {i} velocity x");
                AssertVelocityWithin(read.Velocity.y, sample.Velocity.y, $"sample {i} velocity y");
                AssertVelocityWithin(read.Velocity.z, sample.Velocity.z, $"sample {i} velocity z");
                AssertEachWithin(read.AngularVelocity, sample.AngularVelocity, AngularTolerance, $"sample {i} angular velocity");
                float dot = Mathf.Abs(Quaternion.Dot(read.Rotation, sample.Rotation));
                Assert.That(1f - dot, Is.LessThanOrEqualTo(0.002f), $"sample {i} rotation");
                Assert.That(Quaternion.Dot(read.Rotation, read.Rotation), Is.EqualTo(1f).Within(1e-5f), $"sample {i} rotation normalised");
                Assert.That(read.Forward, Is.EqualTo(sample.Forward));
                Assert.That(read.TeleportByte, Is.EqualTo(sample.TeleportByte & 0x7F));
                Assert.That(read.FlightState, Is.EqualTo(sample.FlightState));
                Assert.That(read.Epoch, Is.EqualTo(sample.Epoch));
                Assert.That(read.Compact, Is.True);
                Assert.That(reader.AvailableBytes, Is.EqualTo(0));
            }
        }

        [Test]
        public void Compact28_VelocitiesBeyondTheRange_AreClamped()
        {
            ServerMeshShipSample sample = new ServerMeshShipSample
            {
                Rotation = Quaternion.identity,
                Velocity = new Vector3(1e5f, -1e5f, 70000f),
                AngularVelocity = new Vector3(0f, 40f, -40f),
            };
            NetDataReader reader = new NetDataReader(Written(ServerMeshShipWireKind.Compact28, sample).CopyData());

            ServerMeshShipWire.ReadCompact28(reader, out ServerMeshShipSample read);

            Assert.That(read.Velocity, Is.EqualTo(new Vector3(ServerMeshShipWire.HalfMax, -ServerMeshShipWire.HalfMax, ServerMeshShipWire.HalfMax)));
            AssertEachWithin(read.AngularVelocity, new Vector3(0f, 4f, -4f), 1e-5f, "angular velocity");
        }

        private static IEnumerable<TestCaseData> DispatchCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, ServerMeshShipWireKind.Header56).SetName("Read_Header56Configured_Mode0Record_Reads56");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, ServerMeshShipWireKind.Compact28).SetName("Read_Header56Configured_Mode3Record_Reads28");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, ServerMeshShipWireKind.Header56).SetName("Read_Compact28Configured_Mode0Record_Reads56");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, ServerMeshShipWireKind.Compact28).SetName("Read_Compact28Configured_Mode3Record_Reads28");
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, ServerMeshShipWireKind.Legacy54).SetName("Read_Legacy54Configured_Reads54");
        }

        [TestCaseSource(nameof(DispatchCases))]
        public void Read_TellsTheRecordByItsMode(ServerMeshShipWireKind configured, ServerMeshShipWireKind written)
        {
            NetDataWriter writer = Written(written, MeshSyncGoldenFixture.Sample(true));
            writer.Put((byte)0xAB);
            NetDataReader reader = new NetDataReader(writer.CopyData());

            ServerMeshShipWire.Read(reader, configured, out ServerMeshShipSample read);

            Assert.That(reader.AvailableBytes, Is.EqualTo(1));
            Assert.That(read.Compact, Is.EqualTo(written == ServerMeshShipWireKind.Compact28));
            Assert.That(read.TeleportByte, Is.EqualTo(MeshSyncGoldenFixture.TeleportByte));
        }

        private static IEnumerable<TestCaseData> HeaderByteCases()
        {
            yield return new TestCaseData((byte)0, (byte)0, (byte)0x00).SetName("HeaderByte_BubbleCombat_Is00");
            yield return new TestCaseData((byte)0, (byte)1, (byte)0x04).SetName("HeaderByte_BubbleSpooling_Is04");
            yield return new TestCaseData((byte)0, (byte)2, (byte)0x08).SetName("HeaderByte_BubbleLowCruise_Is08");
            yield return new TestCaseData((byte)0, (byte)3, (byte)0x0C).SetName("HeaderByte_BubbleBraking_Is0C");
            yield return new TestCaseData((byte)0, (byte)4, (byte)0x10).SetName("HeaderByte_BubbleLanded_Is10");
            yield return new TestCaseData((byte)1, (byte)0, (byte)0x01).SetName("HeaderByte_SupercruiseCombat_Is01");
            yield return new TestCaseData((byte)1, (byte)3, (byte)0x0D).SetName("HeaderByte_SupercruiseBraking_Is0D");
            yield return new TestCaseData((byte)2, (byte)4, (byte)0x12).SetName("HeaderByte_FrameChangeLanded_Is12");
            yield return new TestCaseData((byte)3, (byte)2, (byte)0x0B).SetName("HeaderByte_CompactLowCruise_Is0B");
            yield return new TestCaseData((byte)3, (byte)7, (byte)0x1F).SetName("HeaderByte_CompactFlightState7_Is1F");
        }

        [TestCaseSource(nameof(HeaderByteCases))]
        public void HeaderByte_ComposesTheBits(byte mode, byte flightState, byte expected)
        {
            byte header = ServerMeshShipWire.HeaderByte(mode, flightState);

            Assert.That(header, Is.EqualTo(expected));
            Assert.That(ServerMeshShipWire.ModeOf(header), Is.EqualTo(mode));
            Assert.That(ServerMeshShipWire.ModeOf((byte)(header | 0xE0)), Is.EqualTo(mode));
        }

        private static float Signed(System.Random random) => (float)(random.NextDouble() * 2d - 1d);

        private static void AssertVelocityWithin(float actual, float expected, string what)
        {
            float tolerance = Math.Max(Math.Abs(expected) / 1024f, 1e-3f);
            Assert.That(actual, Is.EqualTo(expected).Within(tolerance), what);
        }

        private static void AssertEachWithin(Vector3 actual, Vector3 expected, float tolerance, string what)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance), what + " x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance), what + " y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance), what + " z");
        }
    }
}
