using System.Collections.Generic;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>DESIGN 1.39: what a passenger's snapshot and handoff carry about its seat.</summary>
    public class MeshPassengerSeatTests
    {
        private static GhostSnapshotMessage RoundTrip(GhostSnapshotMessage msg)
        {
            NetDataWriter writer = new NetDataWriter();
            msg.Serialize(writer);
            GhostSnapshotMessage read = default;
            read.Deserialize(new NetDataReader(writer.CopyData()));
            return read;
        }

        [Test]
        public void Snapshot_CarriesThePassengersSeatAndDriver()
        {
            GhostSnapshotMessage read = RoundTrip(new GhostSnapshotMessage { kind = GhostKind.Player, characterId = "p", characterName = "P", mountEntityId = 77, mountLevel = 3, mountSeat = 1, driverMeshId = "d", sampleMs = 5.0 });

            Assert.That(read.mountEntityId, Is.EqualTo(77));
            Assert.That(read.mountLevel, Is.EqualTo(3));
            Assert.That(read.mountSeat, Is.EqualTo(1));
            Assert.That(read.driverMeshId, Is.EqualTo("d"));
            Assert.That(read.sampleMs, Is.EqualTo(5.0), "the fields after the seat still read in place");
        }

        [Test]
        public void Snapshot_OfADriver_ReadsSeatZeroAndNoDriver()
        {
            GhostSnapshotMessage read = RoundTrip(new GhostSnapshotMessage { kind = GhostKind.Player, characterId = "p", characterName = "P", mountEntityId = 77, driverMeshId = null });

            Assert.That(read.mountSeat, Is.EqualTo(0));
            Assert.That(read.driverMeshId, Is.EqualTo(string.Empty), "a null driver goes out as an empty name");
        }

        [Test]
        public void Seat_WritesAndReadsBack()
        {
            NetDataWriter writer = new NetDataWriter();
            new MeshPassengerSeat { driverMeshId = "driver-1", seat = 2 }.Write(writer);

            MeshPassengerSeat read = MeshPassengerSeat.Read(new NetDataReader(writer.CopyData()));

            Assert.That(read.driverMeshId, Is.EqualTo("driver-1"));
            Assert.That(read.seat, Is.EqualTo(2));
            Assert.That(read.IsEmpty, Is.False);
        }

        [Test]
        public void ADriverOrSomeoneBehindNobody_CarriesNothing()
        {
            Assert.That(default(MeshPassengerSeat).IsEmpty, Is.True);
            Assert.That(new MeshPassengerSeat { driverMeshId = "d", seat = 0 }.IsEmpty, Is.True, "seat 0 drives");
            Assert.That(new MeshPassengerSeat { driverMeshId = "", seat = 1 }.IsEmpty, Is.True, "a passenger behind nobody has no vehicle to be seated on");
            Assert.That(new MeshPassengerSeat { driverMeshId = "d", seat = 1 }.IsEmpty, Is.False);
        }

        [Test]
        public void TheCarrier_IsBuiltIn_UnderItsOwnKey()
        {
            List<IMeshHandoffCarrier> builtIn = MeshHandoffCarriers.BuiltIn;
            HashSet<string> keys = new HashSet<string>();
            foreach (IMeshHandoffCarrier carrier in builtIn)
                Assert.That(keys.Add(carrier.CarrierKey), Is.True, $"carrier key {carrier.CarrierKey} is used twice");

            Assert.That(keys, Does.Contain(MeshPassengerCarrier.Key));
            Assert.That(MeshPassengerCarrier.Key, Is.EqualTo("mesh.passenger"));
        }
    }
}
