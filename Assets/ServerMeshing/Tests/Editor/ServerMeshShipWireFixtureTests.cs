using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// ServerMeshShipWireFixtureTests writes every case of the space game's mode 3 golden fixture through the rig's compact
    /// codec and reads it back. The fixture is a copy of the space repo's Docs/Fixtures/ship_wire_mode3_golden.json; a change
    /// to either copy is a change to the wire, made in both repos.
    /// </summary>
    public class ServerMeshShipWireFixtureTests
    {
        private const string FixturePath = "Docs/ServerMeshing/fixtures/ship_wire_mode3_golden.json";

        /// <summary>FixtureCase is one case of the fixture, as JsonUtility reads it.</summary>
        [Serializable]
        public class FixtureCase
        {
            public string name;
            public int flightMode;
            public int epoch;
            public float[] slotOrigin;
            public float[] offset;
            public float[] rotation;
            public float[] velocity;
            public float[] angularVelocity;
            public bool forward;
            public int teleportLow;
            public double stampSeconds;
            public string bytes;
        }

        /// <summary>Fixture is the file: its version and its cases.</summary>
        [Serializable]
        public class Fixture
        {
            public int version;
            public List<FixtureCase> cases;
        }

        private static Fixture Load()
        {
            string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), FixturePath);
            Assert.That(File.Exists(path), Is.True, $"the fixture is at {FixturePath}");
            return JsonUtility.FromJson<Fixture>(File.ReadAllText(path));
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (FixtureCase fixtureCase in Load().cases)
                yield return new TestCaseData(fixtureCase).SetName($"Compact_Fixture_{fixtureCase.name}");
        }

        private static ServerMeshShipSample SampleOf(FixtureCase c)
        {
            return new ServerMeshShipSample
            {
                Position = new Vector3(c.offset[0], c.offset[1], c.offset[2]),
                Rotation = new Quaternion(c.rotation[0], c.rotation[1], c.rotation[2], c.rotation[3]),
                Velocity = new Vector3(c.velocity[0], c.velocity[1], c.velocity[2]),
                AngularVelocity = new Vector3(c.angularVelocity[0], c.angularVelocity[1], c.angularVelocity[2]),
                Forward = c.forward,
                TeleportByte = (byte)c.teleportLow,
                FlightState = (byte)c.flightMode,
                Epoch = (byte)c.epoch,
                SampleTime = c.stampSeconds,
                FrameTag = ServerMeshShipWire.FrameTagOf(new Vector3(c.slotOrigin[0], c.slotOrigin[1], c.slotOrigin[2])),
            };
        }

        [Test]
        public void Fixture_IsVersion1WithCases()
        {
            Fixture fixture = Load();
            Assert.That(fixture.version, Is.EqualTo(1));
            Assert.That(fixture.cases, Is.Not.Empty);
        }

        [TestCaseSource(nameof(Cases))]
        public void Compact_FixtureCase_WritesItsBytes(FixtureCase c)
        {
            ServerMeshShipSample sample = SampleOf(c);
            NetDataWriter writer = new NetDataWriter();

            Assert.That(ServerMeshShipWire.WriteCompact(writer, in sample), Is.True);
            Assert.That(BitConverter.ToString(writer.CopyData()).Replace("-", ""), Is.EqualTo(c.bytes));
            Assert.That(writer.Length, Is.EqualTo(ServerMeshShipWire.CompactBytes));
        }

        [TestCaseSource(nameof(Cases))]
        public void Compact_FixtureCase_ReadsBack(FixtureCase c)
        {
            ServerMeshShipSample sample = SampleOf(c);
            NetDataWriter writer = new NetDataWriter();
            ServerMeshShipWire.WriteCompact(writer, in sample);
            NetDataReader reader = new NetDataReader(writer.CopyData());

            ServerMeshShipWire.ReadCompact(reader, out ServerMeshShipSample back);

            Assert.That(reader.AvailableBytes, Is.Zero);
            Assert.That(back.Compact, Is.True);
            Assert.That(back.Epoch, Is.EqualTo(sample.Epoch));
            Assert.That(back.FlightState, Is.EqualTo(sample.FlightState));
            Assert.That(back.Forward, Is.EqualTo(sample.Forward));
            Assert.That(back.TeleportByte, Is.EqualTo(sample.TeleportByte & 0x7F));
            Assert.That(back.Stamp, Is.EqualTo(ServerMeshShipWire.StampOf(c.stampSeconds)));
            Assert.That(back.FrameTag, Is.EqualTo(sample.FrameTag));
        }
    }
}
