using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipWireTests
    {
        private const string Compact28Golden = "A9 07 96 00 00 1F FF FF A0 86 01 FF 03 08 60 F4 01 00 00 06 FF 00 00 F4 01 00 00 03";

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

        private static IEnumerable<TestCaseData> HeaderFieldCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, (ServerMeshShipAuthority)3, (byte)0, "Authority").SetName("WriteHeader56_Authority3_ThrowsNamingAuthority");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, (ServerMeshShipAuthority)9, (byte)0, "Authority").SetName("WriteHeader56_Authority9_ThrowsNamingAuthority");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, ServerMeshShipAuthority.Parked, (byte)8, "FlightState").SetName("WriteHeader56_FlightState8_ThrowsNamingFlightState");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, (ServerMeshShipAuthority)3, (byte)0, "Authority").SetName("WriteCompact28_Authority3_ThrowsNamingAuthority");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, ServerMeshShipAuthority.OwnerMirror, (byte)8, "FlightState").SetName("WriteCompact28_FlightState8_ThrowsNamingFlightState");
        }

        [TestCaseSource(nameof(HeaderFieldCases))]
        public void Write_HeaderFieldOutOfRange_ThrowsNamingTheFieldAndWritesNothing(ServerMeshShipWireKind kind, ServerMeshShipAuthority authority, byte flightState, string field)
        {
            ServerMeshShipSample sample = MeshSyncGoldenFixture.Sample();
            sample.Authority = authority;
            sample.FlightState = flightState;
            NetDataWriter writer = new NetDataWriter();

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                if (kind == ServerMeshShipWireKind.Header56)
                    ServerMeshShipWire.WriteHeader56(writer, sample);
                else
                    ServerMeshShipWire.WriteCompact28(writer, sample);
            });

            Assert.That(thrown.ParamName, Is.EqualTo(field));
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
            for (int i = 0; i < data.Length; ++i)
                data[i] = 0x11;
            NetDataReader reader = new NetDataReader(data);

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => ReadAs(kind, reader, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("reader"));
            Assert.That(reader.AvailableBytes, Is.EqualTo(available));
        }

        [Test]
        public void Read_EmptyReaderForAHeaderedWire_ThrowsNamingTheReader()
        {
            NetDataReader reader = new NetDataReader(new byte[0]);

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => ServerMeshShipWire.Read(reader, ServerMeshShipWireKind.Header56, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("reader"));
        }

        [Test]
        public void Read_NullReader_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => ServerMeshShipWire.ReadLegacy54(null, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("reader"));
        }

        private static IEnumerable<TestCaseData> CompactRefusalCases()
        {
            yield return new TestCaseData(new Vector3(83886.08f, 0f, 0f), Vector3.zero, Quaternion.identity).SetName("WriteCompact28_XBeyondTheLimit_Refuses");
            yield return new TestCaseData(new Vector3(0f, -83886.08f, 0f), Vector3.zero, Quaternion.identity).SetName("WriteCompact28_YBeyondTheNegativeLimit_Refuses");
            yield return new TestCaseData(new Vector3(0f, 0f, 1e6f), Vector3.zero, Quaternion.identity).SetName("WriteCompact28_ZFarBeyondTheLimit_Refuses");
            yield return new TestCaseData(new Vector3(float.NaN, 0f, 0f), Vector3.zero, Quaternion.identity).SetName("WriteCompact28_NaNPosition_Refuses");
            yield return new TestCaseData(new Vector3(0f, float.PositiveInfinity, 0f), Vector3.zero, Quaternion.identity).SetName("WriteCompact28_InfinitePosition_Refuses");
            yield return new TestCaseData(Vector3.zero, new Vector3(0f, float.NaN, 0f), Quaternion.identity).SetName("WriteCompact28_NaNVelocity_Refuses");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, new Quaternion(float.NaN, 0f, 0f, 1f)).SetName("WriteCompact28_NaNRotation_Refuses");
        }

        [TestCaseSource(nameof(CompactRefusalCases))]
        public void WriteCompact28_UnencodableSample_ReturnsFalseAndWritesNothing(Vector3 position, Vector3 velocity, Quaternion rotation)
        {
            ServerMeshShipSample sample = new ServerMeshShipSample { Position = position, Velocity = velocity, Rotation = rotation };
            NetDataWriter writer = new NetDataWriter();
            writer.Put((byte)0xEE);

            bool written = ServerMeshShipWire.WriteCompact28(writer, sample);

            Assert.That(written, Is.False);
            Assert.That(Hex(writer), Is.EqualTo("EE"));
        }

        [Test]
        public void WriteCompact28_PositionAtTheLimit_IsWritten()
        {
            ServerMeshShipSample sample = new ServerMeshShipSample { Position = new Vector3(83886.07f, -83886.07f, 0f), Rotation = Quaternion.identity };

            Assert.That(ServerMeshShipWire.WriteCompact28(new NetDataWriter(), sample), Is.True);
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
                Authority = ServerMeshShipAuthority.Parked,
                FlightState = 7,
                Epoch = 255,
            };
            NetDataReader reader = new NetDataReader(Written(kind, sample).CopyData());

            ReadAs(kind, reader, out ServerMeshShipSample read);

            Assert.That(read.Position, Is.EqualTo(sample.Position));
            Assert.That(read.Rotation.x, Is.EqualTo(sample.Rotation.x));
            Assert.That(read.Rotation.y, Is.EqualTo(sample.Rotation.y));
            Assert.That(read.Rotation.z, Is.EqualTo(sample.Rotation.z));
            Assert.That(read.Rotation.w, Is.EqualTo(sample.Rotation.w));
            Assert.That(read.Velocity, Is.EqualTo(sample.Velocity));
            Assert.That(read.AngularVelocity, Is.EqualTo(sample.AngularVelocity));
            Assert.That(read.Forward, Is.True);
            Assert.That(read.TeleportByte, Is.EqualTo(250));
            Assert.That(read.Compact, Is.False);
            bool header = kind == ServerMeshShipWireKind.Header56;
            Assert.That(read.Authority, Is.EqualTo(header ? ServerMeshShipAuthority.Parked : ServerMeshShipAuthority.OwnerMirror));
            Assert.That(read.FlightState, Is.EqualTo(header ? 7 : 0));
            Assert.That(read.Epoch, Is.EqualTo(header ? 255 : 0));
        }

        [Test]
        public void Compact28_RoundTripsWithinTheQuantisation()
        {
            System.Random random = new System.Random(20261003);
            for (int i = 0; i < 500; ++i)
            {
                Quaternion rotation = new Quaternion(Signed(random), Signed(random), Signed(random), Signed(random));
                float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
                rotation = new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
                ServerMeshShipSample sample = new ServerMeshShipSample
                {
                    Position = new Vector3(Signed(random) * 1000f, Signed(random) * 1000f, Signed(random) * 1000f),
                    Rotation = rotation,
                    Velocity = new Vector3(Signed(random) * 600f, Signed(random) * 600f, Signed(random) * 600f),
                    AngularVelocity = new Vector3(Signed(random) * 30f, Signed(random) * 30f, Signed(random) * 30f),
                    Forward = i % 2 == 0,
                    TeleportByte = (byte)i,
                    Authority = (ServerMeshShipAuthority)(i % 3),
                    FlightState = (byte)(i % 8),
                    Epoch = (byte)(i * 7),
                };
                NetDataReader reader = new NetDataReader(Written(ServerMeshShipWireKind.Compact28, sample).CopyData());

                ServerMeshShipWire.ReadCompact28(reader, out ServerMeshShipSample read);

                Assert.That(Vector3.Distance(read.Position, sample.Position), Is.LessThanOrEqualTo(Mathf.Sqrt(3f) * 0.0051f), $"sample {i} position");
                AssertEachWithin(read.Position, sample.Position, 0.0051f, $"sample {i} position");
                AssertEachWithin(read.Velocity, sample.Velocity, 0.01f + 1e-4f, $"sample {i} velocity");
                AssertEachWithin(read.AngularVelocity, sample.AngularVelocity, 0.0005f + 1e-6f, $"sample {i} angular velocity");
                float dot = Mathf.Abs(Quaternion.Dot(read.Rotation, sample.Rotation));
                Assert.That(1f - dot, Is.LessThanOrEqualTo(0.002f), $"sample {i} rotation");
                Assert.That(read.Forward, Is.EqualTo(sample.Forward));
                Assert.That(read.TeleportByte, Is.EqualTo(sample.TeleportByte));
                Assert.That(read.Authority, Is.EqualTo(sample.Authority));
                Assert.That(read.FlightState, Is.EqualTo(sample.FlightState));
                Assert.That(read.Epoch, Is.EqualTo(sample.Epoch));
                Assert.That(read.Compact, Is.True);
            }
        }

        [Test]
        public void Compact28_VelocitiesBeyondTheRange_AreClamped()
        {
            ServerMeshShipSample sample = new ServerMeshShipSample
            {
                Rotation = Quaternion.identity,
                Velocity = new Vector3(1000f, -1000f, 0f),
                AngularVelocity = new Vector3(0f, 40f, -40f),
            };
            NetDataReader reader = new NetDataReader(Written(ServerMeshShipWireKind.Compact28, sample).CopyData());

            ServerMeshShipWire.ReadCompact28(reader, out ServerMeshShipSample read);

            AssertEachWithin(read.Velocity, new Vector3(655.34f, -655.34f, 0f), 1e-3f, "velocity");
            AssertEachWithin(read.AngularVelocity, new Vector3(0f, 32.767f, -32.767f), 1e-4f, "angular velocity");
        }

        [Test]
        public void Compact28_DecodedRotation_IsNormalised()
        {
            ServerMeshShipSample sample = new ServerMeshShipSample { Rotation = new Quaternion(0.2f, 0.4f, -0.1f, 0.8888f) };
            NetDataReader reader = new NetDataReader(Written(ServerMeshShipWireKind.Compact28, sample).CopyData());

            ServerMeshShipWire.ReadCompact28(reader, out ServerMeshShipSample read);

            float length = Mathf.Sqrt(read.Rotation.x * read.Rotation.x + read.Rotation.y * read.Rotation.y + read.Rotation.z * read.Rotation.z + read.Rotation.w * read.Rotation.w);
            Assert.That(length, Is.EqualTo(1f).Within(1e-5f));
        }

        private static IEnumerable<TestCaseData> DispatchCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, ServerMeshShipWireKind.Header56).SetName("Read_Header56Configured_FullRecord_Reads56");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, ServerMeshShipWireKind.Compact28).SetName("Read_Header56Configured_CompactRecord_Reads28");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, ServerMeshShipWireKind.Header56).SetName("Read_Compact28Configured_FullRecord_Reads56");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, ServerMeshShipWireKind.Compact28).SetName("Read_Compact28Configured_CompactRecord_Reads28");
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, ServerMeshShipWireKind.Legacy54).SetName("Read_Legacy54Configured_Reads54");
        }

        [TestCaseSource(nameof(DispatchCases))]
        public void Read_TellsTheRecordByItsCompactBit(ServerMeshShipWireKind configured, ServerMeshShipWireKind written)
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
            yield return new TestCaseData(ServerMeshShipAuthority.OwnerMirror, (byte)0, false, false, (byte)0x00).SetName("HeaderByte_AllZero_IsZero");
            yield return new TestCaseData(ServerMeshShipAuthority.ServerAI, (byte)2, false, false, (byte)0x09).SetName("HeaderByte_ServerAIFlightState2_Is09");
            yield return new TestCaseData(ServerMeshShipAuthority.Parked, (byte)7, false, false, (byte)0x1E).SetName("HeaderByte_ParkedFlightState7_Is1E");
            yield return new TestCaseData(ServerMeshShipAuthority.ServerAI, (byte)2, true, true, (byte)0xA9).SetName("HeaderByte_CompactForward_SetsBits7And5");
            yield return new TestCaseData(ServerMeshShipAuthority.OwnerMirror, (byte)0, true, false, (byte)0x80).SetName("HeaderByte_CompactOnly_IsTheCompactBit");
        }

        [TestCaseSource(nameof(HeaderByteCases))]
        public void HeaderByte_ComposesTheBits(ServerMeshShipAuthority authority, byte flightState, bool compact, bool forward, byte expected)
        {
            Assert.That(ServerMeshShipWire.HeaderByte(authority, flightState, compact, forward), Is.EqualTo(expected));
        }

        private static float Signed(System.Random random) => (float)(random.NextDouble() * 2d - 1d);

        private static void AssertEachWithin(Vector3 actual, Vector3 expected, float tolerance, string what)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance), what + " x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance), what + " y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance), what + " z");
        }
    }
}
